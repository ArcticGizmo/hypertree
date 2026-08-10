namespace Hypertree.Layout;

/// <summary>
/// The minimum a layout must expose for the shared <see cref="MapCamera"/> to frame and follow it: where the
/// selection sits, the content's span on each axis, and the metrics that set the follow margin. The
/// spatial layout (<see cref="Hypertree.Spatial.SpatialLayout"/>) implements it, so the one dead-zone camera
/// — and the single offset it shares with the flash — drives the map unchanged. The camera reads only these
/// four members and treats each axis independently, which is exactly why it works in 2-D as-is.
/// </summary>
public interface ICameraLayout
{
    /// <summary>The selection's world rect — the single thing the camera follows.</summary>
    LayoutRect SelectionRect { get; }

    /// <summary>The sizing that sets the camera's per-axis follow margin (stride horizontally, pitch
    /// vertically).</summary>
    SceneMetrics Metrics { get; }

    /// <summary>The horizontal world span across all content — the fits-in-viewport test on X.</summary>
    (double Lo, double Hi) WorldX();

    /// <summary>The vertical world span across all content — the fits-in-viewport test on Y.</summary>
    (double Lo, double Hi) WorldY();
}
