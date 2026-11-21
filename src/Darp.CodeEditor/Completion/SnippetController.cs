using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// The snippet session of one editor's view of a document: once a snippet goes in, its first stop is selected, and Tab
/// and Shift+Tab select the next and the previous stop.
/// </summary>
/// <remarks>
/// The stops follow the edits, and the selected stop grows as the user types at its edges. The session ends at the final
/// stop, on <see cref="End"/>, as for Escape, when the selection leaves the stops, when the step that inserted the snippet
/// is undone, when a change removes all of its text, and when the editor stops showing the document. A snippet that goes
/// in during a session replaces it. The stops show in the theme's snippet tab stop colors.
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
    /// <paramref name="snippet"/> as it goes in at <paramref name="start"/>, indented by the whitespace before it on its
    /// line; null for one over the limits.
    /// </summary>
    public RenderedSnippet? Render(Snippet snippet, int start)
    {
        DocumentLine line = _buffer.GetLineByOffset(start);
        int indentationEnd = line.Offset;
        int limit = Math.Min(start, VisibleText.End(line));
        while (indentationEnd < limit && _buffer.GetCharAt(indentationEnd) is ' ' or '\t')
            indentationEnd++;
        return snippet.Render(
            new SnippetContext(
                _buffer.GetText(line.Offset, indentationEnd - line.Offset),
                _document.Indentation,
                _document.LineBreak
            )
        );
    }

    /// <summary>
    /// Starts the session of a snippet that went in at <paramref name="start"/>, in the undo step of
    /// <paramref name="mark"/>, and selects its first stop; without stops, the caret stays where it is.
    /// </summary>
    public void Start(RenderedSnippet snippet, int start, SnippetMark mark)
    {
        if (snippet.Stops.Count == 0)
            return;
        End();
        List<Stop> stops =
        [
            .. snippet
                .Stops.OrderBy(stop => stop.Index == 0)
                .ThenBy(stop => stop.Index)
                .Select(stop => new Stop(Anchor(start + stop.Start), Anchor(start + stop.End), stop.Index == 0)),
        ];
        var session = new Session(stops, mark);
        _session = session;
        Move(session, 1);
    }

    /// <summary>Tab and Shift+Tab, from the editor's key handler while the text area has the keyboard.</summary>
    /// <returns>Whether the key was the session's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        if (_session is not { } session || e.Key != Key.Tab)
            return false;
        if (e.KeyModifiers == KeyModifiers.None)
        {
            Move(session, 1);
            return true;
        }
        // On the first stop, Shift+Tab outdents, as in VS Code.
        if (e.KeyModifiers == KeyModifiers.Shift && session.Current > 0)
        {
            Move(session, -1);
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
            var segment = new TextSegment { StartOffset = start, EndOffset = end };
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
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

    /// <summary>Selects the stop <paramref name="step"/> away; the session ends on the final stop.</summary>
    private void Move(Session session, int step)
    {
        session.Current += step;
        Stop selected = session.Stops[session.Current];
        foreach (Stop stop in session.Stops)
            stop.GrowsAtEdges = stop == selected;
        (int start, int end) = (selected.StartOffset, selected.EndOffset);
        _selecting = true;
        if (start == end)
            _textArea.ClearSelection();
        else
            _textArea.Selection = Selection.Create(_textArea, start, end);
        _textArea.Caret.Offset = end;
        _selecting = false;
        _textArea.Caret.BringCaretToView();
        if (session.Current == session.Stops.Count - 1)
            End();
        _textArea.TextView.InvalidateLayer(Layer);
    }

    /// <summary>An anchor that stays when the text around it is deleted.</summary>
    private TextAnchor Anchor(int offset)
    {
        TextAnchor anchor = _buffer.CreateAnchor(offset);
        anchor.SurviveDeletion = true;
        return anchor;
    }

    /// <summary>
    /// Ends the session before a change that removes all of its text, such as setting the document's text, unless it is
    /// inside the selected stop. An undo or a redo is left to the session's mark.
    /// </summary>
    private void OnBufferChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_session is not { } session || e.RemovalLength == 0 || !_buffer.UndoStack.AcceptChanges)
            return;
        int removalEnd = e.Offset + e.RemovalLength;
        Stop selected = session.Stops[session.Current];
        bool insideSelected = selected.StartOffset <= e.Offset && removalEnd <= selected.EndOffset;
        if (!insideSelected && session.Stops.TrueForAll(s => e.Offset <= s.StartOffset && s.EndOffset <= removalEnd))
            End();
    }

    /// <summary>Ends the session when the step that inserted it was undone; otherwise the stops may have moved.</summary>
    private void OnBufferTextChanged(object? sender, EventArgs e)
    {
        if (_session is not { } session)
            return;
        if (session.Mark.IsUndone)
            End();
        else
            _textArea.TextView.InvalidateLayer(Layer);
    }

    /// <summary>Ends the session once the selection is outside its stops.</summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_selecting || _session is not { } session)
            return;
        (int start, int end) = _textArea.Selection.IsEmpty
            ? (_textArea.Caret.Offset, _textArea.Caret.Offset)
            : (_textArea.Selection.SurroundingSegment.Offset, _textArea.Selection.SurroundingSegment.EndOffset);
        if (!session.Stops.Exists(stop => !stop.IsFinal && stop.StartOffset <= start && end <= stop.EndOffset))
            End();
    }

    /// <summary>A snippet's session: its stops in the order Tab selects them, the final one last.</summary>
    private sealed class Session(List<Stop> stops, SnippetMark mark)
    {
        public List<Stop> Stops { get; } = stops;

        /// <summary>In the undo step that inserted the snippet: undoing it ends the session.</summary>
        public SnippetMark Mark { get; } = mark;

        /// <summary>The selected stop in <see cref="Stops"/>; -1 before the first.</summary>
        public int Current { get; set; } = -1;
    }

    /// <summary>A stop in the text.</summary>
    /// <param name="start">Where it starts.</param>
    /// <param name="end">Where it ends; before <paramref name="start"/> once text went in at an empty stop.</param>
    /// <param name="isFinal">Whether it is the session's final stop.</param>
    private sealed class Stop(TextAnchor start, TextAnchor end, bool isFinal)
    {
        private readonly TextAnchor _start = start;
        private readonly TextAnchor _end = end;

        public bool IsFinal { get; } = isFinal;

        public int StartOffset => _start.Offset;

        public int EndOffset => Math.Max(_start.Offset, _end.Offset);

        /// <summary>
        /// Whether text that goes in at its edges goes inside it, as for the selected stop, or else outside, and after an
        /// empty stop.
        /// </summary>
        public bool GrowsAtEdges
        {
            set
            {
                _start.MovementType = value ? AnchorMovementType.BeforeInsertion : AnchorMovementType.AfterInsertion;
                _end.MovementType = value ? AnchorMovementType.AfterInsertion : AnchorMovementType.BeforeInsertion;
            }
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
