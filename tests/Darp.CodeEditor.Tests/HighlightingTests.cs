using Avalonia.Media;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;
using TextMateSharp.Internal.Grammars;
using TextMateSharp.Internal.Types;

namespace Darp.CodeEditor.Tests;

/// <summary>Background TextMate highlighting: the lessons of the prototype, and colors in the frames that show text.</summary>
[Collection(UiFixture.Collection)]
public sealed class HighlightingTests(UiFixture ui)
{
    /// <summary>Light+ colors of JSON.</summary>
    private static readonly Color s_comment = Color.Parse("#008000");
    private static readonly Color s_string = Color.Parse("#A31515");
    private static readonly Color s_propertyName = Color.Parse("#0451A5");
    private static readonly Color s_number = Color.Parse("#098658");

    private readonly UiFixture _ui = ui;

    [Fact]
    public async Task Tokenizing_stops_between_lines_once_the_text_changed()
    {
        TextMateGrammar grammar = await JsonGrammar();
        using var changed = new CancellationTokenSource();
        await changed.CancelAsync();

        LineResult[] lines = BackgroundTextMateHighlighter.Tokenize(
            grammar,
            null,
            ["{", "  \"a\": 1", "}"],
            changed.Token
        );

        lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task After_a_line_over_the_length_limit_tokenizing_starts_from_the_start_of_the_grammar()
    {
        TextMateGrammar grammar = await JsonGrammar();
        // The long line ends the comment that the first line opens; a state passed over it would keep it open.
        string longLine = new string('x', BackgroundTextMateHighlighter.MaxTokenizedLineLength) + " */";

        LineResult[] lines = BackgroundTextMateHighlighter.Tokenize(
            grammar,
            null,
            ["/* a comment", longLine, "\"key\": 1"],
            TestContext.Current.CancellationToken
        );

        lines[1].ShouldBe(new LineResult(null, null));
        HashSet<Color> after = Colors(grammar, lines[2]);
        after.ShouldContain(s_string);
        after.ShouldContain(s_number);
        after.ShouldNotContain(s_comment);
    }

    [Fact]
    public Task A_line_over_the_length_limit_stays_uncolored_between_colored_lines() =>
        _ui.Run(async () =>
        {
            string longValue = new('x', BackgroundTextMateHighlighter.MaxTokenizedLineLength);
            CodeDocument document = CodeDocument.Create(
                CodeLanguage.Json,
                $"{{\n  \"before\": 1,\n  \"long\": \"{longValue}\",\n  \"after\": 2\n}}"
            );
            using var window = new EditorWindow(new CodeEditor { Document = document });

            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(4).Contains(s_string);
            });

            window.ColorsOfLine(2).ShouldContain(s_propertyName);
            window.ColorsOfLine(3).ShouldBe([Avalonia.Media.Colors.Black]);
        });

    [Fact]
    public Task The_first_lines_that_come_into_view_are_colored_without_waiting_for_the_background() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(5000));
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(2).Contains(s_propertyName);
            });
            TextSnapshot text = document.CurrentSnapshot;

            window.Editor.ScrollTo(new TextRange(text.GetOffset(new TextPosition(4000, 1)), 0));
            // A layout without the dispatcher's jobs: the background's results cannot arrive in between.
            window.Window.UpdateLayout();

            int top = window.TextView.VisualLines[0].FirstDocumentLine.LineNumber;
            top.ShouldBeGreaterThan(3900);
            // Without the lines above, the line starts from the start of the grammar: its key is colored as a string.
            window.ColorsOfLine(top + 1).ShouldContain(s_string);
        });

    [Fact]
    public Task An_edit_that_changes_the_state_at_a_lines_end_recolors_the_lines_below() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(100));
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(20).Contains(s_propertyName);
            });

            document.Replace(new TextRange(document.CurrentSnapshot.GetOffset(new TextPosition(2, 1)), 0), "/*");
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(20).SetEquals([s_comment]);
            });

            document.Undo();
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(20).Contains(s_propertyName);
            });
        });

    private static async Task<TextMateGrammar> JsonGrammar()
    {
        IRawGrammar grammar = (await CodeLanguage.Json.Loaded).Grammar.ShouldNotBeNull();
        ColorThemeFile theme = (await CodeEditorColorTheme.LightPlus.Loaded).ShouldNotBeNull();
        return TextMateGrammar.Load(theme, grammar);
    }

    /// <summary>The colors of a tokenized line, as the editor shows them.</summary>
    private static HashSet<Color> Colors(TextMateGrammar grammar, LineResult line)
    {
        int[] tokens = line.Tokens.ShouldNotBeNull();
        var colors = new HashSet<Color>();
        for (int i = 1; i < tokens.Length; i += 2)
        {
            if (grammar.Brushes[EncodedTokenAttributes.GetForeground(tokens[i])] is ISolidColorBrush brush)
                colors.Add(brush.Color);
        }
        return colors;
    }
}
