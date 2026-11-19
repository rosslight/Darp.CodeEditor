using System.Text;
using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;

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
    private readonly List<Node>? _nodes;

    private Snippet(string source, List<Node>? nodes)
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
        var parser = new Parser(text);
        List<Node> nodes = parser.Parse();
        if (parser.IsAborted)
            return AsWritten(text);
        List<PlaceholderNode> placeholders = Placeholders(nodes);
        if (placeholders.Count > 0 && !placeholders.Exists(p => p.Index == 0))
        {
            var final = new PlaceholderNode(0);
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
    /// their values. A text that would be longer than <see cref="MaxText"/> goes in as written, without stops: rendering
    /// stops there, so it costs the UI thread at most that many characters and parts, and a variable is asked for once.
    /// </summary>
    public RenderedSnippet Render(SnippetContext context)
    {
        if (_nodes is null)
            return new RenderedSnippet(_source, []);
        var renderer = new Renderer(context);
        return renderer.Write(_nodes, parent: -1)
            ? new RenderedSnippet(renderer.Text.ToString(), renderer.Stops)
            : new RenderedSnippet(_source, []);
    }

    /// <summary>A snippet that goes in as written, without stops.</summary>
    private static Snippet AsWritten(string text) => new(text, null);

    /// <summary>Whether the nodes have the variable <paramref name="name"/>, also in a default.</summary>
    private static bool Uses(List<Node> nodes, string name) =>
        nodes.Exists(node =>
            (node is VariableNode variable && variable.Name == name)
            || (node.Children is { } children && Uses(children, name))
        );

    /// <summary>The placeholders, also in variables' defaults, in the order of the text: parents before children.</summary>
    private static List<PlaceholderNode> Placeholders(List<Node> nodes)
    {
        var found = new List<PlaceholderNode>();
        Collect(nodes);
        return found;

        void Collect(List<Node> children)
        {
            foreach (Node node in children)
            {
                if (node is PlaceholderNode placeholder)
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
    private static bool FillMirrors(List<PlaceholderNode> placeholders)
    {
        var defaults = new Dictionary<int, PlaceholderNode>();
        var mirrors = new List<PlaceholderNode>();
        foreach (PlaceholderNode placeholder in placeholders)
        {
            if (placeholder.Index == 0)
                continue;
            if (placeholder.Children.Count > 0 && defaults.TryAdd(placeholder.Index, placeholder))
                continue;
            mirrors.Add(placeholder);
        }
        int budget = MaxText;
        var filled = new List<(PlaceholderNode Mirror, List<Node> Text)>();
        foreach (PlaceholderNode mirror in mirrors)
        {
            if (!defaults.TryGetValue(mirror.Index, out PlaceholderNode? source))
                continue;
            var text = new List<Node>();
            if (!Flatten(source.Children, text, [mirror.Index], depth: 0))
                return false;
            filled.Add((mirror, text));
        }
        // Read from the snippet as written, so that the order in which mirrors fill makes no difference.
        foreach ((PlaceholderNode mirror, List<Node> text) in filled)
        {
            mirror.Children.Clear();
            mirror.Children.AddRange(text);
        }
        return true;

        // The text of nodes, with nested placeholders as the text of their number, unless that number is being
        // filled in already.
        bool Flatten(List<Node> nodes, List<Node> into, HashSet<int> filling, int depth)
        {
            if (depth > MaxNesting)
                return false;
            foreach (Node node in nodes)
            {
                budget -=
                    1
                    + node switch
                    {
                        TextNode text => text.Value.Length,
                        ChoiceNode choice => choice.Value.Length,
                        _ => 0,
                    };
                if (budget < 0)
                    return false;
                switch (node)
                {
                    case TextNode or ChoiceNode:
                        into.Add(node);
                        break;
                    case VariableNode variable:
                        var copy = new VariableNode(variable.Name)
                        {
                            Source = variable.Source ?? variable,
                            Transform = variable.Transform,
                        };
                        if (!Flatten(variable.Children, copy.Children, filling, depth + 1))
                            return false;
                        into.Add(copy);
                        break;
                    case PlaceholderNode placeholder:
                        if (
                            defaults.TryGetValue(placeholder.Index, out PlaceholderNode? source)
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
    private static void JoinTexts(List<Node> nodes)
    {
        var joined = new List<Node>(nodes.Count);
        StringBuilder? text = null;
        foreach (Node node in nodes)
        {
            if (node is TextNode { Value: var value })
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
            joined.Add(new TextNode(text.ToString()));
            text = null;
        }
    }

    /// <summary>A part of a snippet.</summary>
    private abstract class Node
    {
        /// <summary>The parts inside; null for a text.</summary>
        public virtual List<Node>? Children => null;
    }

    /// <summary>Text, which goes in indented by the line it goes in.</summary>
    private sealed class TextNode(string value) : Node
    {
        public string Value { get; } = value;
    }

    /// <summary>A choice: its first option goes in, as VS Code inserts a choice.</summary>
    private sealed class ChoiceNode(IReadOnlyList<string> options) : Node
    {
        public IReadOnlyList<string> Options { get; } = options;

        public string Value => Options[0];
    }

    /// <summary>A variable, with its default.</summary>
    private sealed class VariableNode(string name) : Node
    {
        public string Name { get; } = name;

        /// <summary>What its value goes in as; null for the value itself.</summary>
        public SnippetTransform? Transform { get; set; }

        /// <summary>For a mirror's copy, the variable it copies, whose value it has; null otherwise.</summary>
        public VariableNode? Source { get; init; }

        public override List<Node> Children { get; } = [];
    }

    /// <summary>A stop: <c>$1</c>, or a placeholder with its text.</summary>
    private sealed class PlaceholderNode(int index) : Node
    {
        public int Index { get; } = index;

        /// <summary>What its text becomes as Tab or Shift+Tab leaves its number; null for none.</summary>
        public SnippetTransform? Transform { get; set; }

        public override List<Node> Children { get; } = [];
    }

    /// <summary>
    /// VS Code's snippet parser, token by token with the same grammar and the same fallbacks. It stops once it has
    /// read the text's characters four times over, going back included, or nests deeper than
    /// <see cref="MaxNesting"/>, as a text crafted to go back could otherwise take time quadratic in its length.
    /// </summary>
    private sealed class Parser
    {
        private const string SingleCharacterTokens = "$:,{}\\/|+-?";

        private readonly string _text;

        /// <summary>How many characters parsing may read, each time it reads them.</summary>
        private readonly long _budget;

        private long _read;
        private int _position;
        private Token _token;

        public Parser(string text)
        {
            _text = text;
            _budget = 4L * text.Length + 16;
            _token = Next();
        }

        /// <summary>Whether parsing stopped at a limit: the snippet goes in as written.</summary>
        public bool IsAborted { get; private set; }

        public List<Node> Parse()
        {
            var nodes = new List<Node>();
            while (ParseAny(nodes, depth: 0)) { }
            return nodes;
        }

        private bool ParseAny(List<Node> parent, int depth) =>
            ParseEscaped(parent)
            || ParseTabStopOrVariableName(parent)
            || ParseComplexPlaceholder(parent, depth)
            || ParseComplexVariable(parent, depth)
            || ParseAnything(parent);

        /// <summary><c>\$</c>, <c>\}</c> and <c>\\</c> are text; any other backslash is itself.</summary>
        private bool ParseEscaped(List<Node> parent)
        {
            if (AcceptText(TokenType.Backslash) is not { } backslash)
                return false;
            string text =
                AcceptText(TokenType.Dollar)
                ?? AcceptText(TokenType.CurlyClose)
                ?? AcceptText(TokenType.Backslash)
                ?? backslash;
            parent.Add(new TextNode(text));
            return true;
        }

        /// <summary><c>$1</c> is a stop, <c>$NAME</c> a variable.</summary>
        private bool ParseTabStopOrVariableName(List<Node> parent)
        {
            Token start = _token;
            if (!Accept(TokenType.Dollar))
                return BackTo(start);
            if (AcceptText(TokenType.VariableName) is { } name)
            {
                parent.Add(new VariableNode(name));
                return true;
            }
            if (AcceptText(TokenType.Int) is { } digits && int.TryParse(digits, out int index))
            {
                parent.Add(new PlaceholderNode(index));
                return true;
            }
            return BackTo(start);
        }

        /// <summary><c>${1}</c>, <c>${1:text}</c>, <c>${1|a,b|}</c> and <c>${1/regex/format/options}</c>.</summary>
        private bool ParseComplexPlaceholder(List<Node> parent, int depth)
        {
            Token start = _token;
            if (
                !Accept(TokenType.Dollar)
                || !Accept(TokenType.CurlyOpen)
                || AcceptText(TokenType.Int) is not { } digits
                || !int.TryParse(digits, out int index)
            )
            {
                return BackTo(start);
            }
            var placeholder = new PlaceholderNode(index);
            if (Accept(TokenType.Colon))
                return ParseChildren(parent, placeholder, placeholder.Children, "${" + digits + ":", depth);
            if (index > 0 && Accept(TokenType.Pipe))
            {
                var options = new List<string>();
                while (ParseChoiceOption(options))
                {
                    if (Accept(TokenType.Comma))
                        continue;
                    if (Accept(TokenType.Pipe) && Accept(TokenType.CurlyClose))
                    {
                        placeholder.Children.Add(new ChoiceNode(options));
                        parent.Add(placeholder);
                        return true;
                    }
                    break;
                }
                return BackTo(start);
            }
            if (Accept(TokenType.Forwardslash))
            {
                if (ParseTransform() is not { } transform)
                    return BackTo(start);
                placeholder.Transform = transform;
                parent.Add(placeholder);
                return true;
            }
            if (Accept(TokenType.CurlyClose))
            {
                parent.Add(placeholder);
                return true;
            }
            return BackTo(start);
        }

        /// <summary><c>${NAME}</c>, <c>${NAME:default}</c> and <c>${NAME/regex/format/options}</c>.</summary>
        private bool ParseComplexVariable(List<Node> parent, int depth)
        {
            Token start = _token;
            if (
                !Accept(TokenType.Dollar)
                || !Accept(TokenType.CurlyOpen)
                || AcceptText(TokenType.VariableName) is not { } name
            )
            {
                return BackTo(start);
            }
            var variable = new VariableNode(name);
            if (Accept(TokenType.Colon))
                return ParseChildren(parent, variable, variable.Children, "${" + name + ":", depth);
            if (Accept(TokenType.Forwardslash))
            {
                if (ParseTransform() is not { } transform)
                    return BackTo(start);
                variable.Transform = transform;
                parent.Add(variable);
                return true;
            }
            if (Accept(TokenType.CurlyClose))
            {
                parent.Add(variable);
                return true;
            }
            return BackTo(start);
        }

        /// <summary>
        /// The text of a placeholder or a variable's default, up to its <c>}</c>. Without one, it is text: its start
        /// as written, and what was read of it.
        /// </summary>
        private bool ParseChildren(List<Node> parent, Node node, List<Node> children, string opening, int depth)
        {
            if (depth >= MaxNesting)
            {
                Abort();
                return false;
            }
            while (true)
            {
                if (Accept(TokenType.CurlyClose))
                {
                    parent.Add(node);
                    return true;
                }
                if (ParseAny(children, depth + 1))
                    continue;
                parent.Add(new TextNode(opening));
                parent.AddRange(children);
                return true;
            }
        }

        /// <summary>An option of a choice, with <c>\,</c>, <c>\|</c> and <c>\\</c> as text; false for an empty one.</summary>
        private bool ParseChoiceOption(List<string> options)
        {
            Token start = _token;
            var option = new StringBuilder();
            while (_token.Type is not (TokenType.Comma or TokenType.Pipe))
            {
                string text = AcceptText(TokenType.Backslash) is { } backslash
                    ? AcceptText(TokenType.Comma)
                        ?? AcceptText(TokenType.Pipe)
                        ?? AcceptText(TokenType.Backslash)
                        ?? backslash
                    : AcceptAnyText();
                if (text.Length == 0)
                    return BackTo(start);
                option.Append(text);
            }
            if (option.Length == 0)
                return BackTo(start);
            options.Add(option.ToString());
            return true;
        }

        /// <summary>
        /// A transformation after its first <c>/</c>, read as VS Code reads it: the regular expression, the format, and
        /// JavaScript's options up to <c>}</c>; null when it does not parse, or its regular expression or its options
        /// are invalid.
        /// </summary>
        private SnippetTransform? ParseTransform()
        {
            var pattern = new StringBuilder();
            while (!Accept(TokenType.Forwardslash))
            {
                if (AcceptText(TokenType.Backslash) is { } backslash)
                    pattern.Append(AcceptText(TokenType.Forwardslash) ?? backslash);
                else if (_token.Type == TokenType.End)
                    return null;
                else
                    pattern.Append(AcceptAnyText());
            }
            var format = new List<SnippetFormatPart>();
            while (!Accept(TokenType.Forwardslash))
            {
                if (AcceptText(TokenType.Backslash) is { } backslash)
                {
                    format.Add(
                        new SnippetFormatText(
                            AcceptText(TokenType.Backslash) ?? AcceptText(TokenType.Forwardslash) ?? backslash
                        )
                    );
                }
                else if (!ParseFormatString(format))
                {
                    if (_token.Type == TokenType.End)
                        return null;
                    format.Add(new SnippetFormatText(AcceptAnyText()));
                }
            }
            var options = new StringBuilder();
            while (!Accept(TokenType.CurlyClose))
            {
                if (_token.Type == TokenType.End)
                    return null;
                options.Append(AcceptAnyText());
            }
            return SnippetTransform.Create(pattern.ToString(), options.ToString(), format);
        }

        /// <summary>
        /// A group reference of a format: <c>$1</c>, <c>${1}</c>, <c>${1:/upcase}</c>, <c>${1:+if}</c>,
        /// <c>${1:-else}</c>, <c>${1:?if:else}</c> or <c>${1:else}</c>.
        /// </summary>
        private bool ParseFormatString(List<SnippetFormatPart> format)
        {
            Token start = _token;
            if (!Accept(TokenType.Dollar))
                return false;
            bool complex = Accept(TokenType.CurlyOpen);
            if (AcceptText(TokenType.Int) is not { } digits)
                return BackTo(start);
            // A number too large for a group is a group that never matches.
            int index = int.TryParse(digits, out int parsed) ? parsed : int.MaxValue;
            if (!complex || Accept(TokenType.CurlyClose))
            {
                format.Add(new SnippetFormatGroup(index, null, null, null));
                return true;
            }
            if (!Accept(TokenType.Colon))
                return BackTo(start);
            SnippetFormatGroup? group = null;
            if (Accept(TokenType.Forwardslash))
            {
                if (AcceptText(TokenType.VariableName) is { } shorthand && Accept(TokenType.CurlyClose))
                    group = new SnippetFormatGroup(index, shorthand, null, null);
            }
            else if (Accept(TokenType.Plus))
            {
                if (Until(TokenType.CurlyClose) is { } ifValue)
                    group = new SnippetFormatGroup(index, null, ifValue, null);
            }
            else if (Accept(TokenType.Dash))
            {
                if (Until(TokenType.CurlyClose) is { } elseValue)
                    group = new SnippetFormatGroup(index, null, null, elseValue);
            }
            else if (Accept(TokenType.QuestionMark))
            {
                if (Until(TokenType.Colon) is { } ifValue && Until(TokenType.CurlyClose) is { } elseValue)
                    group = new SnippetFormatGroup(index, null, ifValue, elseValue);
            }
            else if (Until(TokenType.CurlyClose) is { } elseValue)
            {
                group = new SnippetFormatGroup(index, null, null, elseValue);
            }
            if (group is null)
                return BackTo(start);
            format.Add(group);
            return true;
        }

        /// <summary>
        /// Reads up to <paramref name="type"/> and past it, with <c>\$</c>, <c>\}</c> and <c>\\</c> as text: what came
        /// before it, unescaped. Null when nothing comes before it, or when the text ends or another escape comes first.
        /// </summary>
        private string? Until(TokenType type)
        {
            Token start = _token;
            while (_token.Type != type)
            {
                if (_token.Type == TokenType.End)
                    return null;
                if (
                    _token.Type == TokenType.Backslash
                    && Next().Type is not (TokenType.Dollar or TokenType.CurlyClose or TokenType.Backslash)
                )
                {
                    return null;
                }
                _token = Next();
            }
            ReadOnlySpan<char> read = _text.AsSpan(start.Position, _token.Position - start.Position);
            _token = Next();
            if (read.IsEmpty)
                return null;
            var value = new StringBuilder(read.Length);
            for (int i = 0; i < read.Length; i++)
            {
                if (read[i] == '\\' && i + 1 < read.Length && read[i + 1] is '$' or '}' or '\\')
                    i++;
                value.Append(read[i]);
            }
            return value.ToString();
        }

        /// <summary>Any token as text.</summary>
        private bool ParseAnything(List<Node> parent)
        {
            if (_token.Type == TokenType.End)
                return false;
            parent.Add(new TextNode(AcceptAnyText()));
            return true;
        }

        private bool Accept(TokenType type)
        {
            if (_token.Type != type)
                return false;
            _token = Next();
            return true;
        }

        private string? AcceptText(TokenType type)
        {
            if (_token.Type != type)
                return null;
            return AcceptAnyText();
        }

        /// <summary>The token's text, and on to the next; empty at the end.</summary>
        private string AcceptAnyText()
        {
            string text = _text.Substring(_token.Position, _token.Length);
            _token = Next();
            return text;
        }

        /// <summary>Returns to <paramref name="token"/>, to read it again in another way.</summary>
        private bool BackTo(Token token)
        {
            _position = token.Position + token.Length;
            _token = token;
            return false;
        }

        private void Abort()
        {
            IsAborted = true;
            _token = new Token(TokenType.End, _position, 0);
        }

        /// <summary>
        /// The next token: a single character of <see cref="SingleCharacterTokens"/>, a number, a name, or a run of
        /// anything else.
        /// </summary>
        private Token Next()
        {
            if (_position >= _text.Length || IsAborted)
                return new Token(TokenType.End, _position, 0);
            int start = _position;
            char c = _text[start];
            int single = SingleCharacterTokens.IndexOf(c);
            TokenType type;
            if (single >= 0)
            {
                _position++;
                type = (TokenType)single;
            }
            else if (char.IsAsciiDigit(c))
            {
                while (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
                    _position++;
                type = TokenType.Int;
            }
            else if (IsNameCharacter(c))
            {
                while (
                    _position < _text.Length
                    && (IsNameCharacter(_text[_position]) || char.IsAsciiDigit(_text[_position]))
                )
                {
                    _position++;
                }
                type = TokenType.VariableName;
            }
            else
            {
                do _position++;
                while (
                    _position < _text.Length
                    && SingleCharacterTokens.IndexOf(_text[_position]) < 0
                    && !char.IsAsciiDigit(_text[_position])
                    && !IsNameCharacter(_text[_position])
                );
                type = TokenType.Format;
            }
            _read += _position - start;
            if (_read > _budget)
            {
                Abort();
                return _token;
            }
            return new Token(type, start, _position - start);
        }

        private static bool IsNameCharacter(char c) => c == '_' || char.IsAsciiLetter(c);

        /// <summary>The kinds of tokens; the first ones in the order of <see cref="SingleCharacterTokens"/>.</summary>
        private enum TokenType
        {
            Dollar,
            Colon,
            Comma,
            CurlyOpen,
            CurlyClose,
            Backslash,
            Forwardslash,
            Pipe,
            Plus,
            Dash,
            QuestionMark,
            Int,
            VariableName,
            Format,
            End,
        }

        private readonly record struct Token(TokenType Type, int Position, int Length);
    }

    /// <summary>Writes the nodes' text and records the stops, as <see cref="Render"/> describes.</summary>
    private sealed class Renderer
    {
        private readonly SnippetContext _context;
        private readonly Indenting _indenting;

        /// <summary>The width of the line's indentation in columns, which each line after a line break starts with.</summary>
        private readonly int _lineColumns;

        /// <summary>The variables' values: each is asked for once, and a mirror's copy has its source's.</summary>
        private readonly Dictionary<VariableNode, string?> _values = [];

        /// <summary>The characters and parts that the text may still take.</summary>
        private int _budget = MaxText;

        /// <summary>
        /// The whitespace that the last line of what went in last starts with, the snippet's text or a variable's value,
        /// as VS Code finds it for selected text that spans lines; null before anything went in.
        /// </summary>
        private string? _lastIndentation;

        public Renderer(SnippetContext context)
        {
            _context = context;
            _indenting = new Indenting(context.Indentation);
            _lineColumns = _indenting.Width(context.LineIndentation);
        }

        public StringBuilder Text { get; } = new();

        public List<SnippetStop> Stops { get; } = [];

        /// <summary>Writes the nodes; false once the text would be longer than <see cref="MaxText"/>.</summary>
        public bool Write(List<Node> nodes, int parent)
        {
            foreach (Node node in nodes)
            {
                if (--_budget < 0)
                    return false;
                switch (node)
                {
                    case TextNode text:
                        if (!WriteText(text.Value))
                            return false;
                        break;
                    case ChoiceNode choice:
                        // Its lines keep their indentation; its line breaks, as everything's that goes in, are the
                        // document's.
                        if (!Append(TextUtilities.NormalizeNewLines(choice.Value, _context.LineBreak)))
                            return false;
                        break;
                    case VariableNode variable:
                        VariableNode source = variable.Source ?? variable;
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
                    case PlaceholderNode placeholder:
                        int index = Stops.Count;
                        int start = Text.Length;
                        Stops.Add(
                            new SnippetStop(placeholder.Index, start, start, parent)
                            {
                                Transform = placeholder.Transform,
                                Choice = placeholder.Children is [ChoiceNode { Options: var options }] ? options : null,
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
        /// spans lines gets the indentation that the snippet adds where it goes in, on each of its lines after the first.
        /// A value longer than what is left is not read.
        /// </summary>
        private bool WriteValue(VariableNode variable, string value)
        {
            if (value.Length > _budget)
                return false;
            if (
                variable.Name is "SELECTION" or "TM_SELECTED_TEXT"
                && _context.MultilineSelectionIndentation is { } selectionIndentation
            )
            {
                value = SnippetVariables.IndentSelection(
                    value,
                    _lastIndentation ?? selectionIndentation,
                    selectionIndentation
                );
            }
            value = TextUtilities.NormalizeNewLines(value, _context.LineBreak);
            if (!Append(value))
                return false;
            _lastIndentation = LeadingWhitespace(value.AsSpan(value.LastIndexOfAny(['\r', '\n']) + 1));
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
                    Text.Length == 0 ? WriteIndented(line, column: 0)
                    : Text[^1] is '\r' or '\n' ? WriteIndented(line, _lineColumns)
                    : Append(line);
                if (!written)
                    return false;
                if (length < 0)
                {
                    // The line as it went in: indented, at a line's start, or as written after other text.
                    _lastIndentation = LeadingWhitespace(Text.ToString(lineStart, Text.Length - lineStart));
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

        /// <summary>The spaces and tabs that <paramref name="line"/> starts with.</summary>
        private static string LeadingWhitespace(ReadOnlySpan<char> line)
        {
            int length = 0;
            while (length < line.Length && line[length] is ' ' or '\t')
                length++;
            return line[..length].ToString();
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
    /// <c>SELECTION</c> and <c>TM_SELECTED_TEXT</c> go in with <see cref="SnippetVariables.IndentSelection"/>. Null for
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
