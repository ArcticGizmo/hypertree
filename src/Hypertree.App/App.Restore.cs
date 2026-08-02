using System.Threading.Tasks;
using Hypertree.App.Views;
using Hypertree.Desktops;
using Hypertree.Launch;
using Hypertree.Loadouts;
using Hypertree.Scopes;

namespace Hypertree.App;

/// <summary>
/// Restoring a loadout (docs/design/session-restore.md, Phase B): create the loadout's desktops plus a
/// throwaway <b>staging</b> desktop, launch every step on staging (where new windows reliably appear), find
/// each launched window and move it to its target desktop, then fold the targets into a new branch and land
/// there. A blocking <see cref="RestoreProgressContent"/> overlay shows each step's state and offers cancel;
/// windows launched but never placed can be closed (only ever while still on staging) or left.
///
/// The OS-free decisions (flatten, match-a-new-window, cleanup candidates) live in <see cref="LoadoutRun"/>;
/// this is the timing + Win32 glue, which the running tray has to exercise for real — it can't be unit-tested.
/// </summary>
public sealed partial class App
{
    private const int PollMs = 150;          // how often to look for a launched window
    private const int LaunchTimeoutMs = 8000; // give a slow app this long to show its first window

    // Applying a loadout: if it uses {name} variables, fill them first, substitute, then confirm the filled
    // commands; otherwise straight to the confirm. Keeps one loadout reusable across projects.
    private void BeginApply(Loadout loadout)
    {
        if (loadout.Desktops.Count == 0)
        {
            Notify("Empty loadout", $"“{loadout.Name}” has no desktops — build one first.");
            return;
        }

        IReadOnlyList<VariableSpec> prompts = LoadoutVariables.Prompts(loadout);
        if (prompts.Count == 0) { ConfirmRestore(loadout); return; }

        _stage?.Present(new VariableFillContent(prompts, loadout.Name,
            values => ConfirmRestore(LoadoutSubstitution.Apply(loadout, values))));
    }

    // The htree-populate path: pre-fill each variable from the supplied values, then its declared default.
    // If everything's known, apply straight away (no confirm — the CLI call was the intent); otherwise prompt
    // for the ones still missing, with the known values prefilled.
    private void ApplyLoadoutFromValues(Loadout loadout, IReadOnlyDictionary<string, string> supplied)
    {
        var supplyLookup = new Dictionary<string, string>(supplied, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<VariableSpec> prompts = LoadoutVariables.Prompts(loadout);

        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (VariableSpec p in prompts)
        {
            if (supplyLookup.TryGetValue(p.Name, out string? v) && v.Trim().Length > 0) known[p.Name] = v.Trim();
            else if (!string.IsNullOrWhiteSpace(p.Default)) known[p.Name] = p.Default!;
        }

        if (prompts.All(p => known.ContainsKey(p.Name)))
        {
            RestoreLoadout(LoadoutSubstitution.Apply(loadout, known)); // fully specified — go
            return;
        }

        // Prompt for the rest, pre-filling what we already know.
        var seeded = prompts
            .Select(p => p with { Default = known.TryGetValue(p.Name, out string? v) ? v : p.Default })
            .ToList();
        _stage?.Summon(new VariableFillContent(seeded, loadout.Name,
            values => RestoreLoadout(LoadoutSubstitution.Apply(loadout, values))));
    }

    // Ask before applying — the confirm doubles as an inspector, listing the desktops and (now filled-in)
    // commands it'll create.
    private void ConfirmRestore(Loadout loadout)
    {
        if (loadout.Desktops.Count == 0)
        {
            Notify("Empty loadout", $"“{loadout.Name}” has nothing to apply — build one first.");
            return;
        }
        _stage?.Present(new ConfirmContent(RestoreConfirmMessage(loadout), () => RestoreLoadout(loadout), confirmLabel: "Apply"));
    }

    private static string RestoreConfirmMessage(Loadout r)
    {
        IEnumerable<string> lines = r.Desktops.Select(d =>
            $"  {d.Label}: {string.Join("  ·  ", d.Steps.Select(s => CommandLine.Join(s.Target, s.Arguments)))}");
        return $"Apply “{r.Name}”?\n" +
               $"Creates a new branch and runs {r.StepCount} command{(r.StepCount == 1 ? "" : "s")} across " +
               $"{r.Desktops.Count} desktop{(r.Desktops.Count == 1 ? "" : "s")}:\n" +
               string.Join("\n", lines);
    }

    private async void RestoreLoadout(Loadout loadout)
    {
        if (_model is null || _desktops is null || _appLauncher is null || _stage is null) return;
        if (loadout.Desktops.Count == 0) return;

        IReadOnlyList<RunStep> steps = LoadoutRun.Plan(loadout);
        var content = new RestoreProgressContent($"Applying “{loadout.Name}”", steps);
        bool cancelled = false;
        content.Cancelled += () => cancelled = true;
        _stage.Summon(content); // full-surface, pinned to every desktop, so it stays up while we hop to staging

        _model.Reconcile();

        // 1) Create the loadout's target desktops (by label) + a throwaway staging desktop to launch on.
        var targets = new Dictionary<string, DesktopId>();
        foreach (LoadoutDesktop d in loadout.Desktops)
        {
            DesktopId id = _desktops.Create($"{loadout.Name} · {d.Label}");
            _created.Add(id.Value);
            targets[d.Label] = id;
        }
        DesktopId staging = _desktops.Create($"{loadout.Name} · staging");
        _created.Add(staging.Value);

        _desktops.SwitchTo(staging); // new windows open on the current desktop → land them here, then move
        _stage.BringToFront();

        // 2) Launch each step on staging, find the window it produced, move it to its target desktop.
        foreach (RunStep rs in steps)
        {
            if (cancelled) break;

            rs.State = StepState.Creating;
            content.Refresh();

            var before = _desktops.AllWindows().Select(w => w.Hwnd).ToHashSet();
            if (!_appLauncher.Launch(rs.Step.Target, rs.Step.Arguments, rs.Step.WorkingDirectory))
            {
                rs.State = StepState.Error;
                rs.Note = "couldn’t launch";
                content.Refresh();
                continue;
            }

            nint hwnd = 0;
            for (int waited = 0; waited < LaunchTimeoutMs && !cancelled; waited += PollMs)
            {
                await Task.Delay(PollMs); // resumes on the UI thread (Avalonia sync context) for the next COM call
                hwnd = LoadoutRun.MatchNewWindow(rs.Step, before, _desktops.AllWindows());
                if (hwnd != 0) break;
            }
            if (hwnd == 0)
            {
                rs.State = StepState.AlreadyOpen; // no new window — already running / single-instance focus
                rs.Note = "no new window";
                content.Refresh();
                continue;
            }

            rs.Window = hwnd;
            rs.State = StepState.Placing;
            content.Refresh();
            try
            {
                _desktops.MoveWindowToDesktop(hwnd, targets[rs.DesktopLabel]);
                if (rs.Step.Placement.Monitor is int monitor) _desktops.MoveWindowToMonitor(hwnd, monitor);
                rs.State = StepState.Done;
            }
            catch { rs.State = StepState.Error; rs.Note = "couldn’t place"; }
            content.Refresh();
            _stage.BringToFront();
        }

        // 3) Windows we launched but couldn't place are still on staging. Confirm each really is on staging
        //    (the certainty rule) before it's ever eligible to be closed.
        List<nint> residue = LoadoutRun.CleanupCandidates(steps)
            .Where(h => _desktops.DesktopOf(h) == staging)
            .ToList();

        RestoreDecision decision = await content.Finish(residue.Count);
        bool keepStaging = false;
        if (decision == RestoreDecision.CleanUp)
            foreach (nint h in residue) { try { _desktops.CloseWindow(h); } catch { /* window may refuse / prompt — best-effort */ } }
        else if (residue.Count > 0)
            keepStaging = true; // leave the unplaced windows where they are — keep staging as a branch desktop

        // 4) Fold the target desktops into a new branch, drop staging (unless we're keeping leftovers), land there.
        var refs = loadout.Desktops.Select(d => new DesktopRef(targets[d.Label], d.Label)).ToList();
        if (keepStaging) refs.Add(new DesktopRef(staging, "unplaced"));

        var branch = new Branch(loadout.Name, refs);
        _model.AddBranch(branch);

        if (!keepStaging)
        {
            _created.Remove(staging.Value);
            try { _desktops.Remove(staging, targets[loadout.Desktops[0].Label]); } catch { /* already gone */ }
        }

        _model.Reconcile();
        _model.GoTo(branch.Id, 0, out _); // land on the branch's first desktop

        _stage.Dismiss();
        RefreshOrFlash();

        int placed = steps.Count(s => s.State == StepState.Done);
        Notify("Loadout applied",
               $"“{loadout.Name}” — placed {placed} of {steps.Count} app{(steps.Count == 1 ? "" : "s")}.");
    }
}
