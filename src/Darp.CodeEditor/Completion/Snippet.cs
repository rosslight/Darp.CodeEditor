using System.Text;
using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// A completion item's text in the LSP snippet syntax, parsed as VS Code parses it: tab stops <c>$1</c> and
/// <c>${1}</c>, placeholders <c>${1:text}</c>, also nested, choices <c>${1|a,b|}</c>, of which the first goes in and the
/// session offers the others, the final stop <c>$0</c>, variables <c>$NAME</c> and <c>${NAME:default}</c>,
/// transformations, and the escapes <c>\$</c>, <c>\}</c> and <c>\\</c>, and <c>\,</c> and <c>\|</c> in choices. What
/// does not parse is text, as in VS Code.
/// </summary>
/// <remarks>
/// <para>
/// Stops of one number are mirrors: the first one with text gives every other one its text, as in VS Code, and a
/// mirror's variables have that text's values. A variable without a value, whether VS Code knows it or not, goes in as
/// its default, or as nothing, as VS Code's completion inserts it. A transformation, such as
/// <c>${1/(.*)/${1:/upcase}/}</c> (<see cref="SnippetTransform"/>), applies to a variable's value, or to nothing when it
/// has none, as it goes in; a stop with one is a mirror of its number, which the session transforms when Tab or
/// Shift+Tab leaves the number. A snippet with stops gets a final stop at its end, unless it has one.
/// </para>
/// <para>
/// A snippet may have as many stops as it likes, as in VS Code. The limits bound the UI thread's work with a snippet,
/// whatever the server sends (#117). Parsing runs where the server's answer is read, off the UI thread, and reads the
/// characters at most four times over. A snippet that parsing would read more often, nested deeper than
/// <see cref="MaxNesting"/> levels, or whose text would be longer than <see cref="MaxText"/> characters with its
/// mirrors, its indentation and its variables' values goes in as written, without stops, as a plain item goes in.
/// </para>
/// </remarks>
internal sealed class Snippet
{
    /// <summary>How deep placeholders and variables may nest.</summary>
    public const int MaxNesting = 100;

    /// <summary>
    /// The most characters that a snippet's text may have once it is rendered, its variables' values among them and each
    /// part of it counting one too: far beyond a real snippet's few lines, whose cost it bounds.
    /// </summary>
    public const int MaxText = 1_000_000;

    /// <summary>The snippet as the server wrote it.</summary>
    private readonly string _source;

    /// <summary>Its parts; null for a snippet that goes in as written.</summary>
    private readonly List<SnippetNode>? _nodes;

    private Snippet(string source, List<SnippetNode>? nodes)
    {
        _source = source;
        _nodes = nodes;
        NeedsClipboard = nodes is not null && Uses(nodes, "CLIPBOARD");
    }

    /// <summary>Whether it has the variable <c>CLIPBOARD</c>: completion reads the clipboard for it.</summary>
    public bool NeedsClipboard { get; }

    /// <summary>Parses <paramref name="text"/>, in the snippet syntax.</summary>
    public static Snippet Parse(string text)
    {
        var parser = new SnippetParser(text);
        List<SnippetNode> nodes = parser.Parse();
        if (parser.IsAborted)
            return AsWritten(text);
        List<SnippetPlaceholderNode> placeholders = Placeholders(nodes);
        if (placeholders.Count > 0 && !placeholders.Exists(p => p.Index == 0))
        {
            var final = new SnippetPlaceholderNode(0);
            nodes.Add(final);
            placeholders.Add(final);
        }
        if (!FillMirrors(placeholders))
            return AsWritten(text);
        JoinTexts(nodes);
        return new Snippet(text, nodes);
    }

    /// <summary>
    /// The text that goes in, and its stops, as VS Code adjusts and resolves them: lines after the first are indented
    /// by the line it goes in, the indentation is that of the document, line breaks are the line's, and variables have
    /// their values. A text that would be longer than <see cref="MaxText"/> goes in as written, without stops, in the
    /// document's line breaks: rendering stops there, so it costs the UI thread at most that many characters and parts,
    /// and a variable is asked for once.
    /// </summary>
    public RenderedSnippet Render(SnippetContext context)
    {
        var renderer = new SnippetRenderer(context);
        return _nodes is not null && renderer.Write(_nodes, parent: -1)
            ? new RenderedSnippet(renderer.Text.ToString(), renderer.Stops)
            : new RenderedSnippet(TextUtilities.NormalizeNewLines(_source, context.LineBreak), []);
    }

    /// <summary>A snippet that goes in as written, without stops.</summary>
    private static Snippet AsWritten(string text) => new(text, null);

    /// <summary>Whether the nodes have the variable <paramref name="name"/>, also in a default.</summary>
    private static bool Uses(List<SnippetNode> nodes, string name) =>
        nodes.Exists(node =>
            (node is SnippetVariableNode variable && variable.Name == name)
            || (node.Children is { } children && Uses(children, name))
        );

    /// <summary>The placeholders, also in variables' defaults, in the order of the text: parents before children.</summary>
    private static List<SnippetPlaceholderNode> Placeholders(List<SnippetNode> nodes)
    {
        var found = new List<SnippetPlaceholderNode>();
        Collect(nodes);
        return found;

        void Collect(List<SnippetNode> children)
        {
            foreach (SnippetNode node in children)
            {
                if (node is SnippetPlaceholderNode placeholder)
                    found.Add(placeholder);
                if (node.Children is { } nested)
                    Collect(nested);
            }
        }
    }

    /// <summary>
    /// Gives each mirror the text of its number's first placeholder with text, as VS Code does: every other
    /// placeholder of that number, except the final stop's. A mirror's text is plain, without stops of its own: it
    /// follows its stop as a whole, and its variables are those of the text it copies. False when the mirrors would add
    /// more than <see cref="MaxText"/> characters and parts, or nest deeper than <see cref="MaxNesting"/>.
    /// </summary>
    private static bool FillMirrors(List<SnippetPlaceholderNode> placeholders)
    {
        var defaults = new Dictionary<int, SnippetPlaceholderNode>();
        var mirrors = new List<SnippetPlaceholderNode>();
        foreach (SnippetPlaceholderNode placeholder in placeholders)
        {
            if (placeholder.Index == 0)
                continue;
            if (placeholder.Children.Count > 0 && defaults.TryAdd(placeholder.Index, placeholder))
                continue;
            mirrors.Add(placeholder);
        }
        int budget = MaxText;
        var filled = new List<(SnippetPlaceholderNode Mirror, List<SnippetNode> Text)>();
        foreach (SnippetPlaceholderNode mirror in mirrors)
        {
            if (!defaults.TryGetValue(mirror.Index, out SnippetPlaceholderNode? source))
                continue;
            var text = new List<SnippetNode>();
            if (!Flatten(source.Children, text, [mirror.Index], depth: 0))
                return false;
            filled.Add((mirror, text));
        }
        // Read from the snippet as written, so that the order in which mirrors fill makes no difference.
        foreach ((SnippetPlaceholderNode mirror, List<SnippetNode> text) in filled)
        {
            mirror.Children.Clear();
            mirror.Children.AddRange(text);
        }
        return true;

        // The text of nodes, with nested placeholders as the text of their number, unless that number is being
        // filled in already.
        bool Flatten(List<SnippetNode> nodes, List<SnippetNode> into, HashSet<int> filling, int depth)
        {
            if (depth > MaxNesting)
                return false;
            foreach (SnippetNode node in nodes)
            {
                budget -=
                    1
                    + node switch
                    {
                        SnippetTextNode text => text.Value.Length,
                        SnippetChoiceNode choice => choice.Value.Length,
                        _ => 0,
                    };
                if (budget < 0)
                    return false;
                switch (node)
                {
                    case SnippetTextNode or SnippetChoiceNode:
                        into.Add(node);
                        break;
                    case SnippetVariableNode variable:
                        var copy = new SnippetVariableNode(variable.Name)
                        {
                            Source = variable.Source ?? variable,
                            Transform = variable.Transform,
                        };
                        if (!Flatten(variable.Children, copy.Children, filling, depth + 1))
                            return false;
                        into.Add(copy);
                        break;
                    case SnippetPlaceholderNode placeholder:
                        if (
                            defaults.TryGetValue(placeholder.Index, out SnippetPlaceholderNode? source)
                            && filling.Add(placeholder.Index)
                        )
                        {
                            bool flattened = Flatten(source.Children, into, filling, depth + 1);
                            filling.Remove(placeholder.Index);
                            if (!flattened)
                                return false;
                        }
                        else if (!Flatten(placeholder.Children, into, filling, depth + 1))
                        {
                            return false;
                        }
                        break;
                }
            }
            return true;
        }
    }

    /// <summary>Joins adjacent texts, as VS Code joins them as it parses.</summary>
    private static void JoinTexts(List<SnippetNode> nodes)
    {
        var joined = new List<SnippetNode>(nodes.Count);
        StringBuilder? text = null;
        foreach (SnippetNode node in nodes)
        {
            if (node is SnippetTextNode { Value: var value })
            {
                (text ??= new StringBuilder()).Append(value);
                continue;
            }
            Flush();
            if (node.Children is { } children)
                JoinTexts(children);
            joined.Add(node);
        }
        Flush();
        nodes.Clear();
        nodes.AddRange(joined);

        void Flush()
        {
            if (text is null)
                return;
            joined.Add(new SnippetTextNode(text.ToString()));
            text = null;
        }
    }
}

/// <summary>A part of a snippet.</summary>
internal abstract class SnippetNode
{
    /// <summary>The parts inside; null for a text.</summary>
    public virtual List<SnippetNode>? Children => null;
}

/// <summary>Text, which goes in indented by the line it goes in.</summary>
internal sealed class SnippetTextNode(string value) : SnippetNode
{
    public string Value { get; } = value;
}

/// <summary>A choice: its first option goes in, as VS Code inserts a choice.</summary>
internal sealed class SnippetChoiceNode(IReadOnlyList<string> options) : SnippetNode
{
    public IReadOnlyList<string> Options { get; } = options;

    public string Value => Options[0];
}

/// <summary>A variable, with its default.</summary>
internal sealed class SnippetVariableNode(string name) : SnippetNode
{
    public string Name { get; } = name;

    /// <summary>What its value goes in as; null for the value itself.</summary>
    public SnippetTransform? Transform { get; set; }

    /// <summary>For a mirror's copy, the variable it copies, whose value it has; null otherwise.</summary>
    public SnippetVariableNode? Source { get; init; }

    public override List<SnippetNode> Children { get; } = [];
}

/// <summary>A stop: <c>$1</c>, or a placeholder with its text.</summary>
internal sealed class SnippetPlaceholderNode(int index) : SnippetNode
{
    public int Index { get; } = index;

    /// <summary>What its text becomes as Tab or Shift+Tab leaves its number; null for none.</summary>
    public SnippetTransform? Transform { get; set; }

    public override List<SnippetNode> Children { get; } = [];
}
