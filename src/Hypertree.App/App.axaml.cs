using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hypertree.App.Views;
using Hypertree.Desktops;
using Hypertree.Platform;
using Hypertree.Scopes;
using Hypertree.Settings;
using Hypertree.Store;

namespace Hypertree.App;

/// <summary>
/// The tray/HUD/hotkey shell. Builds the desktop controller and navigation model (top row = the
/// existing unbranched desktops; branches are created at runtime), wires the Ctrl+Alt+Arrow nav hotkeys
/// and the Ctrl+Alt+P command palette, and flashes the board on navigation. Tray-only, outlives its
/// windows (ShutdownMode.OnExplicitShutdown).
/// </summary>
public sealed class App : Application
{
    // Ctrl+Alt+Arrow — the default layer from M0 (Win+Ctrl+Arrow is the native desktop switch).
    // Down = dive, Up = surface, Left/Right = within the current level.
    private const HotkeyModifiers Mods = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private static readonly (HotkeyKey key, NavAction action)[] Bindings =
    {
        (HotkeyKey.ArrowDown,  NavAction.Dive),
        (HotkeyKey.ArrowUp,    NavAction.Surface),
        (HotkeyKey.ArrowLeft,  NavAction.MoveLeft),
        (HotkeyKey.ArrowRight, NavAction.MoveRight),
    };
    private const HotkeyKey PaletteKey = HotkeyKey.P; // Ctrl+Alt+P — command palette (spotlight/jump lives inside it)
    private const HotkeyKey MoveKey = HotkeyKey.M;    // Ctrl+Alt+M — the "move windows to another desktop" flow

    private readonly List<IGlobalHotkey> _hotkeys = new();
    // Desktops Hypertree created (for branches). Only these are ever torn down — the top row is the
    // user's own desktops and must never be removed.
    private readonly HashSet<Guid> _created = new();
    private IDesktopController? _desktops;
    private IForegroundActivator? _activator;
    private IStartupManager? _startup;
    private NavigationModel? _model;
    private HudWindow? _hud;
    private MapOverlay? _overlay;
    private DesktopId? _moveOrigin; // where the current move flow started, for cancel/restore
    private TaskbarLabel? _taskbarLabel;
    private TrayIcon? _tray;
    private BranchDialog? _dialog;
    private OverlayStage? _stage;
    private SettingsWindow? _settingsWindow;
    private NameDialog? _nameDialog;
    private RenameDialog? _renameDialog;
    private ISettingsStore? _settingsStore;
    private ISnapshotStore? _snapshots;
    private AppSettings _settings = new();

    // "Last visited" = the desktop you came from, committed when a navigation completes (Ctrl+Alt
    // released) or on a discrete jump. Surfaced first in the jump palette so you can hop back.
    private DesktopId? _lastVisited;
    private DesktopId? _gestureFrom; // where the in-progress keyboard gesture started
    private DispatcherTimer? _gesturePoll;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            string[] args = Environment.GetCommandLineArgs();
            int shot = Array.IndexOf(args, "--shot");
            if (shot >= 0)
            {
                string dir = shot + 1 < args.Length ? args[shot + 1] : "captures";
                Dispatcher.UIThread.Post(() => { DesignShot.Capture(dir); desktop.Shutdown(); });
                base.OnFrameworkInitializationCompleted();
                return;
            }

            desktop.Exit += (_, _) => Teardown();
            try { Startup(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Hypertree failed to start: {ex}");
                desktop.Shutdown(1);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Startup()
    {
        _desktops = PlatformServices.CreateDesktopController();
        _activator = PlatformServices.CreateForegroundActivator();
        _startup = PlatformServices.CreateStartupManager();
        _model = new NavigationModel(_desktops, new FileStateStore());
        // Desktops restored from persisted branches were created by Hypertree — track them so the
        // teardown guard still only ever destroys our own desktops.
        foreach (DesktopId id in _model.BranchDesktopIds()) _created.Add(id.Value);

        _settingsStore = new FileSettingsStore();
        _settings = _settingsStore.Load();
        _snapshots = new FileSnapshotStore();

        _hud = new HudWindow();
        ApplyFlashSettings();

        // Persistent desktop-name pill over the taskbar. It re-reads the current desktop itself, but we
        // also poke it on every navigation so it never lags a keystroke.
        _taskbarLabel = new TaskbarLabel(CurrentDesktopLabel, _desktops);
        _model.Changed += () => _taskbarLabel?.Sync();
        ApplyTaskbarLabel();

        // One shared, persistent presentation surface for the full-screen overlays (map + palettes).
        // Swapping between them is an in-place content change, not a window teardown — no flash.
        _stage = new OverlayStage(_desktops, _activator);

        _overlay = new MapOverlay(_stage);
        _overlay.JumpTopRequested += i => JumpFromMap(() => _model!.GoToTop(i));
        _overlay.JumpBranchRequested += (g, d) => JumpFromMap(() => _model!.GoToBranchDesktop(g, d));
        _overlay.RenameRequested += RenameSelected;
        _overlay.DeleteDesktopRequested += DeleteSelectedDesktop;
        _overlay.DeleteBranchRequested += ConfirmRemoveBranch;
        _overlay.NewDesktopRequested += PromptNewDesktop;
        _overlay.NewBranchRequested += () => OpenNewBranchDialog(null); // blank dialog keeps the map up (templates: command palette)
        _overlay.FinderRequested += () => OpenSpotlight(ReopenMap); // Ctrl+F — finder over the map; Esc returns to it
        _overlay.SettingsRequested += OpenSettings;

        _stage.Prewarm(); // size the overlay host now, so the first summon doesn't render at the top-left then jump

        RegisterHotkeys();
        BuildTray();
    }

    private void RegisterHotkeys()
    {
        foreach (var (key, action) in Bindings)
        {
            var hk = PlatformServices.CreateGlobalHotkey();
            bool ok = hk.Register(Mods, key, () => Dispatcher.UIThread.Post(() => Navigate(action)));
            if (ok) _hotkeys.Add(hk);
            else { hk.Dispose(); Console.Error.WriteLine($"Hotkey Ctrl+Alt+{key} was refused by the OS."); }
        }

        // Ctrl+Alt+P — command palette. (The spotlight/jump is still reachable from there via the
        // "Jump to desktop…" command.)
        var cmdHk = PlatformServices.CreateGlobalHotkey();
        if (cmdHk.Register(Mods, PaletteKey, () => Dispatcher.UIThread.Post(ToggleCommandPalette))) _hotkeys.Add(cmdHk);
        else { cmdHk.Dispose(); Console.Error.WriteLine("Hotkey Ctrl+Alt+P (command palette) was refused by the OS."); }

        // Ctrl+Alt+M — move the current desktop's windows to another desktop.
        var moveHk = PlatformServices.CreateGlobalHotkey();
        if (moveHk.Register(Mods, MoveKey, () => Dispatcher.UIThread.Post(ToggleMoveWindows))) _hotkeys.Add(moveHk);
        else { moveHk.Dispose(); Console.Error.WriteLine("Hotkey Ctrl+Alt+M (move windows) was refused by the OS."); }
    }

    // Navigate. While the map overlay is open it stays open (its windows are pinned across the
    // desktop switch) and re-homes its selection onto the desktop we land on; otherwise the flash shows.
    private void Navigate(NavAction action)
    {
        if (_model is null || _desktops is null) return;
        // The move flow owns the arrows while it's up (its own plain-arrow handlers drive it), so an
        // out-of-habit Ctrl+Alt+Arrow mustn't also navigate underneath.
        if (_stage?.Current is MoveContent) return;
        // Start of a gesture: remember where we came from, so releasing Ctrl+Alt can record it as
        // "last visited". A poll watches for the release (works whether flashing or in the map).
        _gestureFrom ??= _desktops.Current;
        _model.Apply(action);
        // In the map, Ctrl+Alt+Arrow switches for real, so the selection follows onto the new desktop
        // (green "here" and blue selection rejoin); in the transient flash, the green outline marks the
        // gesture's origin so the jump's direction/distance reads at a glance.
        if (_overlay is { IsOpen: true }) _overlay.SyncToCurrent(_model.BuildMap());
        else _hud?.Flash(_model.BuildMap(_gestureFrom));
        StartGesturePoll();
    }

    // A double-click / arrow-driven jump from the map: switch to the chosen desktop, record where we
    // came from, then re-home the selection onto it (green + blue rejoin), keeping the map open.
    private void JumpFromMap(Func<bool> doJump)
    {
        if (_desktops is null || _model is null) return;
        DesktopId from = _desktops.Current;
        doJump();
        if (_desktops.Current != from) _lastVisited = from;
        if (_overlay is { IsOpen: true }) _overlay.SyncToCurrent(_model.BuildMap());
    }

    private void StartGesturePoll()
    {
        if (_gesturePoll is null)
        {
            _gesturePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _gesturePoll.Tick += (_, _) => { if (!CtrlAltHeld()) CompleteGesture(); };
        }
        if (!_gesturePoll.IsEnabled) _gesturePoll.Start();
    }

    // The gesture is over once Ctrl+Alt is released: if we actually moved, the desktop we started on
    // becomes "last visited".
    private void CompleteGesture()
    {
        _gesturePoll?.Stop();
        if (_gestureFrom is { } from && _desktops is not null && _desktops.Current != from)
            _lastVisited = from;
        _gestureFrom = null;
    }

    // A discrete jump (palette / map click): record where we came from immediately.
    private void Jump(Func<bool> doJump)
    {
        if (_desktops is null) return;
        DesktopId from = _desktops.Current;
        doJump();
        if (_desktops.Current != from) _lastVisited = from;
        RefreshOverlay();
    }

    private const int VK_CONTROL = 0x11, VK_MENU = 0x12;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private static bool CtrlAltHeld()
        => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

    private void ToggleMap()
    {
        if (_model is null || _overlay is null || _desktops is null) return;
        if (_overlay.IsOpen) { _overlay.Close(); return; }

        _model.Reconcile(); // drop any externally-deleted desktops before showing the map
        _overlay.Open(_model.BuildMap()); // vertical model renders the stack around main; selection homes to current
    }

    private void RefreshOverlay()
    {
        if (_model is not null && _overlay is { IsOpen: true }) _overlay.Refresh(_model.BuildMap());
    }

    // ── Move windows to another desktop (Ctrl+Alt+M) ────────────────────────────────

    // Phase 1: snapshot the current desktop's windows and open the picker. Re-press toggles it closed.
    private void ToggleMoveWindows()
    {
        if (_model is null || _desktops is null || _stage is null) return;
        if (_stage.Current is MoveContent) { _stage.Dismiss(); return; } // re-press cancels (via OnRemoved)

        _model.Reconcile();
        _moveOrigin = _desktops.Current;
        var session = new WindowMoveSession(_desktops.WindowsOn(_moveOrigin.Value));

        // Presenting the move content on the stage swaps out any open map/palette in place. Navigation
        // and the move are serviced here; the board is pulled live from the model, centred on the origin.
        var content = new MoveContent(session) { BoardProvider = () => _model!.BuildMap(_moveOrigin) };
        content.NavigateRequested += a => _model!.Apply(a);
        content.MoveRequested += MoveSelectedWindows;
        content.Cancelled += CancelMove;
        _stage.Present(content);
    }

    // Phase 2 commit: we've navigated to the destination (it's the current desktop), so move each
    // selected window there. The content dismisses the stage itself; we stay on the destination.
    private void MoveSelectedWindows(IReadOnlyList<nint> hwnds)
    {
        if (_desktops is null) return;
        DesktopId dest = _desktops.Current;
        foreach (nint h in hwnds)
        {
            try { _desktops.MoveWindowToDesktop(h, dest); } catch { /* window may have closed — best-effort */ }
        }
        _moveOrigin = null;
        RefreshOrFlash(); // flash the destination with its now-updated window counts
    }

    // Cancel (Esc / Backspace / click-away / re-press) — raised from the move content's OnRemoved as the
    // stage dismisses it: return to where the move started (phase 2 may have navigated us away) and
    // re-anchor the model there.
    private void CancelMove()
    {
        if (_desktops is not null && _moveOrigin is { } origin && _desktops.Current != origin)
            _desktops.SwitchTo(origin);
        _moveOrigin = null;
        _model?.Resync();
    }

    // ── Manage-map actions (r / Del / Shift+Del / n) ───────────────────────────────

    // r on the map: open the rename prompt prefilled with the selected desktop's current name. On confirm,
    // rename the OS desktop and the model's stored label, then refresh the map in place. The prompt steals
    // focus (it's a top-most window); when it closes we hand the stage its key focus back so the arrow
    // selection resumes.
    private void RenameSelected(DesktopSelection sel)
    {
        if (_model is null || _desktops is null || _activator is null) return;
        if (_renameDialog is not null) { _renameDialog.Activate(); return; }

        var peek = sel.OnMain
            ? _model.PeekTopDesktop(sel.DesktopIndex)
            : _model.PeekBranchDesktop(sel.BranchIndex, sel.DesktopIndex);
        if (peek is null) return;

        _renameDialog = new RenameDialog(peek.Value.label, _activator, _desktops);
        _renameDialog.Closed += (_, _) =>
        {
            _renameDialog = null;
            ReassertMap(); // reclaim key focus for the selection
        };
        _renameDialog.Confirmed += name =>
        {
            try { _desktops.Rename(peek.Value.id, name); } catch { /* best-effort — desktop may have gone */ }
            _model.SetDesktopLabel(sel.OnMain, sel.BranchIndex, sel.DesktopIndex, name);
            RefreshOverlay(); // keep the map open, now relabelled
        };
        _renameDialog.Show();
    }

    // Del on the map: delete the selected desktop (with a confirm), resolving main vs. branch.
    private void DeleteSelectedDesktop(DesktopSelection sel)
    {
        if (sel.OnMain) DeleteTopDesktop(sel.DesktopIndex);
        else DeleteBranchDesktop(sel.BranchIndex, sel.DesktopIndex);
    }

    // Shift+Del on the map: delete an entire branch (all its desktops) behind a confirm.
    private void ConfirmRemoveBranch(int index)
    {
        if (_model is null) return;
        var map = _model.BuildMap();
        if (index < 0 || index >= map.Branches.Count) return;
        NavMapBranch g = map.Branches[index];
        Confirm($"Delete branch “{g.Name}”?\nIts {g.Desktops.Count} desktop{(g.Desktops.Count == 1 ? "" : "s")} " +
                "are removed and any windows on them move to another desktop.", () => RemoveBranch(index));
    }

    // n on the map: prompt for a name, create a new main-timeline desktop (no switch — the manage surface
    // stays where you are), then home the selection onto it so you can rename/act on it immediately.
    private void PromptNewDesktop()
    {
        if (_model is null || _desktops is null || _activator is null) return;
        if (_nameDialog is not null) { _nameDialog.Activate(); return; }

        _nameDialog = new NameDialog("New desktop",
            "Create a new desktop on the main timeline. You stay on the current desktop.",
            "desktop name (e.g. email)", _activator, _desktops, confirmLabel: "Create");
        _nameDialog.Closed += (_, _) => { _nameDialog = null; ReassertMap(); };
        _nameDialog.Confirmed += name =>
        {
            _desktops.Create(name); // a main-timeline desktop is the user's own — not tracked in _created
            _model.SyncTopRow();    // picks up the new desktop (appended to the top row)
            NavMap map = _model.BuildMap();
            _overlay?.Refresh(map);
            _overlay?.Select(new DesktopSelection(true, -1, map.TopRow.Count - 1)); // the just-created desktop
        };
        _nameDialog.Show();
    }

    // After a child prompt over the map closes, hand the stage its key focus back so arrow selection resumes.
    private void ReassertMap()
    {
        if (_overlay is { IsOpen: true }) _stage?.Reassert();
    }

    // ── Spotlight (F4): jump to any existing desktop, or create one named the query ─────

    // <paramref name="onBack"/>, when supplied, is what Esc returns to (the map via Ctrl+F, or the command
    // palette via its "Jump to desktop…" row) instead of dismissing outright.
    private void OpenSpotlight(Action? onBack = null)
    {
        if (_model is null) return;

        _model.Reconcile(); // drop any desktops deleted out from under us before offering jumps
        NavMap map = _model.BuildMap();
        var items = new List<PaletteItem>();
        int lastIndex = -1; // the last-visited row, to float to the top

        // Is this desktop the last-visited one? Decorated with "(last)" + a ↩ icon and moved first.
        bool IsLast(DesktopId? id) => _lastVisited is { } lv && id is { } tid && tid == lv;
        string Detail(string ctx, bool last) => last ? $"{ctx} · (last)" : ctx;
        string Icon(bool last) => last ? "↩" : "→";

        // Every main-timeline desktop, then every branch's desktops (branch name in the detail so
        // typing a branch name filters to its desktops). Each carries a Preview board that highlights
        // where the jump would land, shown in the middle of the palette as you move the selection.
        for (int i = 0; i < map.TopRow.Count; i++)
        {
            int idx = i;
            bool last = IsLast(_model.PeekTopDesktop(i)?.id);
            items.Add(new PaletteItem(map.TopRow[i].Label, Detail("main", last), Icon(last),
                () => Jump(() => _model!.GoToTop(idx)), // no flash — the preview already showed it
                Preview: () => PreviewMap(onMain: true, topIndex: idx, branchIndex: -1, desktopIndex: -1)));
            if (last) lastIndex = items.Count - 1;
        }
        foreach (NavMapBranch g in map.Branches)
        {
            int gi = g.Index;
            for (int j = 0; j < g.Desktops.Count; j++)
            {
                int dj = j;
                bool last = IsLast(_model.PeekBranchDesktop(gi, dj)?.id);
                items.Add(new PaletteItem(g.Desktops[j].Label, Detail(g.Name, last), Icon(last),
                    () => Jump(() => _model!.GoToBranchDesktop(gi, dj)),
                    Preview: () => PreviewMap(onMain: false, topIndex: -1, branchIndex: gi, desktopIndex: dj)));
                if (last) lastIndex = items.Count - 1;
            }
        }

        // Float the last-visited desktop to the top so it's the default (empty-query) selection.
        if (lastIndex >= 0)
        {
            PaletteItem lastItem = items[lastIndex];
            items.RemoveAt(lastIndex);
            items.Insert(0, lastItem);
        }

        string hint = onBack is null
            ? "↑↓ move · ↵ jump/create · Esc close · blue = you are here"
            : "↑↓ move · ↵ jump/create · Esc back · blue = you are here";
        OpenPalette("Jump to or create a desktop…", hint, items,
            query => new PaletteItem($"Create desktop “{query}”", "new · main", "+",
                () => CreateAndGoToDesktop(query),
                Preview: () => _model!.BuildMap()), // no target tile yet — show the current board
            previewMode: true, onBack: onBack);
    }

    // Build a board snapshot that marks a specific desktop as current (for the jump palette's preview),
    // without moving the model. Rebuilds the tiles from the live map with the target highlighted and
    // centred on its own row.
    // Build a preview board for the jump palette. IsCurrent (blue) marks where you ARE now; IsHere
    // (green) marks the selected target (which defaults to the last-visited desktop). The board is
    // centred on your current position, so the green target shows the direction/distance of the jump.
    // (onMain/topIndex/branchIndex/desktopIndex describe the target row.)
    private NavMap PreviewMap(bool onMain, int topIndex, int branchIndex, int desktopIndex)
    {
        NavMap b = _model!.BuildMap();

        bool hereMain = _model.OnTop;
        int hereTop = _model.CurrentTopIndex;
        (int hereBranch, int hereDesktop) = _model.CurrentBranchDesktop ?? (-1, -1);

        var top = b.TopRow.Select((t, i) => new NavMapTile(
            t.Label,
            hereMain && i == hereTop,      // IsCurrent (blue) = you are here
            onMain && i == topIndex,       // IsHere (green) = the target
            t.WindowCount)).ToList();      // keep the at-a-glance count on the preview board
        var branches = b.Branches.Select(g => new NavMapBranch(
            g.Index, g.Name,
            g.Desktops.Select((d, j) => new NavMapTile(
                d.Label,
                !hereMain && g.Index == hereBranch && j == hereDesktop,   // blue = current
                !onMain && g.Index == branchIndex && j == desktopIndex,   // green = target
                d.WindowCount)).ToList(),
            // Keep both the current branch and the target branch bright (un-rested).
            (!hereMain && g.Index == hereBranch) || (!onMain && g.Index == branchIndex),
            g.Index == hereBranch ? hereDesktop : g.Index == branchIndex ? desktopIndex : g.Cursor)).ToList();
        int topCursor = hereMain ? hereTop : b.TopCursor;
        return new NavMap(top, topCursor, hereMain, branches, b.TopPosition);
    }

    // Create a new unbranched desktop named the query and jump straight to it.
    private void CreateAndGoToDesktop(string name)
    {
        if (_model is null || _desktops is null) return;
        DesktopId from = _desktops.Current;
        DesktopId id = _desktops.Create(name);
        _created.Add(id.Value);
        _model.SyncTopRow();
        _desktops.SwitchTo(id);
        _model.Resync(); // land the model on the freshly-created desktop
        if (_desktops.Current != from) _lastVisited = from;
        RefreshOverlay(); // no flash — the jump/create is decisive on its own
    }

    private void OpenPalette(string placeholder, string hint, IReadOnlyList<PaletteItem> items,
                             Func<string, PaletteItem?>? createRow = null, bool previewMode = false,
                             Action? onBack = null)
    {
        _stage?.Present(new PaletteContent(placeholder, hint, items, createRow, previewMode, onBack));
    }

    // Reopen the map fresh (used as the finder's "back" target when it was summoned from the map).
    private void ReopenMap()
    {
        if (_model is null || _overlay is null || _desktops is null) return;
        _model.Reconcile();
        _overlay.Open(_model.BuildMap());
    }

    // ── Command palette (F5): same look/feel, items are commands. Bones only. ───────────

    private void ToggleCommandPalette()
    {
        if (_stage?.Current is MoveContent) return; // don't stack the palette over an active move
        if (_stage?.Current is PaletteContent) { _stage.Dismiss(); return; } // re-press toggles closed
        OpenCommandPalette();
    }

    private void OpenCommandPalette()
    {
        if (_model is null) return;

        _model.Reconcile(); // drop any externally-deleted desktops so the context board is accurate

        // Preview mode, like the jump palette: show a board underneath so a command reads in context
        // ("blue = you are here"). Most rows fall back to the current map; commands with a distinct
        // target supply their own board that highlights what they'll act on (green).
        var items = BuildCommands()
            .Select(c => new PaletteItem(c.Name, c.DisabledReason,
                                         c.DisabledReason is null ? "▸" : null, c.Run,
                                         Preview: c.Preview ?? (() => _model!.BuildMap()),
                                         DisabledReason: c.DisabledReason))
            .ToList();
        OpenPalette("Run a command…", "↑↓ move · ↵ run · Esc close · blue = you are here", items,
                    previewMode: true);
    }

    // The command registry. A few real commands (reusing existing handlers) plus stubs for features
    // that don't exist yet — the exact set isn't the point this iteration, the wiring is.
    private IReadOnlyList<Command> BuildCommands()
    {
        Action stub(string name) => () => Console.Error.WriteLine($"Command “{name}” is not implemented yet.");

        // "Save current branch as template…" only makes sense inside a branch — greyed out with a reason
        // on the main timeline, so it stays discoverable rather than disappearing.
        bool inBranch = _model is not null && !_model.OnTop && _model.CurrentBranchIndex >= 0;
        string? saveTemplateDisabled = inBranch ? null : "you’re on the main timeline — enter a branch first";

        // The branch a "current branch" command would act on: the one you're in, or (on main) the one
        // directly below main. Used to highlight that branch green in the command's preview board.
        int targetBranch = _model?.CurrentBranchIndex ?? -1;
        Func<NavMap>? branchTargetPreview = targetBranch >= 0 ? () => PreviewBranchTarget(targetBranch) : null;

        // Commands run synchronously: those that open another stage surface (map / a palette) swap it
        // in place, so PaletteContent.Choose sees the stage is no longer the command palette and leaves
        // it be; those that open a separate window (dialogs, settings, the move overlay) leave the
        // command palette current, so Choose dismisses the stage behind them. Either way, no flash.
        return new List<Command>
        {
            new("Jump to desktop…", () => OpenSpotlight(OpenCommandPalette)), // Esc returns to the command palette
            new("Open map", ToggleMap),
            new("Move windows to another desktop…", ToggleMoveWindows,
                _desktops is not null && _desktops.WindowsOn(_desktops.Current).Count == 0 ? "no windows on this desktop" : null),
            new("Settings", OpenSettings),
            new("New branch…", PromptNewBranch),
            new("Save current branch as template…", PromptSaveBranchAsTemplate,
                saveTemplateDisabled, inBranch ? branchTargetPreview : null),
            new("Manage templates…", ManageTemplatesPrompt,
                _settings.BranchTemplates.Count == 0 ? "no templates saved yet" : null),
            new("Delete current desktop", DeleteCurrentDesktop),
            new("Remove current branch", RemoveCurrentBranch, Preview: branchTargetPreview),
            // Hard reset: collapse everything back to one desktop. Pointless (and greyed out) when
            // there's already a single desktop and no branches.
            new("Implode — reset to a single desktop", Implode,
                _model is not null && _model.TotalDesktops <= 1 ? "already a single desktop" : null),
            new("Snapshot layout…", PromptSnapshot),
            new("Restore snapshot…", RestoreSnapshotPrompt),
            new("Move desktop to branch…", stub("Move desktop to branch…")),
        };
    }

    private void RemoveCurrentBranch()
    {
        if (_model is null) return;
        int index = _model.CurrentBranchIndex;
        if (index >= 0) RemoveBranch(index);
    }

    // ── Implode: hard reset to a single desktop ────────────────────────────────────

    // Remove every desktop but one and clear all branches — a clean slate. Guarded by a confirm.
    // Mirrors RestoreSnapshot's teardown: stand on the survivor first so the current view is never
    // yanked out from under us, then remove the rest (their windows fall back onto the survivor).
    private void Implode()
    {
        if (_model is null || _desktops is null || _activator is null) return;

        var dlg = new ConfirmDialog(
            "Implode all desktops?\nEvery desktop and branch is removed and you’re reset to a single desktop. Windows from the others move onto it. This can’t be undone.",
            _activator, _desktops, confirmLabel: "Implode");
        dlg.Confirmed += DoImplode;
        dlg.Show();
    }

    private void DoImplode()
    {
        if (_model is null || _desktops is null) return;

        _model.Reconcile(); // act on the live layout, not stale/externally-deleted desktops
        IReadOnlyList<DesktopInfo> all = _desktops.List();
        if (all.Count == 0) return; // nothing to do — never strip the machine to zero desktops

        // Keep the OS's first desktop (the canonical "Desktop 1") as the survivor; everything
        // consolidates onto it.
        DesktopId survivor = all[0].Id;
        _desktops.SwitchTo(survivor);

        foreach (DesktopInfo d in all)
        {
            if (d.Id == survivor) continue;
            _created.Remove(d.Id.Value);
            try { _desktops.Remove(d.Id, survivor); } catch { /* already gone — best-effort */ }
        }

        _model.RestoreStructure(0, Array.Empty<Branch>()); // no branches; top row re-derives to the survivor
        RefreshOrFlash();
    }

    // Preview board that marks a whole branch as the target: its tiles get the green "here" outline and
    // the box stays bright (un-rested), so a "current branch" command shows exactly which branch it hits —
    // even from the main timeline, where the blue cursor is elsewhere.
    private NavMap PreviewBranchTarget(int branchIndex)
    {
        NavMap b = _model!.BuildMap();
        var branches = b.Branches.Select(g => g.Index == branchIndex
            ? g with { Desktops = g.Desktops.Select(d => d with { IsHere = true }).ToList(), IsCurrentLevel = true }
            : g).ToList();
        return b with { Branches = branches };
    }

    // ── Snapshots: capture the whole layout under a name, restore it later ─────────

    // Prompt for a name, then save the current layout (main timeline + branches) under it. A stage-content
    // prompt (not a NameDialog window), so summoning it from the command palette is a swap in place — the
    // palette becomes the prompt on the same host, no dim tear-down/rebuild flash.
    private void PromptSnapshot()
    {
        if (_model is null || _snapshots is null || _stage is null) return;

        _stage.Present(new PromptContent("Snapshot layout",
            "Save the current desktops and branches under a name you can restore to later.",
            "snapshot name (e.g. before-refactor)", SaveSnapshot));
    }

    private void SaveSnapshot(string name)
    {
        if (_model is null || _snapshots is null) return;
        _model.Reconcile(); // capture the live layout, not stale/deleted desktops

        // Same name overwrites, so re-snapshotting a layout updates it in place.
        var list = _snapshots.Load().Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(_model.CaptureSnapshot(name));
        _snapshots.Save(list);
    }

    // Show a palette of saved snapshots; choosing one confirms, then restores.
    private void RestoreSnapshotPrompt()
    {
        if (_snapshots is null || _activator is null) return;

        IReadOnlyList<Snapshot> snaps = _snapshots.Load();
        var items = snaps.Select(s => new PaletteItem(
            s.Name,
            $"{s.DesktopCount} desktops · {s.Branches.Count} branches",
            "⟲",
            () => Dispatcher.UIThread.Post(() => ConfirmRestore(s)), // let the palette close first
            Preview: () => SnapshotPreview(s)))                       // show the layout you'd restore to
            .ToList();

        OpenPalette(snaps.Count == 0 ? "No snapshots saved yet" : "Restore a snapshot…",
                    "↑↓ move · ↵ restore · Esc close · preview = the saved layout", items,
                    previewMode: true);
    }

    // Build a board from a saved snapshot (persisted labels only — no live windows), so the restore
    // palette previews the layout you'd land in. All branches render bright (this is the whole target),
    // and window counts are absent since the desktops may not exist yet.
    private static NavMap SnapshotPreview(Snapshot snap)
    {
        var top = snap.MainDesktops.Select(d => new NavMapTile(d.Label, IsCurrent: false)).ToList();
        var branches = new List<NavMapBranch>(snap.Branches.Count);
        for (int gi = 0; gi < snap.Branches.Count; gi++)
        {
            PersistedBranch pg = snap.Branches[gi];
            var tiles = pg.Desktops.Select(d => new NavMapTile(d.Label, IsCurrent: false)).ToList();
            int cursor = tiles.Count == 0 ? 0 : Math.Clamp(pg.LastUsedIndex, 0, tiles.Count - 1);
            branches.Add(new NavMapBranch(gi, pg.Name, tiles, IsCurrentLevel: true, cursor));
        }
        return new NavMap(top, TopCursor: 0, OnTop: true, branches, Math.Clamp(snap.MainSlot, 0, branches.Count));
    }

    private void ConfirmRestore(Snapshot snap)
    {
        if (_activator is null || _desktops is null) return;
        var dlg = new ConfirmDialog(
            $"Restore snapshot “{snap.Name}”?\nYour desktops are rebuilt to match it. Desktops that aren’t part of the snapshot are removed (any windows on them move to another desktop).",
            _activator, _desktops, confirmLabel: "Restore");
        dlg.Confirmed += () => RestoreSnapshot(snap);
        dlg.Show();
    }

    // Rebuild the OS desktops + the model to match a snapshot. Re-attaches to a saved desktop by its GUID
    // when it still exists, re-creates it by label when it doesn't, then removes anything not in the
    // snapshot. We switch to the snapshot's first desktop BEFORE any removal so the current view can't be
    // yanked out from under us.
    private void RestoreSnapshot(Snapshot snap)
    {
        if (_model is null || _desktops is null) return;
        if (snap.DesktopCount == 0) return; // nothing to restore to — never strip the machine to zero desktops

        _model.Reconcile();
        var live = _desktops.List().Select(d => d.Id.Value).ToHashSet();
        var keep = new HashSet<Guid>();

        // Resolve one saved desktop to a live id: reuse its GUID if present (renamed to the saved label),
        // else create a fresh desktop with that label. Branch desktops are tracked in _created so the
        // teardown guard may remove them later; main desktops are the user's and are never tracked.
        DesktopId Resolve(PersistedDesktop d, bool branch)
        {
            DesktopId id;
            if (live.Contains(d.Id))
            {
                id = new DesktopId(d.Id);
                if (!string.IsNullOrWhiteSpace(d.Label)) { try { _desktops!.Rename(id, d.Label); } catch { } }
            }
            else
            {
                id = _desktops!.Create(d.Label);
            }
            keep.Add(id.Value);
            if (branch) _created.Add(id.Value);
            return id;
        }

        // Main desktops first, so the first one exists to stand on before any removal.
        var mainIds = snap.MainDesktops.Select(d => Resolve(d, branch: false)).ToList();

        var branches = new List<Branch>(snap.Branches.Count);
        foreach (PersistedBranch pg in snap.Branches)
        {
            var refs = pg.Desktops.Select(d => new DesktopRef(Resolve(d, branch: true), d.Label)).ToList();
            if (refs.Count > 0) branches.Add(new Branch(pg.Name, refs, pg.LastUsedIndex));
        }

        // Land on the snapshot's first desktop (main[0], else the first branch desktop) before removing.
        DesktopId first = mainIds.Count > 0 ? mainIds[0] : branches[0].Desktops[0].Id;
        _desktops.SwitchTo(first);

        // Remove every desktop that isn't part of the snapshot; windows fall back to the first desktop.
        foreach (DesktopInfo d in _desktops.List().ToList())
        {
            if (keep.Contains(d.Id.Value)) continue;
            _created.Remove(d.Id.Value);
            try { _desktops.Remove(d.Id, first); } catch { /* already gone — best-effort */ }
        }

        _model.RestoreStructure(snap.MainSlot, branches); // re-derives the top row + re-anchors to `first`
        RefreshOrFlash();
    }

    // ── Settings (tray · map cog · command palette "settings") ──────────────────────

    private void OpenSettings()
    {
        if (_activator is null || _startup is null) return;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }

        _settingsWindow = new SettingsWindow(_settings, _startup.IsEnabled, SaveSettings, _activator);
        _settingsWindow.Topmost = true; // sit above the map/flash if one is showing
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.TakeFocus();
    }

    private void SaveSettings(AppSettings settings, bool startOnLogin)
    {
        _settings = settings;
        _settingsStore?.Save(settings);
        ApplyFlashSettings();
        ApplyTaskbarLabel();
        _startup?.SetEnabled(startOnLogin);
    }

    private void ApplyFlashSettings()
        => _hud?.Configure(_settings.FlashHoldToKeep, _settings.FlashGraceMs, _settings.FlashTimeoutMs);

    // Show or hide the persistent taskbar label to match the setting.
    private void ApplyTaskbarLabel()
    {
        if (_taskbarLabel is null) return;
        if (_settings.ShowTaskbarLabel) _taskbarLabel.Enable();
        else _taskbarLabel.Disable();
    }

    // The (branch, name) the taskbar label should show for the desktop the OS is currently on — resolved
    // by id, so it's right even after a switch made outside Hypertree. Null before startup / during teardown.
    private (string? branch, string name)? CurrentDesktopLabel()
    {
        if (_model is null || _desktops is null) return null;
        (string? branch, string label) = _model.Describe(_desktops.Current);
        return (branch, label);
    }

    private void RefreshOrFlash()
    {
        if (_model is null) return;
        if (_overlay is { IsOpen: true }) _overlay.Refresh(_model.BuildMap());
        else _hud?.Flash(_model.BuildMap());
    }

    private void BuildTray()
    {
        var header = new NativeMenuItem("Hypertree 0.1.0") { IsEnabled = false };
        var map = new NativeMenuItem("Open map");
        map.Click += (_, _) => ToggleMap();
        var move = new NativeMenuItem("Move windows…");
        move.Click += (_, _) => ToggleMoveWindows();
        var newBranch = new NativeMenuItem("New branch…");
        newBranch.Click += (_, _) => PromptNewBranch();
        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => OpenSettings();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

        _tray = new TrayIcon
        {
            Icon = TrayIconFactory.Create(),
            ToolTipText = "Hypertree",
            IsVisible = true,
            Menu = new NativeMenu { header, new NativeMenuItemSeparator(), map, move, newBranch, settings, new NativeMenuItemSeparator(), exit },
        };
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    // ── Branch definition ─────────────────────────────────────────────────────────

    // "New branch…" — template-first when any templates exist, else straight to the blank dialog.
    private void PromptNewBranch()
    {
        if (_desktops is null || _activator is null) return;

        // No templates yet → the blank dialog, exactly as before (nothing regresses until you make one).
        if (_settings.BranchTemplates.Count == 0) { OpenNewBranchDialog(null); return; }

        // Otherwise pick a template first. "Blank branch" is row 0 (the default selection), so the
        // no-template path stays a quick Enter-Enter; picking a template pre-fills the labels instead.
        var items = new List<PaletteItem>
        {
            new("Blank branch", "type your own desktops", "+",
                () => Dispatcher.UIThread.Post(() => OpenNewBranchDialog(null))),
        };
        foreach (BranchTemplate template in _settings.BranchTemplates)
        {
            BranchTemplate t = template; // capture per iteration
            items.Add(new PaletteItem(t.Name, string.Join(" · ", t.Labels), "▸",
                () => Dispatcher.UIThread.Post(() => OpenNewBranchDialog(t.Labels))));
        }
        OpenPalette("Pick a template…", "↑↓ move · ↵ use · Esc close", items);
    }

    // Open the branch dialog, optionally pre-filled with a template's desktop labels.
    private void OpenNewBranchDialog(IReadOnlyList<string>? prefillLabels)
    {
        if (_desktops is null || _activator is null) return;
        if (_dialog is not null) { _dialog.Activate(); return; }

        _dialog = new BranchDialog(_activator, _desktops, prefillLabels); // pinned + top-most overlay, sits above the map
        _dialog.Closed += (_, _) => { _dialog = null; ReassertMap(); };
        _dialog.Confirmed += CreateBranch;
        _dialog.Show();
    }

    private void CreateBranch(BranchSpec spec)
    {
        if (_model is null || _desktops is null) return;

        var refs = new List<DesktopRef>(spec.Labels.Count);
        foreach (string label in spec.Labels)
        {
            DesktopId id = _desktops.Create($"{spec.Name} · {label}");
            _created.Add(id.Value);
            refs.Add(new DesktopRef(id, label));
        }

        var branch = new Branch(spec.Name, refs);
        // Created from the map: attach the branch below the highlighted desktop's row, not below main.
        // (Tray / command-palette creation has no map selection, so it falls back to below main.)
        if (_overlay is { IsOpen: true })
        {
            DesktopSelection sel = _overlay.Selection;
            _model.AddBranchBelow(sel.OnMain, sel.BranchIndex, branch);
        }
        else _model.AddBranch(branch);
        RefreshOrFlash();
    }

    // ── Branch templates (reusable desktop recipes for new branches) ─────────────────

    // Promote the current branch's desktop set into a named, reusable template.
    private void PromptSaveBranchAsTemplate()
    {
        if (_model is null || _activator is null || _settingsStore is null || _desktops is null) return;
        if (_model.OnTop) return; // greyed out in the palette; guard the direct path too
        if (_nameDialog is not null) { _nameDialog.Activate(); return; }

        int gi = _model.CurrentBranchIndex;
        if (gi < 0) return;
        NavMapBranch branch = _model.BuildMap().Branches[gi];
        var labels = branch.Desktops.Select(d => d.Label).ToList();

        _nameDialog = new NameDialog("Save as template",
            "Save this branch’s desktops as a reusable template you can pick when creating new branches.",
            $"template name (e.g. {branch.Name})", _activator, _desktops);
        _nameDialog.Closed += (_, _) => _nameDialog = null;
        _nameDialog.Confirmed += name => SaveTemplate(name, labels);
        _nameDialog.Show();
    }

    private void SaveTemplate(string name, IReadOnlyList<string> labels)
    {
        if (_settingsStore is null) return;
        // Same name overwrites, so re-saving updates a template in place (mirrors snapshots).
        _settings.BranchTemplates.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _settings.BranchTemplates.Add(new BranchTemplate(name, labels));
        _settingsStore.Save(_settings);
    }

    // A palette of saved templates; choosing one confirms, then deletes it.
    private void ManageTemplatesPrompt()
    {
        if (_activator is null || _settingsStore is null) return;

        var items = _settings.BranchTemplates.Select(template =>
        {
            BranchTemplate t = template; // capture per iteration
            return new PaletteItem(t.Name, string.Join(" · ", t.Labels), "🗑",
                () => Dispatcher.UIThread.Post(() => ConfirmDeleteTemplate(t)));
        }).ToList();

        OpenPalette(items.Count == 0 ? "No templates saved yet" : "Delete a template…",
                    "↑↓ move · ↵ delete · Esc close", items);
    }

    private void ConfirmDeleteTemplate(BranchTemplate template)
    {
        if (_activator is null || _desktops is null) return;
        var dlg = new ConfirmDialog($"Delete template “{template.Name}”?", _activator, _desktops, confirmLabel: "Delete");
        dlg.Confirmed += () =>
        {
            _settings.BranchTemplates.RemoveAll(t => t.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase));
            _settingsStore?.Save(_settings);
        };
        dlg.Show();
    }

    private void RemoveBranch(int index)
    {
        if (_model is null) return;
        TearDown(_model.RemoveBranch(index));
        RefreshOrFlash();
    }

    // ── Delete a single desktop (map × badge) with a confirm prompt ───────────────

    // Delete whatever desktop is currently selected (footer button).
    private void DeleteCurrentDesktop()
    {
        if (_model is null) return;
        if (_model.OnTop) DeleteTopDesktop(_model.CurrentTopIndex);
        else if (_model.CurrentBranchDesktop is { } sel) DeleteBranchDesktop(sel.branch, sel.desktop);
    }

    private void DeleteTopDesktop(int index)
    {
        if (_model is null || _desktops is null) return;
        var peek = _model.PeekTopDesktop(index);
        if (peek is null || _model.TotalDesktops <= 1) return; // never delete the last desktop

        Confirm($"Delete desktop “{peek.Value.label}”?\nAny windows on it move to another desktop.", () =>
        {
            _desktops.Remove(peek.Value.id, Fallback(peek.Value.id));
            _created.Remove(peek.Value.id.Value);
            _model.Resync();
            RefreshOrFlash();
        });
    }

    private void DeleteBranchDesktop(int branchIndex, int desktopIndex)
    {
        if (_model is null || _desktops is null) return;
        var peek = _model.PeekBranchDesktop(branchIndex, desktopIndex);
        if (peek is null || _model.TotalDesktops <= 1) return;

        Confirm($"Delete desktop “{peek.Value.label}”?\nAny windows on it move to another desktop.", () =>
        {
            DesktopId fallback = Fallback(peek.Value.id);
            DesktopId? id = _model.DetachBranchDesktop(branchIndex, desktopIndex);
            if (id is not null)
            {
                _created.Remove(id.Value.Value);
                try { _desktops.Remove(id.Value, fallback); } catch { /* already gone */ }
            }
            _model.Resync();
            RefreshOrFlash();
        });
    }

    // Any live desktop other than the one being deleted (prefer the current view).
    private DesktopId Fallback(DesktopId avoid)
    {
        DesktopId cur = _desktops!.Current;
        if (cur != avoid) return cur;
        foreach (DesktopInfo d in _desktops.List()) if (d.Id != avoid) return d.Id;
        return avoid; // unreachable — guarded by TotalDesktops > 1
    }

    private void Confirm(string message, Action onConfirm)
    {
        if (_activator is null || _desktops is null) return;
        // Always top-most and pinned across desktops now (OverlayPrompt), so it sits above the map on its own.
        var dlg = new ConfirmDialog(message, _activator, _desktops);
        dlg.Confirmed += onConfirm;
        dlg.Closed += (_, _) => ReassertMap(); // hand the map its key focus back once the prompt is gone
        dlg.Show();
    }

    // Remove a branch's desktops — but ONLY ones Hypertree created, never the user's own desktops.
    private void TearDown(Branch? branch)
    {
        if (branch is null || _model is null || _desktops is null) return;
        DesktopId fallback = _model.FallbackDesktopId;
        foreach (DesktopRef d in branch.Desktops)
        {
            if (_created.Remove(d.Id.Value))
            {
                try { _desktops.Remove(d.Id, fallback); } catch { /* already gone — best-effort */ }
            }
        }
    }

    private void Teardown()
    {
        _gesturePoll?.Stop();
        foreach (var hk in _hotkeys) hk.Dispose();
        _hotkeys.Clear();
        if (_tray is not null) _tray.IsVisible = false;
        _dialog?.Close();
        _stage?.Close(); // closes the shared host + dims (map / palettes / move all live here)
        _hud?.Close();
        _taskbarLabel?.Close();
        _nameDialog?.Close();
        _renameDialog?.Close();
        _settingsWindow?.Close();
    }
}
