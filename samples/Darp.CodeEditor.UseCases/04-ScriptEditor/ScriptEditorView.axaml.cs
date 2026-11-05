using Avalonia.Controls;
using Darp.CodeEditor;

namespace UseCases.ScriptEditor;

/// <summary>The script editor page: editor, Run and Stop, output and error list.</summary>
public partial class ScriptEditorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ScriptEditorView() => InitializeComponent();

    // Moving the caret is the view's job: the view model knows the document, not the editors that show it.
    // A diagnostic refers to the text the server analyzed; the user may have typed since.
    private void OnErrorSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Errors.SelectedItem is not Diagnostic diagnostic || Editor.Document is not { } document)
            return;
        TextRange range = document.MapToCurrent(diagnostic.Snapshot, diagnostic.Range);
        Editor.Selection = new TextSelection(range.Start, range.End);
        Editor.ScrollTo(range);
        Editor.Focus();
    }

    // Inserts at the caret, or replaces the selection, in one step the user can undo.
    private void OnInsertTemplate(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Editor.Document?.Replace(Editor.Selection.Range, "SetCalculation(x => x);\n");

    // For users who don't know Ctrl+F.
    private void OnFind(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Editor.OpenSearch();
}
