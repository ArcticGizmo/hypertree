using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Hypertree.App.Views;
using Hypertree.App.Views.Scene;
using Hypertree.Layout;
using Hypertree.Scopes;

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

        // Window counts vary per tile (with one empty desktop, "Notes"=0) so the shot exercises the
        // at-a-glance count badges and the dimmed-empty styling. `here` marks the "came from" desktop
        // with the green outline shown while navigating.
        List<NavMapTile> Top(int current, int here = -1) => new()
        {
            new("Home", current == 0, here == 0, 4), new("Comms", current == 1, here == 1, 2),
            new("Web", current == 2, here == 2, 7), new("Notes", current == 3, here == 3, 0),
        };
        NavMapBranch Feat(bool live, int cur) => new(0, "FEAT-123", new List<NavMapTile>
        {
            new("SPA", live && cur == 0, WindowCount: 3), new("API", live && cur == 1, WindowCount: 1),
            new("Mobile", live && cur == 2, WindowCount: 0),
        }, live, cur);
        NavMapBranch Hotfix() => new(1, "hotfix", new List<NavMapTile>
            { new("db", false, WindowCount: 1), new("api", false, WindowCount: 0) }, false, 0);

        // Stable pivot: FEAT-123 sits above main, hotfix below (main slot 1). On the main timeline,
        // Web (cursor 2) is current and main renders between the two branches.
        Save(new NavMap(Top(2), 2, true, new List<NavMapBranch> { Feat(false, 1), Hotfix() }, 1),
             Path.Combine(outDir, "board-top-row.png"));

        // Same fixed layout, now with the cursor inside FEAT-123 (on API=cursor 1) — the branch above
        // main. Main keeps its slot; it does not move. We dived from Web, so it wears the green
        // "came from" outline.
        Save(new NavMap(Top(2, here: 2), 2, false, new List<NavMapBranch> { Feat(true, 1), Hotfix() }, 1),
             Path.Combine(outDir, "board-dived.png"));

        // The drag geometry: the same board with the BoardLayout it reported drawn back over it. The map's
        // drag hit-tests that layout rather than the visual tree, so this is how we check the two agree —
        // every outline should sit exactly on the tile (or branch box) it claims, and each caret in the
        // middle of the gap it inserts at.
        SaveLayoutCheck(new NavMap(Top(2), 2, true, new List<NavMapBranch> { Feat(false, 1), Hotfix() }, 1),
                        Path.Combine(outDir, "board-drag-layout.png"));

        // The metro-map view of the same two states, so it can be compared tile-for-station against the board.
        SaveMetro(new NavMap(Top(2), 2, true, new List<NavMapBranch> { Feat(false, 1), Hotfix() }, 1),
                  Path.Combine(outDir, "metro-top-row.png"));
        SaveMetro(new NavMap(Top(2, here: 2), 2, false, new List<NavMapBranch> { Feat(true, 1), Hotfix() }, 1),
                  Path.Combine(outDir, "metro-dived.png"));

        // A busier board: four branches (two above main, two below) exercise the line-colour cycle and the
        // vertical stack, and a one-desktop branch checks the single-station stub route.
        var busy = new List<NavMapBranch>
        {
            new(0, "FEAT-123", new List<NavMapTile> { new("SPA", false, WindowCount: 3), new("API", false, WindowCount: 1), new("Mobile", false, WindowCount: 0) }, false, 1),
            new(1, "release-4.2", new List<NavMapTile> { new("build", false, WindowCount: 2), new("test", false, WindowCount: 5), new("docs", false, WindowCount: 1), new("ship", false, WindowCount: 0) }, false, 0),
            new(2, "hotfix", new List<NavMapTile> { new("db", false, WindowCount: 1), new("api", false, WindowCount: 0) }, false, 0),
            new(3, "spike", new List<NavMapTile> { new("scratch", false, WindowCount: 2) }, false, 0),
        };
        SaveMetro(new NavMap(Top(2), 2, true, busy, 2), Path.Combine(outDir, "metro-busy.png"));

        // The ASCII terminal theme of the same states, so it can be compared card-for-tile against the others.
        SaveAscii(new NavMap(Top(2), 2, true, new List<NavMapBranch> { Feat(false, 1), Hotfix() }, 1),
                  Path.Combine(outDir, "ascii-top-row.png"));
        SaveAscii(new NavMap(Top(2, here: 2), 2, false, new List<NavMapBranch> { Feat(true, 1), Hotfix() }, 1),
                  Path.Combine(outDir, "ascii-dived.png"));
        SaveAscii(new NavMap(Top(2), 2, true, busy, 2), Path.Combine(outDir, "ascii-busy.png"));
        SaveMetroLayoutCheck(new NavMap(Top(2, here: 2), 2, false, new List<NavMapBranch> { Feat(true, 1), Hotfix() }, 1),
                             Path.Combine(outDir, "metro-drag-layout.png"));

        // Metro over a bright, busy fake desktop — the flat dim vs. the shipped centre-weighted vignette, so
        // the contrast gain under the coloured lines is visible side by side.
        NavMap backdropMap = new(Top(2, here: 2), 2, false, new List<NavMapBranch> { Feat(true, 1), Hotfix() }, 1);
        SaveMetroBackdrop(backdropMap, Path.Combine(outDir, "metro-backdrop-flat.png"), vignette: false);
        SaveMetroBackdrop(backdropMap, Path.Combine(outDir, "metro-backdrop-vignette.png"), vignette: true);

        SaveCards(outDir);
        SaveLauncher(outDir);
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

    // Render the board, then draw the layout it reported on top: a box per tile, a box per row band, and a
    // caret at every insertion point of every row.
    private static void SaveLayoutCheck(NavMap map, string path)
    {
        var layout = new BoardLayout();
        Control board = SceneRenderer.Render(new BoardPainter(), map, ScreenW, ScreenH, 1.0, new MapCamera(), layout: layout);
        SaveWithLayoutMarks(board, layout, path);
    }

    // The same verification for the metro view: prove the station cells, line bands, carets and boundaries
    // MetroView reports sit on the diagram, so the interactive map's click/drag hit-testing lines up there
    // exactly as it does on the board.
    private static void SaveMetroLayoutCheck(NavMap map, string path)
    {
        var layout = new BoardLayout();
        Control board = SceneRenderer.Render(new MetroPainter(), map, ScreenW, ScreenH, 1.0, new MapCamera(), layout: layout);
        SaveWithLayoutMarks(board, layout, path);
    }

    private static void SaveWithLayoutMarks(Control board, BoardLayout layout, string path)
    {
        var marks = new Canvas { Width = ScreenW, Height = ScreenH };
        void Outline(Rect r, Color c, double thickness)
        {
            var b = new Border
            {
                Width = r.Width, Height = r.Height,
                BorderBrush = new SolidColorBrush(c), BorderThickness = new Thickness(thickness),
            };
            Canvas.SetLeft(b, r.X);
            Canvas.SetTop(b, r.Y);
            marks.Children.Add(b);
        }

        foreach (BoardRow row in layout.Rows)
        {
            Outline(row.Bounds, Color.Parse("#38BDF8"), 1); // row band — what a branch drag grabs
            // Every insertion point, drawn as the map's own drop caret is (a filled bar, not an outline).
            for (int i = 0; i <= row.DesktopCount; i++)
            {
                var caret = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 3, Height = row.TileHeight + 8, RadiusX = 2, RadiusY = 2,
                    Fill = new SolidColorBrush(Color.Parse("#F472B6")),
                };
                Canvas.SetLeft(caret, row.BoundaryX(i) - 1.5);
                Canvas.SetTop(caret, row.TileTop - 4);
                marks.Children.Add(caret);
            }
        }
        // Every row boundary, drawn as the map's branch-drop separator is: across both rows it splits.
        for (int b = 0; b < layout.BoundaryCount; b++)
        {
            (double left, double right) = layout.BoundarySpan(b);
            var sep = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = right - left, Height = 3, RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush(Color.Parse("#FBBF24")),
            };
            Canvas.SetLeft(sep, left);
            Canvas.SetTop(sep, layout.BoundaryY(b) - 1.5);
            marks.Children.Add(sep);
        }

        foreach (BoardTile tile in layout.Tiles)
            Outline(tile.Bounds, Color.Parse("#34D399"), 1); // tile hit rects

        Save(new Panel { Children = { board, marks } }, path);
    }

    // Render the metro-map view of a board to PNG, over the same dark ground the real overlay uses.
    private static void SaveMetro(NavMap map, string path)
        => Save(SceneRenderer.Render(new MetroPainter(), map, ScreenW, ScreenH, 1.0, new MapCamera()), path);

    private static void SaveAscii(NavMap map, string path)
        => Save(SceneRenderer.Render(new AsciiPainter(), map, ScreenW, ScreenH, 1.0, new MapCamera()), path);

    // The overlay is semi-transparent over the live desktop, so how the board reads depends on the screen
    // behind it. This shot fakes a bright, busy desktop, lays the real stage dim (the centre-weighted
    // vignette) over it, then the metro board — the only way to eyeball that contrast without the tray.
    private static void SaveMetroBackdrop(NavMap map, string path, bool vignette)
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
        Control board = SceneRenderer.Render(new MetroPainter(), map, ScreenW, ScreenH, 1.0, new MapCamera());
        Save(new Panel { Children = { desktop, dim, board } }, path);
    }

    private static void Save(NavMap map, string path)
        // Pass delete callbacks so the × badges render in the verification shot.
        => Save(SceneRenderer.Render(new BoardPainter(), map, ScreenW, ScreenH, 1.0, new MapCamera(),
                                     onTopDelete: _ => { }, onBranchDelete: (_, _) => { }), path);

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
