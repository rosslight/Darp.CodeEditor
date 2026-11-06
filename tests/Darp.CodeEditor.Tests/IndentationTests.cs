using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Tests;

/// <summary>Indentation in the editor: the document's setting, Tab and Shift+Tab, and new lines.</summary>
[Collection(UiFixture.Collection)]
public sealed class IndentationTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Every_editor_of_a_document_indents_by_its_indentation_and_follows_a_change_at_once() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, "ab\n\tx");
            var first = new CodeEditor { Document = document };
            var second = new CodeEditor { Document = document };
            using var window = new EditorWindow(first, new StackPanel { Children = { first, second } });
            first.Selection = new TextSelection(2);

            // Spaces to the next stop of 4.
            window.Press(Key.Tab);
            document.Text.ShouldBe("ab  \n\tx");
            TabWidth(second).ShouldBe(4);

            document.Indentation = Indentation.Tabs(8);
            window.Layout();
            second.Focus();
            second.Selection = new TextSelection(4);
            window.Press(Key.Tab);

            document.Text.ShouldBe("ab  \t\n\tx");
            TabWidth(first).ShouldBe(8);
            TabWidth(second).ShouldBe(8);
        });

    [Fact]
    public Task Tab_and_Shift_Tab_indent_and_outdent_the_selected_lines() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("«a\n  b»\nc", CodeLanguage.Json);

            window.Press(Key.Tab);
            MarkedText.Of(window).ShouldBe("«    a\n      b»\nc");

            window.Press(Key.Tab, KeyModifiers.Shift);
            window.Press(Key.Tab, KeyModifiers.Shift);
            MarkedText.Of(window).ShouldBe("«a\nb»\nc");
        });

    /// <summary>How many spaces wide the editor draws the tab at the start of the document's second line.</summary>
    private static double TabWidth(CodeEditor editor)
    {
        TextView textView = EditorWindow.TextViewOf(editor);
        textView.EnsureVisualLines();
        double x = textView.GetVisualPosition(new TextViewPosition(2, 2), VisualYPosition.LineTop).X;
        return Math.Round(x / textView.WideSpaceWidth, 2);
    }
}
