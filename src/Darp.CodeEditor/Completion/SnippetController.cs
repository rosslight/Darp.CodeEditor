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
    private Session? _session;

    /// <summary>The stops with mirrors that the change under way is inside: their mirrors follow once it is done.</summary>
    private readonly List<TabStop> _edited = [];

    /// <summary>Whether the mirrors follow a change: their own changes are not followed again.</summary>
    private bool _following;

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
        if (
            _session is { } session
            // The innermost: placeholders come before those inside them.
            && session.Stops.LastOrDefault(stop =>
                !stop.IsFinal && !stop.IsGone && stop.StartOffset <= start && end <= stop.EndOffset
            )
                is { } enclosing
        )
        {
            // VS Code's merge: the snippet's stops, its final one too, come right after the selected stop, and in the
            // order of the text right after the stop they are in.
            List<TabStop> joining = CreateStops(snippet, start, enclosing, isFinal: false);
            session.Stops.InsertRange(enclosing.Order + 1, joining);
            Number(session.Stops);
            session.Groups.InsertRange(session.Current + 1, Groups(snippet, joining));
            Move(session, forward: true);
            return;
        }
        End();
        List<TabStop> stops = CreateStops(snippet, start, parent: null, isFinal: true);
        Number(stops);
        var started = new Session(stops, Groups(snippet, stops), mark, LineIndentation(start));
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
    private void Move(Session session, bool forward)
    {
        if (session.Current >= 0)
            Transform(session, session.Groups[session.Current]);
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
    /// Gives the stops of <paramref name="group"/> with a transformation their text transformed, as changes of their
    /// own in one undo step, which mirrors do not follow; not those that are gone. Each line of a result after the first
    /// starts with the whitespace before the snippet on its line, in the document's indentation and line breaks.
    /// </summary>
    private void Transform(Session session, List<TabStop> group)
    {
        List<(TabStop Stop, string Text)> transformed = [];
        // From the last in the text to the first, as mirrors are copied.
        foreach (TabStop stop in group.OrderByDescending(stop => stop.StartOffset).ThenByDescending(stop => stop.Order))
        {
            if (stop.Transform is not { } transform || stop.IsGone)
                continue;
            string text = _buffer.GetText(stop.StartOffset, stop.EndOffset - stop.StartOffset);
            string result = Indented(transform.Resolve(text), session.LineIndentation);
            if (result != text)
                transformed.Add((stop, result));
        }
        if (transformed.Count == 0)
            return;
        ILookup<int, TabStop> empty = session
            .Stops.Where(other => other.StartOffset == other.EndOffset)
            .ToLookup(other => other.StartOffset);
        foreach (TabStop stop in session.Stops)
            stop.Keep();
        _following = true;
        UndoStack undo = _buffer.UndoStack;
        undo.StartUndoGroup();
        try
        {
            using (_buffer.RunUpdate())
            {
                foreach ((TabStop stop, string text) in transformed)
                    Replace(stop, text, empty[stop.StartOffset]);
            }
        }
        finally
        {
            undo.EndUndoGroup();
            _following = false;
        }
    }

    /// <summary>
    /// <paramref name="text"/> with each line after the first starting with <paramref name="lineIndentation"/>, the
    /// whole indentation in the document's, and the document's line breaks.
    /// </summary>
    private string Indented(string text, string lineIndentation)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0)
            return text;
        var indenting = new Indenting(_document.Indentation);
        string[] lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        for (int i = 1; i < lines.Length; i++)
            lines[i] = indenting.Normalize(lineIndentation + lines[i]);
        return string.Join(_document.LineBreak, lines);
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

    /// <summary>The stops of a snippet that went in at <paramref name="start"/>, inside <paramref name="parent"/>.</summary>
    private List<TabStop> CreateStops(RenderedSnippet snippet, int start, TabStop? parent, bool isFinal)
    {
        var stops = new List<TabStop>(snippet.Stops.Count);
        foreach (SnippetStop stop in snippet.Stops)
        {
            stops.Add(
                new TabStop(
                    Anchor(start + stop.Start),
                    Anchor(start + stop.End),
                    stop.Parent >= 0 ? stops[stop.Parent] : parent,
                    hadText: stop.End > stop.Start,
                    isFinal && stop.Index == 0
                )
                {
                    Transform = stop.Transform,
                    Choice = stop.Choice,
                }
            );
        }
        return stops;
    }

    /// <summary>Gives each stop its place in <paramref name="stops"/>, the session's, in the order of the text.</summary>
    private static void Number(List<TabStop> stops)
    {
        for (int i = 0; i < stops.Count; i++)
            stops[i].Order = i;
    }

    /// <summary>
    /// The stops by number, in the order Tab selects them: by number, the final stop last, and those of one number in
    /// the order of the text. Each stop knows its group, for its mirrors.
    /// </summary>
    private static List<List<TabStop>> Groups(RenderedSnippet snippet, List<TabStop> stops)
    {
        List<List<TabStop>> groups =
        [
            .. Enumerable
                .Range(0, stops.Count)
                .GroupBy(i => snippet.Stops[i].Index)
                .OrderBy(group => group.Key == 0)
                .ThenBy(group => group.Key)
                .Select(group => group.Select(i => stops[i]).ToList()),
        ];
        foreach (List<TabStop> group in groups)
        {
            foreach (TabStop stop in group)
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
    /// Before a change of the text, but not of an undo or a redo, which bring the mirrors back themselves, nor of the
    /// mirrors that follow a change: notes the stops with mirrors that it is inside, drops the stops inside a
    /// placeholder whose text it replaces, and ends the session when it removes all of the snippet's text, unless it is
    /// inside the selected stop.
    /// </summary>
    private void OnBufferChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_following || _session is not { } session || !_buffer.UndoStack.AcceptChanges)
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
        var containing = new HashSet<TabStop>();
        foreach (TabStop stop in session.Stops)
        {
            if (stop.IsGone || !stop.Contains(e))
                continue;
            containing.Add(stop);
            if (stop.Group.Count > 1)
                _edited.Add(stop);
        }
        if (e.RemovalLength == 0 || containing.Count == 0)
            return;
        int removalEnd = e.Offset + e.RemovalLength;
        foreach (TabStop stop in session.Stops)
        {
            if (
                !containing.Contains(stop)
                && e.Offset <= stop.StartOffset
                && stop.EndOffset <= removalEnd
                && IsInside(stop)
            )
            {
                stop.IsGone = true;
            }
        }

        bool IsInside(TabStop stop)
        {
            for (TabStop? around = stop.Parent; around is not null; around = around.Parent)
            {
                if (containing.Contains(around))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// After a change of the text, within its undo step: ends the session when the step that inserted the snippet was
    /// undone, and gives the mirrors of the stops that the change was inside their text. The innermost stops' mirrors
    /// follow first, and a mirror inside a stop of another number has that stop's mirrors follow in turn, as VS Code's
    /// cursors in nested mirrors do; each number once. A mirror around or inside its own stop keeps its text, as copying
    /// would grow it forever.
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
        if (_following || _edited.Count == 0)
            return;
        var queue = new PriorityQueue<TabStop, int>(_edited.Select(stop => (stop, -stop.Depth)));
        _edited.Clear();
        var followed = new HashSet<List<TabStop>>(ReferenceEqualityComparer.Instance);
        _following = true;
        // Text that goes in at a stop's edges goes outside it while mirrors copy: each copy changes only the stops it
        // touches, and puts them back.
        foreach (TabStop stop in session.Stops)
            stop.Keep();
        // The copies, with the change they follow, are one update: one undo step, and one end of an update for the
        // editor's work that waits for it.
        IDisposable update = _buffer.RunUpdate();
        try
        {
            while (queue.TryDequeue(out TabStop? stop, out _))
            {
                if (stop.IsGone || !followed.Add(stop.Group))
                    continue;
                // From the last in the text to the first: a copy moves only the text after it, so that where the empty
                // stops are holds for each mirror still to come.
                List<TabStop> mirrors =
                [
                    .. stop
                        .Group.Where(mirror => mirror != stop && !mirror.IsGone && !mirror.Overlaps(stop))
                        .OrderByDescending(mirror => mirror.StartOffset)
                        .ThenByDescending(mirror => mirror.Order),
                ];
                if (mirrors.Count == 0)
                    continue;
                int length = stop.EndOffset - stop.StartOffset;
                string text = _buffer.GetText(stop.StartOffset, length);
                ILookup<int, TabStop> empty = session
                    .Stops.Where(other => other.StartOffset == other.EndOffset)
                    .ToLookup(other => other.StartOffset);
                foreach (TabStop mirror in mirrors)
                {
                    if (
                        mirror.EndOffset - mirror.StartOffset == length
                        && _buffer.GetText(mirror.StartOffset, length) == text
                    )
                    {
                        continue;
                    }
                    Replace(mirror, text, empty[mirror.StartOffset]);
                    for (TabStop? around = mirror.Parent; around is not null; around = around.Parent)
                    {
                        if (around.Group.Count > 1)
                            queue.Enqueue(around, -around.Depth);
                    }
                }
            }
        }
        finally
        {
            update.Dispose();
            _following = false;
            foreach (TabStop stop in session.Stops)
                stop.FollowEdits();
        }
        OnSelectionChanged(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gives <paramref name="mirror"/> the text, as a change of its own, as a mirror copies or a transformation changes a
    /// stop: the work is that of its text and of the stops it touches, never of the text between mirrors nor of the
    /// other stops. The mirror and the placeholders around it take the new text; the other stops keep theirs, also those
    /// that touch it, such as its placeholder right before it: an empty one of <paramref name="empty"/>, those that are
    /// empty where the mirror starts, keeps its place before or after it, as in the order of the text, so that empty
    /// mirrors side by side stay apart. The caret stays before the text, as before a mirror right after the stop typed
    /// in.
    /// </summary>
    private void Replace(TabStop mirror, string text, IEnumerable<TabStop> empty)
    {
        int at = mirror.StartOffset;
        var touched = new List<TabStop>();
        for (TabStop? around = mirror; around is not null; around = around.Parent)
        {
            around.Grow();
            touched.Add(around);
        }
        foreach (TabStop stop in empty)
        {
            if (stop != mirror && !stop.Encloses(mirror))
            {
                stop.Stay(after: stop.Order > mirror.Order);
                touched.Add(stop);
            }
        }
        // Text that goes in where the caret is, into an empty mirror, would carry the caret along.
        bool caretStays = mirror.EndOffset == at && _textArea.Caret.Offset == at;
        _changes.Apply([new TextEdit(at, mirror.EndOffset - at, text)]);
        if (caretStays)
            _textArea.Caret.Offset = at;
        foreach (TabStop stop in touched)
            stop.Keep();
    }

    /// <summary>Ends the session once the selection is outside its stops, or the step that inserted it was undone.</summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        // Mirrors that copy leave the selection in the stop typed in: it is looked at once they are done.
        if (_selecting || _following || _session is not { } session)
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

    /// <summary>A snippet's session: its stops, the order Tab selects them in, and the selected group.</summary>
    private sealed class Session(
        List<TabStop> stops,
        List<List<TabStop>> groups,
        SnippetMark mark,
        string lineIndentation
    )
    {
        /// <summary>The whitespace before the snippet on its line, which a transformation's lines start with.</summary>
        public string LineIndentation { get; } = lineIndentation;

        /// <summary>
        /// The stops, mirrors included, of the snippet and of those that joined it, in the order of the text:
        /// placeholders before the stops inside them.
        /// </summary>
        public List<TabStop> Stops { get; } = stops;

        /// <summary>The stops by number, in the order Tab selects them; the final stop last.</summary>
        public List<List<TabStop>> Groups { get; } = groups;

        /// <summary>In the undo step that inserted the snippet: undoing it ends the session.</summary>
        public SnippetMark Mark { get; } = mark;

        /// <summary>The selected group in <see cref="Groups"/>; -1 before the first.</summary>
        public int Current { get; set; } = -1;
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
