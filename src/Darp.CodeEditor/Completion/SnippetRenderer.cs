using System.Text;
using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Completion;

/// <summary>Writes the nodes' text and records the stops, as <see cref="Snippet.Render"/> describes.</summary>
internal sealed class SnippetRenderer
{
    private readonly SnippetContext _context;
    private readonly Indenting _indenting;

    /// <summary>The width of the line's indentation in columns, which each line after a line break starts with.</summary>
    private readonly int _lineColumns;

    /// <summary>The variables' values: each is asked for once, and a mirror's copy has its source's.</summary>
    private readonly Dictionary<SnippetVariableNode, string?> _values = [];

    /// <summary>The characters and parts that the text may still take.</summary>
    private int _budget = Snippet.MaxText;

    /// <summary>
    /// The whitespace that the last line of what went in last starts with, the snippet's text or a variable's value,
    /// as VS Code finds it for selected text that spans lines; null before anything went in.
    /// </summary>
    private string? _lastIndentation;

    /// <summary>Whether the text goes in after other text on its line, which its first line keeps as it is.</summary>
    private readonly bool _continued;

    public SnippetRenderer(SnippetContext context)
        : this(context, continued: false) { }

    private SnippetRenderer(SnippetContext context, bool continued)
    {
        _context = context;
        _continued = continued;
        _indenting = new Indenting(context.Indentation);
        _lineColumns = _indenting.Width(context.LineIndentation);
    }

    /// <summary>
    /// <paramref name="text"/>, such as a stop's transformed text, as it goes in after other text on its line: its first
    /// line as it is, and each line after a line break as the snippet's text goes in; null when it would be longer than
    /// <see cref="Snippet.MaxText"/>, with the indentation and the line breaks that it takes.
    /// </summary>
    public static string? Continued(string text, SnippetContext context)
    {
        var renderer = new SnippetRenderer(context, continued: true);
        return renderer.WriteText(text) ? renderer.Text.ToString() : null;
    }

    public StringBuilder Text { get; } = new();

    public List<SnippetStop> Stops { get; } = [];

    /// <summary>Writes the nodes; false once the text would be longer than <see cref="Snippet.MaxText"/>.</summary>
    public bool Write(List<SnippetNode> nodes, int parent)
    {
        foreach (SnippetNode node in nodes)
        {
            if (--_budget < 0)
                return false;
            switch (node)
            {
                case SnippetTextNode text:
                    if (!WriteText(text.Value))
                        return false;
                    break;
                case SnippetChoiceNode choice:
                    // Its lines keep their indentation; its line breaks, as everything's that goes in, are the
                    // document's.
                    if (!Append(TextUtilities.NormalizeNewLines(choice.Value, _context.LineBreak)))
                        return false;
                    break;
                case SnippetVariableNode variable:
                    SnippetVariableNode source = variable.Source ?? variable;
                    if (!_values.TryGetValue(source, out string? value))
                        _values[source] = value = _context.Variable(variable.Name);
                    // Transformed, a variable without a value is empty text, as in VS Code; the transformation stops
                    // once its result is longer than what is left.
                    if (
                        variable.Transform is { } transform
                        && (value = transform.Resolve(value ?? "", _budget)) is null
                    )
                        return false;
                    if (value is null ? !Write(variable.Children, parent) : !WriteValue(variable, value))
                        return false;
                    break;
                case SnippetPlaceholderNode placeholder:
                    int index = Stops.Count;
                    int start = Text.Length;
                    Stops.Add(
                        new SnippetStop(placeholder.Index, start, start, parent)
                        {
                            Transform = placeholder.Transform,
                            // In the document's line breaks, as its text: the text is one of them.
                            Choice = placeholder.Children is [SnippetChoiceNode { Options: var options }]
                                ?
                                [
                                    .. options.Select(option =>
                                        TextUtilities.NormalizeNewLines(option, _context.LineBreak)
                                    ),
                                ]
                                : null,
                        }
                    );
                    if (!Write(placeholder.Children, index))
                        return false;
                    Stops[index] = Stops[index] with { End = Text.Length };
                    break;
            }
        }
        return true;
    }

    /// <summary>
    /// A variable's value as it is, as VS Code puts it in, but in the document's line breaks; selected text that
    /// spans lines gets the indentation that the snippet adds where it goes in, on each of its lines after the first
    /// (<see cref="SnippetVariables.SelectionIndentation"/>). A value longer than what is left is not read, and each
    /// line, line break and indentation counts before it goes in.
    /// </summary>
    private bool WriteValue(SnippetVariableNode variable, string value)
    {
        if (value.Length > _budget)
            return false;
        string indentation =
            variable.Name is "SELECTION" or "TM_SELECTED_TEXT"
            && _context.MultilineSelectionIndentation is { } selectionIndentation
                ? SnippetVariables.SelectionIndentation(_lastIndentation ?? selectionIndentation, selectionIndentation)
                : "";
        // Where the value's last line starts: its indentation is what the snippet's next lines go on from.
        int lastLine = Text.Length;
        int start = 0;
        while (value.AsSpan(start).IndexOfAny('\r', '\n') is var length and >= 0)
        {
            int lineBreak = start + length;
            if (!Append(value.AsSpan(start, length)) || !Append(_context.LineBreak))
                return false;
            lastLine = Text.Length;
            if (!Append(indentation))
                return false;
            bool crlf = value[lineBreak] == '\r' && lineBreak + 1 < value.Length && value[lineBreak + 1] == '\n';
            start = lineBreak + (crlf ? 2 : 1);
        }
        if (!Append(value.AsSpan(start)))
            return false;
        _lastIndentation = LeadingWhitespace(lastLine);
        return true;
    }

    /// <summary>
    /// Text, as VS Code adjusts it: each line after a line break starts with the indentation of the line the
    /// snippet goes in, and its own; the indentation at the start of the snippet or of a line is the document's;
    /// line breaks are the document's. A text longer than what is left is not read.
    /// </summary>
    private bool WriteText(string value)
    {
        if (value.Length > _budget)
            return false;
        int start = 0;
        while (true)
        {
            int length = value.AsSpan(start).IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line = length < 0 ? value.AsSpan(start) : value.AsSpan(start, length);
            int lineStart = Text.Length;
            bool written =
                Text.Length == 0 ? (_continued ? Append(line) : WriteIndented(line, column: 0))
                : Text[^1] is '\r' or '\n' ? WriteIndented(line, _lineColumns)
                : Append(line);
            if (!written)
                return false;
            if (length < 0)
            {
                // The line as it went in: indented, at a line's start, or as written after other text.
                _lastIndentation = LeadingWhitespace(lineStart);
                return true;
            }
            int lineBreak = start + length;
            bool crlf = value[lineBreak] == '\r' && lineBreak + 1 < value.Length && value[lineBreak + 1] == '\n';
            start = lineBreak + (crlf ? 2 : 1);
            if (!Append(_context.LineBreak))
                return false;
        }
    }

    /// <summary>A line whose indentation starts at <paramref name="column"/>, in the document's indentation.</summary>
    private bool WriteIndented(ReadOnlySpan<char> line, int column)
    {
        int whitespace = 0;
        while (whitespace < line.Length && line[whitespace] is ' ' or '\t')
            whitespace++;
        int columns = _indenting.Width(line[..whitespace], column);
        // Counted before it is made: a deep indentation by wide tabs is long.
        if (_indenting.Length(columns) > _budget)
            return false;
        return Append(_indenting.Whitespace(columns)) && Append(line[whitespace..]);
    }

    /// <summary>The spaces and tabs of the text from <paramref name="start"/> on, up to anything else.</summary>
    private string LeadingWhitespace(int start)
    {
        int end = start;
        while (end < Text.Length && Text[end] is ' ' or '\t')
            end++;
        return Text.ToString(start, end - start);
    }

    private bool Append(ReadOnlySpan<char> text)
    {
        _budget -= text.Length;
        if (_budget < 0)
            return false;
        Text.Append(text);
        return true;
    }
}

/// <summary>Where a snippet goes in, for <see cref="Snippet.Render"/>.</summary>
/// <param name="LineIndentation">The whitespace at the start of the line it goes in, up to where it goes in.</param>
/// <param name="Indentation">The document's indentation.</param>
/// <param name="LineBreak">The document's line break.</param>
/// <param name="Variable">The value of a variable by its name; null when it has none.</param>
internal sealed record SnippetContext(
    string LineIndentation,
    Indentation Indentation,
    string LineBreak,
    Func<string, string?> Variable
)
{
    /// <summary>
    /// For selected text that spans lines, the whitespace at the start of the selection's line up to where it starts:
    /// <c>SELECTION</c> and <c>TM_SELECTED_TEXT</c> take <see cref="SnippetVariables.SelectionIndentation"/>. Null for
    /// selected text on one line, and without any.
    /// </summary>
    public string? MultilineSelectionIndentation { get; init; }
}

/// <summary>A snippet's text and its stops, ready to go in.</summary>
/// <param name="Text">The text.</param>
/// <param name="Stops">The stops, mirrors included, in the order of the text: placeholders before those inside them.</param>
internal sealed record RenderedSnippet(string Text, IReadOnlyList<SnippetStop> Stops);

/// <summary>A stop of a rendered snippet.</summary>
/// <param name="Index">Its number: 1 and up, and 0 for the final stop.</param>
/// <param name="Start">Where its text starts in the snippet's text.</param>
/// <param name="End">Where its text ends.</param>
/// <param name="Parent">The position of the placeholder it is inside in the list of stops; -1 for none.</param>
internal readonly record struct SnippetStop(int Index, int Start, int End, int Parent)
{
    /// <summary>What its text becomes as Tab or Shift+Tab leaves its number; null for none.</summary>
    public SnippetTransform? Transform { get; init; }

    /// <summary>The options of a choice, the first of which it has; null for a stop that is no choice.</summary>
    public IReadOnlyList<string>? Choice { get; init; }
}
