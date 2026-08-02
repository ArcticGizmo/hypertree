using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Hypertree.Loadouts;

namespace Hypertree.App.Views;

/// <summary>
/// Fills a loadout's <c>{name}</c> variables before it's applied (docs/design/session-restore.md): one field
/// per variable, prefilled with its declared default, so the same loadout can outfit any project. Hosted as a
/// card on the <see cref="OverlayStage"/>. A <b>folder</b> variable (and the built-in <c>{dir}</c>) gets a
/// type-ahead of matching directories plus a <b>Browse…</b> button that opens the native folder picker; a
/// plain variable is a text box. Every field is required — a blank would launch a broken command. Esc
/// cancels; Ctrl+Enter runs.
/// </summary>
internal sealed class VariableFillContent : IStageContent
{
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush CardStroke = new SolidColorBrush(Color.Parse("#2A3444"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#999"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));

    private readonly Action<IReadOnlyDictionary<string, string>> _onFill;
    // Each field: its variable name, how to read its current value, and how to focus it.
    private readonly List<(string Name, Func<string> Value, Action Focus)> _fields = new();
    private readonly PromptButton _ok;
    private readonly PromptButton _cancel;
    private readonly Control _root;
    private OverlayStage? _stage;
    private bool _submitted;

    public VariableFillContent(IReadOnlyList<VariableSpec> specs, string loadoutName,
                               Action<IReadOnlyDictionary<string, string>> onFill)
    {
        _onFill = onFill;

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = $"Apply “{loadoutName}”", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Fill in the loadout's variables — its commands use these before it builds the branch.",
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
        });

        foreach (VariableSpec spec in specs)
            panel.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = Label(spec), Foreground = spec.IsDir ? Accent : Muted, FontSize = 11 },
                    spec.Kind == VariableKind.Folder ? FolderField(spec) : TextField(spec),
                },
            });

        _ok = new PromptButton("Apply");
        _ok.Invoked += Submit;
        _cancel = new PromptButton("Cancel");
        _cancel.Invoked += Cancel;
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
            Children = { _cancel, _ok },
        });

        var card = new Border
        {
            Background = CardBg, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Width = 500, Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
        };
        _root = new Grid { Children = { card } };
        _root.AddHandler(InputElement.KeyDownEvent, OnTunnelKey, RoutingStrategies.Tunnel);
    }

    // A plain text variable.
    private Control TextField(VariableSpec spec)
    {
        var box = new TextBox { Text = spec.Default ?? "", PlaceholderText = $"value for {{{spec.Name}}}" };
        _fields.Add((spec.Name, () => box.Text ?? "", () => box.Focus()));
        return box;
    }

    // A folder variable: a type-ahead of matching directories, plus a Browse… button.
    private Control FolderField(VariableSpec spec)
    {
        var box = new AutoCompleteBox
        {
            Text = spec.Default ?? "",
            PlaceholderText = @"a folder — e.g. C:\repos\app",
            FilterMode = AutoCompleteFilterMode.None,       // the populator already returns matches
            MinimumPrefixLength = 0,
            IsTextCompletionEnabled = false,                 // suggest, don't auto-type into the box
            AsyncPopulator = SuggestFolders,
        };
        _fields.Add((spec.Name, () => box.Text ?? "", () => box.Focus()));

        var browse = new PromptButton("Browse…");
        browse.Invoked += () => _ = Browse(box);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.ColumnSpacing = 8;
        Grid.SetColumn(box, 0);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(box);
        grid.Children.Add(browse);
        return grid;
    }

    // Directory suggestions for what's typed so far: children of the folder when the text ends in a
    // separator (or is itself a folder), otherwise siblings whose name starts with the last segment. Off the
    // UI thread — a directory listing can touch a slow drive. Any error yields no suggestions.
    private static Task<IEnumerable<object>> SuggestFolders(string? text, CancellationToken ct) => Task.Run<IEnumerable<object>>(() =>
    {
        try
        {
            string input = (text ?? "").Trim();
            if (input.Length == 0) return Array.Empty<object>();

            string parent, prefix;
            if (input.EndsWith('\\') || input.EndsWith('/')) { parent = input; prefix = ""; }
            else { parent = System.IO.Path.GetDirectoryName(input) ?? ""; prefix = System.IO.Path.GetFileName(input); }
            if (parent.Length == 0 || !Directory.Exists(parent)) return Array.Empty<object>();

            return Directory.EnumerateDirectories(parent)
                .Where(d => System.IO.Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .Cast<object>()
                .ToArray();
        }
        catch { return Array.Empty<object>(); }
    }, ct);

    // The native folder picker, parented to the overlay host. On pick, the chosen path fills the box.
    private static async Task Browse(AutoCompleteBox box)
    {
        if (TopLevel.GetTopLevel(box) is not { StorageProvider: { } storage }) return;

        IStorageFolder? start = null;
        try { if (Directory.Exists(box.Text)) start = await storage.TryGetFolderFromPathAsync(box.Text!); }
        catch { /* a bad path just means no start location */ }

        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder", AllowMultiple = false, SuggestedStartLocation = start,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { Length: > 0 } path) box.Text = path;
    }

    private static string Label(VariableSpec s) =>
        s.IsDir ? $"{s.Name}   —   the htree CLI fills this from the current directory"
        : s.Kind == VariableKind.Folder ? $"{s.Name}   (folder)"
        : s.Name;

    public Control View => _root;
    public StageLayer Layer => StageLayer.Card;
    public bool DismissOnDeactivate => false; // opening the folder picker deactivates us — must not dismiss
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        _submitted = false;
        if (_fields.Count > 0) _fields[0].Focus();
    }

    public void OnRemoved() { }
    public void OnKey(KeyEventArgs e) { }

    private void OnTunnelKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Submit(); e.Handled = true; }
    }

    private void Submit()
    {
        if (_submitted) return;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, Func<string> value, Action focus) in _fields)
        {
            string v = value().Trim();
            if (v.Length == 0) { focus(); return; } // every variable is required
            values[name] = v;
        }

        _submitted = true;
        _onFill(values);
        if (_stage?.Current == this) _stage.Back();
    }

    private void Cancel() => _stage?.Back();
}
