using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Whether typing opens completion, as VS Code's quick suggestions decide (<c>editor.quickSuggestions</c>): a word
/// character typed at a word's end, or as its first character, in a word that starts with no digit, in code, comments or
/// strings as the language's <see cref="CodeLanguage.QuickSuggestions"/> say, by the token of the character typed.
/// </summary>
/// <remarks>
/// A word that a request opened by typing found nothing for is quiet: typing on in it opens nothing, so that a server
/// that answers nothing there, as some do in strings and comments, is not asked again on every key. Anything else, such
/// as Backspace, a caret move or another edit, ends it, so that typing after it asks again.
/// </remarks>
internal sealed class QuickSuggest : IDisposable
{
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly Func<int, TextRange> _wordBefore;

    /// <summary>The quiet word, up to where it ends: the caret while the user types on in it; null for none.</summary>
    private TextRange? _quiet;

    /// <param name="textArea">The text area, for the caret.</param>
    /// <param name="document">The document, for its language.</param>
    /// <param name="highlighter">The grammar's tokens, for whether text is code, a comment or a string.</param>
    /// <param name="wordBefore">The word before an offset, which completion asks for.</param>
    public QuickSuggest(
        TextArea textArea,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        Func<int, TextRange> wordBefore
    )
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        _highlighter = highlighter;
        _wordBefore = wordBefore;
        _buffer.Changing += OnBufferChanging;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
    }

    /// <summary>Whether <paramref name="typed"/>, typed last and now before the caret, opens completion.</summary>
    public bool Opens(char typed)
    {
        QuickSuggestions where = _document.Language.QuickSuggestions;
        int caret = _textArea.Caret.Offset;
        TextRange before = _wordBefore(caret);
        if (
            where is { Other: false, Comments: false, Strings: false }
            || !WordCharacters.IsWordCharacter(typed)
            || before.Length == 0
            || _quiet == before
        )
        {
            return false;
        }
        int end = WordEnd(caret);
        if ((end != caret && before.Length != 1) || char.IsAsciiDigit(_buffer.GetCharAt(before.Start)))
            return false;
        if (where is { Other: true, Comments: true, Strings: true })
            return true;
        return (_highlighter.ContextAt(caret)?.Before ?? TokenType.Other) switch
        {
            TokenType.Comment => where.Comments,
            TokenType.String => where.Strings,
            _ => where.Other,
        };
    }

    /// <summary>
    /// A request opened by typing found nothing: the word it was asked in goes quiet, while the caret is still in it, at
    /// its end, and the text before it did not change.
    /// </summary>
    public void Quiet(CompletionRequest request)
    {
        TextRange word = _wordBefore(_textArea.Caret.Offset);
        if (!request.IsStale && word.Length > 0 && word.Start == request.Word.Start)
            _quiet = word;
    }

    public void Dispose()
    {
        _buffer.Changing -= OnBufferChanging;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
    }

    /// <summary>
    /// Where the word that goes on after <paramref name="caret"/> ends, reaching as far as a hovered word does; the
    /// caret itself when no word goes on.
    /// </summary>
    private int WordEnd(int caret)
    {
        int last = Math.Min(_buffer.GetLineByOffset(caret).EndOffset, caret + Hovered.MaxWordReach);
        int end = caret;
        while (end < last && WordCharacters.IsWordCharacter(_buffer.GetCharAt(end)))
            end++;
        return end;
    }

    /// <summary>Typing on at the quiet word's end goes on with it; any other change ends it.</summary>
    private void OnBufferChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_quiet is { } quiet)
            _quiet =
                e.RemovalLength == 0 && e.Offset == quiet.End
                    ? quiet with
                    {
                        Length = quiet.Length + e.InsertionLength,
                    }
                    : null;
    }

    /// <summary>A caret that leaves the quiet word's end ends it.</summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_quiet is { } quiet && _textArea.Caret.Offset != quiet.End)
            _quiet = null;
    }
}
