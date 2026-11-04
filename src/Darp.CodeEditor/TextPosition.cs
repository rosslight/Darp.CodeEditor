namespace Darp.CodeEditor;

/// <summary>A line and a column in a document's text, both counted from 0.</summary>
/// <remarks>
/// Lines end at <c>\r\n</c>, <c>\n</c> or <c>\r</c>, in the editor and in every language server alike. Columns count
/// UTF-16 code units from the start of the line. Add 1 to both to show them to users.
/// </remarks>
/// <param name="Line">The line, from 0.</param>
/// <param name="Column">The column, from 0, in UTF-16 code units.</param>
public readonly record struct TextPosition(int Line, int Column);
