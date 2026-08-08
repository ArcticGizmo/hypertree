using System;
using System.Linq;
using Hypertree.Desktops;
using Hypertree.Layout;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// Covers the pure geometry behind the map's off-screen indicators: which rooms earn an arrow (only the
/// wholly-clipped ones) and where on the border it sits (where the line from the viewport centre to the room
/// crosses). No Avalonia, no painter — just <see cref="OffscreenMarkers"/>.
/// </summary>
public class OffscreenMarkersTests
{
    // A square-ish grid: a cell is 50×50, cells one stride apart, so grid (n,0) sits at world x = n·100.
    private static readonly SceneMetrics M = new(
        CellStride: 100, CellWidth: 50, CellHeight: 50, RowPitch: 100, RowHeight: 50);

    private static DesktopId Id(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));

    private static SpatialLayout Layout(params (int n, int gx, int gy)[] rooms)
    {
        Guid group = new("aaaaaaaa-0000-0000-0000-000000000000");
        var placed = rooms
            .Select(r => new SpatialRoom(Id(r.n), $"d{r.n}", new GridPos(r.gx, r.gy), group,
                                         IsMainGroup: true, Selected: false, Here: false, WindowCount: 0))
            .ToList();
        var scene = new SpatialScene(
            new[] { new SpatialGroup(group, "main", "#888888", true, placed.Select(p => p.Id).ToList()) },
            placed);
        return new SpatialLayout(scene, M);
    }

    // Offset that puts world (0,0) at the centre of a viewW×viewH viewport (room 0 sits dead centre).
    private static (double ox, double oy) CentreOnOrigin(double viewW, double viewH) => (viewW / 2, viewH / 2);

    [Fact]
    public void A_room_fully_on_screen_gets_no_marker()
    {
        SpatialLayout layout = Layout((0, 0, 0));
        (double ox, double oy) = CentreOnOrigin(400, 300);

        Assert.Empty(OffscreenMarkers.Compute(layout, ox, oy, 400, 300, inset: 4));
    }

    [Fact]
    public void A_room_off_the_right_edge_gets_a_marker_on_the_right_border_pointing_right()
    {
        // Room 5 sits at world x = 500; centred on origin in a 400-wide viewport its screen x ≈ 700 — off right.
        SpatialLayout layout = Layout((0, 0, 0), (5, 5, 0));
        (double ox, double oy) = CentreOnOrigin(400, 300);

        var markers = OffscreenMarkers.Compute(layout, ox, oy, 400, 300, inset: 4);

        EdgeMarker m = Assert.Single(markers);
        Assert.Equal(Id(5), m.Room);
        Assert.Equal(400 - 4, m.X, 3);      // pinned to the inset right border
        Assert.Equal(150, m.Y, 3);          // same row as the centre → mid-height
        Assert.Equal(0, m.Angle, 3);        // pointing straight right (+x)
    }

    [Fact]
    public void A_room_off_the_top_edge_points_up()
    {
        // Room at grid (0,-5): world y = -500, well above a viewport centred on the origin.
        SpatialLayout layout = Layout((0, 0, 0), (7, 0, -5));
        (double ox, double oy) = CentreOnOrigin(400, 300);

        EdgeMarker m = Assert.Single(OffscreenMarkers.Compute(layout, ox, oy, 400, 300, inset: 4));
        Assert.Equal(Id(7), m.Room);
        Assert.Equal(200, m.X, 3);                 // straight up from centre → mid-width
        Assert.Equal(4, m.Y, 3);                   // pinned to the inset top border
        Assert.Equal(-Math.PI / 2, m.Angle, 3);    // atan2(-y, 0) = -90°
    }

    [Fact]
    public void A_partly_visible_room_is_not_off_screen()
    {
        // Room 3 at world x = 300; centred on origin in a 700-wide viewport its cell spans screen
        // x ≈ [625, 675] — still inside 700, so it's visible and earns no marker.
        SpatialLayout layout = Layout((0, 0, 0), (3, 3, 0));
        (double ox, double oy) = CentreOnOrigin(700, 300);

        Assert.Empty(OffscreenMarkers.Compute(layout, ox, oy, 700, 300, inset: 4));
    }

    [Fact]
    public void Every_off_screen_room_gets_its_own_marker()
    {
        SpatialLayout layout = Layout((0, 0, 0), (5, 5, 0), (6, -5, 0), (7, 0, -5));
        (double ox, double oy) = CentreOnOrigin(400, 300);

        var markers = OffscreenMarkers.Compute(layout, ox, oy, 400, 300, inset: 4);

        Assert.Equal(3, markers.Count);
        Assert.Equal(new[] { Id(5), Id(6), Id(7) }, markers.Select(m => m.Room).OrderBy(i => i.Value).ToArray());
    }

    [Fact]
    public void A_degenerate_viewport_yields_no_markers()
    {
        SpatialLayout layout = Layout((0, 0, 0), (5, 5, 0));
        Assert.Empty(OffscreenMarkers.Compute(layout, 0, 0, 0, 0, inset: 4));
    }
}
