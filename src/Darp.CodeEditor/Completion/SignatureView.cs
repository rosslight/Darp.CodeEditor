using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// What signature help shows, as IntelliJ shows parameter info: every signature of the call at once, in the editor's
/// font, the active one on <c>list.inactiveSelectionBackground</c> while there are more, and those whose active
/// parameter falls outside their parameters dimmed, as they cannot match the call. The active parameter is bold in
/// <c>editorHoverWidget.highlightForeground</c>. A label longer than a line of the popup breaks after the commas between
/// its parameters, which go on indented. Below the signatures, the active parameter's and signature's documentation.
/// </summary>
internal static class SignatureView
{
    /// <summary>How far the parameters of a broken label are indented, in characters.</summary>
    private const int Indent = 4;

    /// <summary>The opacity of signatures that cannot match the call.</summary>
    private const double Dimmed = 0.5;

    /// <param name="help">The signature help.</param>
    /// <param name="labels">
    /// Each signature's label, as much of it as the budget left, with "…" where it was cut; null for one that does not
    /// show.
    /// </param>
    /// <param name="documentation">The documentation of the active parameter and signature.</param>
    /// <param name="look">How the popup looks.</param>
    /// <param name="columns">How many characters of the editor's font fit on a line of the popup.</param>
    /// <param name="active">The active signature's row, to scroll into view.</param>
    public static Control Create(
        SignatureHelpInfo help,
        IReadOnlyList<string?> labels,
        IReadOnlyList<HoverBlock> documentation,
        PopupLook look,
        int columns,
        out Control active
    )
    {
        var signatures = new PopupStack { Spacing = 2 };
        active = signatures;
        for (int i = 0; i < help.Signatures.Count; i++)
        {
            if (labels[i] is not { } label)
                continue;
            SignatureInfo signature = help.Signatures[i];
            bool isActive = i == help.ActiveSignature;
            var row = new PopupBox
            {
                Child = HoverView.Code(Label(signature, label, look, columns), look),
                Padding = new Thickness(4, 1),
                CornerRadius = new CornerRadius(2),
                Background = isActive && help.Signatures.Count > 1 ? look.Colors.ListInactiveSelectionBackground : null,
                Opacity = signature.CannotMatch && !isActive ? Dimmed : 1,
            };
            if (isActive)
                active = row;
            signatures.Children.Add(row);
        }
        var panel = new PopupStack { Spacing = 6, Children = { signatures } };
        if (documentation.Count > 0)
        {
            Control text = HoverView.Create(documentation, look);
            text.Margin = new Thickness(4, 0);
            panel.Children.Add(text);
        }
        return panel;
    }

    /// <summary>
    /// <paramref name="label"/>, what shows of the signature's, with its active parameter in bold in the highlight color,
    /// broken after the commas between its parameters when it is longer than <paramref name="columns"/>.
    /// </summary>
    private static InlineCollection Label(SignatureInfo signature, string label, PopupLook look, int columns)
    {
        // The parameter's offsets are the whole label's: a cut one ends before its "…".
        int kept = label.Length == signature.Label.Length ? label.Length : label.Length - 1;
        (int start, int end) =
            signature.ActiveParameter >= 0 && signature.Parameters[signature.ActiveParameter] is var parameter
                ? (Math.Min(parameter.Start, kept), Math.Min(parameter.Start + parameter.Length, kept))
                : (0, 0);
        List<int> breaks = label.Length > columns ? Breaks(label) : [];
        var inlines = new InlineCollection();
        var text = new StringBuilder();
        bool bold = false;
        int next = 0;
        for (int i = 0; i < label.Length; i++)
        {
            bool inParameter = i >= start && i < end;
            if (inParameter != bold)
            {
                Flush();
                bold = inParameter;
            }
            if (next < breaks.Count && breaks[next] == i)
            {
                next++;
                text.Append('\n').Append(' ', Indent);
                // The space after the comma gives way to the indentation.
                if (label[i] == ' ')
                    continue;
            }
            text.Append(label[i]);
        }
        Flush();
        return inlines;

        void Flush()
        {
            if (text.Length == 0)
                return;
            var run = new Run(text.ToString());
            if (bold)
            {
                run.FontWeight = FontWeight.Bold;
                run.Foreground = look.Colors.HoverHighlight;
            }
            inlines.Add(run);
            text.Clear();
        }
    }

    /// <summary>
    /// Where a long label breaks: after each comma inside its first parentheses, as between the parameters of
    /// <c>name(a: int, b: Dict[str, int])</c>, not inside the brackets of a type, nor in the return type after them.
    /// </summary>
    private static List<int> Breaks(string label)
    {
        var breaks = new List<int>();
        int depth = 0;
        int parameters = -1;
        for (int i = 0; i < label.Length; i++)
        {
            switch (label[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    if (parameters == -1 && label[i] == '(')
                        parameters = depth;
                    break;
                case ')' or ']' or '}':
                    if (depth == parameters)
                        return breaks;
                    depth--;
                    break;
                case ',' when depth == parameters && i + 1 < label.Length:
                    breaks.Add(i + 1);
                    break;
            }
        }
        return breaks;
    }
}
