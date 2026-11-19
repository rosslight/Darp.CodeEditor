using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Typed text and the keys Enter, Tab, Shift+Tab, Backspace and Delete in one editor's view, as VS Code's <c>type</c>,
/// <c>tab</c>, <c>outdent</c>, <c>deleteLeft</c> and <c>deleteRight</c> take them: each goes to the rules that apply,
/// in VS Code's order (<see cref="IndentRules"/>, <see cref="TabStops"/>, <see cref="AutoClosingPairs"/>), and through
/// <see cref="TextChanges"/> into its undo step. Shift+Enter and Shift+Backspace do as Enter and Backspace. A typed
/// character goes in as AvaloniaEdit types it, so that the other handlers of typed text see it; what the rules add goes
/// in before they do.
/// </summary>
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

    /// <summary>A pair with a longer opening, whose last character went in: it closes in a step of its own.</summary>
    private AutoClosingPair? _closeAfter;

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
    private LanguageConfiguration Configuration => LanguageConfiguration.Of(_document.Language);

    private IndentRules Rules =>
        new(Configuration, _document.Language.AutoIndent, _document.Indentation, _document.LineBreak);

    private RuleLines Lines => RuleLines.Of(Buffer, _highlighter, Configuration.BracketPattern);

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
            // Shift+Delete cuts.
            Key.Delete when e.KeyModifiers == KeyModifiers.None => DeleteRight(),
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
        EditStep step = _changes.Typing(text);
        if (text is [char typed])
        {
            if ((_indentation = Retyped(typed, Rules.Outdented)) is not null)
                step = new EditStep(EditKind.TypingOther, StartsStep: true);
            else if (_pairs.TypeOver(typed) || _pairs.Surround(typed))
            {
                e.Handled = true;
                return;
            }
            else
            {
                // A block opened under a statement goes back to the statement's indentation, and its pair still closes.
                _indentation = Retyped(typed, Rules.Opened);
                _closing = _pairs.PairToClose(typed);
                // The closing text of a longer opening, such as "<!--", waits for a step of its own.
                if (_indentation is not null || _closing is not null)
                    step = new EditStep(EditKind.TypingOther, StartsStep: true, EndsStep: _closing?.Open.Length > 1);
                else if ((_indentation = LinedUp(typed)) is not null)
                    step = new EditStep(EditKind.TypingOther, StartsStep: false, EndsStep: true);
            }
        }
        // Text of several characters, as from an input method.
        else if (text.AsSpan().IndexOfAny('\r', '\n') >= 0)
            e.Text = TextUtilities.NormalizeNewLines(text, _document.LineBreak);
        try
        {
            _changes.Run(step, () => _area.PerformTextInput(e));
        }
        finally
        {
            _indentation = null;
            _closing = null;
        }
        if (_closeAfter is { } longPair)
        {
            _closeAfter = null;
            _changes.Run(new EditStep(EditKind.TypingOther, StartsStep: true), () => _pairs.Close(longPair));
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
        if (_closing is { Open.Length: 1 } pair)
            _pairs.Close(pair);
        else
            _closeAfter = _closing;
    }

    /// <summary>Enter: the selection's text goes, a rectangle selection ends, and the rules' new line goes in.</summary>
    private bool Enter()
    {
        _changes.Run(
            new EditStep(EditKind.TypingOther, StartsStep: true),
            () =>
            {
                using (Buffer.RunUpdate())
                {
                    if (!_area.Selection.IsEmpty)
                        _area.Selection.ReplaceSelectionWithText("");
                    _area.ClearSelection();
                    int caret = _area.Caret.Offset;
                    DocumentLine line = Buffer.GetLineByOffset(caret);
                    NewLine newLine = Rules.Enter(Lines, line.LineNumber, caret - line.Offset);
                    int replaced = _changes.CanChange(caret, newLine.Replaced) ? newLine.Replaced : 0;
                    _changes.Apply(
                        [new TextEdit(caret, replaced, newLine.Text)],
                        new TextSelection(caret + newLine.Caret)
                    );
                }
            }
        );
        return true;
    }

    /// <summary>Tab and Shift+Tab, each a step of its own; in a rectangle selection, AvaloniaEdit's.</summary>
    private bool Tab(bool outdent)
    {
        if (_area.Selection is RectangleSelection)
            return false;
        TabEdit edit = outdent
            ? TabStops.Outdent(Buffer, _changes.Selection, _document.Indentation)
            : TabStops.Tab(Buffer, _changes.Selection, _document.Indentation, n => Rules.GoodIndentation(Lines, n));
        _changes.Run(EditStep.Own, () => _changes.Apply(edit.Edits, edit.Selection));
        return true;
    }

    /// <summary>Backspace: an empty pair that the editor closed, or else back to the tab stop, or AvaloniaEdit's.</summary>
    private bool DeleteLeft()
    {
        if (_pairs.DeletePair())
            return true;
        TextRange? range = _area.Selection is { IsEmpty: true } and not RectangleSelection
            ? TabStops.DeleteLeft(Buffer, _area.Caret.Offset, _document.Indentation)
            : null;
        _changes.Run(
            _changes.Deleting(left: true),
            () =>
            {
                if (range is { } whitespace)
                    _changes.Apply([new TextEdit(whitespace.Start, whitespace.Length, "")]);
                else
                    EditingCommands.Backspace.Execute(null, _area);
            }
        );
        return true;
    }

    private bool DeleteRight()
    {
        _changes.Run(_changes.Deleting(left: false), () => EditingCommands.Delete.Execute(null, _area));
        return true;
    }

    /// <summary>
    /// The new indentation that <paramref name="rule"/> gives the line once <paramref name="typed"/> replaces the
    /// selection on it; null for a selection over several lines.
    /// </summary>
    private string? Retyped(char typed, Func<RuleLines, int, int, int, char, string?> rule)
    {
        TextRange range = _changes.Selection.Range;
        DocumentLine line = Buffer.GetLineByOffset(range.Start);
        return _area.Selection is RectangleSelection || range.End > line.EndOffset
            ? null
            : rule(Lines, line.LineNumber, range.Start - line.Offset, range.End - line.Offset, typed);
    }

    /// <summary>
    /// The new indentation of the caret's line, if <paramref name="typed"/> completes a closing bracket first on it
    /// whose opening bracket is on a line above.
    /// </summary>
    private string? LinedUp(char typed)
    {
        if (_area.Selection is not ({ IsEmpty: true } and not RectangleSelection))
            return null;
        int caret = _area.Caret.Offset;
        DocumentLine line = Buffer.GetLineByOffset(caret);
        return Rules.LinedUp(Lines, line.LineNumber, caret - line.Offset, typed, OpeningLine);

        int? OpeningLine(CharacterPair pair) =>
            BracketMatcher.FindOpening(Buffer, caret, pair, _highlighter.TokensOf) is { } opening
                ? Buffer.GetLineByOffset(opening).LineNumber
                : null;
    }
}
