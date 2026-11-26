using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// What signature help's card shows of one signature, as VS Code's parameter hints: the signature in the editor's font,
/// its active parameter bold in <c>editorHoverWidget.highlightForeground</c>, and below a line, the active parameter's
/// documentation, then the signature's. All of it wraps as a hover's text.
/// </summary>
internal static class SignatureView
{
    /// <param name="signature">The signature.</param>
    /// <param name="label">As much of its label as the budget left, with "…" where it was cut.</param>
    /// <param name="documentation">The documentation of its active parameter, then its own.</param>
    /// <param name="look">How code looks.</param>
    public static Control Create(
        SignatureInfo signature,
        string label,
        IReadOnlyList<HoverBlock> documentation,
        CodeLook look
    )
    {
        var card = new PopupStack
        {
            Children =
            {
                new PopupText
                {
                    Inlines = Label(signature, label, look.Colors.HoverHighlight),
                    FontFamily = look.FontFamily,
                    FontSize = look.FontSize,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4),
                },
            },
        };
        if (documentation.Count == 0)
            return card;
        card.Children.Add(new PopupLine { Background = look.Colors.HoverBorder });
        PopupStack docs = HoverView.Create(documentation, look, look.Colors.HoverBorder);
        docs.Margin = new Thickness(0, 6, 0, 2);
        card.Children.Add(docs);
        return card;
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
