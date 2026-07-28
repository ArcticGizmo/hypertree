using Avalonia.Controls;
using Hypertree.App.Views.Scene;
using Hypertree.Layout;
using Hypertree.Scopes;
using Hypertree.Settings;

namespace Hypertree.App.Views;

/// <summary>
/// The single entry point for drawing a board <em>non-interactively</em> — the flash, card backdrops,
/// previews, the move flow. It picks the theme from the app's <see cref="MapStyle"/> so the metro map,
/// once chosen in Settings, shows up everywhere a board does rather than only on the interactive map.
///
/// Both themes now render through the shared <see cref="SceneRenderer"/> pipeline, so they lay out and pan
/// identically. Callers with a persistent <see cref="MapCamera"/> (the interactive map, and the flash once
/// it shares one) pass it in; a null camera means "frame the selection" — a fresh camera centres on where
/// you'd land, which is what a one-shot surface (a preview, a capture) wants.
/// </summary>
internal static class MapSurface
{
    public static Control Render(NavMap map, double width, double height, MapStyle style,
                                 double scale = 1.0, bool animate = false, MapCamera? camera = null)
    {
        IScenePainter painter = style == MapStyle.Metro ? new MetroPainter(animate) : new BoardPainter();
        return SceneRenderer.Render(painter, map, width, height, scale, camera ?? new MapCamera());
    }
}
