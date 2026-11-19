using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The language's brackets of one character each, by kind, as <see cref="BracketMatcher"/> scans for them: a kind is
/// an opening character, which may have several closing ones, as <c>(</c> may for <c>)</c> and <c>]</c>.
/// </summary>
internal sealed class BracketKinds
{
    private readonly string _opens;
    private readonly string _closes;

    /// <summary>The kind that each closing character of <see cref="_closes"/> closes.</summary>
    private readonly int[] _closed;

    public BracketKinds(IReadOnlyList<CharacterPair> brackets)
    {
        string opens = "";
        string closes = "";
        var closed = new List<int>();
        foreach (CharacterPair pair in brackets)
        {
            if (pair is not { Open: [char open], Close: [char close] } || open == close)
                continue;
            int kind = opens.IndexOf(open);
            if (kind < 0)
            {
                kind = opens.Length;
                opens += open;
            }
            closes += close;
            closed.Add(kind);
        }
        _opens = opens;
        _closes = closes;
        _closed = [.. closed];
        Characters = opens + closes;
    }

    /// <summary>How many kinds there are.</summary>
    public int Count => _opens.Length;

    /// <summary>The opening and the closing characters, which a scan looks for.</summary>
    public string Characters { get; }

    /// <summary>The kind that <paramref name="c"/> opens; -1 for none.</summary>
    public int Opening(char c) => _opens.IndexOf(c);

    /// <summary>The kind that <paramref name="c"/> closes; -1 for none.</summary>
    public int Closing(char c) => _closes.IndexOf(c) is var index and >= 0 ? _closed[index] : -1;
}
