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
    private static readonly IBrush Ink = Palette.InkBrush;
    private static readonly IBrush Muted = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;
    private static readonly IBrush Rule = Palette.StrokeBrush;

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

    // Placeholder delimiters for parked code spans: control characters carry no markdown meaning and
    // cannot occur in changelog prose, so restoring them can't collide with real text the way a printable
    // marker could.
    private const string CodeOpen = "\u0001";
    private const string CodeClose = "\u0002";
    private static readonly Regex CodeSpan = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex CodeMark = new(CodeOpen + @"(\d+)" + CodeClose, RegexOptions.Compiled);

    /// <summary>Strips inline markdown (bold/italic/code/links) down to its display text.</summary>
    /// <remarks>
    /// Code spans are lifted out before the emphasis passes and put back afterwards. Without that, the
    /// emphasis rules chew through the middle of whatever is inside the backticks — a changelog line
    /// mentioning <c>HYPERTREE_STATE_DIR</c> rendered as "HYPERTREESTATEDIR", because its two underscores
    /// looked like a pair of italic markers.
    /// <para>
    /// Underscore emphasis must also sit on a word boundary, as CommonMark requires. That is the rule that
    /// keeps any <c>snake_case</c> identifier intact even outside backticks. Asterisks keep the looser
    /// treatment: intra-word <c>*</c> is legal emphasis and doesn't turn up inside identifiers.
    /// </para>
    /// </remarks>
    public static string StripInline(string text)
    {
        var code = new List<string>();
        text = CodeSpan.Replace(text, m =>
        {
            code.Add(m.Groups[1].Value);
            return CodeOpen + (code.Count - 1) + CodeClose;
        });

        text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
        text = Regex.Replace(text, @"(?<![A-Za-z0-9])__(.+?)__(?![A-Za-z0-9])", "$1");
        text = Regex.Replace(text, @"\*(.*?)\*", "$1");
        text = Regex.Replace(text, @"(?<![A-Za-z0-9])_(.+?)_(?![A-Za-z0-9])", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");

        return CodeMark.Replace(text, m => code[int.Parse(m.Groups[1].Value)]);
    }
}
