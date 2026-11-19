using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// The snippet session of one editor's view of a document, as VS Code's: once a snippet goes in, its first stop is
/// selected, Tab and Shift+Tab select the next and the previous stop, and typing in a stop changes its mirrors with it.
/// </summary>
/// <remarks>
/// <para>
/// The stops follow the edits: the selected stop, and the placeholders around it, grow as the user types at their
/// edges, the others don't. When a placeholder's text is replaced, the stops inside it are gone, as in VS Code, and Tab
/// skips them, as it skips a stop whose text was deleted. Shift+Tab on the first stop is left to indentation, as in VS
/// Code. Mirrors follow the stop the user types in, in the same undo step, the innermost stops first, so that a mirror
/// inside another stop changes that stop's mirrors too; an undo or a redo changes them itself. When Tab or Shift+Tab
/// leaves a number, its stops with a transformation, such as <c>${1/(.*)/${1:/upcase}/}</c>, take their text
/// transformed, as in VS Code, in an undo step of their own that mirrors do not follow. When the selected stop is a
/// choice, such as <c>${1|red,green,blue|}</c>, completion's list offers its options (<see cref="Choices"/>), asked for
/// each time another choice is selected (<see cref="ChoiceSelected"/>); committing one goes on to the next stop.
/// </para>
/// <para>
/// The session ends at the final stop, on <see cref="End"/>, as for Escape, when the selection leaves the stops, when
/// the step that inserted the snippet is undone, when a change removes all of its text, such as setting the document's
/// text, and when the editor stops showing the document. A snippet that goes in inside a stop while a session runs, as
/// completion inside a placeholder commits one, joins it: its stops come right after the selected stop, as in VS Code.
/// </para>
/// <para>
/// As in VS Code, a session has as many stops as its snippets, and mirrors copy whatever the stop they follow holds
/// (#117). The UI thread's work grows with them, and no faster: each change and caret move looks at each stop, and the
/// copies that follow a change are each a change of that mirror alone, which looks only at the stops it touches, and
/// all of them with the change they follow one update and one undo step.
/// </para>
/// <para>
/// The stops show as boxes in the theme's <c>editor.snippetTabstopHighlightBackground</c>, and the final stop as a
/// border in <c>editor.snippetFinalTabstopHighlightBorder</c>, in VS Code's colors where the theme has none.
/// </para>
/// </remarks>
internal sealed class SnippetController : IBackgroundRenderer, IDisposable
{
    /// <summary>The width of the box of an empty stop, as VS Code's.</summary>
    private const double EmptyStopWidth = 2;

    private readonly TextArea _textArea;
    private readonly TextChanges _changes;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private TextAreaColors? _colors;
    private readonly SnippetMirrors _mirrors;
    private SnippetSession? _session;

    /// <summary>Whether the controller selects a stop: it does not follow its own selection.</summary>
    private bool _selecting;

    /// <summary>The selected choice that <see cref="ChoiceSelected"/> told of last; null while none is selected.</summary>
    private TabStop? _choice;

    public SnippetController(TextArea textArea, CodeDocument document, TextChanges changes)
    {
        _textArea = textArea;
        _changes = changes;
        _buffer = textArea.Document;
        _document = document;
        _mirrors = new SnippetMirrors(textArea, document, changes);
        _buffer.Changing += OnBufferChanging;
        _buffer.TextChanged += OnBufferTextChanged;
        _textArea.Caret.PositionChanged += OnSelectionChanged;
        _textArea.SelectionChanged += OnSelectionChanged;
        _textArea.TextView.BackgroundRenderers.Add(this);
    }

    /// <summary>
    /// Another choice is selected: completion should show its options, as VS Code asks for them once the selection is
    /// set. Not again while the same choice stays selected.
    /// </summary>
    public event EventHandler? ChoiceSelected;

    /// <summary>Behind the selection, in the layer that the text view draws itself whenever a layer changes.</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            _textArea.TextView.InvalidateLayer(Layer);
        }
    }

    /// <summary>
    /// <paramref name="snippet"/> as it goes in at <paramref name="start"/>: indented by the whitespace before it on
    /// its line, and with the values of its variables for the caret and the text as they are now, before it goes in.
    /// The UI thread reads at most the shown part of the caret's line, and of the line it goes in.
    /// </summary>
    /// <param name="snippet">The snippet.</param>
    /// <param name="start">Where it goes in.</param>
    /// <param name="selected">The text for <c>SELECTION</c> and <c>TM_SELECTED_TEXT</c>; null for none.</param>
    /// <param name="clipboard">The clipboard's text, read for <c>CLIPBOARD</c>; null for none.</param>
    public RenderedSnippet Render(Snippet snippet, int start, SelectedText? selected, string? clipboard)
    {
        int caret = _textArea.Caret.Offset;
        DocumentLine caretLine = _buffer.GetLineByOffset(caret);
        // One time for all of the snippet's variables, as in VS Code.
        DateTimeOffset now = _document.TimeProvider.GetLocalNow();
        // Each name is resolved once, except the random ones, which VS Code draws for each variable.
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        return snippet.Render(
            new SnippetContext(
                LineIndentation(start),
                _document.Indentation,
                _document.LineBreak,
                name =>
                    SnippetVariables.Random(name)
                    ?? (values.TryGetValue(name, out string? value) ? value : values[name] = Variable(name))
            )
            {
                // The selection that typing replaced was on the caret's line.
                MultilineSelectionIndentation = selected is { IsMultiline: true } ? LineIndentation(caret) : null,
            }
        );

        string? Variable(string name) =>
            name switch
            {
                "SELECTION" or "TM_SELECTED_TEXT" => selected?.Text,
                "CLIPBOARD" => SnippetVariables.Clipboard(clipboard),
                "TM_CURRENT_LINE" => _buffer.GetText(caretLine.Offset, VisibleText.End(caretLine) - caretLine.Offset),
                "TM_CURRENT_WORD" => WordAt(caretLine, caret) is { Length: > 0 } word ? word : null,
                "TM_LINE_INDEX" => (caretLine.LineNumber - 1).ToString(CultureInfo.InvariantCulture),
                "TM_LINE_NUMBER" => caretLine.LineNumber.ToString(CultureInfo.InvariantCulture),
                // One caret.
                "CURSOR_INDEX" => "0",
                "CURSOR_NUMBER" => "1",
                "LINE_COMMENT" => Comments()?.LineComment,
                "BLOCK_COMMENT_START" => Comments()?.BlockComment?.Open,
                "BLOCK_COMMENT_END" => Comments()?.BlockComment?.Close,
                _ => SnippetVariables.OfFile(name, _document.FilePath)
                    ?? SnippetVariables.OfTime(name, now, _document.TimeProvider.LocalTimeZone),
            };
    }

    /// <summary>
    /// Starts the session of a snippet that went in at <paramref name="start"/>, in the undo step of
    /// <paramref name="mark"/>, and selects its first stop; without stops, the caret stays where it is. Inside a stop of
    /// the session that runs, the snippet joins it.
    /// </summary>
    public void Start(RenderedSnippet snippet, int start, SnippetMark mark)
    {
        if (snippet.Stops.Count == 0)
            return;
        int end = start + snippet.Text.Length;
        if (_session is { } session && session.Enclosing(start, end) is { } enclosing)
        {
            session.Join(snippet, start, enclosing);
            Move(session, forward: true);
            return;
        }
        End();
        var started = new SnippetSession(_buffer, snippet, start, mark, LineIndentation(start));
        _session = started;
        Move(started, forward: true);
    }

    /// <summary>Tab and Shift+Tab, from the editor's key handler while the text area has the keyboard.</summary>
    /// <returns>Whether the key was the session's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        if (_session is not { } session || e.Key != Key.Tab)
            return false;
        if (e.KeyModifiers == KeyModifiers.None)
        {
            Move(session, forward: true);
            return true;
        }
        // On the first stop, Shift+Tab outdents, as in VS Code.
        if (e.KeyModifiers == KeyModifiers.Shift && session.Current > 0)
        {
            Move(session, forward: false);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The options of the selected choice as completion's list, while the caret is in its text; null otherwise.
    /// Committing one should go on to <see cref="Next"/>.
    /// </summary>
    public CompletionList? Choices()
    {
        if (_session is null || _choice is not { Choice: { } options } choice)
            return null;
        var range = TextRange.FromBounds(choice.StartOffset, choice.EndOffset);
        int caret = _textArea.Caret.Offset;
        return caret < range.Start || caret > range.End
            ? null
            : CompletionList.OfChoice(options, range, _buffer.GetText(range.Start, range.Length));
    }

    /// <summary>Selects the next stop, as Tab does, while a session runs.</summary>
    public void Next()
    {
        if (_session is { } session)
            Move(session, forward: true);
    }

    /// <summary>Ends the session; the text and the selection stay as they are.</summary>
    /// <returns>Whether a session ran.</returns>
    public bool End()
    {
        if (_session is null)
            return false;
        _session = null;
        _choice = null;
        _mirrors.Clear();
        _textArea.TextView.InvalidateLayer(Layer);
        return true;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (
            _session is not { } session
            || _colors is not { } colors
            || !textView.VisualLinesValid
            || textView.VisualLines.Count == 0
        )
        {
            return;
        }
        int first = textView.VisualLines[0].FirstDocumentLine.Offset;
        int last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (TabStop stop in session.Stops)
        {
            (int start, int end) = (stop.StartOffset, stop.EndOffset);
            if (stop.IsGone || end < first || start > last)
                continue;
            IBrush? background = stop.IsFinal ? colors.SnippetFinalTabStop : colors.SnippetTabStop;
            IPen? border = stop.IsFinal ? colors.SnippetFinalTabStopBorder : colors.SnippetTabStopBorder;
            if (background is null && border is null)
                continue;
            var builder = new BackgroundGeometryBuilder
            {
                AlignToWholePixels = true,
                BorderThickness = border?.Thickness ?? 0,
            };
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, Segment(start, end)))
                builder.AddRectangle(textView, rect.Width >= EmptyStopWidth ? rect : rect.WithWidth(EmptyStopWidth));
            if (builder.CreateGeometry() is { } geometry)
                drawingContext.DrawGeometry(background, border, geometry);
        }
    }

    public void Dispose()
    {
        End();
        _buffer.Changing -= OnBufferChanging;
        _buffer.TextChanged -= OnBufferTextChanged;
        _textArea.Caret.PositionChanged -= OnSelectionChanged;
        _textArea.SelectionChanged -= OnSelectionChanged;
        _textArea.TextView.BackgroundRenderers.Remove(this);
    }

    /// <summary>
    /// Selects the next or the previous stop, skipping those whose text was deleted or that are gone, as VS Code does;
    /// the session ends on the final stop. The selected stop and the placeholders around it grow as the user types at
    /// their edges; its mirrors follow it, and grow only as they do.
    /// </summary>
    private void Move(SnippetSession session, bool forward)
    {
        if (session.Current >= 0)
            _mirrors.Transform(session, session.Groups[session.Current]);
        int current = session.Current;
        int next = forward ? current + 1 : current - 1;
        while (next >= 0 && next < session.Groups.Count)
        {
            current = next;
            if (!session.Groups[current].TrueForAll(stop => stop.IsCollapsed))
                break;
            next = forward ? current + 1 : current - 1;
        }
        session.Current = current;
        TabStop selected = session.Groups[current].Find(stop => !stop.IsGone) ?? session.Groups[current][0];
        foreach (TabStop stop in session.Stops)
            stop.IsSelected = false;
        for (TabStop? around = selected; around is { IsFinal: false }; around = around.Parent)
            around.IsSelected = true;
        foreach (TabStop stop in session.Stops)
            stop.FollowEdits();
        Select(selected);
        if (current == session.Groups.Count - 1)
            End();
        else if (selected.Choice is null)
            _choice = null;
        else if (selected != _choice)
        {
            _choice = selected;
            ChoiceSelected?.Invoke(this, EventArgs.Empty);
        }
        _textArea.TextView.InvalidateLayer(Layer);
    }

    /// <summary>
    /// The whitespace at the start of the line of <paramref name="offset"/>, up to it, in the shown part of the line.
    /// </summary>
    private string LineIndentation(int offset)
    {
        DocumentLine line = _buffer.GetLineByOffset(offset);
        int end = line.Offset;
        int limit = Math.Min(offset, VisibleText.End(line));
        while (end < limit && _buffer.GetCharAt(end) is ' ' or '\t')
            end++;
        return _buffer.GetText(line.Offset, end - line.Offset);
    }

    /// <summary>
    /// Selects a stop's text, with the caret at its end; an empty stop puts the caret there. Each step stops once a
    /// handler of the one before shows another document.
    /// </summary>
    private void Select(TabStop stop)
    {
        (int start, int end) = (stop.StartOffset, stop.EndOffset);
        _selecting = true;
        try
        {
            _ =
                _changes.WhileShown(() =>
                {
                    if (start == end)
                        _textArea.ClearSelection();
                    else
                        _textArea.Selection = Selection.Create(_textArea, start, end);
                })
                && _changes.WhileShown(() => _textArea.Caret.Offset = end)
                && _changes.WhileShown(_textArea.Caret.BringCaretToView);
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <summary>
    /// The word at <paramref name="caret"/>, by VS Code's word characters, as far as a hovered word reaches to either
    /// side.
    /// </summary>
    private string WordAt(DocumentLine line, int caret)
    {
        int start = caret;
        int first = Math.Max(line.Offset, caret - HoverController.MaxWordReach);
        while (start > first && WordCharacters.IsWordCharacter(_buffer.GetCharAt(start - 1)))
            start--;
        int end = caret;
        int last = Math.Min(line.EndOffset, caret + HoverController.MaxWordReach);
        while (end < last && WordCharacters.IsWordCharacter(_buffer.GetCharAt(end)))
            end++;
        return _buffer.GetText(start, end - start);
    }

    /// <summary>The language's comments; null while they load.</summary>
    private LanguageConfiguration? Comments() =>
        _document.Language.Loaded is { IsCompletedSuccessfully: true } loaded ? loaded.Result.Configuration : null;

    private static TextSegment Segment(int start, int end) => new() { StartOffset = start, EndOffset = end };

    /// <summary>
    /// Before a change of the text, but not of an undo or a redo, which bring the mirrors back themselves, nor of the
    /// mirrors that follow a change: ends the session when it removes all of the snippet's text, unless it is inside the
    /// selected stop, and otherwise lets the mirrors note it (<see cref="SnippetMirrors.OnChanging"/>).
    /// </summary>
    private void OnBufferChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_mirrors.IsChanging || _session is not { } session || !_buffer.UndoStack.AcceptChanges)
            return;
        if (
            e.RemovalLength > 0
            && e.Offset <= session.Stops.Min(stop => stop.StartOffset)
            && e.Offset + e.RemovalLength >= session.Stops.Max(stop => stop.EndOffset)
            && !session.Stops.Exists(stop => stop.IsSelected && stop.Contains(e))
        )
        {
            End();
            return;
        }
        _mirrors.OnChanging(session, e);
    }

    /// <summary>
    /// After a change of the text, within its undo step: ends the session when the step that inserted the snippet was
    /// undone, and has the mirrors of the stops that the change was inside follow it (<see cref="SnippetMirrors.Follow"/>).
    /// </summary>
    private void OnBufferTextChanged(object? sender, EventArgs e)
    {
        if (_session is not { } session)
            return;
        if (session.Mark.IsUndone)
        {
            End();
            return;
        }
        _textArea.TextView.InvalidateLayer(Layer);
        if (_mirrors.Follow(session))
            OnSelectionChanged(this, EventArgs.Empty);
    }

    /// <summary>Ends the session once the selection is outside its stops, or the step that inserted it was undone.</summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        // Mirrors that copy leave the selection in the stop typed in: it is looked at once they are done.
        if (_selecting || _mirrors.IsChanging || _session is not { } session)
            return;
        ISegment selected = _textArea.Selection.IsEmpty
            ? Segment(_textArea.Caret.Offset, _textArea.Caret.Offset)
            : _textArea.Selection.SurroundingSegment;
        if (
            session.Mark.IsUndone
            || !session.Stops.Exists(stop =>
                !stop.IsFinal
                && !stop.IsGone
                && stop.StartOffset <= selected.Offset
                && selected.EndOffset <= stop.EndOffset
            )
        )
        {
            End();
        }
    }
}
