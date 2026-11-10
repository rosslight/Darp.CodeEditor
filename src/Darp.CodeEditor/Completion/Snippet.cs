using System.Text;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// A completion item's text in the LSP snippet syntax, parsed as VS Code parses it: tab stops <c>$1</c> and
/// <c>${1}</c>, placeholders <c>${1:text}</c>, also nested, choices <c>${1|a,b|}</c>, of which the first goes in, the
/// final stop <c>$0</c>, variables <c>$NAME</c> and <c>${NAME:default}</c>, and the escapes <c>\$</c>, <c>\}</c> and
/// <c>\\</c>, and <c>\,</c> and <c>\|</c> in choices. What does not parse is text, as in VS Code.
/// </summary>
/// <remarks>
/// <para>
/// Stops of one number are mirrors: the first one with text gives every other one its text, as in VS Code, and a
/// mirror's variables have that text's values. A variable that VS Code does not know, without a default, becomes a
/// placeholder with its name, as the protocol says. Transformations, such as <c>${1/(.*)/${1:/upcase}/}</c>, are read
/// and not applied: a variable goes in as its value, and a stop with one is a mirror of its number. A snippet with stops
/// gets a final stop at its end, unless it has one.
/// </para>
/// <para>
/// The limits are sized for real snippets, which have a few lines and fewer than about 20 stops, so that the UI thread's
/// work with a snippet is bounded by small constants, whatever the server sends. Parsing runs where the server's answer
/// is read, off the UI thread, and reads the characters at most four times over. A snippet that parsing would read more
/// often, nested deeper than <see cref="MaxNesting"/> levels, with more than <see cref="MaxStops"/> stops, mirrors
/// included, or whose text would be longer than <see cref="MaxText"/> characters with its mirrors, its variables'
/// values and its indentation goes in as written, without stops, as a plain item goes in.
/// </para>
/// </remarks>
internal sealed class Snippet
{
    /// <summary>
    /// The most stops that a snippet tracks, mirrors included: more than twice a real snippet's, and few enough that each
    /// edit and caret move can look at all of them.
    /// </summary>
    public const int MaxStops = 50;

    /// <summary>How deep placeholders and variables may nest.</summary>
    public const int MaxNesting = 100;

    /// <summary>
    /// The most characters that a snippet's text may have once it is rendered, each part of it counting one too: many
    /// times a real snippet's few lines.
    /// </summary>
    public const int MaxText = 5_000;

    /// <summary>The variables that VS Code knows; others without a default become placeholders.</summary>
    private static readonly HashSet<string> s_knownVariables =
    [
        "CURRENT_YEAR",
        "CURRENT_YEAR_SHORT",
        "CURRENT_MONTH",
        "CURRENT_DATE",
        "CURRENT_HOUR",
        "CURRENT_MINUTE",
        "CURRENT_SECOND",
        "CURRENT_MILLISECOND",
        "CURRENT_DAY_NAME",
        "CURRENT_DAY_NAME_SHORT",
        "CURRENT_MONTH_NAME",
        "CURRENT_MONTH_NAME_SHORT",
        "CURRENT_SECONDS_UNIX",
        "CURRENT_MILLISECONDS_UNIX",
        "CURRENT_TIMEZONE_OFFSET",
        "CURRENT_TIMEZONE_NAME",
        "SELECTION",
        "CLIPBOARD",
        "TM_SELECTED_TEXT",
        "TM_CURRENT_LINE",
        "TM_CURRENT_WORD",
        "TM_LINE_INDEX",
        "TM_LINE_NUMBER",
        "TM_FILENAME",
        "TM_FILENAME_BASE",
        "TM_DIRECTORY",
        "TM_DIRECTORY_BASE",
        "TM_FILEPATH",
        "CURSOR_INDEX",
        "CURSOR_NUMBER",
        "RELATIVE_FILEPATH",
        "BLOCK_COMMENT_START",
        "BLOCK_COMMENT_END",
        "LINE_COMMENT",
        "WORKSPACE_NAME",
        "WORKSPACE_FOLDER",
        "RANDOM",
        "RANDOM_HEX",
        "UUID",
    ];

    /// <summary>The snippet as the server wrote it.</summary>
    private readonly string _source;

    /// <summary>Its parts; null for a snippet that goes in as written.</summary>
    private readonly List<Node>? _nodes;

    private Snippet(string source, List<Node>? nodes)
    {
        _source = source;
        _nodes = nodes;
    }

    /// <summary>Parses <paramref name="text"/>, in the snippet syntax.</summary>
    public static Snippet Parse(string text)
    {
        var parser = new Parser(text);
        List<Node> nodes = parser.Parse();
        if (parser.IsAborted)
            return AsWritten(text);
        PlaceholdersForUnknownVariables(nodes);
        List<PlaceholderNode> placeholders = Placeholders(nodes);
        if (placeholders.Count > 0 && !placeholders.Exists(p => p.Index == 0))
        {
            var final = new PlaceholderNode(0);
            nodes.Add(final);
            placeholders.Add(final);
        }
        if (placeholders.Count > MaxStops || !FillMirrors(placeholders))
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

    /// <summary>
    /// Turns each variable without a default that VS Code does not know into a placeholder with its name, numbered
    /// after the highest stop, the same name with the same number, in the order VS Code finds them: level by level. As
    /// the protocol says, also in a variable's default, where VS Code's own snippets leave them.
    /// </summary>
    private static void PlaceholdersForUnknownVariables(List<Node> nodes)
    {
        int highest = Placeholders(nodes).Select(p => p.Index).DefaultIfEmpty(0).Max();
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<List<Node>>([nodes]);
        while (queue.TryDequeue(out List<Node>? level))
        {
            for (int i = 0; i < level.Count; i++)
            {
                if (
                    level[i] is VariableNode { Children.Count: 0 } variable
                    && !s_knownVariables.Contains(variable.Name)
                )
                {
                    if (!numbers.TryGetValue(variable.Name, out int index))
                        numbers[variable.Name] = index = ++highest;
                    var placeholder = new PlaceholderNode(index);
                    placeholder.Children.Add(new TextNode(variable.Name));
                    level[i] = placeholder;
                }
                else if (level[i].Children is { } children)
                {
                    queue.Enqueue(children);
                }
            }
        }
    }

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
                        var copy = new VariableNode(variable.Name) { Source = variable.Source ?? variable };
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

    /// <summary>The first option of a choice, which goes in as it is, as VS Code inserts a choice.</summary>
    private sealed class ChoiceNode(string value) : Node
    {
        public string Value { get; } = value;
    }

    /// <summary>A variable, with its default.</summary>
    private sealed class VariableNode(string name) : Node
    {
        public string Name { get; } = name;

        /// <summary>For a mirror's copy, the variable it copies, whose value it has; null otherwise.</summary>
        public VariableNode? Source { get; init; }

        public override List<Node> Children { get; } = [];
    }

    /// <summary>A stop: <c>$1</c>, or a placeholder with its text.</summary>
    private sealed class PlaceholderNode(int index) : Node
    {
        public int Index { get; } = index;

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
                        placeholder.Children.Add(new ChoiceNode(options[0]));
                        parent.Add(placeholder);
                        return true;
                    }
                    break;
                }
                return BackTo(start);
            }
            if (Accept(TokenType.Forwardslash))
            {
                if (!SkipTransform())
                    return BackTo(start);
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
                if (!SkipTransform())
                    return BackTo(start);
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
        /// A transformation after its first <c>/</c>, read as VS Code reads it, and dropped: the regular expression,
        /// the format, and JavaScript's options up to <c>}</c>. The regular expression is not checked.
        /// </summary>
        private bool SkipTransform()
        {
            while (!Accept(TokenType.Forwardslash))
            {
                if (Accept(TokenType.Backslash))
                    Accept(TokenType.Forwardslash);
                else if (_token.Type == TokenType.End)
                    return false;
                else
                    AcceptAnyText();
            }
            while (!Accept(TokenType.Forwardslash))
            {
                if (Accept(TokenType.Backslash))
                {
                    if (!Accept(TokenType.Backslash))
                        Accept(TokenType.Forwardslash);
                }
                else if (!SkipFormatString() && AcceptAnyText().Length == 0)
                {
                    return false;
                }
            }
            var options = new StringBuilder();
            while (!Accept(TokenType.CurlyClose))
            {
                if (AcceptAnyText() is not { Length: > 0 } text)
                    return false;
                options.Append(text);
            }
            string flags = options.ToString();
            return flags.All("dgimsuvy".Contains) && flags.Distinct().Count() == flags.Length;
        }

        /// <summary>
        /// A group reference of a format: <c>$1</c>, <c>${1}</c>, <c>${1:/upcase}</c>, <c>${1:+if}</c>,
        /// <c>${1:-else}</c>, <c>${1:?if:else}</c> or <c>${1:else}</c>.
        /// </summary>
        private bool SkipFormatString()
        {
            Token start = _token;
            if (!Accept(TokenType.Dollar))
                return false;
            bool complex = Accept(TokenType.CurlyOpen);
            if (!Accept(TokenType.Int))
                return BackTo(start);
            if (!complex || Accept(TokenType.CurlyClose))
                return true;
            if (!Accept(TokenType.Colon))
                return BackTo(start);
            bool read;
            if (Accept(TokenType.Forwardslash))
                read = Accept(TokenType.VariableName) && Accept(TokenType.CurlyClose);
            else if (Accept(TokenType.Plus) || Accept(TokenType.Dash))
                read = Until(TokenType.CurlyClose);
            else if (Accept(TokenType.QuestionMark))
                read = Until(TokenType.Colon) && Until(TokenType.CurlyClose);
            else
                read = Until(TokenType.CurlyClose);
            return read || BackTo(start);
        }

        /// <summary>
        /// Reads up to <paramref name="type"/> and past it, with <c>\$</c>, <c>\}</c> and <c>\\</c> as text; false
        /// when nothing comes before it, or when the text ends or another escape comes first.
        /// </summary>
        private bool Until(TokenType type)
        {
            Token start = _token;
            while (_token.Type != type)
            {
                if (_token.Type == TokenType.End)
                    return false;
                if (
                    _token.Type == TokenType.Backslash
                    && Next().Type is not (TokenType.Dollar or TokenType.CurlyClose or TokenType.Backslash)
                )
                {
                    return false;
                }
                _token = Next();
            }
            bool empty = _token.Position == start.Position;
            _token = Next();
            return !empty;
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
                        if (!Append(choice.Value))
                            return false;
                        break;
                    case VariableNode variable:
                        VariableNode source = variable.Source ?? variable;
                        if (!_values.TryGetValue(source, out string? value))
                            _values[source] = value = _context.Variable(variable.Name);
                        if (value is null ? !Write(variable.Children, parent) : !Append(value))
                            return false;
                        break;
                    case PlaceholderNode placeholder:
                        int index = Stops.Count;
                        int start = Text.Length;
                        Stops.Add(new SnippetStop(placeholder.Index, start, start, parent));
                        if (!Write(placeholder.Children, index))
                            return false;
                        Stops[index] = Stops[index] with { End = Text.Length };
                        break;
                }
            }
            return true;
        }

        /// <summary>
        /// Text, as VS Code adjusts it: each line after a line break starts with the indentation of the line the
        /// snippet goes in, and its own; the indentation at the start of the snippet or of a line is the document's;
        /// line breaks are the line's. A text longer than what is left is not read.
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
                bool written =
                    Text.Length == 0 ? WriteIndented(line, column: 0)
                    : Text[^1] is '\r' or '\n' ? WriteIndented(line, _lineColumns)
                    : Append(line);
                if (!written)
                    return false;
                if (length < 0)
                    return true;
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
/// <param name="LineBreak">The line break of the line it goes in.</param>
/// <param name="Variable">The value of a variable by its name; null when it has none.</param>
internal sealed record SnippetContext(
    string LineIndentation,
    Indentation Indentation,
    string LineBreak,
    Func<string, string?> Variable
);

/// <summary>A snippet's text and its stops, ready to go in.</summary>
/// <param name="Text">The text.</param>
/// <param name="Stops">The stops, mirrors included, in the order of the text: placeholders before those inside them.</param>
internal sealed record RenderedSnippet(string Text, IReadOnlyList<SnippetStop> Stops);

/// <summary>A stop of a rendered snippet.</summary>
/// <param name="Index">Its number: 1 and up, and 0 for the final stop.</param>
/// <param name="Start">Where its text starts in the snippet's text.</param>
/// <param name="End">Where its text ends.</param>
/// <param name="Parent">The position of the placeholder it is inside in the list of stops; -1 for none.</param>
internal readonly record struct SnippetStop(int Index, int Start, int End, int Parent);
