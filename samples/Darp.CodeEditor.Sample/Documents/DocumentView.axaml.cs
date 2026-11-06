using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Darp.CodeEditor;
using FluentAvalonia.UI.Controls;
using Sample.Localization;

namespace Sample.Documents;

/// <summary>
/// The document page: one editor for every open document, and a status bar. Its public methods are the editor's
/// commands, which the title bar calls.
/// </summary>
public sealed partial class DocumentView : UserControl
{
    private static readonly Indentation[] s_indentations =
    [
        Indentation.Spaces(2),
        Indentation.Spaces(4),
        Indentation.Tabs(4),
    ];

    private Workspace? _workspace;

    /// <summary>The document the editor shows.</summary>
    private OpenDocument? _shown;

    public DocumentView()
    {
        InitializeComponent();
        // The number box handles Enter itself, to commit its value.
        LineNumber.AddHandler(KeyUpEvent, OnLineNumberKeyUp, handledEventsToo: true);
    }

    /// <summary>The status bar's text for a document's indentation: the texts for spaces and tabs, and the indentation.</summary>
    public static FuncMultiValueConverter<object?, string?> IndentationText { get; } =
        new(values =>
            values.ToList() is [string spaces, string tabs, Indentation indentation]
                ? Strings.Current.Format(indentation.UseTabs ? tabs : spaces, indentation.Size)
                : null
        );

    public void Undo()
    {
        Editor.Document?.Undo();
        Editor.Focus();
    }

    public void Redo()
    {
        Editor.Document?.Redo();
        Editor.Focus();
    }

    public void Find() => Editor.OpenSearch();

    public void Replace() => Editor.OpenSearch(replace: true);

    public void OpenGoToLine() => CaretButton.Flyout?.ShowAt(CaretButton);

    public void InsertDateTime()
    {
        // Replaces the selection, as typing does, in one step that the user can undo.
        Editor.Document?.Replace(Editor.Selection.Range, DateTime.Now.ToString("g", Strings.Current.Culture));
        Editor.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_workspace is not null)
            _workspace.PropertyChanged -= OnWorkspaceChanged;
        _workspace = (DataContext as MainViewModel)?.Workspace;
        if (_workspace is not null)
            _workspace.PropertyChanged += OnWorkspaceChanged;
        Show(_workspace?.Current);
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.Current))
            Show(_workspace?.Current);
    }

    /// <summary>
    /// Shows <paramref name="document"/> where the user left it. The caret, the selection and the scroll position
    /// belong to the editor, so the view saves them for the document it leaves and restores them for the one it shows.
    /// </summary>
    private void Show(OpenDocument? document)
    {
        if (document == _shown)
            return;
        if (_shown is not null)
            _shown.ViewState = Editor.SaveViewState();
        _shown = document;
        Editor.Document = document?.Document;
        if (document?.ViewState is { } state)
            Editor.RestoreViewState(state);
        Editor.Focus();
    }

    private void OnGoToLineOpened(object? sender, EventArgs e)
    {
        if (Editor.Document is not { } document)
            return;
        TextSnapshot snapshot = document.CurrentSnapshot;
        LineNumber.Maximum = snapshot.GetPosition(snapshot.Length).Line;
        LineNumber.Value = Editor.CaretPosition.Line;
        // Once the flyout shows, which is after it opened.
        Dispatcher.Post(() => LineNumber.Focus());
    }

    private void OnLineNumberKeyUp(object? sender, KeyEventArgs e)
    {
        // The key going down committed the value.
        if (e.Key == Key.Enter)
            GoToLine();
    }

    private void OnGoToLineConfirmed(object? sender, RoutedEventArgs e) => GoToLine();

    private void GoToLine()
    {
        CaretButton.Flyout?.Hide();
        if (Editor.Document is not { } document || double.IsNaN(LineNumber.Value))
            return;
        int offset = document.CurrentSnapshot.GetOffset(new TextPosition((int)LineNumber.Value, 1));
        Editor.Selection = new TextSelection(offset);
        Editor.ScrollTo(new TextRange(offset, 0));
        Editor.Focus();
    }

    private void OnIndentationMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not FAMenuFlyout menu || Editor.Document is not { } document)
            return;
        menu.Items.Clear();
        foreach (Indentation indentation in s_indentations)
        {
            var item = new FARadioMenuFlyoutItem
            {
                Text = Strings.Current.Format(
                    indentation.UseTabs ? Strings.Current.IndentTabs : Strings.Current.IndentSpaces,
                    indentation.Size
                ),
                IsChecked = document.Indentation == indentation,
            };
            // Every editor that shows the document indents so from now on. The text stays as it is.
            item.Click += (_, _) => document.Indentation = indentation;
            menu.Items.Add(item);
        }
    }
}
