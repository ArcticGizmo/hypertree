using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hypertree.Launch;
using Hypertree.Recipes;

namespace Hypertree.App.Views;

/// <summary>
/// The graphical recipe builder (docs/design/session-restore.md): a branch drawn as a stack of <b>desktop</b>
/// rows, each split into a slot per physical <b>monitor</b>, and each slot holding an ordered list of
/// <b>commands</b> to run there when the recipe is applied. Add / remove desktops, and add / edit / remove
/// commands per monitor. Everything is edited on an in-memory working copy; Save hands it back, Cancel drops it.
///
/// It's a full-surface stage content that rebuilds its body on every (re)presentation, so returning from a
/// pushed command form reflects the change without any manual refresh plumbing.
/// </summary>
internal sealed class RecipeBuilderContent : IStageContent
{
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#12161F"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#171C27"));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.Parse("#2A3444"));
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E8EDF5"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA6B8"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#6EA8FF"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E86A6A"));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly Recipe _recipe;      // the working copy — mutated in place, handed back on Save
    private readonly int _monitors;
    private readonly Action<Recipe> _onSave;

    private readonly TextBox _name;
    private readonly StackPanel _body;
    private readonly Control _root;
    private OverlayStage? _stage;

    public RecipeBuilderContent(Recipe working, int monitors, Action<Recipe> onSave)
    {
        _recipe = working;
        _monitors = Math.Max(1, monitors);
        _onSave = onSave;

        _name = new TextBox { Text = _recipe.Name, Width = 320, FontFamily = Mono, FontSize = 14, Watermark = "recipe name" };
        _body = new StackPanel { Spacing = 10 };

        var save = Btn("Save recipe", Save, accent: true);
        var cancel = Btn("Cancel", () => _stage?.Back());
        var addDesktop = Btn("＋ Add desktop", AddDesktop);

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children =
        {
            new TextBlock { Text = "Name", Foreground = Muted, FontFamily = Mono, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
            _name,
        } };

        var inner = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Build recipe", Foreground = Ink, FontFamily = Mono, FontSize = 18, FontWeight = FontWeight.SemiBold },
                nameRow,
                new TextBlock { Text = $"{_monitors} monitor{(_monitors == 1 ? "" : "s")} per desktop · commands run top-to-bottom when the recipe is applied", Foreground = Muted, FontFamily = Mono, FontSize = 12 },
                new ScrollViewer { Content = _body, MaxHeight = 440, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                addDesktop,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } },
            },
        };

        var card = new Border
        {
            Background = CardBg, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 20),
            MaxWidth = 900, MaxHeight = 680,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = inner,
        };
        _root = new Panel { Children = { card } };
    }

    // ── IStageContent ────────────────────────────────────────────────────────────
    public Control View => _root;
    public StageLayer Layer => StageLayer.FullSurface;
    public bool DismissOnDeactivate => false;
    public bool DismissOnClickAway => false;

    public void OnPresented(OverlayStage stage)
    {
        _stage = stage;
        BuildBody(); // rebuild every time we're (re)shown — reflects a command added/edited on a pushed card
    }

    public void OnRemoved() { }

    public void OnKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _stage?.Back(); e.Handled = true; } // Cancel
    }

    // ── The grid body ─────────────────────────────────────────────────────────────

    private void BuildBody()
    {
        _body.Children.Clear();
        if (_recipe.Desktops.Count == 0)
            _body.Children.Add(new TextBlock { Text = "No desktops yet — add one below.", Foreground = Muted, FontFamily = Mono, FontSize = 13, Margin = new Thickness(2, 6) });
        for (int di = 0; di < _recipe.Desktops.Count; di++)
            _body.Children.Add(DesktopSection(di));
    }

    private Control DesktopSection(int di)
    {
        RecipeDesktop d = _recipe.Desktops[di];

        var label = new TextBox { Text = d.Label, Width = 220, FontFamily = Mono, FontSize = 14 };
        label.TextChanged += (_, _) => d.Label = label.Text ?? "";

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = $"Desktop {di + 1}", Foreground = Accent, FontFamily = Mono, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(label);
        Grid.SetColumn(left, 0);
        var remove = Btn("× Remove desktop", () => { _recipe.Desktops.RemoveAt(di); BuildBody(); }, danger: true);
        Grid.SetColumn(remove, 1);
        header.Children.Add(left);
        header.Children.Add(remove);

        var monitors = new StackPanel { Spacing = 8, Margin = new Thickness(10, 0, 0, 0) };
        for (int m = 1; m <= _monitors; m++)
            monitors.Children.Add(MonitorSection(d, m));

        return new Border
        {
            Background = Panel, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12),
            Child = new StackPanel { Children = { header, monitors } },
        };
    }

    private Control MonitorSection(RecipeDesktop d, int m)
    {
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        foreach (RecipeStep step in d.Steps.Where(s => s.Placement.Monitor == m).ToList())
            list.Children.Add(CommandRow(d, step));
        list.Children.Add(Btn("＋ add command", () => AddCommand(d, m)));

        return new StackPanel
        {
            Children =
            {
                new TextBlock { Text = $"Monitor {m}", Foreground = Muted, FontFamily = Mono, FontSize = 12, FontWeight = FontWeight.SemiBold },
                list,
            },
        };
    }

    private Control CommandRow(RecipeDesktop d, RecipeStep step)
    {
        string line = CommandLine.Join(step.Target, step.Arguments);
        string caption = string.IsNullOrWhiteSpace(step.Name) ? line : $"{step.Name}   {line}";
        if (!string.IsNullOrWhiteSpace(step.WorkingDirectory)) caption += $"   · in {step.WorkingDirectory}";

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var text = new TextBlock { Text = caption, Foreground = Ink, FontFamily = Mono, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var edit = Btn("edit", () => EditCommand(step));
        var del = Btn("×", () => { d.Steps.Remove(step); BuildBody(); }, danger: true);
        Grid.SetColumn(text, 0); Grid.SetColumn(edit, 1); Grid.SetColumn(del, 2);
        grid.Children.Add(text); grid.Children.Add(edit); grid.Children.Add(del);
        return grid;
    }

    // ── Mutations ─────────────────────────────────────────────────────────────────

    private void AddDesktop()
    {
        _recipe.Desktops.Add(new RecipeDesktop { Label = $"desktop {_recipe.Desktops.Count + 1}" });
        BuildBody();
    }

    private void AddCommand(RecipeDesktop d, int m)
    {
        _stage?.Present(new CommandFormContent(res =>
        {
            var (target, args) = CommandLine.Split(res.CommandLine);
            d.Steps.Add(new RecipeStep
            {
                Name = res.Name.Length > 0 ? res.Name : target,
                Target = target,
                Arguments = args.Length > 0 ? args : null,
                WorkingDirectory = res.WorkingDirectory,
                Placement = new Placement { Desktop = d.Label, Monitor = m },
            });
        }, title: $"Add command · Monitor {m}"));
    }

    private void EditCommand(RecipeStep step)
    {
        _stage?.Present(new CommandFormContent(res =>
        {
            var (target, args) = CommandLine.Split(res.CommandLine);
            step.Name = res.Name.Length > 0 ? res.Name : target;
            step.Target = target;
            step.Arguments = args.Length > 0 ? args : null;
            step.WorkingDirectory = res.WorkingDirectory;
        }, title: "Edit command",
           name: step.Name, commandLine: CommandLine.Join(step.Target, step.Arguments), workingDirectory: step.WorkingDirectory));
    }

    private void Save()
    {
        string name = _name.Text?.Trim() ?? "";
        if (name.Length == 0) { _name.Focus(); return; } // a recipe needs a name
        _recipe.Name = name;

        // Keep each step's placement label in step with its desktop (a rename could have left it stale), then
        // hand the finished recipe back to the App to persist.
        foreach (RecipeDesktop d in _recipe.Desktops)
            foreach (RecipeStep s in d.Steps)
                s.Placement.Desktop = d.Label;
        _onSave(_recipe);
    }

    // ── Small buttons ─────────────────────────────────────────────────────────────

    private Button Btn(string text, Action onClick, bool accent = false, bool danger = false)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(10, 5), FontFamily = Mono, FontSize = 12,
            Foreground = danger ? Red : accent ? Accent : Ink,
            Background = new SolidColorBrush(Color.Parse("#1B2230")),
            BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        };
        b.Click += (_, _) => onClick();
        return b;
    }
}
