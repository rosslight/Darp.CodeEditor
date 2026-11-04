using Avalonia.Controls;
using Darp.CodeEditor;

namespace UseCases.PacketScripts;

/// <summary>The packet script page: editor, Run and Stop, output and error list.</summary>
public partial class PacketScriptView : UserControl
{
    /// <summary>Creates the view.</summary>
    public PacketScriptView() => InitializeComponent();

    // Moving the caret is the view's job: the view model knows the document, not the editors that show it.
    private void OnErrorSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Errors.SelectedItem is not Diagnostic diagnostic)
            return;
        Editor.Selection = diagnostic.Range;
        Editor.ScrollTo(diagnostic.Range.Start);
        Editor.Focus();
    }
}
