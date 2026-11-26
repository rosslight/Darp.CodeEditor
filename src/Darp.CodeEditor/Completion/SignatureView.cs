using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// What signature help shows: every signature of the call at once, in the editor's font on the editor's background, the
/// active one on <c>list.inactiveSelectionBackground</c> while there are more, with its active parameter bold in
/// <c>editorHoverWidget.highlightForeground</c>. Right below the active signature, the active parameter's and the
/// signature's documentation, so that it stays in view with the signature it is about. Signatures take the width they
/// need, as code is read line by line; the documentation wraps as a hover's text does.
/// </summary>
internal static class SignatureView
{
    /// <param name="help">The signature help.</param>
    /// <param name="labels">
    /// Each signature's label, as much of it as the budget left, with "…" where it was cut; null for one that does not
    /// show.
    /// </param>
    /// <param name="documentation">The documentation of the active parameter and signature.</param>
    /// <param name="documentationWidth">The most width of the documentation.</param>
    /// <param name="look">How code looks.</param>
    /// <param name="colors">The popups' colors.</param>
    /// <param name="active">The active signature's row with its documentation, to scroll into view.</param>
    public static Control Create(
        SignatureHelpInfo help,
        IReadOnlyList<string?> labels,
        IReadOnlyList<HoverBlock> documentation,
        double documentationWidth,
        CodeLook look,
        PopupColors colors,
        out Control active
    )
    {
        var rows = new PopupStack();
        active = rows;
        for (int i = 0; i < help.Signatures.Count; i++)
        {
            if (labels[i] is not { } label)
                continue;
            bool isActive = i == help.ActiveSignature;
            // On the text's background, as its colors are chosen for it.
            Control row = new PopupBox
            {
                Background = look.Background,
                Padding = new Thickness(0, 1),
                Child = new PopupBox
                {
                    Background = isActive && help.Signatures.Count > 1 ? colors.ListInactiveSelectionBackground : null,
                    Padding = new Thickness(6, 2),
                    CornerRadius = new CornerRadius(2),
                    Child = new PopupText
                    {
                        Inlines = Label(help.Signatures[i], label, colors.HoverHighlight),
                        FontFamily = look.FontFamily,
                        FontSize = look.FontSize,
                        Foreground = look.Foreground,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            };
            if (isActive)
            {
                if (documentation.Count > 0)
                {
                    PopupStack docs = HoverView.Create(documentation, look, colors.HoverBorder);
                    docs.Margin = new Thickness(0, 6);
                    // The text wraps as a hover's does; the lines between its parts span the popup.
                    foreach (Control block in docs.Children.Where(block => block is not PopupLine))
                    {
                        block.MaxWidth = documentationWidth;
                        block.HorizontalAlignment = HorizontalAlignment.Left;
                    }
                    row = new PopupStack { Children = { row, docs } };
                }
                active = row;
            }
            rows.Children.Add(row);
        }
        return rows;
    }

    /// <summary><paramref name="label"/>, what shows of the signature's, with its active parameter in bold.</summary>
    private static InlineCollection Label(SignatureInfo signature, string label, IBrush highlight)
    {
        // The parameter's offsets are the whole label's: a cut one ends before its "…".
        int kept = label.Length == signature.Label.Length ? label.Length : label.Length - 1;
        (int start, int end) =
            signature.ActiveParameter >= 0 && signature.Parameters[signature.ActiveParameter] is var parameter
                ? (Math.Min(parameter.Start, kept), Math.Min(parameter.Start + parameter.Length, kept))
                : (0, 0);
        if (end <= start)
            return [new Run(label)];
        return
        [
            new Run(label[..start]),
            new Run(label[start..end]) { FontWeight = FontWeight.Bold, Foreground = highlight },
            new Run(label[end..]),
        ];
    }
}
