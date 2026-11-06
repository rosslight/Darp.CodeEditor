namespace Darp.CodeEditor.Tests;

/// <summary>
/// Texts with the caret and the selection marked, for the editing tests: <c>|</c> is the caret, and <c>«…»</c> a
/// selection from its start, the anchor, to its end, the caret.
/// </summary>
internal static class MarkedText
{
    public static (string Text, TextSelection Selection) Parse(string marked)
    {
        int start = marked.IndexOf('«');
        if (start >= 0)
            return (marked.Replace("«", "").Replace("»", ""), new TextSelection(start, marked.IndexOf('»') - 1));
        int caret = marked.IndexOf('|');
        return (marked.Remove(caret, 1), new TextSelection(caret));
    }

    public static string Format(string text, TextSelection selection) =>
        selection.IsEmpty
            ? text.Insert(selection.Caret, "|")
            : text.Insert(selection.Range.End, "»").Insert(selection.Range.Start, "«");

    /// <summary>Shows an editor of <paramref name="language"/> with the marked text, the caret and the selection.</summary>
    public static EditorWindow Show(string marked, CodeLanguage language)
    {
        (string text, TextSelection selection) = Parse(marked);
        var window = new EditorWindow(new CodeEditor { Document = CodeDocument.Create(language, text) });
        window.Editor.Selection = selection;
        return window;
    }

    /// <summary>The editor's text, with its caret and selection marked.</summary>
    public static string Of(EditorWindow window) =>
        Format(
            window.Editor.Document?.Text ?? throw new InvalidOperationException("No document."),
            window.Editor.Selection
        );
}
