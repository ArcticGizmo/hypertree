using Avalonia.Controls;
using Hypertree.Scopes;
using Hypertree.Settings;

namespace Hypertree.App.Views;

/// <summary>
/// The single entry point for drawing a board <em>non-interactively</em> — the flash, card backdrops,
/// previews, the move flow. It picks the renderer from the app's <see cref="MapStyle"/> so the metro map,
/// once chosen in Settings, shows up everywhere a board does rather than only on the interactive map.
///
/// The interactive <see cref="MapOverlay"/> doesn't go through here: it needs the renderer's click/drag
/// callbacks and the reported <see cref="BoardLayout"/>, so it calls <see cref="BoardView"/>/<see cref="MetroView"/>
/// directly (passing the same style).
/// </summary>
internal static class MapSurface
{
    public static Control Render(NavMap map, double width, double height, MapStyle style,
                                 double scale = 1.0, bool animate = false)
        => style == MapStyle.Metro
            ? MetroView.Render(map, width, height, scale, animate)
            : BoardView.Render(map, width, height, scale);
}
