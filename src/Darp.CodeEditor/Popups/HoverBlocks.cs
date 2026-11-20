using System.Text;
using Avalonia.Media;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using TextMateSharp.Grammars;

namespace Darp.CodeEditor.Popups;

/// <summary>A block of a hover: a paragraph, a code block or a line between parts.</summary>
internal abstract record HoverBlock
{
    /// <summary>
    /// Whether it goes on from the block before, as a piece of a long paragraph or code block does: no space between
    /// them.
    /// </summary>
    public bool Continues { get; init; }
}

/// <summary>A paragraph, a heading or a list item: text with inline styles.</summary>
/// <param name="Spans">The text.</param>
/// <param name="Level">How deep it is in lists: 0 outside them.</param>
/// <param name="Marker">The list item's marker, such as <c>•</c> or <c>2.</c>; null for none.</param>
internal sealed record HoverParagraph(IReadOnlyList<HoverSpan> Spans, int Level = 0, string? Marker = null)
    : HoverBlock;

/// <summary>A code block, highlighted with the document's grammar.</summary>
/// <param name="Runs">The text, in runs of one color; line breaks are runs of their own.</param>
internal sealed record HoverCode(IReadOnlyList<CodeRun> Runs) : HoverBlock;

/// <summary>A line between two parts.</summary>
internal sealed record HoverRule : HoverBlock;

/// <summary>
/// A diagnostic, as VS Code's hover shows it, in the editor's font: the icon of its severity beside its line, its message,
/// which keeps its line breaks, then its source and code, dimmed, the code as a link to its web page where it has one.
/// The other places it concerns follow it as blocks of their own (<see cref="HoverRelated"/>).
/// </summary>
/// <param name="Severity">Its severity, for its icon.</param>
/// <param name="Line">Its message, source and code, or a piece of them.</param>
/// <param name="Icon">Whether its severity's icon shows beside it, or only the room of one.</param>
internal sealed record HoverDiagnostic(
    DiagnosticSeverity Severity,
    IReadOnlyList<HoverSpan> Line,
    HoverIcon Icon = HoverIcon.Shown
) : HoverBlock;

/// <summary>Whether a diagnostic's icon shows beside it.</summary>
internal enum HoverIcon
{
    /// <summary>No icon, and no room for one, as below a heading that shows it.</summary>
    None,

    Shown,

    /// <summary>The room of the icon, beside a piece of a diagnostic after the first.</summary>
    Room,
}

/// <summary>A place that a diagnostic concerns, below it, or a piece of one.</summary>
/// <param name="Place">Where it is, as VS Code writes it: <c>file.py(3, 5): </c>.</param>
/// <param name="Message">What it has to do with the problem.</param>
/// <param name="Open">Goes there; null where the editor cannot, as in another document.</param>
/// <param name="Indented">Whether it lines up with a diagnostic beside its icon.</param>
internal sealed record HoverRelated(string Place, string Message, Action? Open, bool Indented) : HoverBlock;

/// <summary>
/// The heading of the problem that F8 went to, as VS Code's widget for it shows it: the icon of its severity, the
/// document's name and which problem it is of how many.
/// </summary>
/// <param name="Severity">The problem's severity.</param>
/// <param name="Name">The document's name; null for none.</param>
/// <param name="Count">Which problem it is of how many, such as "2 of 5 problems".</param>
internal sealed record HoverHeading(DiagnosticSeverity Severity, string? Name, string Count) : HoverBlock;

/// <summary>Text with one style.</summary>
/// <param name="Text">The text; a line break in it breaks the line.</param>
/// <param name="Style">Its emphasis, or code.</param>
/// <param name="Link">The web page it opens; null for none.</param>
internal readonly record struct HoverSpan(string Text, SpanStyle Style = SpanStyle.None, Uri? Link = null);

/// <summary>Code of one color.</summary>
/// <param name="Text">The code.</param>
/// <param name="Foreground">Its token's color; null for the editor's foreground.</param>
internal readonly record struct CodeRun(string Text, IBrush? Foreground);

[Flags]
internal enum SpanStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Dimmed = 8,
    Underline = 16,
}

/// <summary>What a hover shows: the messages of the diagnostics under the pointer, above the server's hover.</summary>
internal static class HoverBlocks
{
    /// <summary>How long a line of a code block may take to tokenize, as the background highlighter allows.</summary>
    private static readonly TimeSpan s_lineTimeLimit = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// <paramref name="diagnostics"/>, as VS Code's hover shows them, each followed by its related places:
    /// <paramref name="open"/> makes what goes to a related place, or null where the editor cannot go there. A code's
    /// web page is a link only when it is one, an <c>http</c> or <c>https</c> URI. Each block is made as it is read, so
    /// that a hover that builds thousands a part at a time makes them a part at a time.
    /// </summary>
    public static IReadOnlyCollection<HoverBlock> Of(
        IReadOnlyCollection<Diagnostic> diagnostics,
        Func<Diagnostic, DiagnosticRelatedInformation, Action?>? open = null,
        HoverIcon icon = HoverIcon.Shown
    ) => new DiagnosticBlocks(diagnostics.Count, diagnostics.SelectMany(diagnostic => Of(diagnostic, open, icon)));

    private static IEnumerable<HoverBlock> Of(
        Diagnostic diagnostic,
        Func<Diagnostic, DiagnosticRelatedInformation, Action?>? open,
        HoverIcon icon
    )
    {
        yield return new HoverDiagnostic(diagnostic.Severity, Line(diagnostic), icon);
        foreach (DiagnosticRelatedInformation related in diagnostic.RelatedInformation)
        {
            yield return new HoverRelated(
                $"{Name(related.Uri)}({related.Start.Line}, {related.Start.Column}): ",
                related.Message,
                open?.Invoke(diagnostic, related),
                icon != HoverIcon.None
            );
        }
    }

    /// <summary>A diagnostic's message, then its source and its code, dimmed, as VS Code writes them: <c>message source(code)</c>.</summary>
    private static IReadOnlyList<HoverSpan> Line(Diagnostic diagnostic)
    {
        var line = new List<HoverSpan> { new(diagnostic.Message) };
        if (diagnostic.Source is null && diagnostic.Code is null)
            return line;
        line.Add(new HoverSpan(" " + diagnostic.Source, SpanStyle.Dimmed));
        if (diagnostic.Code is not { } code)
            return line;
        if (diagnostic.CodeDescription is not { Scheme: "http" or "https" } page)
        {
            line.Add(new HoverSpan($"({code})", SpanStyle.Dimmed));
            return line;
        }
        line.Add(new HoverSpan("(", SpanStyle.Dimmed));
        line.Add(new HoverSpan(code, SpanStyle.Dimmed | SpanStyle.Underline, page));
        line.Add(new HoverSpan(")", SpanStyle.Dimmed));
        return line;
    }

    /// <summary>The name of a document by its URI, as VS Code's <c>basename</c> takes it: the last part of its path.</summary>
    public static string Name(Uri uri)
    {
        string path = uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath);
        return Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : path;
    }

    /// <summary>The server's hover, all of it, as <see cref="Of(HoverInfo, TextMateGrammar?, HoverBudget?, CancellationToken)"/> reads it.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<HoverBlock> Of(
        HoverInfo hover,
        TextMateGrammar? grammar,
        CancellationToken cancellationToken
    ) => Of(hover, grammar, null, cancellationToken);

    /// <summary>
    /// The server's hover, its parts apart, as much of it as <paramref name="budget"/> leaves, or all of it without
    /// one: Markdown with its code blocks highlighted by <paramref name="grammar"/>, and plain text as it is. Off the UI
    /// thread: tokenizing a code block takes the grammar from the background highlighter for a line at a time. Reading
    /// stops between lines and paragraphs once <paramref name="cancellationToken"/> is cancelled, as the hover is no
    /// longer needed.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<HoverBlock> Of(
        HoverInfo hover,
        TextMateGrammar? grammar,
        HoverBudget? budget,
        CancellationToken cancellationToken
    )
    {
        var blocks = new List<HoverBlock>();
        foreach (HoverText part in hover.Contents)
        {
            if ((budget is null ? part.Text : budget.Take(part.Text)) is not { } text)
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
        return budget is null ? blocks : budget.Fit(blocks);
    }

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
/// How much completion's details and signature help show: a part of a hover (<see cref="HoverParts"/>), so that the UI
/// thread builds and lays them out within a frame, their parts in order. The text is cut where any of its limits runs
/// out, and "…" marks the cut.
/// </summary>
/// <remarks>
/// The text is cut before it is read, which bounds the work in the background too, and what is read is cut by its runs.
/// </remarks>
internal sealed class HoverBudget
{
    public const int MaxCharacters = HoverParts.MaxCharacters;

    public const int MaxLines = HoverParts.MaxLines;

    public const int MaxRuns = HoverParts.MaxRuns;

    private int _characters = MaxCharacters;
    private int _lines = MaxLines;
    private int _runs = MaxRuns;

    /// <summary>
    /// The start of a part of the details or of signature help, on lines of its own, that the characters and lines left
    /// allow, with "…" at its end when it was cut; null when nothing is left. The part is <paramref name="pieces"/> one
    /// after another, and each is read only as far as it is kept, so the work is bounded by the budget, not by the text.
    /// Every piece of their text passes here before anything else reads it.
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
                bool lineBreak = HoverParts.IsLineBreak(c) && !(c == '\n' && afterCarriageReturn);
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

/// <summary>The blocks of diagnostics, made as they are read; their count is the diagnostics'.</summary>
internal sealed class DiagnosticBlocks(int count, IEnumerable<HoverBlock> blocks) : IReadOnlyCollection<HoverBlock>
{
    private readonly IEnumerable<HoverBlock> _blocks = blocks;

    public int Count { get; } = count;

    public IEnumerator<HoverBlock> GetEnumerator() => _blocks.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
