using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// What signature help shows, as IntelliJ's parameter info: every signature of the call at once, in the editor's font,
/// the active one on <c>list.inactiveSelectionBackground</c>, and those whose active parameter falls outside their
/// parameters dimmed, as they cannot match the call. The active parameter is bold in the hover's highlight color, as in
/// VS Code. A label too long for the popup breaks after its commas, with the parameters indented. Below them the active
/// parameter's and the active signature's documentation.
/// </summary>
internal static class SignatureView
{
    /// <summary>How far the parameters of a broken label are indented, in characters.</summary>
    private const int Indent = 4;

    /// <summary>The opacity of signatures that cannot match the call.</summary>
    private const double Dimmed = 0.5;

    /// <param name="help">The signature help.</param>
    /// <param name="labels">Each signature's label, as much of it as the budget left, with "…" where it was cut.</param>
    /// <param name="documentation">The documentation of the active parameter and signature.</param>
    /// <param name="look">How the popup looks.</param>
    /// <param name="columns">How many characters of the editor's font fit on a line of the popup.</param>
    /// <param name="active">The active signature's control, to scroll into view.</param>
    public static Control Create(
        SignatureHelpInfo help,
        IReadOnlyList<string> labels,
        IReadOnlyList<HoverBlock> documentation,
        PopupLook look,
        int columns,
        out Control active
    )
    {
        var panel = new StackPanel { Spacing = 2 };
        active = panel;
        for (int i = 0; i < labels.Count; i++)
        {
            SignatureInfo signature = help.Signatures[i];
            SelectableTextBlock code = HoverView.Text(look);
            code.Inlines = Label(signature, labels[i], look, columns);
            code.FontFamily = look.CodeFont;
            code.FontSize = look.CodeSize;
            code.Foreground = look.Colors.CodeForeground;
            bool isActive = i == help.ActiveSignature;
            var row = new Border
            {
                Child = code,
                Padding = new Thickness(4, 1),
                CornerRadius = new CornerRadius(2),
                Background = isActive && labels.Count > 1 ? look.Colors.ListInactiveSelectionBackground : null,
                Opacity = signature.CannotMatch && !isActive ? Dimmed : 1,
            };
            if (isActive)
                active = row;
            panel.Children.Add(row);
        }
        if (documentation.Count > 0)
        {
            panel.Children.Add(HoverView.Rule(look));
            Control text = HoverView.Create(documentation, look);
            text.Margin = new Thickness(4, 0);
            panel.Children.Add(text);
        }
        return panel;
    }

    /// <summary>
    /// A signature's label, with its active parameter in bold in the highlight color, broken after the commas between
    /// its parameters when it is longer than <paramref name="columns"/>.
    /// </summary>
    private static InlineCollection Label(SignatureInfo signature, string label, PopupLook look, int columns)
    {
        // The active parameter, in what is left of the label.
        int kept = label.Length == signature.Label.Length ? label.Length : label.Length - 1;
        (int start, int end) =
            signature.ActiveParameter >= 0 && signature.Parameters[signature.ActiveParameter] is var parameter
                ? (Math.Min(parameter.Start, kept), Math.Min(parameter.Start + parameter.Length, kept))
                : (0, 0);
        IReadOnlyList<int> breaks = label.Length > columns ? Breaks(label) : [];
        var inlines = new InlineCollection();
        var text = new StringBuilder();
        bool bold = false;
        int next = 0;
        for (int i = 0; i < label.Length; i++)
        {
            bool inActive = i >= start && i < end;
            if (inActive != bold)
                Flush();
            bold = inActive;
            if (next < breaks.Count && breaks[next] == i)
            {
                next++;
                text.Append('\n').Append(' ', Indent);
                // The spaces after the comma give way to the indentation.
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
    /// Where a long label breaks: after each comma inside the outermost parentheses, as between the parameters of
    /// <c>name(a: int, b: Dict[str, int])</c>, not inside the brackets of a type.
    /// </summary>
    private static List<int> Breaks(string label)
    {
        var breaks = new List<int>();
        int depth = 0;
        int outer = -1;
        for (int i = 0; i < label.Length; i++)
        {
            switch (label[i])
            {
                case '(' or '[' or '{':
                    depth++;
                    if (outer == -1 && label[i] == '(')
                        outer = depth;
                    break;
                case ')' or ']' or '}':
                    // The parameters end with the outermost parentheses: no more breaks in the return type.
                    if (depth == outer)
                        outer = -2;
                    depth--;
                    break;
                case ',' when depth == outer && i + 1 < label.Length:
                    breaks.Add(i + 1);
                    break;
            }
        }
        return breaks;
    }
}
