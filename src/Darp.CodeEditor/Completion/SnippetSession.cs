using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// A snippet's session: its stops, anchored in the text, the order Tab selects them in, and the selected group. A
/// snippet that goes in inside one of its stops joins it.
/// </summary>
internal sealed class SnippetSession
{
    private readonly TextDocument _buffer;

    /// <summary>Starts the session of a snippet that went in at <paramref name="start"/>.</summary>
    /// <param name="buffer">The text it went in.</param>
    /// <param name="snippet">The snippet, with stops.</param>
    /// <param name="start">Where it went in.</param>
    /// <param name="mark">The mark in the undo step that inserted it.</param>
    /// <param name="lineIndentation">The whitespace before it on its line.</param>
    public SnippetSession(
        TextDocument buffer,
        RenderedSnippet snippet,
        int start,
        SnippetMark mark,
        string lineIndentation
    )
    {
        _buffer = buffer;
        Mark = mark;
        LineIndentation = lineIndentation;
        Stops = CreateStops(snippet, start, parent: null, isFinal: true);
        Number();
        Groups = ByNumber(snippet, Stops);
    }

    /// <summary>The whitespace before the snippet on its line, which a transformation's lines start with.</summary>
    public string LineIndentation { get; }

    /// <summary>
    /// The stops, mirrors included, of the snippet and of those that joined it, in the order of the text: placeholders
    /// before the stops inside them.
    /// </summary>
    public List<TabStop> Stops { get; }

    /// <summary>The stops by number, in the order Tab selects them; the final stop last.</summary>
    public List<List<TabStop>> Groups { get; }

    /// <summary>In the undo step that inserted the snippet: undoing it ends the session.</summary>
    public SnippetMark Mark { get; }

    /// <summary>The selected group in <see cref="Groups"/>; -1 before the first.</summary>
    public int Current { get; set; } = -1;

    /// <summary>
    /// The innermost stop that the text from <paramref name="start"/> to <paramref name="end"/> is inside, which a
    /// snippet that goes in there joins; null for none. The final stop takes none.
    /// </summary>
    public TabStop? Enclosing(int start, int end) =>
        // The innermost: placeholders come before those inside them.
        Stops.LastOrDefault(stop =>
            !stop.IsFinal && !stop.IsGone && stop.StartOffset <= start && end <= stop.EndOffset
        );

    /// <summary>
    /// Joins a snippet that went in at <paramref name="start"/>, inside <paramref name="enclosing"/>, as VS Code merges
    /// it: its stops, its final one too, come right after the selected stop, and in the order of the text right after
    /// the stop they are in.
    /// </summary>
    public void Join(RenderedSnippet snippet, int start, TabStop enclosing)
    {
        List<TabStop> joining = CreateStops(snippet, start, enclosing, isFinal: false);
        Stops.InsertRange(enclosing.Order + 1, joining);
        Number();
        Groups.InsertRange(Current + 1, ByNumber(snippet, joining));
    }

    /// <summary>The empty stops by where they are, which the copy of a mirror there keeps in their order.</summary>
    public ILookup<int, TabStop> EmptyStops() =>
        Stops.Where(stop => stop.StartOffset == stop.EndOffset).ToLookup(stop => stop.StartOffset);

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

    /// <summary>Gives each stop its place in <see cref="Stops"/>, in the order of the text.</summary>
    private void Number()
    {
        for (int i = 0; i < Stops.Count; i++)
            Stops[i].Order = i;
    }

    /// <summary>
    /// The stops by number, in the order Tab selects them: by number, the final stop last, and those of one number in
    /// the order of the text. Each stop knows its group, for its mirrors.
    /// </summary>
    private static List<List<TabStop>> ByNumber(RenderedSnippet snippet, List<TabStop> stops)
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
