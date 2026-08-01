using Avalonia.Media;
using Avalonia.Media.Imaging;
using Hypertree.App.Views;
using Hypertree.Launch;
using Hypertree.Settings;

namespace Hypertree.App;

/// <summary>
/// The application launcher (Ctrl+Alt+O): a spotlight over the installed apps — drawn from the same
/// Start-menu shortcuts Windows Search lists — plus the user's own custom commands. It reuses the shared
/// <see cref="PaletteContent"/> card on the <see cref="OverlayStage"/>, so it looks and drives exactly like
/// the command palette; the only additions are per-row app icons (loaded lazily off the UI thread) and the
/// custom-command setup flow.
///
/// Deliberately, typed text is never launched on its own — the only way to run an arbitrary command is the
/// explicit "Command…" row, which opens a prompt. That keeps a fast typist filtering the list from ever
/// firing off a half-typed string by accident.
/// </summary>
public sealed partial class App
{
    // Decoded app icons, keyed by launch path, kept across opens so re-filtering never re-extracts. A cached
    // null means "tried, nothing to show" — we don't keep hammering the shell for an iconless entry.
    private readonly Dictionary<string, IImage?> _iconCache = new();
    // The discovered app list, cached because discovery is slow (~1s — it walks the shell AppsFolder over
    // COM for packaged apps). Warmed on a background thread at startup and refreshed after each open, so the
    // launcher itself never blocks on it. Volatile: written from the refresh thread, read on the UI thread.
    private volatile IReadOnlyList<AppEntry>? _cachedApps;
    private volatile bool _refreshingApps;
    // The launcher palette currently on the stage, so a second Ctrl+Alt+O toggles *it* closed (rather than
    // any other palette that happens to be showing). Stale once dismissed — compared by reference to Current.
    private PaletteContent? _launcherPalette;

    // Ctrl+Alt+O. Re-press while the launcher is the current surface toggles it closed (back to the map if it
    // was opened over one, else dismiss); otherwise open it. Never stacks over an active window move.
    private void ToggleAppLauncher()
    {
        if (_stage?.Current is MoveContent) return;
        if (_launcherPalette is not null && _stage?.Current == _launcherPalette)
        {
            if (_stage.HasDurableBase) _stage.Back(); else _stage.Dismiss();
            return;
        }
        // Opened over the map (Esc pops back to it) when the map is up; a fresh root otherwise.
        OpenAppLauncher(overCurrent: _overlay?.IsOpen == true);
    }

    private void OpenAppLauncher(bool overCurrent = false)
    {
        if (_appLauncher is null) return;

        var items = new List<PaletteItem>
        {
            // The one path from typed text to a running command — explicit, so filtering can't launch by accident.
            new("Command…", "run a one-off command", ">", RunCommandPrompt),
            // Set up / edit / remove the saved commands.
            new("Custom commands…", "add, edit or remove", "⚙", () => ShowCustomCommandManager(refresh: false)),
        };

        // Saved custom commands sit above the discovered apps — they're the ones you set up on purpose.
        foreach (CustomCommand command in _settings.CustomCommands)
        {
            CustomCommand c = command; // capture per iteration
            items.Add(new PaletteItem(c.Name, DescribeTarget(c), "⚡", () => LaunchCustom(c)));
        }

        // Every installed app, with its real icon loaded lazily as the row renders.
        foreach (AppEntry entry in DiscoverApps())
        {
            AppEntry a = entry;
            items.Add(new PaletteItem(a.Name, null, null, () => LaunchApp(a.LaunchPath, a.Name),
                                      LoadIcon: MakeIconLoader(a.LaunchPath)));
        }

        var palette = new PaletteContent("Search apps and commands…",
            "↑↓ move · ↵ launch · Esc close", items, clearSearchOnShow: true);
        _launcherPalette = palette;
        if (overCurrent) _stage?.Present(palette); else _stage?.Summon(palette);
    }

    // The app list for this open: whatever's cached (instant), while a background refresh keeps it current
    // for next time. Empty only until the startup warm-up finishes — a rare, self-correcting first open.
    private IReadOnlyList<AppEntry> DiscoverApps()
    {
        RefreshAppsInBackground();
        return _cachedApps ?? Array.Empty<AppEntry>();
    }

    // Kick off discovery on a background STA thread (the shell AppsFolder automation object wants an STA),
    // caching the result. Guarded so overlapping opens don't stack scans. Called at startup to warm the
    // cache and after each open to refresh it — the running launcher always uses the already-cached list.
    private void RefreshAppsInBackground()
    {
        if (_refreshingApps || _appCatalog is null) return;
        _refreshingApps = true;
        var thread = new Thread(() =>
        {
            try { _cachedApps = SafeDiscover(); }
            finally { _refreshingApps = false; }
        })
        {
            IsBackground = true,
            Name = "HypertreeAppDiscovery",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // Discovery is best-effort: a broken scan yields an empty list rather than a broken launcher.
    private IReadOnlyList<AppEntry> SafeDiscover()
    {
        try { return _appCatalog?.Discover() ?? Array.Empty<AppEntry>(); }
        catch { return Array.Empty<AppEntry>(); }
    }

    // A row's detail line: the target, plus its arguments when it has them.
    private static string DescribeTarget(CustomCommand c) =>
        string.IsNullOrWhiteSpace(c.Arguments) ? c.Target : $"{c.Target} {c.Arguments}";

    // ── Launching ────────────────────────────────────────────────────────────────

    private void LaunchApp(string path, string name)
    {
        _stage?.Dismiss(); // clear our top-most overlay first, so the launched window comes up in front
        if (_appLauncher?.Launch(path) == false)
            Notify("Couldn’t launch", $"“{name}” could not be started.");
    }

    private void LaunchCustom(CustomCommand c)
    {
        _stage?.Dismiss();
        if (_appLauncher?.Launch(c.Target, c.Arguments, c.WorkingDirectory) == false)
            Notify("Couldn’t launch", $"“{c.Name}” could not be started.");
    }

    // "Command…" — the explicit ad-hoc runner. A prompt over the launcher; whatever you type is shell-run
    // (an app, a file, a folder, a URL — like Win+R), and nothing is saved.
    private void RunCommandPrompt()
    {
        _stage?.Present(new PromptContent("Run a command",
            "Runs through the shell like Win+R — an app, a file, a folder, or a URL. Nothing is saved.",
            @"e.g. notepad · https://example.com · C:\path",
            LaunchRaw, confirmLabel: "Run"));
    }

    private void LaunchRaw(string command)
    {
        _stage?.Dismiss(); // the prompt (and the launcher under it) are done — tear down, then run
        if (_appLauncher?.Launch(command) == false)
            Notify("Couldn’t run", $"“{command}” could not be started.");
    }

    // ── Icons ───────────────────────────────────────────────────────────────────

    // A per-path icon loader for a palette row: returns the cached image, or extracts one off the UI thread
    // (the shell call is I/O), decodes it, and caches the result — including a null, so an iconless entry is
    // only ever probed once.
    private Func<Task<IImage?>> MakeIconLoader(string path) => async () =>
    {
        if (_iconCache.TryGetValue(path, out IImage? cached)) return cached;

        IImage? image = null;
        try
        {
            byte[]? png = await Task.Run(() => _appIcons?.GetIconPng(path));
            if (png is { Length: > 0 })
            {
                using var ms = new MemoryStream(png);
                image = new Bitmap(ms); // decoded on the UI thread (await resumed here) — a cheap 32px PNG
            }
        }
        catch { image = null; }

        _iconCache[path] = image;
        return image;
    };

    // ── Custom-command setup (add / edit / remove) ────────────────────────────────

    // The manager palette, mirroring "Manage templates…": an "Add" row, then each saved command (Enter to
    // edit, Del to remove). Pushed over the launcher; refresh rebuilds it in place after an add/edit/delete.
    private void ShowCustomCommandManager(bool refresh)
    {
        if (_settingsStore is null) return;

        var items = new List<PaletteItem>
        {
            new("Add custom command", "name it and set its target", "＋", () => OpenCustomCommandEditor()),
        };
        foreach (CustomCommand command in _settings.CustomCommands)
        {
            CustomCommand c = command; // capture per iteration
            items.Add(new PaletteItem(c.Name, DescribeTarget(c), "✎",
                () => OpenCustomCommandEditor(existing: c),        // Enter edits…
                OnDelete: () => ConfirmDeleteCustomCommand(c)));   // …Del removes
        }

        // Typing a name that matches nothing offers to add it with that name pre-filled.
        PaletteItem? CreateRow(string q) =>
            new($"Add “{q}”", "new custom command", "＋", () => OpenCustomCommandEditor(prefillName: q));

        var palette = new PaletteContent("Custom commands…",
            "↑↓ move · ↵ edit · ⌦ delete · Esc back", items, CreateRow);
        // Refresh pops the card/confirm (top) + the stale manager beneath (popCount 2), keeping the launcher
        // under that; the initial open just pushes over the launcher.
        if (refresh) _stage?.ReplaceTop(2, palette); else _stage?.Present(palette);
    }

    private void OpenCustomCommandEditor(CustomCommand? existing = null, string? prefillName = null)
    {
        CustomCommand? seed = existing ?? (prefillName is null ? null : new CustomCommand(prefillName, ""));
        _stage?.Present(new CustomCommandContent(saved =>
        {
            SaveCustomCommand(saved, replacing: existing?.Name);
            ShowCustomCommandManager(refresh: true); // back to the manager, now reflecting the change
        }, seed, isEdit: existing is not null));
    }

    private void SaveCustomCommand(CustomCommand command, string? replacing)
    {
        if (_settingsStore is null) return;
        // Overwrite any command of the same name, and (when a rename happened on edit) the old name too.
        _settings.CustomCommands.RemoveAll(c =>
            c.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase)
            || (replacing is not null && c.Name.Equals(replacing, StringComparison.OrdinalIgnoreCase)));
        _settings.CustomCommands.Add(command);
        _settingsStore.Save(_settings);
    }

    private void ConfirmDeleteCustomCommand(CustomCommand command)
    {
        if (_settingsStore is null) return;
        _stage?.Present(new ConfirmContent($"Delete custom command “{command.Name}”?", () =>
        {
            _settings.CustomCommands.RemoveAll(c => c.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase));
            _settingsStore.Save(_settings);
            ShowCustomCommandManager(refresh: true);
        }, confirmLabel: "Delete"));
    }
}
