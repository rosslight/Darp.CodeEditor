using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
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
/// edges, the others don't. Tab skips a stop whose text was deleted, as when the placeholder around it was typed over,
/// and Shift+Tab on the first stop is left to indentation, as in VS Code. Mirrors follow the stop the user types in,
/// in the same undo step; an undo or a redo changes them itself.
/// </para>
/// <para>
/// The session ends at the final stop, on <see cref="End"/>, as for Escape, when the selection leaves the stops, when
/// the step that inserted the snippet is undone, when a change removes all of its text, such as setting the document's
/// text, and when the editor stops showing the document. A snippet that goes in inside a stop while a session runs, as
/// completion inside a placeholder commits one, joins it: its stops come right after the selected stop, as in VS Code.
/// A session tracks at most <see cref="Snippet.MaxStops"/> stops, so that following an edit and drawing them cost the
/// UI thread microseconds.
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
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private TextAreaColors? _colors;
    private Session? _session;

    /// <summary>The stops with mirrors that the change under way is inside: their mirrors follow once it is done.</summary>
    private readonly List<Stop> _edited = [];

    /// <summary>
    /// The groups whose mirrors follow one change, while they do: each group follows once, so that stops that are
    /// mirrors of each other end.
    /// </summary>
    private HashSet<List<Stop>>? _following;

    /// <summary>Whether the controller selects a stop: it does not follow its own selection.</summary>
    private bool _selecting;

    public SnippetController(TextArea textArea, CodeDocument document)
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        _buffer.Changing += OnBufferChanging;
        _buffer.TextChanged += OnBufferTextChanged;
        _textArea.Caret.PositionChanged += OnSelectionChanged;
        _textArea.SelectionChanged += OnSelectionChanged;
        _textArea.TextView.BackgroundRenderers.Add(this);
    }

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
    public RenderedSnippet Render(Snippet snippet, int start)
    {
        DocumentLine line = _buffer.GetLineByOffset(start);
        int indentationEnd = line.Offset;
        int limit = Math.Min(start, VisibleText.End(line));
        while (indentationEnd < limit && _buffer.GetCharAt(indentationEnd) is ' ' or '\t')
            indentationEnd++;
        int caret = _textArea.Caret.Offset;
        DocumentLine caretLine = _buffer.GetLineByOffset(caret);
        // One time for all of the snippet's variables, as in VS Code.
        DateTimeOffset now = _document.TimeProvider.GetLocalNow();
        string? currentLine = null;
        string? currentWord = null;
        return snippet.Render(
            new SnippetContext(
                _buffer.GetText(line.Offset, indentationEnd - line.Offset),
                _document.Indentation,
                TextUtilities.GetNewLineFromDocument(_buffer, line.LineNumber),
                Variable
            )
        );

        // Read once, however often the snippet names them.
        string? Variable(string name) =>
            name switch
            {
                "TM_CURRENT_LINE" => currentLine ??= _buffer.GetText(
                    caretLine.Offset,
                    VisibleText.End(caretLine) - caretLine.Offset
                ),
                "TM_CURRENT_WORD" => (currentWord ??= WordAt(caretLine, caret)) is { Length: > 0 } word ? word : null,
                "TM_LINE_INDEX" => (caretLine.LineNumber - 1).ToString(CultureInfo.InvariantCulture),
                "TM_LINE_NUMBER" => caretLine.LineNumber.ToString(CultureInfo.InvariantCulture),
                // One caret.
                "CURSOR_INDEX" => "0",
                "CURSOR_NUMBER" => "1",
                "LINE_COMMENT" => Comments()?.LineComment,
                "BLOCK_COMMENT_START" => Comments()?.BlockComment?.Open,
                "BLOCK_COMMENT_END" => Comments()?.BlockComment?.Close,
                _ => SnippetVariables.OfFile(name, _document.FilePath)
                    ?? SnippetVariables.OfTime(name, now, _document.TimeProvider.LocalTimeZone)
                    ?? SnippetVariables.Random(name),
            };
    }

    /// <summary>
    /// Starts the session of a snippet that went in at <paramref name="start"/>, in the undo step of
    /// <paramref name="mark"/>, and selects its first stop; without stops, the caret stays where it is. Inside a stop of
    /// the session that runs, the snippet joins it, unless they would have more than <see cref="Snippet.MaxStops"/>
    /// stops together.
    /// </summary>
    public void Start(RenderedSnippet snippet, int start, SnippetMark mark)
    {
        if (snippet.Stops.Count == 0)
            return;
        int end = start + snippet.Text.Length;
        if (
            _session is { } session
            && session.Stops.Count + snippet.Stops.Count <= Snippet.MaxStops
            // The innermost: placeholders come before those inside them.
            && session.Stops.LastOrDefault(stop => !stop.IsFinal && stop.StartOffset <= start && end <= stop.EndOffset)
                is { } enclosing
        )
        {
            // VS Code's merge: the snippet's stops, its final one too, come right after the selected stop.
            List<Stop> joining = CreateStops(snippet, start, enclosing, isFinal: false);
            session.Stops.AddRange(joining);
            session.Groups.InsertRange(session.Current + 1, Groups(snippet, joining));
            Move(session, forward: true);
            return;
        }
        End();
        List<Stop> stops = CreateStops(snippet, start, parent: null, isFinal: true);
        var started = new Session(stops, Groups(snippet, stops), mark);
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

    /// <summary>Ends the session; the text and the selection stay as they are.</summary>
    /// <returns>Whether a session ran.</returns>
    public bool End()
    {
        if (_session is null)
            return false;
        _session = null;
        _edited.Clear();
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
        foreach (Stop stop in session.Stops)
        {
            (int start, int end) = (stop.StartOffset, stop.EndOffset);
            if (end < first || start > last)
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
    /// Selects the next or the previous stop, skipping those whose text was deleted, as VS Code does; the session ends
    /// on the final stop. The selected stop and the placeholders around it grow as the user types at their edges.
    /// </summary>
    private void Move(Session session, bool forward)
    {
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
        foreach (Stop stop in session.Stops)
            stop.IsSelected = false;
        foreach (Stop stop in session.Groups[current])
        {
            for (Stop? around = stop; around is { IsFinal: false }; around = around.Parent)
                around.IsSelected = true;
        }
        foreach (Stop stop in session.Stops)
            stop.FollowEdits();
        Select(session.Groups[current][0]);
        if (current == session.Groups.Count - 1)
            End();
        _textArea.TextView.InvalidateLayer(Layer);
    }

    /// <summary>Selects a stop's text, with the caret at its end; an empty stop puts the caret there.</summary>
    private void Select(Stop stop)
    {
        (int start, int end) = (stop.StartOffset, stop.EndOffset);
        _selecting = true;
        try
        {
            if (start == end)
                _textArea.ClearSelection();
            else
                _textArea.Selection = Selection.Create(_textArea, start, end);
            _textArea.Caret.Offset = end;
        }
        finally
        {
            _selecting = false;
        }
        _textArea.Caret.BringCaretToView();
    }

    /// <summary>The stops of a snippet that went in at <paramref name="start"/>, inside <paramref name="parent"/>.</summary>
    private List<Stop> CreateStops(RenderedSnippet snippet, int start, Stop? parent, bool isFinal)
    {
        var stops = new List<Stop>(snippet.Stops.Count);
        foreach (SnippetStop stop in snippet.Stops)
        {
            stops.Add(
                new Stop(
                    Anchor(start + stop.Start),
                    Anchor(start + stop.End),
                    stop.Parent >= 0 ? stops[stop.Parent] : parent,
                    hadText: stop.End > stop.Start,
                    isFinal && stop.Index == 0
                )
            );
        }
        return stops;
    }

    /// <summary>
    /// The stops by number, in the order Tab selects them: by number, the final stop last, and those of one number in
    /// the order of the text. Each stop knows its group, for its mirrors.
    /// </summary>
    private static List<List<Stop>> Groups(RenderedSnippet snippet, List<Stop> stops)
    {
        List<List<Stop>> groups =
        [
            .. Enumerable
                .Range(0, stops.Count)
                .GroupBy(i => snippet.Stops[i].Index)
                .OrderBy(group => group.Key == 0)
                .ThenBy(group => group.Key)
                .Select(group => group.Select(i => stops[i]).ToList()),
        ];
        foreach (List<Stop> group in groups)
        {
            foreach (Stop stop in group)
                stop.Group = group;
        }
        return groups;
    }

    /// <summary>An anchor that stays when the text around it is deleted, as VS Code's decorations do.</summary>
    private TextAnchor Anchor(int offset)
    {
        TextAnchor anchor = _buffer.CreateAnchor(offset);
        anchor.SurviveDeletion = true;
        return anchor;
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
    /// Before a change of the text, but not of an undo or a redo, which bring the mirrors back themselves: notes the
    /// stops with mirrors that it is inside, also a mirror's change inside a stop of another group, and ends the
    /// session when it removes all of the snippet's text, unless it is inside the selected stop.
    /// </summary>
    private void OnBufferChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_session is not { } session || !_buffer.UndoStack.AcceptChanges)
            return;
        if (
            _following is null
            && e.RemovalLength > 0
            && e.Offset <= session.Stops.Min(stop => stop.StartOffset)
            && e.Offset + e.RemovalLength >= session.Stops.Max(stop => stop.EndOffset)
            && !session.Stops.Exists(stop => stop.IsSelected && stop.Contains(e))
        )
        {
            End();
            return;
        }
        foreach (Stop stop in session.Stops)
        {
            if (stop.Group.Count > 1 && _following?.Contains(stop.Group) != true && stop.Contains(e))
                _edited.Add(stop);
        }
    }

    /// <summary>
    /// After a change of the text, within its undo step: ends the session when the step that inserted the snippet was
    /// undone, and gives the mirrors of the stops that the change was inside their new text. A mirror that changes
    /// inside a stop of another group changes that group's mirrors in turn, as VS Code's cursors in nested mirrors do;
    /// each group once. A mirror around or inside its own stop keeps its text, as copying would grow it forever.
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
        if (_following is not null || _edited.Count == 0)
            return;
        _following = [];
        try
        {
            // The changes of the mirrors note the stops around them, which follow in turn.
            for (int i = 0; i < _edited.Count; i++)
            {
                Stop stop = _edited[i];
                if (!_following.Add(stop.Group))
                    continue;
                string text = _buffer.GetText(Segment(stop.StartOffset, stop.EndOffset));
                foreach (Stop mirror in stop.Group)
                {
                    if (
                        mirror != stop
                        && !mirror.Overlaps(stop)
                        && _buffer.GetText(Segment(mirror.StartOffset, mirror.EndOffset)) != text
                    )
                    {
                        mirror.Replace(_buffer, text);
                    }
                }
            }
        }
        finally
        {
            _following = null;
            _edited.Clear();
        }
    }

    /// <summary>Ends the session once the selection is outside its stops, or the step that inserted it was undone.</summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_selecting || _session is not { } session)
            return;
        ISegment selected = _textArea.Selection.IsEmpty
            ? Segment(_textArea.Caret.Offset, _textArea.Caret.Offset)
            : _textArea.Selection.SurroundingSegment;
        if (
            session.Mark.IsUndone
            || !session.Stops.Exists(stop =>
                !stop.IsFinal && stop.StartOffset <= selected.Offset && selected.EndOffset <= stop.EndOffset
            )
        )
        {
            End();
        }
    }

    /// <summary>A snippet's session: its stops, the order Tab selects them in, and the selected group.</summary>
    private sealed class Session(List<Stop> stops, List<List<Stop>> groups, SnippetMark mark)
    {
        /// <summary>The stops, mirrors included, of the snippet and of those that joined it.</summary>
        public List<Stop> Stops { get; } = stops;

        /// <summary>The stops by number, in the order Tab selects them; the final stop last.</summary>
        public List<List<Stop>> Groups { get; } = groups;

        /// <summary>In the undo step that inserted the snippet: undoing it ends the session.</summary>
        public SnippetMark Mark { get; } = mark;

        /// <summary>The selected group in <see cref="Groups"/>; -1 before the first.</summary>
        public int Current { get; set; } = -1;
    }

    /// <summary>A stop in the text: a placeholder, a mirror, or the final stop.</summary>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends; before <paramref name="start"/> once text went in at an empty stop that does not grow.</param>
    /// <param name="parent">The placeholder it is inside; null for none.</param>
    /// <param name="hadText">Whether it had text when it went in: once it is empty, its text was deleted.</param>
    /// <param name="isFinal">Whether it is the session's final stop.</param>
    private sealed class Stop(TextAnchor start, TextAnchor end, Stop? parent, bool hadText, bool isFinal)
    {
        private readonly TextAnchor _start = start;
        private readonly TextAnchor _end = end;
        private readonly bool _hadText = hadText;

        public Stop? Parent { get; } = parent;

        public bool IsFinal { get; } = isFinal;

        /// <summary>The stops of its number, itself included: its mirrors.</summary>
        public List<Stop> Group { get; set; } = [];

        /// <summary>Whether it is selected, or around the selected stop: it grows as the user types at its edges.</summary>
        public bool IsSelected { get; set; }

        public int StartOffset => _start.Offset;

        public int EndOffset => Math.Max(_start.Offset, _end.Offset);

        /// <summary>Whether its text, or that of a placeholder around it, was deleted, as VS Code tells.</summary>
        public bool IsCollapsed
        {
            get
            {
                for (Stop? stop = this; stop is not null; stop = stop.Parent)
                {
                    if (stop._hadText && stop.StartOffset == stop.EndOffset)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Makes the stop grow as the user types at its edges while it is selected. Otherwise text typed at its edges
        /// goes outside it, and at an empty one after it, as at the final stop after a placeholder.
        /// </summary>
        public void FollowEdits()
        {
            if (IsSelected)
            {
                _start.MovementType = AnchorMovementType.BeforeInsertion;
                _end.MovementType = AnchorMovementType.AfterInsertion;
            }
            else if (StartOffset == EndOffset)
            {
                _start.MovementType = AnchorMovementType.AfterInsertion;
                _end.MovementType = AnchorMovementType.AfterInsertion;
            }
            else
            {
                _start.MovementType = AnchorMovementType.AfterInsertion;
                _end.MovementType = AnchorMovementType.BeforeInsertion;
            }
        }

        /// <summary>Whether a change goes inside the stop, as its anchors will move: also at an edge that grows.</summary>
        public bool Contains(DocumentChangeEventArgs e)
        {
            (int start, int end) = (StartOffset, EndOffset);
            int changeEnd = e.Offset + e.RemovalLength;
            if (e.Offset < start || changeEnd > end)
                return false;
            if (e.RemovalLength > 0 || (start < e.Offset && e.Offset < end))
                return true;
            return e.Offset == start
                ? _start.MovementType == AnchorMovementType.BeforeInsertion
                : _end.MovementType == AnchorMovementType.AfterInsertion;
        }

        /// <summary>Whether the stops share text, or one is inside the other; not when they only touch.</summary>
        public bool Overlaps(Stop other) => StartOffset < other.EndOffset && other.StartOffset < EndOffset;

        /// <summary>Replaces the stop's text, which it keeps as a whole, also when it was empty.</summary>
        public void Replace(TextDocument buffer, string text)
        {
            (AnchorMovementType start, AnchorMovementType end) = (_start.MovementType, _end.MovementType);
            _start.MovementType = AnchorMovementType.BeforeInsertion;
            _end.MovementType = AnchorMovementType.AfterInsertion;
            buffer.Replace(StartOffset, EndOffset - StartOffset, text);
            (_start.MovementType, _end.MovementType) = (start, end);
        }
    }
}

/// <summary>
/// The mark of a snippet in the undo step that inserted it: undoing that step tells the snippet's session to end. It
/// holds no reference to the editor, so the document's undo history keeps no editor alive.
/// </summary>
internal sealed class SnippetMark : IUndoableOperation
{
    public bool IsUndone { get; private set; }

    public void Undo() => IsUndone = true;

    public void Redo() => IsUndone = false;
}
