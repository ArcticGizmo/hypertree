using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Settings;
using Hypertree.Spatial;

namespace Hypertree.App.Views;

/// <summary>
/// Builds the <see cref="SpatialOverlay"/>'s chrome — the key legend (and its collapsed hint pill) and the
/// groups &amp; colours panel — separately from the overlay's state and event logic. The legend is pure
/// construction from a table; the groups panel is view construction wired to a small set of
/// <see cref="GroupsPanelCallbacks"/> the overlay supplies, so the state transitions (select a group,
/// recolour, expand a palette) stay with the overlay while the layout lives here.
/// </summary>
internal static class SpatialOverlayChrome
{
    private static readonly IBrush Fg = Palette.InkBrush;
    private static readonly IBrush FgDim = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;
    private static readonly Color LegendBg = Color.FromArgb(0xC8, 0x14, 0x19, 0x22);
    private static readonly Color KeyCapBg = Color.FromArgb(0xFF, 0x22, 0x2C, 0x3A);
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    /// <summary>The state transitions the groups panel drives back into the overlay — kept as explicit
    /// callbacks so the panel builds the view and the overlay owns the mutation.</summary>
    public readonly record struct GroupsPanelCallbacks(
        Action<Guid> SelectGroup, Action<Guid> TogglePalette, Action<Guid, string> Recolour);

    // ── Legend (l) ─────────────────────────────────────────────────────────────

    /// <summary>The key legend (top-left). The "v" row's label follows the current <paramref name="style"/>
    /// (it names the style a press of <c>v</c> switches to next).</summary>
    public static Control Legend(MapStyle style)
    {
        var rows = new StackPanel { Spacing = 7 };
        rows.Children.Add(new TextBlock
        {
            Text = "Spatial map", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            Margin = new Thickness(0, 0, 0, 4),
        });

        foreach ((string key, string desc) in LegendRows(style))
            rows.Children.Add(LegendRow(key, desc));

        rows.Children.Add(new TextBlock
        {
            Text = "click to select · double-click to switch", FontSize = 11, Foreground = FgDim,
            Margin = new Thickness(0, 5, 0, 0),
        });
        rows.Children.Add(new TextBlock
        {
            Text = "drag a room · ⇧-drag its block", FontSize = 11, Foreground = FgDim,
        });

        var legend = new Border
        {
            Background = new SolidColorBrush(LegendBg),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 24, 0, 0), Child = rows,
        };
        legend.PointerPressed += (_, e) => e.Handled = true; // reading the legend never selects behind it
        return legend;
    }

    // The legend body as a (keycap, description) table, rendered in a loop rather than 20-odd imperative Adds.
    // The "v" row is the one dynamic entry — it names the style v switches to next.
    private static (string Key, string Desc)[] LegendRows(MapStyle style) => new[]
    {
        ("←→↑↓", "select the nearest room"),
        ("Enter/Space", "switch to selected"),
        ("Ctrl+Alt+←→↑↓", "switch to a desktop"),
        ("Ctrl+←→↑↓", "move the room / group"),
        ("Ctrl+Shift+←→↑↓", "move the block"),
        ("g", "set the room's group"),
        ("Shift+g", "groups & colours"),
        ("r", "rename room"),
        ("Shift+r", "rename group"),
        ("n", "new desktop · b new branch"),
        ("m", "move windows"),
        ("Shift+m", "pull windows"),
        ("f", "find · p palette · o apps"),
        ("t", "tidy up (reunite groups)"),
        ("+ / −", "zoom in / out · 0 reset"),
        ("v", style switch { MapStyle.Board => "metro view", MapStyle.Metro => "ascii view", _ => "board view" }),
        ("Del", "remove room"),
        ("Shift+Del", "remove group"),
        ("Ctrl+z", "undo the last tidy"),
        ("l", "hide this legend"),
        ("Esc", "close"),
    };

    /// <summary>A hint shown in place of the full legend: a small pill in the same corner so the <c>l</c>
    /// toggle stays discoverable once the legend is hidden. Clicking it never selects the map behind it.</summary>
    public static Control LegendHint()
    {
        var hint = new Border
        {
            Background = new SolidColorBrush(LegendBg),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 7),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 24, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    KeyCap("l"),
                    new TextBlock { Text = "legend", FontSize = 11, Foreground = FgDim, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        hint.PointerPressed += (_, e) => e.Handled = true;
        return hint;
    }

    // A keycap chip — the accent-on-dark rounded label the legend and its hint share.
    private static Control KeyCap(string key) => new Border
    {
        Background = new SolidColorBrush(KeyCapBg),
        CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = key, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Accent, FontFamily = Mono,
        },
    };

    private static Control LegendRow(string key, string desc)
    {
        Control cap = KeyCap(key);
        Grid.SetColumn(cap, 0);
        var label = new TextBlock
        {
            Text = desc, FontSize = 12, Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(label, 1);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };
        grid.Children.Add(cap);
        grid.Children.Add(label);
        return grid;
    }

    // ── Groups & colours panel (top-right, ⇧G) ─────────────────────────────────

    /// <summary>The groups &amp; colours panel: a row per group (swatch, name, room tally) with the highlighted
    /// group's colour palette expanded beneath it. <paramref name="selectedGroup"/> highlights the active row;
    /// <paramref name="paletteFor"/> is the group whose palette is open. Interaction routes through
    /// <paramref name="cb"/>.</summary>
    public static Control GroupsPanel(SpatialScene scene, Guid? selectedGroup, Guid? paletteFor, GroupsPanelCallbacks cb)
    {
        var rows = new StackPanel { Spacing = 4, MinWidth = 210 };
        rows.Children.Add(new TextBlock
        {
            Text = "Groups", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            Margin = new Thickness(0, 0, 0, 2),
        });
        rows.Children.Add(new TextBlock
        {
            Text = "click a swatch to recolour — colours are stable", FontSize = 10.5, Foreground = FgDim,
            Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
        });

        foreach (SpatialGroup g in scene.Groups)
        {
            rows.Children.Add(GroupRow(g, scene.Rooms.Count(r => r.GroupId == g.Id), selectedGroup, cb));
            if (paletteFor == g.Id && !g.IsMain) rows.Children.Add(PaletteRow(g.Id, cb));
        }

        var panel = new Border
        {
            Background = new SolidColorBrush(LegendBg),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 24, 0), Child = rows,
        };
        panel.PointerPressed += (_, e) => e.Handled = true; // operating the panel never drags/deselects behind it
        return panel;
    }

    private static Control GroupRow(SpatialGroup g, int count, Guid? selectedGroup, GroupsPanelCallbacks cb)
    {
        Color c = Color.Parse(g.Color);
        var swatch = new Border
        {
            Width = 15, Height = 15, Background = new SolidColorBrush(c),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(g.IsMain ? 8 : 4), // main reads as the round "default" chip
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = g.IsMain ? null : new Cursor(StandardCursorType.Hand),
        };
        Guid id = g.Id;
        if (!g.IsMain)
            swatch.PointerPressed += (_, e) => { e.Handled = true; cb.TogglePalette(id); };

        var name = new TextBlock
        {
            Text = g.IsMain ? "main" : g.Name, FontFamily = Mono, FontSize = 11.5,
            Foreground = new SolidColorBrush(g.IsMain ? Color.Parse("#9AA6B8") : c),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0),
        };
        var tally = new TextBlock
        {
            Text = count.ToString(), FontFamily = Mono, FontSize = 11, Foreground = FgDim,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(swatch, 0); Grid.SetColumn(name, 1); Grid.SetColumn(tally, 2);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(swatch); grid.Children.Add(name); grid.Children.Add(tally);

        var row = new Border
        {
            Padding = new Thickness(6, 5), CornerRadius = new CornerRadius(7),
            Background = selectedGroup == id ? new SolidColorBrush(Color.FromArgb(0x1F, 0x6E, 0xA8, 0xFF)) : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = grid,
        };
        row.PointerPressed += (_, e) =>
        {
            if (e.Handled) return; // the swatch was clicked
            e.Handled = true;
            cb.SelectGroup(id);
        };
        return row;
    }

    private static Control PaletteRow(Guid group, GroupsPanelCallbacks cb)
    {
        var wrap = new WrapPanel { Margin = new Thickness(24, 2, 0, 6) };
        foreach (string hex in SpatialPalette.Colors)
        {
            string h = hex;
            var chip = new Border
            {
                Width = 17, Height = 17, Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.Parse(h)), CornerRadius = new CornerRadius(5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            chip.PointerPressed += (_, e) => { e.Handled = true; cb.Recolour(group, h); };
            wrap.Children.Add(chip);
        }
        return wrap;
    }
}
