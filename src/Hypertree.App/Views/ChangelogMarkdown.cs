using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Hypertree.App.Views;

/// <summary>
/// Loads the embedded <c>CHANGELOG.md</c> and renders its (lightweight) markdown into a stacked column of
/// themed text. Shared by the Settings "View changelog" window and the post-update <see cref="ChangelogWindow"/>
/// so the two read identically. Handles just the subset the changelog uses: <c>## </c>/<c>### </c>
/// headings, <c>-</c>/<c>*</c> bullets, <c>&gt; </c> quotes, <c>---</c> rules, and inline emphasis/links.
/// Colours match <see cref="SettingsWindow"/> so every surface reads as one dark app.
/// </summary>
internal static class ChangelogMarkdown
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly IBrush Rule = new SolidColorBrush(Color.Parse("#2A3444"));

    /// <summary>Reads the changelog embedded at build time (csproj: <c>Hypertree.CHANGELOG.md</c>), or null.</summary>
    public static string? LoadEmbedded()
    {
        try
        {
            using var s = typeof(ChangelogMarkdown).Assembly.GetManifestResourceStream("Hypertree.CHANGELOG.md");
            if (s is null) return null;
            using var reader = new StreamReader(s);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>Appends one control per markdown line into <paramref name="page"/>.</summary>
    public static void Render(StackPanel page, IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## "))
                page.Children.Add(new TextBlock
                {
                    Text = StripInline(line[3..]), FontSize = 13, FontWeight = FontWeight.Bold,
                    Foreground = Accent, Margin = new Thickness(0, 10, 0, 4),
                });
            else if (line.StartsWith("### "))
                page.Children.Add(new TextBlock
                {
                    Text = StripInline(line[4..]), FontSize = 12, FontWeight = FontWeight.Bold,
                    Foreground = Ink, Margin = new Thickness(0, 6, 0, 2),
                });
            else if (line.StartsWith("# ")) { /* the H1 title is redundant here */ }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                page.Children.Add(Body("•  " + StripInline(line[2..]), new Thickness(8, 1, 0, 1)));
            else if (line == "---")
                page.Children.Add(new Border { Height = 1, Background = Rule, Margin = new Thickness(0, 8) });
            else if (line.StartsWith("> "))
                page.Children.Add(new TextBlock
                {
                    Text = StripInline(line[2..]), TextWrapping = TextWrapping.Wrap, FontSize = 12,
                    FontStyle = FontStyle.Italic, Foreground = Muted, Margin = new Thickness(12, 0, 0, 4),
                });
            else if (line.Trim().Length > 0)
                page.Children.Add(Body(StripInline(line), new Thickness(0, 1)));
        }
    }

    private static TextBlock Body(string text, Thickness margin) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ink, Margin = margin,
    };

    /// <summary>Strips inline markdown (bold/italic/code/links) down to its display text.</summary>
    public static string StripInline(string text)
    {
        text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.*?)__", "$1");
        text = Regex.Replace(text, @"\*(.*?)\*", "$1");
        text = Regex.Replace(text, @"_(.*?)_", "$1");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text;
    }
}
