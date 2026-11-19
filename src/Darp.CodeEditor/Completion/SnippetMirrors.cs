using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// The changes of a snippet's stops that its session makes: mirrors that follow the stop the user types in, in the same
/// undo step, the innermost stops first, and stops that take their text transformed as Tab or Shift+Tab leaves their
/// number, in an undo step of their own. Neither is followed again.
/// </summary>
internal sealed class SnippetMirrors
{
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly TextChanges _changes;

    /// <summary>The stops with mirrors that the change under way is inside: their mirrors follow once it is done.</summary>
    private readonly List<TabStop> _edited = [];

    public SnippetMirrors(TextArea textArea, CodeDocument document, TextChanges changes)
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        _changes = changes;
    }

    /// <summary>Whether the mirrors follow a change, or stops are transformed: these changes are not followed again.</summary>
    public bool IsChanging { get; private set; }

    /// <summary>Forgets the stops that the change under way is inside, as the session ends.</summary>
    public void Clear() => _edited.Clear();

    /// <summary>
    /// Before a change of the text that the user makes: notes the stops with mirrors that it is inside, and drops the
    /// stops inside a placeholder whose text it replaces.
    /// </summary>
    public void OnChanging(SnippetSession session, DocumentChangeEventArgs e)
    {
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
    /// After a change of the text, within its undo step: gives the mirrors of the stops that the change was inside their
    /// text. The innermost stops' mirrors follow first, and a mirror inside a stop of another number has that stop's
    /// mirrors follow in turn, as VS Code's cursors in nested mirrors do; each number once. A mirror around or inside
    /// its own stop keeps its text, as copying would grow it forever.
    /// </summary>
    /// <returns>Whether mirrors followed: the selection is then where the change left it.</returns>
    public bool Follow(SnippetSession session)
    {
        if (IsChanging || _edited.Count == 0)
            return false;
        var queue = new PriorityQueue<TabStop, int>(_edited.Select(stop => (stop, -stop.Depth)));
        _edited.Clear();
        var followed = new HashSet<List<TabStop>>(ReferenceEqualityComparer.Instance);
        IsChanging = true;
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
                ILookup<int, TabStop> empty = session.EmptyStops();
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
            IsChanging = false;
            foreach (TabStop stop in session.Stops)
                stop.FollowEdits();
        }
        return true;
    }

    /// <summary>
    /// Gives the stops of <paramref name="group"/> with a transformation their text transformed, as changes of their
    /// own in one undo step, which mirrors do not follow; not those that are gone. Each line of a result after the first
    /// starts with the whitespace before the snippet on its line, in the document's indentation and line breaks.
    /// </summary>
    public void Transform(SnippetSession session, List<TabStop> group)
    {
        List<(TabStop Stop, string Text)> transformed = [];
        // From the last in the text to the first, as mirrors are copied.
        foreach (TabStop stop in group.OrderByDescending(stop => stop.StartOffset).ThenByDescending(stop => stop.Order))
        {
            if (stop.Transform is not { } transform || stop.IsGone)
                continue;
            string text = _buffer.GetText(stop.StartOffset, stop.EndOffset - stop.StartOffset);
            // A result longer than a snippet's text may be leaves the stop as it is.
            if (transform.Resolve(text, Snippet.MaxText) is not { } resolved)
                continue;
            string result = Indented(resolved, session.LineIndentation);
            if (result != text)
                transformed.Add((stop, result));
        }
        if (transformed.Count == 0)
            return;
        ILookup<int, TabStop> empty = session.EmptyStops();
        foreach (TabStop stop in session.Stops)
            stop.Keep();
        IsChanging = true;
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
            IsChanging = false;
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
}
