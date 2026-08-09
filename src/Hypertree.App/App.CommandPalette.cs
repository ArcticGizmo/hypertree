using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hypertree.App.Ipc;
using Hypertree.App.Status;
using Hypertree.App.Updates;
using Hypertree.App.Views;
using Hypertree.Changelog;
using Hypertree.Desktops;
using Hypertree.Ipc;
using Hypertree.Layout;
using Hypertree.Platform;
using Hypertree.Scopes;
using Hypertree.Settings;
using Hypertree.Spatial;
using Hypertree.Store;
using Hypertree.WindowLayout;

namespace Hypertree.App;

public sealed partial class App
{
    // Reopen the map fresh (used as the finder's "back" target when it was summoned from the map).
    // ── Command palette: same look/feel, items are commands. ────────────────────────────

    private void ToggleCommandPalette()
    {
        if (_stage?.Current is MoveContent) return; // don't stack the palette over an active move
        // Re-press toggles the palette closed — back to the surface it opened over (the map) if there is one,
        // matching what Esc does, rather than tearing the whole chain down from under it.
        if (_stage?.Current is PaletteContent)
        {
            if (_stage.HasDurableBase) _stage.Back(); else _stage.Dismiss();
            return;
        }
        // Pressed while the map is up, the chord means the same as the map's own "p": push the palette over
        // the map so back returns there, instead of replacing the map with a fresh root.
        ShowCommandPalette(overCurrent: _spatialOverlay?.IsOpen == true);
    }

    private void OpenCommandPalette() => ShowCommandPalette(overCurrent: false);

    /// <param name="overCurrent">true: push the palette over the current surface (the map), so Esc/back and a
    /// completed command return to it. false: a fresh root, so a re-press over a half-open chain resets to a
    /// clean command palette rather than stacking deeper.</param>
    private void ShowCommandPalette(bool overCurrent)
    {
        if (_model is null) return;

        _model.Reconcile(); // drop any externally-deleted desktops so the context board is accurate

        // Show the live map behind each command ("blue = you are here") — the stage draws it in the user's
        // current model (rows or spatial). Commands with a distinct target supply their own board that
        // highlights what they'll act on (green); a null preview falls back to the stage's live board.
        var items = BuildCommands()
            .Select(c => new PaletteItem(c.Name, c.DisabledReason,
                                         c.DisabledReason is null ? "▸" : null, c.Run,
                                         Preview: c.Preview,
                                         DisabledReason: c.DisabledReason))
            .ToList();
        var palette = new PaletteContent("Run a command…",
            "↑↓ move · ↵ run · Esc back · blue = you are here", items,
            clearSearchOnShow: true); // popping back here (Esc from a command's sub-surface) lands on the full list
        if (overCurrent) _stage?.Present(palette); else _stage?.Summon(palette);
    }

    // The command registry — real commands reusing existing handlers.
    private IReadOnlyList<Command> BuildCommands()
    {
        // Commands run synchronously. Those that push another stage surface (a palette, a prompt, the map,
        // the move flow) become the current surface, so PaletteContent.Choose sees the palette is no longer
        // current and leaves the chain in place (Esc pops back through it). Terminal commands leave the
        // palette current, so Choose unwinds to the start. Either way, no flash — one surface throughout.
        // When the last check found a newer release, the palette offers to apply it directly ("Update
        // now — vX") instead of re-checking; otherwise it's a plain "Check for updates".
        bool updateReady = _lastUpdate is { Availability: UpdateAvailability.Available };
        var update = updateReady
            ? new Command($"Update now — v{_lastUpdate!.AvailableVersion}", ApplyLastUpdate)
            : new Command("Check for updates", CheckForUpdates);

        var commands = new List<Command>
        {
            new("Jump to desktop…", OpenSpotlight), // pushed over the command palette; Esc pops back to it
            new("Open map", ToggleMap),
            new("Settings", OpenSettings),
            update,
            new("New branch…", PromptNewBranch),
            // Create, preview and delete branch templates — always available (you can create the first one from here).
            new("Manage templates…", ManageTemplatesPrompt),
            // Delete-current-desktop / remove-current-branch are intentionally not commands — do them from the
            // map, where the target is visible (each tile / branch carries its own × control). Likewise
            // move-windows is triggered from the map ("m"), not from here.
            // Save / restore / reset the whole desktop+branch arrangement — one manager for all three.
            new("Layouts…", LayoutsPrompt),
            // Quit Hypertree — behind a confirm (see ExitHypertree), since it's easy to land on while
            // typing/navigating the palette (unlike the deliberate tray menu item).
            new("Exit Hypertree", ExitHypertree),
        };

        // Monitor-layout save/restore is automatic (dock/undock); the only manual entry is the diagnose +
        // trace overlay, which is a debugging aid — gated on a dev build (DevChrome.Active) so it never shows
        // in a release/installed copy. Sits just above "Exit Hypertree".
        if (DevChrome.Active)
            commands.Insert(commands.Count - 1, new Command("Monitor placement (debug)",
                () => _monitorLayout?.OpenDebugOverlay(),
                DisabledReason: _monitorLayout?.IsAvailable != true ? "monitor tracking unavailable" : null));

        return commands;
    }
}
