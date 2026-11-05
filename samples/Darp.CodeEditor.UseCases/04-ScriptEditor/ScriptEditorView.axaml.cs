using Avalonia.Controls;
using Darp.CodeEditor;

namespace UseCases.ScriptEditor;

/// <summary>The script editor page: editor, Run and Stop, output and error list.</summary>
public partial class ScriptEditorView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ScriptEditorView() => InitializeComponent();

    // Moving the caret is the view's job: the view model knows the document, not the editors that show it.
    private void OnErrorSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Errors.SelectedItem is not Diagnostic diagnostic)
            return;
        Editor.Selection = new TextSelection(diagnostic.Range.Start, diagnostic.Range.End);
        Editor.ScrollTo(diagnostic.Range);
        Editor.Focus();
    }

    // For users who don't know Ctrl+F.
    private void OnFind(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Editor.OpenSearch();
}
