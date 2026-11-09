using AvaloniaEdit.Document;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>
/// Converts between the editor's positions, from 1, and the protocol's, from 0. Both split lines at <c>\r\n</c>,
/// <c>\n</c> and <c>\r</c>, and count UTF-16 code units.
/// </summary>
internal static class Positions
{
    /// <summary>A location in AvaloniaEdit's document, which counts from 1.</summary>
    public static LspPosition ToLsp(TextLocation location) => new(location.Line - 1, location.Column - 1);

    /// <summary>A line and column of the editor, which count from 1.</summary>
    public static LspPosition ToLsp(TextPosition position) => new(position.Line - 1, position.Column - 1);

    /// <summary>
    /// The offset of a server's position in <paramref name="snapshot"/>. A position outside the text is clamped to it,
    /// and no value overflows.
    /// </summary>
    public static int ToOffset(TextSnapshot snapshot, LspPosition position) =>
        snapshot.GetOffset(
            new TextPosition(
                Math.Clamp(position.Line, 0, int.MaxValue - 1) + 1,
                Math.Clamp(position.Character, 0, int.MaxValue - 1) + 1
            )
        );
}
