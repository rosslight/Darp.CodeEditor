using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.Editing;
using Shouldly;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The editing helpers, driven through keyboard input into a real editor. In the texts, <c>|</c> marks the caret and
/// <c>«…»</c> the selection.
/// </summary>
public sealed class EditingHelpersTests
{
    private static readonly LanguageConfiguration s_cLike = LanguageConfiguration.Default with { LineComment = "//" };

    [AvaloniaFact]
    public void Enter_keeps_the_indentation_of_the_line()
    {
        using var editor = new EditorUnderTest("    a|b");
        editor.Press(Key.Enter);
        editor.Text.ShouldBe("    a\n    |b");
    }

    [AvaloniaFact]
    public void Enter_after_an_opening_bracket_indents_one_level_more()
    {
        using var editor = new EditorUnderTest("  if (x) {|");
        editor.Press(Key.Enter);
        editor.Text.ShouldBe("  if (x) {\n      |");
    }

    [AvaloniaFact]
    public void Enter_between_brackets_moves_the_closing_bracket_to_its_own_line()
    {
        using var editor = new EditorUnderTest("  call({|})");
        editor.Press(Key.Enter);
        editor.Text.ShouldBe("  call({\n      |\n  })");
    }

    [AvaloniaFact]
    public void A_closing_bracket_on_an_empty_line_takes_the_indentation_of_its_opening_bracket()
    {
        using var editor = new EditorUnderTest("  if (x) {\n      y();\n          |");
        editor.Type("}");
        editor.Text.ShouldBe("  if (x) {\n      y();\n  }|");
    }

    [AvaloniaFact]
    public void Undo_after_an_outdented_closing_bracket_restores_the_line()
    {
        using var editor = new EditorUnderTest("{\n        |");
        editor.Type("}");
        editor.Text.ShouldBe("{\n}|");
        editor.Press(Key.Z, KeyModifiers.Control);
        editor.Text.ShouldBe("{\n        |");
    }

    [AvaloniaFact]
    public void An_opening_bracket_closes_itself_and_typing_the_closer_steps_over_it()
    {
        using var editor = new EditorUnderTest("x = |;");
        editor.Type("(");
        editor.Text.ShouldBe("x = (|);");
        editor.Type("a)");
        editor.Text.ShouldBe("x = (a)|;");
    }

    [AvaloniaFact]
    public void Undo_removes_the_closer_with_the_typed_bracket()
    {
        using var editor = new EditorUnderTest("x = |");
        editor.Type("[");
        editor.Text.ShouldBe("x = [|]");
        editor.Press(Key.Z, KeyModifiers.Control);
        editor.Text.ShouldBe("x = |");
    }

    [AvaloniaFact]
    public void Backspace_in_an_empty_pair_removes_both_brackets()
    {
        using var editor = new EditorUnderTest("x = |");
        editor.Type("{");
        editor.Text.ShouldBe("x = {|}");
        editor.Press(Key.Back);
        editor.Text.ShouldBe("x = |");
    }

    [AvaloniaFact]
    public void A_bracket_typed_before_a_word_does_not_close()
    {
        using var editor = new EditorUnderTest("|foo");
        editor.Type("(");
        editor.Text.ShouldBe("(|foo");
    }

    [AvaloniaFact]
    public void A_quote_typed_after_a_word_does_not_close()
    {
        using var editor = new EditorUnderTest("don|");
        editor.Type("'");
        editor.Text.ShouldBe("don'|");
    }

    [AvaloniaFact]
    public void A_quote_typed_in_code_closes_and_steps_over()
    {
        using var editor = new EditorUnderTest("x = |");
        editor.Type("\"");
        editor.Text.ShouldBe("x = \"|\"");
        editor.Type("hi\"");
        editor.Text.ShouldBe("x = \"hi\"|");
    }

    [AvaloniaFact]
    public void Typing_an_opening_bracket_around_a_selection_surrounds_it()
    {
        using var editor = new EditorUnderTest("x = «abc»;");
        editor.Type("(");
        editor.Text.ShouldBe("x = («abc»);");
    }

    [AvaloniaFact]
    public void Ctrl_slash_comments_the_selected_lines_at_their_smallest_indentation_and_back()
    {
        using var editor = new EditorUnderTest("«    a\n\n      b\n»c", s_cLike);
        editor.Press(Key.Oem2, KeyModifiers.Control, "/");
        editor.Plain.ShouldBe("    // a\n\n    //   b\nc");
        editor.Press(Key.Oem2, KeyModifiers.Control, "/");
        editor.Plain.ShouldBe("    a\n\n      b\nc");
    }

    [AvaloniaFact]
    public void Ctrl_slash_uses_the_block_comment_when_the_language_has_no_line_comment()
    {
        using var editor = new EditorUnderTest(
            "x = «1 + 2»;",
            LanguageConfiguration.Default with
            {
                BlockComment = new BlockComment("/*", "*/"),
            }
        );
        editor.Press(Key.Oem2, KeyModifiers.Control, "/");
        editor.Plain.ShouldBe("x = /*1 + 2*/;");
    }

    [AvaloniaFact]
    public void Ctrl_slash_on_a_blank_line_inserts_an_empty_block_comment()
    {
        using var editor = new EditorUnderTest(
            "    |",
            LanguageConfiguration.Default with
            {
                BlockComment = new BlockComment("/*", "*/"),
            }
        );
        editor.Press(Key.Oem2, KeyModifiers.Control, "/");
        editor.Plain.ShouldBe("    /**/");
    }

    [AvaloniaFact]
    public void The_helpers_leave_read_only_text_alone()
    {
        using var editor = new EditorUnderTest("«a\nb»", s_cLike);
        editor.Editor.IsReadOnly = true;
        editor.Press(Key.Oem2, KeyModifiers.Control, "/");
        editor.Type("(");
        editor.Press(Key.Enter);
        editor.Plain.ShouldBe("a\nb");
    }

    [AvaloniaFact]
    public async Task A_bundled_language_brings_its_comment_token()
    {
        using var editor = new EditorUnderTest("|x = 1");
        editor.Editor.LanguageId = "lua";
        for (int i = 0; i < 500 && editor.Editor.LanguageConfiguration.LineComment is null; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        editor.Press(Key.Oem2, KeyModifiers.Control, "/");
        editor.Plain.ShouldBe("-- x = 1");
    }

    [AvaloniaFact]
    public void Tab_and_shift_tab_indent_and_outdent_the_selected_lines()
    {
        using var editor = new EditorUnderTest("«a\n  b»\nc");
        editor.Press(Key.Tab);
        editor.Text.ShouldBe("«    a\n      b»\nc");
        editor.Press(Key.Tab, KeyModifiers.Shift);
        editor.Press(Key.Tab, KeyModifiers.Shift);
        editor.Text.ShouldBe("«a\nb»\nc");
    }

    /// <summary>A shown, focused editor with text, caret and selection from marked text.</summary>
    private sealed class EditorUnderTest : IDisposable
    {
        private readonly Window _window;

        public EditorUnderTest(string marked, LanguageConfiguration? configuration = null)
        {
            Editor = new CodeEditor();
            if (configuration is not null)
                Editor.LanguageConfiguration = configuration;
            _window = new Window
            {
                Width = 600,
                Height = 400,
                Content = Editor,
            };
            _window.Show();
            Editor.TextArea.Focus();
            int selectionStart = marked.IndexOf('«');
            string text = marked.Replace("«", "").Replace("»", "").Replace("|", "");
            Editor.Text = text;
            if (selectionStart >= 0)
            {
                int selectionEnd = marked.IndexOf('»') - 1;
                Editor.TextArea.Selection = Selection.Create(Editor.TextArea, selectionStart, selectionEnd);
                Editor.CaretOffset = selectionEnd;
            }
            else
            {
                Editor.CaretOffset = marked.IndexOf('|');
            }
            Dispatcher.UIThread.RunJobs();
        }

        public CodeEditor Editor { get; }

        /// <summary>The text with the caret and selection marked.</summary>
        public string Text
        {
            get
            {
                string text = Editor.Text;
                if (Editor.TextArea.Selection.IsEmpty)
                    return text.Insert(Editor.CaretOffset, "|");
                int start = Editor.SelectionStart;
                int end = start + Editor.SelectionLength;
                return text.Insert(end, "»").Insert(start, "«");
            }
        }

        public string Plain => Editor.Text;

        public void Type(string text)
        {
            foreach (char c in text)
                _window.KeyTextInput(c.ToString());
            Dispatcher.UIThread.RunJobs();
        }

        public void Press(Key key, KeyModifiers modifiers = KeyModifiers.None, string? symbol = null)
        {
            var raw = (RawInputModifiers)modifiers;
            _window.KeyPress(key, raw, PhysicalKey.None, symbol);
            _window.KeyRelease(key, raw, PhysicalKey.None, symbol);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => _window.Close();
    }
}
