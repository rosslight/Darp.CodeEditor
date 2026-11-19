using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Themes.Simple;
using Darp.CodeEditor;
using Sample.Documents;
using Sample.Localization;

namespace Sample;

/// <summary>
/// A window in one of Avalonia's own themes, Fluent or Simple, light or dark, with an editor of the app's open
/// documents: to see the editor and its popups under each theme. The app's main window keeps FluentAvalonia, which its
/// controls need; this window holds its theme in its own styles, which its popups take too, as they are in its overlay
/// layer.
/// </summary>
public sealed class ThemeWindow : Window
{
    private readonly CodeEditor _editor = new() { Padding = new Thickness(8, 6, 0, 0) };
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(8, 4),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public ThemeWindow(Workspace workspace, ThemeChoiceOfWindow choice)
    {
        Title = Strings.Default.FormatThemeWindowTitle(choice.Name);
        Width = 1000;
        Height = 680;
        Styles.Add(choice.Fluent ? new FluentTheme() : new SimpleTheme());
        RequestedThemeVariant = choice.Variant;

        var documents = new ComboBox
        {
            ItemsSource = workspace.Documents,
            SelectedItem = workspace.Current,
            ItemTemplate = new FuncDataTemplate<OpenDocument>((document, _) => new TextBlock { Text = document?.Name }),
            Margin = new Thickness(8, 6),
            MinWidth = 200,
        };
        documents.SelectionChanged += (_, _) =>
        {
            _editor.Document = (documents.SelectedItem as OpenDocument)?.Document;
            _editor.Focus();
        };
        _editor.Document = workspace.Current?.Document;
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == CodeEditor.CaretPositionProperty || e.Property == CodeEditor.PendingKeysProperty)
                UpdateStatus();
        };
        UpdateStatus();

        var root = new DockPanel();
        DockPanel.SetDock(documents, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(documents);
        root.Children.Add(_status);
        root.Children.Add(_editor);
        Content = root;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _editor.Focus();
    }

    /// <summary>The caret's place, or which first key of a sequence waits for its second.</summary>
    private void UpdateStatus() =>
        _status.Text = _editor.PendingKeys is { } pending
            ? Strings.Default.FormatWaitingForChord(pending)
            : Strings.Default.FormatCaretPosition(_editor.CaretPosition.Line, _editor.CaretPosition.Column);
}

/// <summary>One of the themes a <see cref="ThemeWindow"/> tries: Fluent or Simple, in a variant.</summary>
public sealed record ThemeChoiceOfWindow(string Name, bool Fluent, ThemeVariant Variant)
{
    public static IReadOnlyList<ThemeChoiceOfWindow> All { get; } =
    [
        new("Fluent Light", Fluent: true, ThemeVariant.Light),
        new("Fluent Dark", Fluent: true, ThemeVariant.Dark),
        new("Simple Light", Fluent: false, ThemeVariant.Light),
        new("Simple Dark", Fluent: false, ThemeVariant.Dark),
    ];
}
