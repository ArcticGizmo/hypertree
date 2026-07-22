using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Hypertree.Scopes;

namespace Hypertree.App.Views;

/// <summary>
/// The HUD overlay — the source-of-truth "where am I" (PLAN.md §3.4), rendered as a small MAP of the
/// 2-D structure rather than a single line: the day-to-day anchor row, and the scope hanging off the
/// current anchor above it, with "you are here" highlighted. Native Task View can't show the depth
/// axis, so this is what makes Model P legible. Borderless, transparent, click-through, never steals
/// focus; centered horizontally and sitting just above the primary taskbar. Flash-on-navigation (M1).
/// </summary>
internal sealed class HudWindow : Window
{
    private readonly Border _chip;
    private readonly DispatcherTimer _hideTimer;

    // Palette — a small, consistent set so the map reads as one thing.
    private static readonly IBrush Current = new SolidColorBrush(Color.Parse("#2D7D46")); // you-are-here (bright)
    private static readonly IBrush Owning  = new SolidColorBrush(Color.Parse("#38513F")); // current column while dived
    private static readonly IBrush Normal  = new SolidColorBrush(Color.Parse("#3A3A3A"));
    private static readonly IBrush Dim      = new SolidColorBrush(Color.Parse("#282828"));
    private static readonly IBrush FgBright = Brushes.White;
    private static readonly IBrush FgNormal = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private static readonly IBrush FgDim    = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private static readonly IBrush Accent   = new SolidColorBrush(Color.Parse("#6FD08C"));

    public HudWindow()
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsVisible = false;

        _chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEA, 0x18, 0x18, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9),
        };
        Content = _chip;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); IsVisible = false; };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MakeClickThrough();
    }

    /// <summary>Render <paramref name="map"/>, show the chip, and restart the auto-hide timer.</summary>
    public void Flash(NavMap map)
    {
        _chip.Child = BuildVisual(map);

        if (!IsVisible) Show(); // first show creates the handle (OnOpened → click-through)
        Reposition();
        Topmost = true;

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    // ── Map rendering ────────────────────────────────────────────────────────────

    private Control BuildVisual(NavMap map)
    {
        var root = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };

        // Scope row (above) — the depth axis. Shown whenever the current anchor has a scope; bright
        // when dived into it, dimmed when still on the top row (a visible dive target).
        if (map.ScopeDesktops is not null)
        {
            var scopeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            scopeRow.Children.Add(new TextBlock
            {
                Text = "▸ " + map.ScopeName,
                Foreground = map.InScope ? Accent : FgDim,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            foreach (var d in map.ScopeDesktops)
                scopeRow.Children.Add(Pill(d.Label, d.IsCurrent ? Current : (map.InScope ? Normal : Dim),
                                                    d.IsCurrent ? FgBright : (map.InScope ? FgNormal : FgDim), small: true));
            root.Children.Add(scopeRow);

            // Connector: a downward chevron showing the scope hangs beneath the anchor row.
            root.Children.Add(new TextBlock
            {
                Text = "▾", Foreground = FgDim, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, -2),
            });
        }

        // Anchor row (the day-to-day line).
        var anchorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var a in map.Anchors)
        {
            IBrush bg = a.IsCurrentColumn ? (map.InScope ? Owning : Current) : Normal;
            IBrush fg = a.IsCurrentColumn && !map.InScope ? FgBright : FgNormal;
            anchorRow.Children.Add(Pill(a.HasScope ? a.Label + "  ▾" : a.Label, bg, fg, small: false));
        }
        root.Children.Add(anchorRow);

        return root;
    }

    private static Border Pill(string text, IBrush bg, IBrush fg, bool small) => new()
    {
        Background = bg,
        CornerRadius = new CornerRadius(6),
        Padding = small ? new Thickness(9, 3) : new Thickness(12, 5),
        Child = new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontSize = small ? 12 : 13,
            FontWeight = small ? FontWeight.Normal : FontWeight.SemiBold,
        },
    };

    // ── Placement: centered horizontally, just above the primary taskbar ───────────

    private void Reposition()
    {
        Screen? screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null) return;

        double scale = screen.Scaling;
        int w = (int)Math.Ceiling(Bounds.Width * scale);
        int h = (int)Math.Ceiling(Bounds.Height * scale);

        PixelRect full = screen.Bounds;
        PixelRect work = screen.WorkingArea;   // excludes the taskbar

        int x = full.X + (full.Width - w) / 2;
        int y = work.Bottom - h - 8;           // sit just above the taskbar, centered
        Position = new PixelPoint(x, y);
    }

    // ── Click-through + no focus steal ─────────────────────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern long GetWindowLongPtr(nint hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern long SetWindowLongPtr(nint hWnd, int nIndex, long dwNewLong);

    private void MakeClickThrough()
    {
        IPlatformHandle? handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == 0) return;
        long ex = GetWindowLongPtr(handle.Handle, GWL_EXSTYLE);
        SetWindowLongPtr(handle.Handle, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}
