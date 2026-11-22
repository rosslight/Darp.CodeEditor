namespace Darp.CodeEditor.Editing;

/// <summary>
/// The brackets open during a scan of <see cref="BracketMatcher"/>, as VS Code's bracket pairs nest them: a closing
/// bracket closes the closest open bracket of its kind, those opened inside it stay unclosed, and one that closes none
/// is no bracket.
/// </summary>
internal sealed class BracketNesting(int kinds)
{
    // Each open bracket's kind and the open one of its kind before it, and the last of each kind: a closing bracket
    // finds its own at once, whatever other kinds are open, so that a scan stays linear.
    private readonly List<(int Kind, int Before)> _open = [];
    private readonly int[] _last = [.. Enumerable.Repeat(-1, kinds)];

    public void Open(int kind)
    {
        _open.Add((kind, _last[kind]));
        _last[kind] = _open.Count - 1;
    }

    /// <summary>Closes the closest open bracket of <paramref name="kind"/>, and those opened inside it.</summary>
    /// <returns>Whether one of its kind was open.</returns>
    public bool Close(int kind)
    {
        int index = _last[kind];
        if (index < 0)
            return false;
        for (int top = _open.Count - 1; top >= index; top--)
            _last[_open[top].Kind] = _open[top].Before;
        _open.RemoveRange(index, _open.Count - index);
        return true;
    }
}
