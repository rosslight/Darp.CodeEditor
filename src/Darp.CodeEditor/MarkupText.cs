using Avalonia.Controls.Documents;
using Avalonia.Media;
using Darp.CodeEditor.LanguageServices;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Darp.CodeEditor;

/// <summary>Shows <see cref="MarkupBlock"/>s in a text block: code in the editor's font, paragraphs in the UI font.</summary>
internal static class MarkupText
{
    public static void Show(TextBlock block, IReadOnlyList<MarkupBlock> content, FontFamily codeFont)
    {
        InlineCollection inlines = block.Inlines ??= new InlineCollection();
        inlines.Clear();
        foreach (MarkupBlock part in content)
        {
            if (inlines.Count > 0)
            {
                inlines.Add(new LineBreak());
                inlines.Add(new LineBreak());
            }
            inlines.Add(
                part.Kind == MarkupBlockKind.Code ? new Run(part.Text) { FontFamily = codeFont } : new Run(part.Text)
            );
        }
    }
}
