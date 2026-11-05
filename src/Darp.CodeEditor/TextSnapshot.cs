namespace Darp.CodeEditor;

/// <summary>
/// The text of a <see cref="CodeDocument"/> at one version, or a string that was compiled without a document.
/// Immutable, and safe to read from any thread.
/// </summary>
/// <remarks>
/// The document makes a snapshot after every change, sharing the parts of the text that did not change, so taking one
/// costs nothing. Holding a snapshot keeps its text alive. Positions that the library reports come with the snapshot
/// they refer to, such as <see cref="Diagnostic.Snapshot"/>.
/// </remarks>
public sealed class TextSnapshot
{
    private TextSnapshot() { }

    /// <summary>
    /// Counts the document's changes: each change of the text makes a snapshot with a higher version. 0 for a compiled
    /// string.
    /// </summary>
    public int Version => throw new NotImplementedException();

    /// <summary>The length of the text, in UTF-16 code units.</summary>
    public int Length => throw new NotImplementedException();

    /// <summary>The text. Each call copies it into a new string.</summary>
    public string GetText() => throw new NotImplementedException();

    /// <summary>The text of <paramref name="range"/>, clamped to the text.</summary>
    public string GetText(TextRange range) => throw new NotImplementedException();

    /// <summary>The line and column of <paramref name="offset"/>, clamped to the text.</summary>
    public TextPosition GetPosition(int offset) => throw new NotImplementedException();

    /// <summary>The offset of <paramref name="position"/>, clamped to the end of its line and to the last line.</summary>
    public int GetOffset(TextPosition position) => throw new NotImplementedException();
}
