using System.Text;
using Avalonia.Media;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using TextMateSharp.Grammars;

namespace Darp.CodeEditor.Popups;

/// <summary>A block of a hover: a paragraph, a code block or a line between parts.</summary>
internal abstract record HoverBlock;

/// <summary>A paragraph, a heading or a list item: text with inline styles.</summary>
/// <param name="Spans">The text.</param>
/// <param name="Level">How deep it is in lists: 0 outside them.</param>
/// <param name="Marker">The list item's marker, such as <c>•</c> or <c>2.</c>; null for none.</param>
/// <param name="Severity">The severity whose icon shows beside it, for a diagnostic; null for none.</param>
internal sealed record HoverParagraph(
    IReadOnlyList<HoverSpan> Spans,
    int Level = 0,
    string? Marker = null,
    DiagnosticSeverity? Severity = null
) : HoverBlock;

/// <summary>A code block, highlighted with the document's grammar.</summary>
/// <param name="Runs">The text, in runs of one color; line breaks are runs of their own.</param>
internal sealed record HoverCode(IReadOnlyList<CodeRun> Runs) : HoverBlock;

/// <summary>A line between two parts.</summary>
internal sealed record HoverRule : HoverBlock;

/// <summary>Text with one style.</summary>
/// <param name="Text">The text; a line break in it breaks the line.</param>
/// <param name="Style">Its emphasis, or code.</param>
/// <param name="Link">The web page it opens; null for none.</param>
internal readonly record struct HoverSpan(string Text, SpanStyle Style = SpanStyle.None, Uri? Link = null);

/// <summary>Code of one color.</summary>
/// <param name="Text">The code.</param>
/// <param name="Foreground">Its token's color; null for the editor's foreground.</param>
/// <param name="Bold">Whether it is bold, as signature help's active parameter.</param>
internal readonly record struct CodeRun(string Text, IBrush? Foreground, bool Bold = false);

[Flags]
internal enum SpanStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,

    /// <summary>At VS Code's 60% opacity, as a diagnostic's source and code.</summary>
    Dimmed = 8,
}

/// <summary>What a hover shows: the messages of the diagnostics under the pointer, above the server's hover.</summary>
internal static class HoverBlocks
{
    /// <summary>How long a line of a code block may take to tokenize, as the background highlighter allows.</summary>
    private static readonly TimeSpan s_lineTimeLimit = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// <paramref name="diagnostics"/>, each with its severity's icon, as VS Code writes them: <c>message source(code)</c>,
    /// the source and code dimmed, as much of them as <paramref name="budget"/> leaves.
    /// </summary>
    public static IReadOnlyList<HoverBlock> Of(IEnumerable<Diagnostic> diagnostics, HoverBudget budget)
    {
        var blocks = new List<HoverBlock>();
        foreach (Diagnostic diagnostic in diagnostics)
        {
            // Cut before it is put together: the work is bounded by the budget, not by the server's message.
            string? text = (diagnostic.Source, diagnostic.Code) switch
            {
                (null, null) => budget.Take(diagnostic.Message),
                ({ } source, null) => budget.Take(diagnostic.Message, " ", source),
                (var source, { } code) => budget.Take(diagnostic.Message, " ", source ?? "", "(", code, ")"),
            };
            if (text is null)
                break;
            blocks.Add(new HoverParagraph(Line(diagnostic, text), Severity: diagnostic.Severity));
        }
        return budget.Fit(blocks);
    }

    /// <summary>
    /// <paramref name="text"/>, a diagnostic's line as far as it was kept, in its spans: the message, then the source and
    /// code dimmed, the code as a link to its web page where it is whole and has an <c>http</c> or <c>https</c> one.
    /// </summary>
    private static List<HoverSpan> Line(Diagnostic diagnostic, string text)
    {
        int message = Math.Min(diagnostic.Message.Length, text.Length);
        var line = new List<HoverSpan> { new(text[..message]) };
        if (text.Length == message)
            return line;
        int code = message + 1 + (diagnostic.Source?.Length ?? 0) + 1;
        if (
            diagnostic.Code is not { } whole
            || diagnostic.CodeDescription is not { Scheme: "http" or "https" } page
            || text.Length != code + whole.Length + 1
        )
        {
            line.Add(new HoverSpan(text[message..], SpanStyle.Dimmed));
            return line;
        }
        line.Add(new HoverSpan(text[message..code], SpanStyle.Dimmed));
        line.Add(new HoverSpan(whole, SpanStyle.Dimmed, page));
        line.Add(new HoverSpan(text[^1..], SpanStyle.Dimmed));
        return line;
    }

    /// <summary>
    /// The server's hover, its parts apart, as much of it as <paramref name="budget"/> leaves: Markdown with its code
    /// blocks highlighted by <paramref name="grammar"/>, and plain text as it is. Off the UI thread: tokenizing a code
    /// block takes the grammar from the background highlighter for a line at a time. Reading stops between lines and
    /// paragraphs once <paramref name="cancellationToken"/> is cancelled, as the hover is no longer needed.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<HoverBlock> Of(
        HoverInfo hover,
        TextMateGrammar? grammar,
        HoverBudget budget,
        CancellationToken cancellationToken
    )
    {
        var blocks = new List<HoverBlock>();
        foreach (HoverText part in hover.Contents)
        {
            if (budget.Take(part.Text) is not { } text)
                break;
            if (blocks.Count > 0)
                blocks.Add(new HoverRule());
            if (part.IsMarkdown)
            {
                blocks.AddRange(
                    Markdown.Parse(text, code => Highlight(code, grammar, cancellationToken), cancellationToken)
                );
            }
            else
                blocks.Add(new HoverParagraph([new HoverSpan(text.Trim())]));
        }
        return budget.Fit(blocks);
    }

    /// <summary>
    /// <paramref name="code"/>, whose lines end at <c>\r\n</c>, <c>\n</c> or <c>\r</c> as everywhere in the library, as
    /// a code block in the colors of <paramref name="grammar"/>'s tokens, as <see cref="Highlight(IEnumerable{string},
    /// TextMateGrammar?, CancellationToken)"/> colors its lines.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static HoverCode Highlight(string code, TextMateGrammar? grammar, CancellationToken cancellationToken) =>
        Highlight(code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'), grammar, cancellationToken);

    /// <summary>
    /// A code block in the colors of <paramref name="grammar"/>'s tokens, line by line, with neighbors of one color in
    /// one run; a line that runs out of time keeps the editor's foreground, and the next starts from the start of the
    /// grammar. Tokenizing stops between lines once <paramref name="cancellationToken"/> is cancelled, as the hover's
    /// highlighting is no longer needed.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static HoverCode Highlight(
        IEnumerable<string> lines,
        TextMateGrammar? grammar,
        CancellationToken cancellationToken
    )
    {
        var runs = new List<CodeRun>();
        IStateStack? state = null;
        bool first = true;
        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!first)
                Add("\n", null);
            first = false;
            if (grammar?.TokenizeInBackground(line, state, s_lineTimeLimit) is not { } result)
            {
                Add(line, null);
                state = null;
                continue;
            }
            state = result.RuleStack;
            int[] tokens = result.Tokens;
            for (int t = 0; t < tokens.Length; t += 2)
            {
                int start = Math.Min(tokens[t], line.Length);
                int end = Math.Min(t + 2 < tokens.Length ? tokens[t + 2] : line.Length, line.Length);
                if (end > start)
                    Add(line[start..end], grammar.Foreground(tokens[t + 1]));
            }
        }
        return new HoverCode(runs);

        // Each run costs the UI thread a text run to build and lay out.
        void Add(string text, IBrush? foreground)
        {
            if (runs.Count > 0 && runs[^1].Foreground == foreground)
                runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            else
                runs.Add(new CodeRun(text, foreground));
        }
    }
}

/// <summary>
/// How much a hover shows, so that the UI thread builds and lays it out well within a frame: at most
/// <see cref="MaxCharacters"/> characters on <see cref="MaxLines"/> lines, in <see cref="MaxRuns"/> runs, its parts in
/// order. The text is cut where any of them runs out, and "…" marks the cut. A hover is a tooltip that scrolls, so more
/// would not help the user.
/// </summary>
/// <remarks>
/// The UI thread's cost follows the runs, the pieces of text of one style or one color that it builds and shapes one by
/// one, more than the characters: about 10 µs a run and 1 µs a character (the hover benchmark). The text is cut before
/// it is read, which bounds the work in the background too, and what is read is cut by its runs.
/// </remarks>
internal sealed class HoverBudget
{
    public const int MaxCharacters = 2_000;

    public const int MaxLines = 50;

    public const int MaxRuns = 250;

    private int _characters = MaxCharacters;
    private int _lines = MaxLines;
    private int _runs = MaxRuns;

    /// <summary>
    /// The start of a part of a hover, on lines of its own, that the characters and lines left allow, with "…" at its
    /// end when it was cut; null when nothing is left. The part is <paramref name="pieces"/> one after another, and each
    /// is read only as far as it is kept, so the work is bounded by the budget, not by the text. Every piece of a
    /// hover's text passes here before anything else reads it.
    /// </summary>
    /// <remarks>
    /// Lines break at <c>\r\n</c>, <c>\n</c> and <c>\r</c>, as everywhere in the library, and also where the layout of
    /// text breaks them: at a vertical tab, a form feed, and the Unicode line and paragraph separators. A cut never
    /// splits a surrogate pair.
    /// </remarks>
    public string? Take(params ReadOnlySpan<string> pieces)
    {
        if (_characters <= 0 || _lines <= 0)
            return null;
        var kept = new StringBuilder();
        int lines = 1;
        bool afterCarriageReturn = false;
        foreach (string piece in pieces)
        {
            int end = 0;
            for (; end < piece.Length; end++)
            {
                char c = piece[end];
                bool lineBreak = IsLineBreak(c) && !(c == '\n' && afterCarriageReturn);
                afterCarriageReturn = c == '\r';
                if (kept.Length + end == _characters || (lineBreak && ++lines > _lines))
                {
                    // Nothing follows a cut.
                    if (end > 0 && char.IsHighSurrogate(piece[end - 1]) && char.IsLowSurrogate(c))
                        end--;
                    kept.Append(piece, 0, end).Append('…');
                    (_characters, _lines) = (0, 0);
                    return kept.ToString();
                }
            }
            kept.Append(piece);
        }
        _characters -= kept.Length;
        _lines -= lines;
        return kept.ToString();
    }

    private static bool IsLineBreak(char c) => c is '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>
    /// The blocks that the runs left allow: those that fit, and as much of the next as fits, ending with "…". Nothing
    /// follows a cut.
    /// </summary>
    public IReadOnlyList<HoverBlock> Fit(IReadOnlyList<HoverBlock> blocks)
    {
        var fitted = new List<HoverBlock>(blocks.Count);
        foreach (HoverBlock block in blocks)
        {
            int runs = Runs(block);
            if (runs <= _runs)
            {
                fitted.Add(block);
                _runs -= runs;
                continue;
            }
            int kept = Math.Max(0, _runs - 1);
            fitted.Add(
                block switch
                {
                    HoverParagraph paragraph => paragraph with
                    {
                        Spans = [.. paragraph.Spans.Take(kept), new HoverSpan("…")],
                    },
                    HoverCode code => new HoverCode([.. code.Runs.Take(kept), new CodeRun("…", null)]),
                    _ => new HoverParagraph([new HoverSpan("…")]),
                }
            );
            (_characters, _lines, _runs) = (0, 0, 0);
            break;
        }
        return fitted;
    }

    /// <summary>The runs a block takes: its spans or tokens, and a list item's marker.</summary>
    private static int Runs(HoverBlock block) =>
        block switch
        {
            HoverParagraph paragraph => paragraph.Spans.Count + (paragraph.Marker is null ? 0 : 1),
            HoverCode code => code.Runs.Count,
            _ => 1,
        };
}
