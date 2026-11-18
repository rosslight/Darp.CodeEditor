namespace Darp.CodeEditor;

/// <summary>
/// What expanding the selection goes through besides the server's ranges, as VS Code's <c>editor.smartSelect</c>. Set
/// it as <see cref="CodeEditor.SmartSelect"/>.
/// </summary>
/// <param name="SelectLeadingAndTrailingWhitespace">
/// Whether a range that grows onto other lines first takes its lines from their first text to their last, and then
/// whole, as VS Code's <c>selectLeadingAndTrailingWhitespace</c>.
/// </param>
/// <param name="SelectSubwords">
/// Whether the selection grows from the part of a word between its humps or underscores, such as <c>Bar</c> of
/// <c>fooBar</c>, before the whole word, as VS Code's <c>selectSubwords</c>.
/// </param>
public readonly record struct SmartSelectOptions(bool SelectLeadingAndTrailingWhitespace, bool SelectSubwords)
{
    /// <summary>VS Code's default: both.</summary>
    public static SmartSelectOptions Default => new(SelectLeadingAndTrailingWhitespace: true, SelectSubwords: true);
}
