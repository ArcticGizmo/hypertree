using Avalonia.Controls;

namespace Hypertree.App;

/// <summary>
/// Loads the tray/window icon from the generated multi-resolution asset
/// (<c>avares://hypertree/Assets/icon.ico</c>), which <c>tools/IconGen</c> rasterises from the
/// single source-of-truth <c>hypertree.svg</c>. Re-run <c>tools/gen-icons.ps1</c> after editing the SVG.
/// On a dev build the icon is tinted pink (see <see cref="DevChrome"/>) so a test copy is obvious.
/// </summary>
internal static class TrayIconFactory
{
    public static WindowIcon Create() => DevChrome.AppWindowIcon();
}
