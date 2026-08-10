using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Hypertree.App.Views;
using Hypertree.App.Views.Scene;
using Hypertree.Desktops;
using Hypertree.Layout;
using Hypertree.Scopes;
using Hypertree.Settings;
using Hypertree.Spatial;

namespace Hypertree.App;

/// <summary>
/// Offscreen render of the board to PNG (invoked with <c>--shot &lt;dir&gt;</c>) — the standing way to
/// eyeball the visualization without a display, and without screenshotting the real desktop (which
/// would capture unrelated windows). Renders ONLY Hypertree's own synthetic board. Sample data mirrors
/// docs/design/p-vs-q.html so the output can be compared against the design directly.
/// </summary>
internal static class DesignShot
{
    public static void Capture(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // The spatial map: desktops placed freely in 2-D as rooms inside group hulls. "top" is a tidy
        // hand-arrangement; "fragmented" flings half of release-4.2 away so a group splits into two hulls
        // (the ⚡-fragments state Tidy will later reunite).
        SaveSpatial(Path.Combine(outDir, "spatial-top.png"), fragmented: false);
        SaveSpatial(Path.Combine(outDir, "spatial-fragmented.png"), fragmented: true);
        SaveSpatial(Path.Combine(outDir, "spatial-group.png"), fragmented: false, selectedGroup: 2); // release-4.2 selected
        SaveSpatial(Path.Combine(outDir, "spatial-tidied.png"), fragmented: true, tidied: true);      // the fragmented board after Tidy
        // The same spatial layout rendered in each Map style, so room glyphs can be checked across themes.
        SaveSpatial(Path.Combine(outDir, "spatial-ascii.png"), fragmented: false, style: MapStyle.Ascii);
        SaveSpatial(Path.Combine(outDir, "spatial-metro.png"), fragmented: false, style: MapStyle.Metro);
        SaveSpatial(Path.Combine(outDir, "spatial-overlap.png"), fragmented: false, overlap: true); // two rooms on one cell

        // The map over a bright, busy fake desktop — the flat dim vs. the shipped centre-weighted vignette,
        // so the contrast gain under the coloured rooms is visible side by side.
        SaveSpatialBackdrop(Path.Combine(outDir, "spatial-backdrop-flat.png"), vignette: false);
        SaveSpatialBackdrop(Path.Combine(outDir, "spatial-backdrop-vignette.png"), vignette: true);

        SaveCards(outDir);
        SaveLauncher(outDir);
    }

    /// <summary>
    /// The spatial map rendered offscreen, over the same dark ground. Builds a scene by hand — the busy
    /// four-branch data as groups, placed at explicit grid positions — so the room tiles, group hulls, name
    /// badges, and the selected/here/empty states can all be eyeballed without the tray.
    /// </summary>
    private static void SaveSpatial(string path, bool fragmented, int? selectedGroup = null, bool tidied = false,
                                    MapStyle style = MapStyle.Board, bool overlap = false)
    {
        (SpatialSource source, SpatialState state) = SampleScene(fragmented, overlap, tidied);
        Guid? sel = selectedGroup is { } sg ? SampleGid(sg) : null;
        Save(SpatialPainter.Render(SpatialScene.From(source, state), ScreenW, ScreenH, 1.0, new MapCamera(),
                                   selectedGroup: sel, style: style), path);
    }

    // Stable group id for the sample data, so a group can be referenced (e.g. the selected one) by number.
    private static Guid SampleGid(int n) => new($"{n:D8}-aaaa-0000-0000-000000000000");

    // The hand-built sample: five groups (main + four branches) placed at explicit grid positions, so the
    // room tiles, group hulls, name badges, and the selected/here/empty states can be eyeballed without the
    // tray. Shared by the plain map shots and the backdrop-contrast shot.
    private static (SpatialSource Source, SpatialState State) SampleScene(bool fragmented, bool overlap, bool tidied)
    {
        DesktopId D(int n) => new(new Guid($"{n:D8}-0000-0000-0000-000000000000"));
        SpatialDesktop Desk(int id, string label, int win, bool sel = false, bool here = false)
            => new(D(id), label, sel, here, win);

        var source = new SpatialSource(new[]
        {
            new SpatialGroupSource(Guid.Empty, "main", IsMain: true, new[]
                { Desk(0, "Home", 4), Desk(1, "Comms", 2), Desk(2, "Web", 7, sel: true, here: true), Desk(3, "Notes", 0) }),
            new SpatialGroupSource(SampleGid(1), "FEAT-123", IsMain: false, new[]
                { Desk(10, "SPA", 3), Desk(11, "API", 1), Desk(12, "Mobile", 0) }),
            new SpatialGroupSource(SampleGid(2), "release-4.2", IsMain: false, new[]
                { Desk(20, "build", 2), Desk(21, "test", 5), Desk(22, "docs", 1), Desk(23, "ship", 0) }),
            new SpatialGroupSource(SampleGid(3), "hotfix", IsMain: false, new[]
                { Desk(30, "db", 1), Desk(31, "api", 0) }),
            new SpatialGroupSource(SampleGid(4), "spike", IsMain: false, new[] { Desk(40, "scratch", 2) }),
        });

        var state = new SpatialState();
        void P(int id, int x, int y) => state.SetPosition(D(id).Value, new GridPos(x, y));
        P(0, 1, 2); P(1, 2, 2); P(2, 3, 2); P(3, 4, 2);          // main across the middle
        P(10, 1, 0); P(11, 2, 0); P(12, 3, 0);                    // FEAT-123 up and to the left
        P(30, 1, 4); P(31, 2, 4);                                 // hotfix down low
        P(40, 6, 3);                                              // spike, a lone room
        if (!fragmented) { P(20, 6, 0); P(21, 7, 0); P(22, 6, 1); P(23, 7, 1); } // release-4.2 as a 2×2 block
        else { P(20, 6, 0); P(21, 7, 0); P(22, -2, 4); P(23, -1, 4); }           // …split into two fragments
        if (overlap) P(22, 6, 0);                                                 // drop docs onto build's cell

        // Apply Tidy to the (fragmented) layout so the shot shows the reassembled, packed result.
        if (tidied)
            foreach (KeyValuePair<DesktopId, GridPos> kv in SpatialTidy.All(SpatialScene.From(source, state)))
                state.SetPosition(kv.Key.Value, kv.Value);

        return (source, state);
    }

    // The spatial map over a bright, busy fake desktop, laid under the real stage dim — either the flat slab
    // or the shipped centre-weighted vignette — so the contrast gain under the coloured rooms can be
    // eyeballed without the tray. The only way to check that contrast offscreen.
    private static void SaveSpatialBackdrop(string path, bool vignette)
    {
        var desktop = new Panel { Width = ScreenW, Height = ScreenH };
        desktop.Children.Add(new Border
        {
            Width = ScreenW, Height = ScreenH,
            Background = new LinearGradientBrush
            {
                StartPoint = RelativePoint.TopLeft, EndPoint = RelativePoint.BottomRight,
                GradientStops =
                {
                    new GradientStop(Color.Parse("#BFD6EE"), 0), new GradientStop(Color.Parse("#F4F7FB"), 0.5),
                    new GradientStop(Color.Parse("#DAE2EC"), 1),
                },
            },
        });
        // A scatter of bright "windows", so the board is judged against varied high-luminance content.
        (double x, double y, double w, double h, string c)[] wins =
        {
            (120, 90, 520, 360, "#FFFFFF"), (900, 120, 380, 300, "#EAF2FF"),
            (300, 520, 560, 300, "#FFF6E6"), (980, 520, 340, 280, "#F0FFF4"),
            (60, 470, 200, 360, "#FDE8EF"),
        };
        var winCanvas = new Canvas { Width = ScreenW, Height = ScreenH };
        foreach ((double x, double y, double w, double h, string c) in wins)
        {
            var win = new Border
            {
                Width = w, Height = h, Background = new SolidColorBrush(Color.Parse(c)),
                CornerRadius = new CornerRadius(8),
            };
            Canvas.SetLeft(win, x);
            Canvas.SetTop(win, y);
            winCanvas.Children.Add(win);
        }
        desktop.Children.Add(winCanvas);

        var dim = new Border
        {
            Width = ScreenW, Height = ScreenH,
            Background = vignette ? StageWindow.BuildDim() : new SolidColorBrush(Color.FromArgb(0x9E, 0x0E, 0x0E, 0x12)),
        };
        (SpatialSource source, SpatialState state) = SampleScene(fragmented: false, overlap: false, tidied: false);
        Control board = SpatialPainter.Render(SpatialScene.From(source, state), ScreenW, ScreenH, 1.0, new MapCamera());
        Save(new Panel { Children = { desktop, dim, board } }, path);
    }

    /// <summary>
    /// The application launcher (Ctrl+Alt+O) card, rendered with the real Start-menu catalog and icon
    /// provider so the shot exercises the whole pipeline — discovery, icon extraction, and the per-row icon
    /// column — against this machine's actual apps. Like the map, the launcher can't otherwise be reached
    /// without registering the global hotkey and driving the live tray.
    /// </summary>
    private static void SaveLauncher(string outDir)
    {
        var catalog = PlatformServices.CreateAppCatalog();
        var icons = PlatformServices.CreateAppIconProvider();

        var items = new List<PaletteItem>
        {
            new("Command…", "run a one-off command", ">", () => { }),
            new("Custom commands…", "add, edit or remove", "⚙", () => { }),
            new("Open work email", "https://mail.example.com", "⚡", () => { }),
        };
        // A slice of the real installed apps, each with its real icon resolved synchronously for the shot.
        foreach (Launch.AppEntry app in catalog.Discover().Take(8))
        {
            Launch.AppEntry a = app;
            items.Add(new PaletteItem(a.Name, null, null, () => { }, LoadIcon: () =>
            {
                byte[]? png = icons.GetIconPng(a.LaunchPath);
                IImage? img = png is { Length: > 0 } ? new Bitmap(new MemoryStream(png)) : null;
                return Task.FromResult(img);
            }));
        }

        var content = new PaletteContent("Search apps and commands…", "↑↓ move · ↵ launch · Esc close", items);
        SaveHostedControl(content.View, Path.Combine(outDir, "launcher.png"));
    }

    // Render a stage-content view (a card) the way it actually appears: inside a real, dark-themed, offscreen
    // window sized to the screen. A real window root is what lets the card's templated controls — the search
    // TextBox, any buttons — acquire their templates (a detached render silently drops them; see SaveCard).
    private static void SaveHostedControl(Control view, string path)
    {
        var window = new Window
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32000, -32000), // offscreen: no flash on the real desktop
            Width = ScreenW, Height = ScreenH, ShowInTaskbar = false,
            WindowDecorations = WindowDecorations.None,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#0F131B")),
            Content = view,
        };
        window.Show();
        for (int i = 0; i < 6; i++) // let styles/templates apply, layout settle, and the sync icon loads pump
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        var rtb = new RenderTargetBitmap(new PixelSize(ScreenW, ScreenH), new Vector(96, 96));
        rtb.Render(window);
        using (var fs = File.Create(path)) rtb.Save(fs);
        Console.WriteLine($"wrote {path} ({ScreenW}x{ScreenH})");
        window.Close();
    }

    /// <summary>
    /// The two card windows, rendered at the exact size they open at.
    /// </summary>
    /// <remarks>
    /// Neither can be reached from here by any other means: both are opened from the tray menu or by a
    /// version change, so checking a layout tweak otherwise means installing a build and clicking through
    /// to it. They are also both <c>CanResize = false</c>, which makes their width a design decision
    /// rather than something the user can fix — worth being able to look at on demand.
    /// </remarks>
    private static void SaveCards(string outDir)
    {
        var activator = PlatformServices.CreateForegroundActivator();

        // Read the real embedded CHANGELOG so the shot shows genuine content at genuine lengths — made-up
        // bullets would not tell us whether real ones wrap.
        var markdown = ChangelogMarkdown.LoadEmbedded() ?? "";
        var sections = Changelog.ChangelogParser.Parse(markdown)
            .Where(s => s.Version is not null)
            .Take(2)
            .ToList();

        var changelog = new ChangelogWindow(
            "What's new in Hypertree", "Here's what changed across the last 2 releases.",
            sections, activator, onSuppress: () => { });
        SaveCard(changelog, Path.Combine(outDir, "card-changelog.png"));

        // Inert update hooks — the shot only needs the row's resting state, not a live check.
        var settings = new SettingsWindow(new Settings.AppSettings(), startOnLogin: true,
                                          onSave: (_, _) => { }, activator,
                                          new UpdateHooks(() => { }, () => { }, () => null));
        SaveCard(settings, Path.Combine(outDir, "card-settings.png"));
    }

    /// <summary>
    /// Render a card window at the size it actually opens at.
    /// </summary>
    /// <remarks>
    /// The window is shown, off the side of the screen, rather than having its content re-hosted in a
    /// detached container. Templated controls — every <c>Button</c> on these cards — only acquire a
    /// template once they are inside a styled window root, so a detached render silently drops them and
    /// produces a screenshot missing exactly the parts most worth looking at. Showing it for real also
    /// means the measured size is the true one, including whatever <c>SizeToContent</c> settles on.
    /// </remarks>
    private static void SaveCard(Window window, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-32000, -32000); // offscreen: no flash on the real desktop
        window.ShowInTaskbar = false;
        window.Show();

        // Let styles apply and layout settle before measuring or rendering. One pass isn't always enough:
        // applying a template can dirty layout again.
        for (int i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        int w = (int)Math.Ceiling(window.Bounds.Width);
        int h = (int)Math.Ceiling(window.Bounds.Height);
        if (w <= 0 || h <= 0) { window.Close(); return; }

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(window);
        using (var fs = File.Create(path)) rtb.Save(fs);
        Console.WriteLine($"wrote {path} ({w}x{h})");

        window.Close();
    }

    // A representative primary-monitor size, so the shot shows the real full-screen, centred layout
    // (F1/F3) rather than a size-to-content card.
    private const int ScreenW = 1440, ScreenH = 900;

    private static void Save(Control content, string path)
    {
        var host = new Border
        {
            Width = ScreenW, Height = ScreenH,
            Background = new SolidColorBrush(Color.Parse("#0F131B")), // design --bg (dark)
            Child = content,
        };
        host.Measure(Size.Infinity);
        host.Arrange(new Rect(new Size(ScreenW, ScreenH)));

        var rtb = new RenderTargetBitmap(new PixelSize(ScreenW, ScreenH), new Vector(96, 96));
        rtb.Render(host);
        using var fs = File.Create(path);
        rtb.Save(fs);
        Console.WriteLine($"wrote {path} ({ScreenW}x{ScreenH})");
    }
}
