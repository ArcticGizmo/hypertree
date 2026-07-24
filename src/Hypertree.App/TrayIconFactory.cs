using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Hypertree.App;

/// <summary>
/// Loads the tray/window icon from the generated multi-resolution asset
/// (<c>avares://hypertree/Assets/icon.ico</c>), which <c>tools/IconGen</c> rasterises from the
/// single source-of-truth <c>hypertree.svg</c>. Re-run <c>tools/gen-icons.ps1</c> after editing the SVG.
/// </summary>
internal static class TrayIconFactory
{
    private static readonly Uri IconUri = new("avares://hypertree/Assets/icon.ico");

    public static WindowIcon Create() => new(AssetLoader.Open(IconUri));
}
