using System.Threading.Tasks;
using Hypertree.App.Views;
using Hypertree.Desktops;
using Hypertree.Recipes;
using Hypertree.Scopes;

namespace Hypertree.App;

/// <summary>
/// Restoring a recipe (docs/design/session-restore.md, Phase B): create the recipe's desktops plus a
/// throwaway <b>staging</b> desktop, launch every step on staging (where new windows reliably appear), find
/// each launched window and move it to its target desktop, then fold the targets into a new branch and land
/// there. A blocking <see cref="RestoreProgressContent"/> overlay shows each step's state and offers cancel;
/// windows launched but never placed can be closed (only ever while still on staging) or left.
///
/// The OS-free decisions (flatten, match-a-new-window, cleanup candidates) live in <see cref="RecipeRun"/>;
/// this is the timing + Win32 glue, which the running tray has to exercise for real — it can't be unit-tested.
/// </summary>
public sealed partial class App
{
    private const int PollMs = 150;          // how often to look for a launched window
    private const int LaunchTimeoutMs = 8000; // give a slow app this long to show its first window

    // Ask before restoring — the confirm doubles as an inspector, listing the desktops and apps it'll create.
    private void ConfirmRestore(Recipe recipe)
    {
        if (recipe.Desktops.Count == 0)
        {
            Notify("Empty recipe", $"“{recipe.Name}” has nothing to restore — save a branch with some apps open first.");
            return;
        }
        _stage?.Present(new ConfirmContent(RestoreConfirmMessage(recipe), () => RestoreRecipe(recipe), confirmLabel: "Restore"));
    }

    private static string RestoreConfirmMessage(Recipe r)
    {
        IEnumerable<string> lines = r.Desktops.Select(d => $"  {d.Label}: {string.Join(", ", d.Steps.Select(s => s.Name))}");
        return $"Restore “{r.Name}”?\n" +
               $"Creates a new branch and launches {r.StepCount} app{(r.StepCount == 1 ? "" : "s")} onto " +
               $"{r.Desktops.Count} desktop{(r.Desktops.Count == 1 ? "" : "s")}:\n" +
               string.Join("\n", lines);
    }

    private async void RestoreRecipe(Recipe recipe)
    {
        if (_model is null || _desktops is null || _appLauncher is null || _stage is null) return;
        if (recipe.Desktops.Count == 0) return;

        IReadOnlyList<RunStep> steps = RecipeRun.Plan(recipe);
        var content = new RestoreProgressContent($"Restoring “{recipe.Name}”", steps);
        bool cancelled = false;
        content.Cancelled += () => cancelled = true;
        _stage.Summon(content); // full-surface, pinned to every desktop, so it stays up while we hop to staging

        _model.Reconcile();

        // 1) Create the recipe's target desktops (by label) + a throwaway staging desktop to launch on.
        var targets = new Dictionary<string, DesktopId>();
        foreach (RecipeDesktop d in recipe.Desktops)
        {
            DesktopId id = _desktops.Create($"{recipe.Name} · {d.Label}");
            _created.Add(id.Value);
            targets[d.Label] = id;
        }
        DesktopId staging = _desktops.Create($"{recipe.Name} · staging");
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
                hwnd = RecipeRun.MatchNewWindow(rs.Step, before, _desktops.AllWindows());
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
            try { _desktops.MoveWindowToDesktop(hwnd, targets[rs.DesktopLabel]); rs.State = StepState.Done; }
            catch { rs.State = StepState.Error; rs.Note = "couldn’t place"; }
            content.Refresh();
            _stage.BringToFront();
        }

        // 3) Windows we launched but couldn't place are still on staging. Confirm each really is on staging
        //    (the certainty rule) before it's ever eligible to be closed.
        List<nint> residue = RecipeRun.CleanupCandidates(steps)
            .Where(h => _desktops.DesktopOf(h) == staging)
            .ToList();

        RestoreDecision decision = await content.Finish(residue.Count);
        bool keepStaging = false;
        if (decision == RestoreDecision.CleanUp)
            foreach (nint h in residue) { try { _desktops.CloseWindow(h); } catch { /* window may refuse / prompt — best-effort */ } }
        else if (residue.Count > 0)
            keepStaging = true; // leave the unplaced windows where they are — keep staging as a branch desktop

        // 4) Fold the target desktops into a new branch, drop staging (unless we're keeping leftovers), land there.
        var refs = recipe.Desktops.Select(d => new DesktopRef(targets[d.Label], d.Label)).ToList();
        if (keepStaging) refs.Add(new DesktopRef(staging, "unplaced"));

        var branch = new Branch(recipe.Name, refs);
        _model.AddBranch(branch);

        if (!keepStaging)
        {
            _created.Remove(staging.Value);
            try { _desktops.Remove(staging, targets[recipe.Desktops[0].Label]); } catch { /* already gone */ }
        }

        _model.Reconcile();
        _model.GoTo(branch.Id, 0, out _); // land on the branch's first desktop

        _stage.Dismiss();
        RefreshOrFlash();

        int placed = steps.Count(s => s.State == StepState.Done);
        Notify("Restore finished",
               $"“{recipe.Name}” — placed {placed} of {steps.Count} app{(steps.Count == 1 ? "" : "s")}.");
    }
}
