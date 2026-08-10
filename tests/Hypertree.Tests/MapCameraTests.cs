using Hypertree.Desktops;
using Hypertree.Layout;
using Hypertree.Spatial;
using Xunit;

namespace Hypertree.Tests;

/// <summary>Covers the dead-zone follow camera: it holds still while the selection is on screen, pans by the
/// minimum needed (leaving one marker of margin) when it reaches an edge, and centres-and-pins an axis the
/// whole map fits on. The pure <see cref="MapCamera.Axis"/> maths is exercised directly.</summary>
public class MapCameraTests
{
    // A content span far larger than the viewport, so the axis is in "follow" mode.
    private const double View = 500, Margin = 100, Lo = 0, Hi = 1000;

    [Fact]
    public void Fits_the_axis_is_centred_and_independent_of_the_cursor()
    {
        // Content [0,300] fits a 500 viewport → centred at (500-300)/2 = 100, whatever the selection is.
        double a = MapCamera.Axis(offset: 0, framed: true, selLo: 0, selHi: 60, contentLo: 0, contentHi: 300, view: View, margin: Margin);
        double b = MapCamera.Axis(offset: 0, framed: true, selLo: 240, selHi: 300, contentLo: 0, contentHi: 300, view: View, margin: Margin);
        Assert.Equal(100, a, precision: 6);
        Assert.Equal(100, b, precision: 6); // same offset: moving the cursor didn't move the map
    }

    [Fact]
    public void Comfortably_on_screen_is_a_dead_zone_the_map_does_not_move()
    {
        // offset 0 shows world [0,500]; a selection at [200,260] sits inside it even with the margin.
        double result = MapCamera.Axis(offset: 0, framed: true, selLo: 200, selHi: 260, contentLo: Lo, contentHi: Hi, view: View, margin: Margin);
        Assert.Equal(0, result, precision: 6);
    }

    [Fact]
    public void Reaching_the_high_edge_pans_the_minimum_leaving_a_margin()
    {
        // World [0,500]; selection [420,480]. Its far edge + margin (580) is past 500, so pan to show it.
        double result = MapCamera.Axis(offset: 0, framed: true, selLo: 420, selHi: 480, contentLo: Lo, contentHi: Hi, view: View, margin: Margin);
        Assert.Equal(-80, result, precision: 6); // view - (selHi + margin) = 500 - 580
    }

    [Fact]
    public void Reaching_the_low_edge_pans_the_minimum_leaving_a_margin()
    {
        // World [180,680] (offset -180); selection moves to [200,260]; its near edge - margin (100) is below
        // 180, so pan back so 100 sits at the viewport's start → offset 0... but clamp is the low edge here.
        double result = MapCamera.Axis(offset: -180, framed: true, selLo: 200, selHi: 260, contentLo: Lo, contentHi: Hi, view: View, margin: Margin);
        Assert.Equal(-100, result, precision: 6); // -(selLo - margin) = -(200-100)
    }

    [Fact]
    public void Panning_up_then_back_holds_still_the_hysteresis()
    {
        // Step 1: world [80,580] (offset -80). Move the selection to the next marker off the top edge.
        double afterUp = MapCamera.Axis(offset: -80, framed: true, selLo: 520, selHi: 580, contentLo: Lo, contentHi: Hi, view: View, margin: Margin);
        Assert.Equal(-180, afterUp, precision: 6); // pans to bring the new marker into view

        // Step 2: move back one marker. The map must NOT move — the marker is still comfortably in view.
        double afterBack = MapCamera.Axis(offset: afterUp, framed: true, selLo: 420, selHi: 480, contentLo: Lo, contentHi: Hi, view: View, margin: Margin);
        Assert.Equal(-180, afterBack, precision: 6); // unchanged: the dead zone
    }

    [Fact]
    public void Never_scrolls_past_the_content_edge()
    {
        // Selection at the very end: even with a margin there's nothing beyond content to show, so the far
        // edge pins to the viewport edge rather than opening a blank gutter.
        double result = MapCamera.Axis(offset: 0, framed: true, selLo: 960, selHi: 1000, contentLo: Lo, contentHi: Hi, view: View, margin: Margin);
        Assert.Equal(-500, result, precision: 6); // clamped to view - contentHi
    }

    [Fact]
    public void An_oversized_margin_is_capped_so_the_dead_zone_still_holds()
    {
        // Margin (300) larger than half the free space around a 60-tall selection in a 500 viewport would make
        // the dead zone unsatisfiable. Capped to (500-60)/2 = 220, a centred selection still reads as "inside".
        // Selection world [260,320], centred → offset -40 puts it at screen [220,280], exactly 220 from each edge.
        double centred = MapCamera.Axis(offset: -40, framed: true, selLo: 260, selHi: 320, contentLo: Lo, contentHi: Hi, view: View, margin: 300);
        Assert.Equal(-40, centred, precision: 6); // held: neither edge is closer than the capped margin
    }

    [Fact]
    public void A_top_edge_selection_keeps_a_gutter_when_edge_padding_is_set()
    {
        // Selection at the content's very start. With no padding it would pin flush (maxOffset = -Lo = 0);
        // an 80px edge pad lets the content top sit 80 down, leaving a gutter above the first marker.
        double result = MapCamera.Axis(offset: 0, framed: true, selLo: 0, selHi: 40, contentLo: Lo, contentHi: Hi, view: View, margin: Margin, edgePad: 80);
        Assert.Equal(80, result, precision: 6); // clamped to maxOffset = -contentLo + edgePad = 0 + 80
    }

    [Fact]
    public void A_bottom_edge_selection_keeps_a_gutter_when_edge_padding_is_set()
    {
        // Mirror of the top: selection at the content's very end keeps a gutter below rather than pinning flush.
        double result = MapCamera.Axis(offset: 0, framed: true, selLo: 960, selHi: 1000, contentLo: Lo, contentHi: Hi, view: View, margin: Margin, edgePad: 80);
        Assert.Equal(-580, result, precision: 6); // clamped to minOffset = view - contentHi - edgePad = 500 - 1000 - 80
    }

    [Fact]
    public void First_framing_centres_the_selection()
    {
        double result = MapCamera.Axis(offset: 0, framed: false, selLo: 400, selHi: 460, contentLo: Lo, contentHi: Hi, view: View, margin: Margin);
        Assert.Equal(-180, result, precision: 6); // view/2 - selCentre = 250 - 430
    }

    // ── Integration through Update on a real overflowing layout ────────────────────────

    private static readonly SceneMetrics Tall = new(CellStride: 100, CellWidth: 80, CellHeight: 60, RowPitch: 120, RowHeight: 90);

    private static DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));

    // A tall stack laid on the grid: a 2-room main row on top, then five 2-room branch rows below it, so the
    // vertical axis overflows a short viewport while the two columns fit a wide one. currentBranch/currentCol
    // pick the selected room. The spatial layout is an ICameraLayout, so it drives the same camera the map does.
    private static SpatialLayout TallLayout(int currentBranch, int currentCol)
    {
        SpatialDesktop Room(int id, string label, bool sel) => new(D(id), label, sel, Here: false, WindowCount: 1);

        var groups = new List<SpatialGroupSource>
        {
            new(Guid.Empty, "main", IsMain: true, new[] { Room(0, "a", false), Room(1, "b", false) }),
        };
        for (int i = 0; i < 5; i++)
            groups.Add(new SpatialGroupSource(Guid.NewGuid(), $"b{i}", IsMain: false, new[]
            {
                Room(10 + i * 2, "d0", currentBranch == i && currentCol == 0),
                Room(11 + i * 2, "d1", currentBranch == i && currentCol == 1),
            }));

        var state = new SpatialState();
        void P(int id, int x, int y) => state.SetPosition(D(id).Value, new GridPos(x, y));
        P(0, 0, 0); P(1, 1, 0);                                  // main row on top
        for (int i = 0; i < 5; i++) { P(10 + i * 2, 0, i + 1); P(11 + i * 2, 1, i + 1); } // branch rows below

        return new SpatialLayout(SpatialScene.From(new SpatialSource(groups), state), Tall);
    }

    [Fact]
    public void Update_centres_a_fitting_axis_and_follows_an_overflowing_one()
    {
        var cam = new MapCamera();
        // Wide viewport (X fits) but short (Y overflows the 6-row stack).
        SpatialLayout layout = TallLayout(currentBranch: 0, currentCol: 0);
        cam.Update(layout, viewW: 2000, viewH: 300);

        // X fits: two columns span a width far under 2000 → centred.
        (double xLo, double xHi) = layout.WorldX();
        double expectX = (2000 - (xHi - xLo)) / 2 - xLo;
        Assert.Equal(expectX, cam.OffsetX, precision: 6);

        // The selected room must be within the 300-tall viewport.
        LayoutRect sel = layout.SelectionRect;
        double top = sel.Top + cam.OffsetY, bottom = sel.Bottom + cam.OffsetY;
        Assert.True(top >= 0 && bottom <= 300, $"selection off screen: [{top},{bottom}]");
    }

    [Fact]
    public void Update_holds_still_when_the_selection_stays_on_screen()
    {
        var cam = new MapCamera();
        SpatialLayout near = TallLayout(currentBranch: 0, currentCol: 0);
        cam.Update(near, viewW: 2000, viewH: 300);
        double firstY = cam.OffsetY;

        // Move the selection one column along the same row (still on screen) — the map should not budge.
        SpatialLayout moved = TallLayout(currentBranch: 0, currentCol: 1);
        cam.Update(moved, viewW: 2000, viewH: 300);
        Assert.Equal(firstY, cam.OffsetY, precision: 6);
    }
}
