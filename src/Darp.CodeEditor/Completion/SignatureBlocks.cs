using Avalonia.Media;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// What signature help's card shows of one signature, as VS Code's parameter hints, in the look of every popup of code
/// and docs (<see cref="HoverView"/>): the signature as code, highlighted with the document's grammar, its active
/// parameter bold in <c>editorHoverWidget.highlightForeground</c> over its tokens' colors; then the active parameter's
/// documentation and the signature's, without a line between them.
/// </summary>
internal static class SignatureBlocks
{
    /// <summary>
    /// The blocks of <paramref name="signature"/>, as much of them as a hover shows, the signature first, so that a
    /// server's long signatures or documentation cost the UI thread no more than the largest hover. Off the UI thread,
    /// as the hover reads its Markdown: tokenizing takes <paramref name="grammar"/> from the background highlighter for
    /// a line at a time.
    /// </summary>
    /// <param name="signature">The signature.</param>
    /// <param name="grammar">The document's grammar; null for none.</param>
    /// <param name="highlight">The active parameter's color.</param>
    /// <param name="cancellationToken">Stops reading, as the card is no longer needed.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<HoverBlock> Of(
        SignatureInfo signature,
        TextMateGrammar? grammar,
        IBrush highlight,
        CancellationToken cancellationToken
    )
    {
        var budget = new HoverBudget();
        string label = budget.Take(signature.Label) ?? "";
        HoverCode code = HoverBlocks.Highlight(label, grammar, cancellationToken);
        var blocks = new List<HoverBlock>(budget.Fit([Emphasized(code, ActiveParameter(signature, label), highlight)]));
        if (signature.ActiveParameter >= 0)
            Read(signature.Parameters[signature.ActiveParameter].Documentation);
        Read(signature.Documentation);
        return blocks;

        void Read(HoverText? text)
        {
            if (text is { } part)
                blocks.AddRange(HoverBlocks.Of(new HoverInfo([part], null), grammar, budget, cancellationToken));
        }
    }

    /// <summary>
    /// Where the active parameter is in <paramref name="label"/>, what shows of the signature's label, with "…" where
    /// it was cut; empty for none.
    /// </summary>
    private static (int Start, int End) ActiveParameter(SignatureInfo signature, string label)
    {
        if (signature.ActiveParameter < 0)
            return default;
        // The parameter's offsets are the whole label's: a cut one ends before its "…".
        int kept = label == signature.Label ? label.Length : label.Length - 1;
        ParameterInfo parameter = signature.Parameters[signature.ActiveParameter];
        return (Math.Min(parameter.Start, kept), Math.Min(parameter.Start + parameter.Length, kept));
    }

    /// <summary>
    /// <paramref name="code"/> with the characters of <paramref name="parameter"/> bold in
    /// <paramref name="highlight"/>.
    /// </summary>
    private static HoverCode Emphasized(HoverCode code, (int Start, int End) parameter, IBrush highlight)
    {
        if (parameter.End <= parameter.Start)
            return code;
        var runs = new List<CodeRun>(code.Runs.Count + 2);
        int at = 0;
        foreach (CodeRun run in code.Runs)
        {
            // The run's parts before, in and after the parameter.
            int from = Math.Clamp(parameter.Start - at, 0, run.Text.Length);
            int to = Math.Clamp(parameter.End - at, 0, run.Text.Length);
            at += run.Text.Length;
            if (from > 0)
                runs.Add(run with { Text = run.Text[..from] });
            if (to > from)
            {
                // The parameter's tokens in one run: each run costs the UI thread a text run.
                string text = run.Text[from..to];
                if (runs.Count > 0 && runs[^1].Bold)
                    runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
                else
                    runs.Add(new CodeRun(text, highlight, Bold: true));
            }
            if (to < run.Text.Length)
                runs.Add(run with { Text = run.Text[to..] });
        }
        return new HoverCode(runs);
    }
}
