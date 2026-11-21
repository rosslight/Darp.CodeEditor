namespace Darp.CodeEditor.Editing;

/// <summary>
/// The brackets open during a scan of <see cref="BracketMatcher"/>, as VS Code's bracket pairs nest them: a closing
/// bracket closes the closest open bracket of its kind, those opened inside it stay unclosed, and one that closes none
/// is no bracket.
/// </summary>
internal sealed class BracketNesting(int kinds)
{
    // Each open bracket's kind, offset and the open one of its kind before it, and the last of each kind: a closing
    // bracket finds its own at once, whatever other kinds are open, so that a scan stays linear.
    private readonly List<(int Kind, int Offset, int Before)> _open = [];
    private readonly int[] _last = [.. Enumerable.Repeat(-1, kinds)];

    /// <summary>How many brackets are open.</summary>
    public int Depth => _open.Count;

    public void Open(int kind, int offset)
    {
        _open.Add((kind, offset, _last[kind]));
        _last[kind] = _open.Count - 1;
    }

    /// <summary>Whether a bracket of <paramref name="kind"/> is open.</summary>
    public bool IsOpen(int kind) => _last[kind] >= 0;

    /// <summary>Closes the closest open bracket of <paramref name="kind"/>, and those opened inside it.</summary>
    /// <returns>Its index among the open brackets and its offset; null when none of its kind is open.</returns>
    public (int Index, int Offset)? Close(int kind)
    {
        int index = _last[kind];
        if (index < 0)
            return null;
        int offset = _open[index].Offset;
        for (int top = _open.Count - 1; top >= index; top--)
            _last[_open[top].Kind] = _open[top].Before;
        _open.RemoveRange(index, _open.Count - index);
        return (index, offset);
    }
}
