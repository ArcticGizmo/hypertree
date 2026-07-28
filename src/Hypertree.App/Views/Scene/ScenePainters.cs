using Hypertree.Settings;

namespace Hypertree.App.Views.Scene;

/// <summary>
/// Maps a <see cref="MapStyle"/> to its painter — the one place the theme set is enumerated, so adding a
/// theme is a new <see cref="IScenePainter"/> plus a case here. <paramref name="animate"/> is honoured by
/// themes with live motion (the metro train, the ASCII cursor); still ones ignore it.
/// </summary>
internal static class ScenePainters
{
    public static IScenePainter For(MapStyle style, bool animate) => style switch
    {
        MapStyle.Metro => new MetroPainter(animate),
        MapStyle.Ascii => new AsciiPainter(animate),
        _ => new BoardPainter(),
    };
}
