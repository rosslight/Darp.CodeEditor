using Avalonia.Input;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Typed text and the keys Enter, Tab, Shift+Tab and Backspace in one editor's view, as VS Code's <c>type</c>,
/// <c>tab</c>, <c>outdent</c> and <c>deleteLeft</c> take them: each goes to the rules that apply, in VS Code's order.
/// The rules are <see cref="IndentRules"/>, <see cref="TabStops"/> and <see cref="AutoClosingPairs"/>; every edit
/// goes through <see cref="TextChanges"/>. Shift+Enter and Shift+Backspace do as Enter and Backspace.
/// </summary>
/// <remarks>
/// A typed character still goes in as AvaloniaEdit types it, so that the other handlers of typed text see it. What
/// the rules add, a closing character or a new indentation, goes in once it is in, before those handlers see it.
/// </remarks>
internal sealed class Typing : IDisposable
{
    private readonly TextArea _area;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly TextChanges _changes;
    private readonly AutoClosingPairs _pairs;

    /// <summary>The new indentation of the caret's line once the character being typed is in; null for none.</summary>
    private string? _indentation;

    /// <summary>The pair to close once the character being typed is in; null for none.</summary>
    private AutoClosingPair? _closing;

    public Typing(
        TextArea area,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        TextChanges changes,
        Func<CodeEditorOptions> options
    )
    {
        _area = area;
        _document = document;
        _highlighter = highlighter;
        _changes = changes;
        _pairs = new AutoClosingPairs(area, document, highlighter, changes, options);
        area.TextEntered += OnTextEntered;
    }

    private TextDocument Buffer => _area.Document;

    /// <summary>The language's editing rules; none while they load.</summary>
    private LanguageConfiguration Configuration =>
        _document.Language.Loaded is { IsCompletedSuccessfully: true } loaded
            ? loaded.Result.Configuration
            : LanguageConfiguration.None;

    private IndentRules Rules => new(Configuration, _document.Language.AutoIndent, _document.Indentation);

    private RuleLines Lines =>
        new(
            number => Buffer.GetText(Buffer.GetLineByNumber(number)),
            _highlighter.ExactTokensOf,
            Configuration.BracketPattern
        );

    public void Dispose()
    {
        _area.TextEntered -= OnTextEntered;
        _pairs.Dispose();
    }

    /// <summary>A key, while the text has the keyboard, once the keymap and the open popups left it.</summary>
    /// <returns>Whether the key was typing's; otherwise AvaloniaEdit takes it.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        if (_area.IsReadOnly || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
            return false;
        return e.Key switch
        {
            Key.Enter => Enter(),
            Key.Tab => Tab(outdent: e.KeyModifiers == KeyModifiers.Shift),
            Key.Back => DeleteLeft(),
            _ => false,
        };
    }

    /// <summary>
    /// Typed text, before AvaloniaEdit's text area takes it, in the order in which VS Code picks how to type a
    /// character: a line break; a character that outdents its line; one that steps over a closing character, closes a
    /// pair or surrounds the selection; a closing bracket that lines up with its opening one; or else plain text.
    /// </summary>
    public void OnTextInput(TextInputEventArgs e)
    {
        // AvaloniaEdit ignores these, and refuses text where it can't go.
        if (
            e.Text is not { Length: > 0 } text
            || text is "\x1b" or "\b" or "\x7f"
            || !_changes.CanChange(_area.Caret.Offset, 0)
        )
            return;
        if (text is "\n" or "\r" or "\r\n")
        {
            e.Handled = Enter();
            return;
        }
        if (text is [char typed])
        {
            _indentation = Outdented(typed);
            if (_indentation is null && (_pairs.TypeOver(typed) || _pairs.Surround(typed)))
            {
                e.Handled = true;
                return;
            }
            _closing = _indentation is null ? _pairs.PairToClose(typed) : null;
            if (_indentation is null && _closing is null)
                _indentation = LinedUp(typed);
        }
        // Text of several characters, as from an input method.
        else if (text.AsSpan().IndexOfAny('\r', '\n') >= 0)
            e.Text = TextUtilities.NormalizeNewLines(text, _document.LineBreak);
        try
        {
            _changes.Run(() => _area.PerformTextInput(e));
        }
        finally
        {
            _indentation = null;
            _closing = null;
        }
        e.Handled = true;
    }

    /// <summary>Once a character is in, and before the other handlers of typed text see it: what the rules add.</summary>
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_indentation is { } indentation)
        {
            DocumentLine line = Buffer.GetLineByOffset(_area.Caret.Offset);
            _changes.Apply([new TextEdit(line.Offset, IndentRules.Leading(Buffer.GetText(line)), indentation)]);
        }
        else if (_closing is { } pair)
            _pairs.Close(pair);
    }

    /// <summary>
    /// Enter: the selection's text goes, and an empty rectangle selection ends, and the new line that the rules give goes
    /// in at the caret.
    /// </summary>
    private bool Enter()
    {
        _changes.Run(() =>
        {
            using (Buffer.RunUpdate())
            {
                if (!_area.Selection.IsEmpty)
                    _area.Selection.ReplaceSelectionWithText("");
                _area.ClearSelection();
                int caret = _area.Caret.Offset;
                DocumentLine line = Buffer.GetLineByOffset(caret);
                NewLine newLine = Rules.Enter(Lines, line.LineNumber, caret - line.Offset, _document.LineBreak);
                int replaced = _changes.CanChange(caret, newLine.Replaced) ? newLine.Replaced : 0;
                _changes.Apply([new TextEdit(caret, replaced, newLine.Text)], new TextSelection(caret + newLine.Caret));
            }
        });
        return true;
    }

    /// <summary>
    /// Tab and Shift+Tab by tab stops, each a step of its own; Tab without a selection on a line of whitespace only
    /// first goes to the indentation that the rules give the line, or one level. A rectangle selection is AvaloniaEdit's.
    /// </summary>
    private bool Tab(bool outdent)
    {
        if (_area.Selection is RectangleSelection)
            return false;
        TextSelection selection = _changes.Selection;
        Indentation indentation = _document.Indentation;
        DocumentLine line = Buffer.GetLineByOffset(selection.Caret);
        string text = Buffer.GetText(line);
        if (!outdent && selection.Range.Length == 0 && IndentRules.Leading(text) == text.Length)
        {
            string good = new Indenting(indentation).Normalize(Rules.GoodIndentation(Lines, line.LineNumber) ?? "\t");
            if (!text.StartsWith(good, StringComparison.Ordinal))
            {
                _changes.Run(() =>
                    _changes.Apply(
                        [new TextEdit(line.Offset, line.Length, good)],
                        new TextSelection(line.Offset + good.Length)
                    )
                );
                return true;
            }
        }
        TabEdit edit = outdent
            ? TabStops.Outdent(Buffer, selection, indentation)
            : TabStops.Tab(Buffer, selection, indentation);
        _changes.Run(() => _changes.Apply(edit.Edits, edit.Selection));
        return true;
    }

    /// <summary>
    /// Backspace: an empty pair that the editor closed goes; in a line's indentation, back to the tab stop before the
    /// caret; otherwise AvaloniaEdit's Backspace.
    /// </summary>
    private bool DeleteLeft()
    {
        if (_pairs.DeletePair())
            return true;
        if (
            _area.Selection is not ({ IsEmpty: true } and not RectangleSelection)
            || TabStops.DeleteLeft(Buffer, _area.Caret.Offset, _document.Indentation) is not { } range
        )
            return false;
        _changes.Run(() => _changes.Apply([new TextEdit(range.Start, range.Length, "")]));
        return true;
    }

    /// <summary>The new indentation of the line once <paramref name="typed"/> replaces the selection on it, if it outdents.</summary>
    private string? Outdented(char typed)
    {
        if (_area.Selection is RectangleSelection || _area.Selection.IsMultiline)
            return null;
        TextRange range = _changes.Selection.Range;
        DocumentLine line = Buffer.GetLineByOffset(range.Start);
        return Rules.Outdented(Lines, line.LineNumber, range.Start - line.Offset, range.End - line.Offset, typed);
    }

    /// <summary>
    /// The new indentation of the caret's line, if <paramref name="typed"/> completes a closing bracket first on it, in
    /// code, whose opening bracket is on a line above.
    /// </summary>
    private string? LinedUp(char typed)
    {
        if (_area.Selection is not ({ IsEmpty: true } and not RectangleSelection))
            return null;
        int caret = _area.Caret.Offset;
        DocumentLine line = Buffer.GetLineByOffset(caret);
        return Rules.LinedUp(Lines, line.LineNumber, caret - line.Offset, typed, OpeningLine);

        int? OpeningLine(CharacterPair pair) =>
            (
                _document.Language.Loaded.Result.Grammar is null
                || _highlighter.ContextAt(caret) is { At: TokenType.Other }
            ) && BracketMatcher.FindOpening(Buffer, caret, pair, _highlighter.TokensOf) is { } opening
                ? Buffer.GetLineByOffset(opening).LineNumber
                : null;
    }
}
