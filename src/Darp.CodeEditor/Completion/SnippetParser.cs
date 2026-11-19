using System.Text;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// VS Code's snippet parser, token by token with the same grammar and the same fallbacks. It stops once it has
/// read the text's characters four times over, going back included, or nests deeper than
/// <see cref="Snippet.MaxNesting"/>, as a text crafted to go back could otherwise take time quadratic in its length.
/// </summary>
internal sealed class SnippetParser
{
    private const string SingleCharacterTokens = "$:,{}\\/|+-?";

    private readonly string _text;

    /// <summary>How many characters parsing may read, each time it reads them.</summary>
    private readonly long _budget;

    private long _read;
    private int _position;
    private Token _token;

    public SnippetParser(string text)
    {
        _text = text;
        _budget = 4L * text.Length + 16;
        _token = Next();
    }

    /// <summary>Whether parsing stopped at a limit: the snippet goes in as written.</summary>
    public bool IsAborted { get; private set; }

    public List<SnippetNode> Parse()
    {
        var nodes = new List<SnippetNode>();
        while (ParseAny(nodes, depth: 0)) { }
        return nodes;
    }

    private bool ParseAny(List<SnippetNode> parent, int depth) =>
        ParseEscaped(parent)
        || ParseTabStopOrVariableName(parent)
        || ParseComplexPlaceholder(parent, depth)
        || ParseComplexVariable(parent, depth)
        || ParseAnything(parent);

    /// <summary><c>\$</c>, <c>\}</c> and <c>\\</c> are text; any other backslash is itself.</summary>
    private bool ParseEscaped(List<SnippetNode> parent)
    {
        if (AcceptText(TokenType.Backslash) is not { } backslash)
            return false;
        string text =
            AcceptText(TokenType.Dollar)
            ?? AcceptText(TokenType.CurlyClose)
            ?? AcceptText(TokenType.Backslash)
            ?? backslash;
        parent.Add(new SnippetTextNode(text));
        return true;
    }

    /// <summary><c>$1</c> is a stop, <c>$NAME</c> a variable.</summary>
    private bool ParseTabStopOrVariableName(List<SnippetNode> parent)
    {
        Token start = _token;
        if (!Accept(TokenType.Dollar))
            return BackTo(start);
        if (AcceptText(TokenType.VariableName) is { } name)
        {
            parent.Add(new SnippetVariableNode(name));
            return true;
        }
        if (AcceptText(TokenType.Int) is { } digits && int.TryParse(digits, out int index))
        {
            parent.Add(new SnippetPlaceholderNode(index));
            return true;
        }
        return BackTo(start);
    }

    /// <summary><c>${1}</c>, <c>${1:text}</c>, <c>${1|a,b|}</c> and <c>${1/regex/format/options}</c>.</summary>
    private bool ParseComplexPlaceholder(List<SnippetNode> parent, int depth)
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
        var placeholder = new SnippetPlaceholderNode(index);
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
                    placeholder.Children.Add(new SnippetChoiceNode(options));
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
    private bool ParseComplexVariable(List<SnippetNode> parent, int depth)
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
        var variable = new SnippetVariableNode(name);
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
    private bool ParseChildren(
        List<SnippetNode> parent,
        SnippetNode node,
        List<SnippetNode> children,
        string opening,
        int depth
    )
    {
        if (depth >= Snippet.MaxNesting)
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
            parent.Add(new SnippetTextNode(opening));
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
    private bool ParseAnything(List<SnippetNode> parent)
    {
        if (_token.Type == TokenType.End)
            return false;
        parent.Add(new SnippetTextNode(AcceptAnyText()));
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
                _position < _text.Length && (IsNameCharacter(_text[_position]) || char.IsAsciiDigit(_text[_position]))
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
