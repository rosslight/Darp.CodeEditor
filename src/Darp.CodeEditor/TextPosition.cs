namespace Darp.CodeEditor;

/// <summary>A line and a column in a document's text, both counted from 1, as editors show them.</summary>
/// <remarks>
/// Lines end at <c>\r\n</c>, <c>\n</c> or <c>\r</c>, in the editor and in every language server alike. Columns count
/// UTF-16 code units: the first character of a line is in column 1, and a tab counts as one column.
/// </remarks>
/// <param name="Line">The line, from 1.</param>
/// <param name="Column">The column, from 1, in UTF-16 code units.</param>
public readonly record struct TextPosition(int Line, int Column);
