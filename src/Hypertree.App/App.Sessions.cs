using System.Threading.Tasks;
using Hypertree.Desktops;
using Hypertree.Launch;
using Hypertree.Scopes;
using Hypertree.Store;

namespace Hypertree.App;

/// <summary>
/// Session restore (docs/design/session-restore.md): remember which apps were open on each desktop of a
/// branch, and put them back on demand. Capture is pure bookkeeping; restore switches to each target
/// desktop and relaunches the apps that aren't already there, since a new window opens on the current
/// desktop. Both act on the branch the cursor is currently inside — <see cref="NavigationModel.CurrentBranchView"/>.
/// </summary>
public sealed partial class App
{
    // The remembered "what was open per desktop" side-table. Separate from the navigation state store
    // because it's keyed by GUID and captured on demand, not rebuilt from the live desktops on every move.
    private ISessionStore? _sessionStore;

    // How long to let a desktop's relaunched windows appear before switching to the next desktop, so a new
    // window lands on the desktop it was captured from rather than the one we move to next. Best-effort: an
    // app slower than this may open on the wrong desktop, which the user can correct with the "m" move flow.
    private const int RestoreSettleMs = 700;

    // "Save session for this branch": record, per desktop in the current branch, the apps that have a window
    // there. Replaces any prior session for this branch (matched by its stable id). An empty capture clears
    // the branch's saved session rather than leaving a stale one.
    private void SaveBranchSession()
    {
        if (_model is null || _desktops is null || _sessionStore is null) return;
        if (_model.CurrentBranchView() is not { } view)
        {
            Notify("No branch", "Dive into a branch first — sessions are saved per branch.");
            return;
        }

        _model.Reconcile(); // capture the live desktops, not any the user deleted outside Hypertree

        var desktops = new List<PersistedDesktopSession>();
        foreach (DesktopId id in view.Desktops)
        {
            IReadOnlyList<CapturedApp> apps = SessionCapture.FromWindows(_desktops.WindowsOn(id));
            if (apps.Count == 0) continue;
            desktops.Add(new PersistedDesktopSession
            {
                DesktopId = id.Value,
                Apps = apps.Select(a => new PersistedApp { Path = a.Path, Name = a.Name }).ToList(),
            });
        }

        PersistedSessions all = _sessionStore.Load();
        all.Branches.RemoveAll(b => b.BranchId == view.Id);
        if (desktops.Count > 0)
            all.Branches.Add(new PersistedBranchSession
            {
                BranchId = view.Id,
                BranchName = view.Name,
                Desktops = desktops,
            });
        _sessionStore.Save(all);

        int appCount = desktops.Sum(d => d.Apps.Count);
        Notify(appCount > 0 ? "Session saved" : "Nothing to save",
               appCount > 0
                   ? $"Remembered {appCount} app{(appCount == 1 ? "" : "s")} across “{view.Name}”."
                   : $"No open apps found on “{view.Name}” — its saved session was cleared.");
    }

    // "Restore this branch's session": relaunch the saved apps onto each of the branch's desktops, skipping
    // any already open there. Switches to each target desktop first (so a new window opens on it) and pauses
    // briefly for the windows to appear before moving on, then returns to where you started. async void is
    // fine for a UI-invoked handler; the awaits resume on the Avalonia UI thread, so the COM calls between
    // them stay on the STA thread and the tray stays responsive while it hops.
    private async void RestoreBranchSession()
    {
        if (_model is null || _desktops is null || _sessionStore is null || _appLauncher is null) return;
        if (_model.CurrentBranchView() is not { } view)
        {
            Notify("No branch", "Dive into a branch first — sessions are saved per branch.");
            return;
        }

        PersistedBranchSession? saved = _sessionStore.Load().Branches.FirstOrDefault(b => b.BranchId == view.Id);
        if (saved is null || saved.Desktops.Count == 0)
        {
            Notify("No saved session", $"Nothing saved for “{view.Name}” yet — run “Save session for this branch” first.");
            return;
        }

        _stage?.Dismiss();  // don't hold the overlay up while we hop desktops relaunching
        _model.Reconcile();

        var liveDesktops = view.Desktops.ToHashSet();
        DesktopId origin = _desktops.Current;
        int launched = 0, missingDesktops = 0;

        foreach (PersistedDesktopSession ds in saved.Desktops)
        {
            var target = new DesktopId(ds.DesktopId);
            if (!liveDesktops.Contains(target)) { missingDesktops++; continue; } // desktop gone — Phase 3 recreates

            IEnumerable<string> present = _desktops.WindowsOn(target).Select(w => w.ExecutablePath);
            IReadOnlyList<CapturedApp> toLaunch = SessionRestore.ToLaunch(
                ds.Apps.Select(a => new CapturedApp(a.Path, a.Name)), present);
            if (toLaunch.Count == 0) continue;

            _desktops.SwitchTo(target); // land the new windows on the desktop they were captured from
            foreach (CapturedApp app in toLaunch)
                if (_appLauncher.Launch(app.Path)) launched++;

            await Task.Delay(RestoreSettleMs); // resumes on the UI thread for the next COM call
        }

        if (_desktops.Current != origin) _desktops.SwitchTo(origin);
        _model.Resync();
        RefreshOrFlash();

        string tail = missingDesktops > 0
            ? $" ({missingDesktops} saved desktop{(missingDesktops == 1 ? "" : "s")} no longer exist)"
            : "";
        Notify(launched > 0 ? "Session restored" : "Nothing to restore",
               launched > 0
                   ? $"Launched {launched} app{(launched == 1 ? "" : "s")} across “{view.Name}”.{tail}"
                   : $"Everything saved for “{view.Name}” is already open.{tail}");
    }
}
