using System.Collections;
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
/// A diagnostic, as VS Code's hover shows it, in the editor's font: the icon of its severity beside its message, which
/// keeps its line breaks, then its source and code, dimmed, the code as a link to its web page where it has one, and
/// below them the other places it concerns, each as a link to the place where the editor can go there. A piece that
/// goes on from the one before with nothing on its message's line, only with places, shows no such line.
/// </summary>
/// <param name="Severity">Its severity, for its icon.</param>
/// <param name="Message">Its message, or a piece of it; empty for none.</param>
/// <param name="Source">What reported it; null for none, as in a piece that more pieces follow.</param>
/// <param name="Code">Its code; null for none.</param>
/// <param name="CodeLink">The web page of its code; null for none.</param>
/// <param name="Related">The other places it concerns.</param>
/// <param name="Icon">Whether its severity's icon shows beside it, or only the room of one, beside a piece after the first.</param>
internal sealed record HoverDiagnostic(
    DiagnosticSeverity Severity,
    string Message,
    string? Source,
    string? Code,
    Uri? CodeLink,
    IReadOnlyList<HoverRelated> Related,
    HoverIcon Icon = HoverIcon.Shown
) : HoverBlock;

/// <summary>Whether a diagnostic's icon shows beside it.</summary>
internal enum HoverIcon
{
    /// <summary>No icon, and no room for one, as below a heading that shows it.</summary>
    None,

    /// <summary>The icon.</summary>
    Shown,

    /// <summary>The room of the icon, beside a piece of a message after the first.</summary>
    Room,
}

/// <summary>A place that a diagnostic concerns.</summary>
/// <param name="Place">Where it is, as VS Code writes it: <c>file.py(3, 5): </c>.</param>
/// <param name="Message">What it has to do with the problem.</param>
/// <param name="Open">Goes there; null where the editor cannot, as in another document.</param>
internal sealed record HoverRelated(string Place, string Message, Action? Open)
{
    /// <summary>
    /// Whether it goes on from the place before, as a piece of a place larger than a part does: without a place of its
    /// own, and no space between them.
    /// </summary>
    public bool Continues { get; init; }
}

/// <summary>
/// The heading of the problem that a key went to, as VS Code's widget for it shows it: the icon of its severity, the
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
}

/// <summary>What a hover shows: the messages of the diagnostics under the pointer, above the server's hover.</summary>
internal static class HoverBlocks
{
    /// <summary>How long a line of a code block may take to tokenize, as the background highlighter allows.</summary>
    private static readonly TimeSpan s_lineTimeLimit = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// <paramref name="diagnostics"/>, as VS Code's hover shows them (<see cref="HoverDiagnostic"/>):
    /// <paramref name="open"/> makes what goes to a related place, or null where the editor cannot go there. A code's
    /// web page is a link only when it is one, an <c>http</c> or <c>https</c> URI. Each is made as it is read, so that
    /// a hover that builds thousands a part at a time reads them a part at a time.
    /// </summary>
    public static IReadOnlyCollection<HoverBlock> Of(
        IReadOnlyCollection<Diagnostic> diagnostics,
        Func<Diagnostic, DiagnosticRelatedInformation, Action?>? open = null,
        HoverIcon icon = HoverIcon.Shown
    ) =>
        new DiagnosticBlocks(
            diagnostics,
            diagnostic => new HoverDiagnostic(
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.Source,
                diagnostic.Code,
                diagnostic.CodeDescription is { Scheme: "http" or "https" } page ? page : null,
                [
                    .. diagnostic.RelatedInformation.Select(related => new HoverRelated(
                        $"{Name(related.Uri)}({related.Start.Line}, {related.Start.Column}): ",
                        related.Message,
                        open?.Invoke(diagnostic, related)
                    )),
                ],
                icon
            )
        );

    /// <summary>The name of a document by its URI, as VS Code's <c>basename</c> takes it: the last part of its path.</summary>
    public static string Name(Uri uri)
    {
        string path = uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath);
        return Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : path;
    }

    /// <summary>
    /// The server's hover, its parts apart, all of it: Markdown with its code blocks highlighted by
    /// <paramref name="grammar"/>, and plain text as it is. Off the UI thread: tokenizing a code block takes the grammar
    /// from the background highlighter for a line at a time. Reading stops between lines and paragraphs once
    /// <paramref name="cancellationToken"/> is cancelled, as the hover is no longer needed.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<HoverBlock> Of(
        HoverInfo hover,
        TextMateGrammar? grammar,
        CancellationToken cancellationToken
    )
    {
        var blocks = new List<HoverBlock>();
        foreach (HoverText part in hover.Contents)
        {
            if (blocks.Count > 0)
                blocks.Add(new HoverRule());
            if (part.IsMarkdown)
            {
                blocks.AddRange(
                    Markdown.Parse(part.Text, code => Highlight(code, grammar, cancellationToken), cancellationToken)
                );
            }
            else
                blocks.Add(new HoverParagraph([new HoverSpan(part.Text.Trim())]));
        }
        return blocks;
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

    /// <summary>A block for each diagnostic, made as it is read.</summary>
    private sealed class DiagnosticBlocks(
        IReadOnlyCollection<Diagnostic> diagnostics,
        Func<Diagnostic, HoverBlock> block
    ) : IReadOnlyCollection<HoverBlock>
    {
        public int Count => diagnostics.Count;

        public IEnumerator<HoverBlock> GetEnumerator() => diagnostics.Select(block).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>
/// How much of a hover's content the UI thread builds and lays out at a time, so that each part takes a frame at most:
/// <see cref="MaxCharacters"/> characters on <see cref="MaxLines"/> lines, in <see cref="MaxRuns"/> runs. A hover shows
/// all of its content, as VS Code's does: the first part as it opens, the next parts in the frames after it, and a block
/// larger than a part in pieces, each going on from the one before. A long signature, such as Python's
/// <c>subprocess.run</c> with about 35 lines and 250 runs, fits a part with its documentation.
/// </summary>
/// <remarks>
/// The UI thread's cost follows the runs, the pieces of text of one style or one color that it builds and shapes one by
/// one, more than the characters: about 10 µs a run and 1 µs a character. The hover benchmark's largest part, its frame
/// included, takes about 13 ms with these limits.
/// </remarks>
internal static class HoverParts
{
    public const int MaxCharacters = 5_000;

    public const int MaxLines = 120;

    public const int MaxRuns = 500;

    /// <summary>
    /// <paramref name="block"/> in pieces of at most a part each: a code block between its lines, a paragraph that is
    /// no list item between its lines, or within a line too long for a part, after a space where there is one. Each
    /// piece after the first goes on from the one before. Other blocks, and blocks that fit, stay whole.
    /// </summary>
    public static IEnumerable<HoverBlock> Split(HoverBlock block)
    {
        switch (block)
        {
            case HoverCode code when !Fits(Size(code)):
                return Pieces(
                    code.Runs.Select(run => (run.Text, run)),
                    (runs, first) =>
                        new HoverCode([.. runs.Select(r => r.Run with { Text = r.Text })])
                        {
                            Continues = !first || code.Continues,
                        }
                );
            case HoverDiagnostic diagnostic when !Fits(Size(diagnostic)):
                return DiagnosticPieces(diagnostic);
            case HoverParagraph { Marker: null } paragraph when !Fits(Size(paragraph)):
                return Pieces(
                    paragraph.Spans.Select(span => (span.Text, span)),
                    (spans, first) =>
                        new HoverParagraph([.. spans.Select(s => s.Run with { Text = s.Text })], paragraph.Level)
                        {
                            Continues = !first || paragraph.Continues,
                        }
                );
            default:
                return [block];
        }
    }

    /// <summary>
    /// The size of <paramref name="block"/> as a part counts it: its characters, lines and runs, a list item's marker
    /// as a run.
    /// </summary>
    public static (int Characters, int Lines, int Runs) Size(HoverBlock block)
    {
        switch (block)
        {
            case HoverParagraph paragraph:
                (int characters, int lines) = Count(paragraph.Spans.Select(span => span.Text));
                return (characters, lines, paragraph.Spans.Count + (paragraph.Marker is null ? 0 : 1));
            case HoverCode code:
                (characters, lines) = Count(code.Runs.Select(run => run.Text));
                return (characters, lines, code.Runs.Count);
            case HoverDiagnostic diagnostic:
                (characters, lines) = Count([diagnostic.Message, diagnostic.Source ?? "", diagnostic.Code ?? ""]);
                (int Characters, int Lines, int Runs) size = (characters, lines, 5);
                foreach (HoverRelated related in diagnostic.Related)
                    size = Add(size, Size(related));
                return size;
            default:
                return (0, 1, 1);
        }
    }

    /// <summary>The size of a related place as a part counts it: on a line of its own, in two runs.</summary>
    private static (int Characters, int Lines, int Runs) Size(HoverRelated related)
    {
        (int characters, int lines) = Count([related.Place, related.Message]);
        return (characters, lines, 2);
    }

    private static (int Characters, int Lines, int Runs) Add(
        (int Characters, int Lines, int Runs) a,
        (int Characters, int Lines, int Runs) b
    ) => (a.Characters + b.Characters, a.Lines + b.Lines, a.Runs + b.Runs);

    /// <summary>
    /// A diagnostic larger than a part in pieces of at most a part each, each going on from the one before: its
    /// message, also an empty one, with the icon beside the first piece and its source and code after the last; then
    /// its related places, with that piece while they fit it and then in pieces of their own, a place larger than a
    /// part in pieces too.
    /// </summary>
    private static IEnumerable<HoverBlock> DiagnosticPieces(HoverDiagnostic diagnostic)
    {
        HoverIcon room = diagnostic.Icon == HoverIcon.None ? HoverIcon.None : HoverIcon.Room;
        HoverDiagnostic? current = null;
        foreach (
            HoverDiagnostic piece in Pieces(
                [(diagnostic.Message, 0)],
                (texts, first) =>
                    new HoverDiagnostic(
                        diagnostic.Severity,
                        string.Concat(texts.Select(t => t.Text)),
                        null,
                        null,
                        null,
                        [],
                        first ? diagnostic.Icon : room
                    )
                    {
                        Continues = !first || diagnostic.Continues,
                    }
            )
        )
        {
            if (current is not null)
                yield return current;
            current = piece;
        }
        current = (current ?? diagnostic with { Message = "" }) with
        {
            Source = diagnostic.Source,
            Code = diagnostic.Code,
            CodeLink = diagnostic.CodeLink,
            Related = [],
        };
        var related = new List<HoverRelated>();
        (int Characters, int Lines, int Runs) size = Size(current);
        foreach (HoverRelated place in diagnostic.Related.SelectMany(RelatedPieces))
        {
            (int Characters, int Lines, int Runs) with = Add(size, Size(place));
            if (Fits(with))
            {
                related.Add(place);
                size = with;
                continue;
            }
            yield return current with
            {
                Related = related,
            };
            // A piece of related places alone: no line of its own for the message.
            current = new HoverDiagnostic(diagnostic.Severity, "", null, null, null, [], room) { Continues = true };
            related = [place];
            size = Add(Size(current), Size(place));
        }
        yield return current with
        {
            Related = related,
        };
    }

    /// <summary>
    /// <paramref name="related"/>, or when it is larger than a part, its pieces: the place, which opens it, with the
    /// first, and each next going on from the one before.
    /// </summary>
    private static IEnumerable<HoverRelated> RelatedPieces(HoverRelated related) =>
        Fits(Size(related))
            ? [related]
            : Pieces(
                [(related.Place, true), (related.Message, false)],
                (texts, first) =>
                    new HoverRelated(
                        string.Concat(texts.Where(t => t.Run).Select(t => t.Text)),
                        string.Concat(texts.Where(t => !t.Run).Select(t => t.Text)),
                        first ? related.Open : null
                    )
                    {
                        Continues = !first,
                    }
            );

    /// <summary>Whether something of <paramref name="size"/> fits a part.</summary>
    public static bool Fits((int Characters, int Lines, int Runs) size) =>
        size.Characters <= MaxCharacters && size.Lines <= MaxLines && size.Runs <= MaxRuns;

    /// <summary>
    /// The runs of a block in pieces of at most a part each: cut at a line break, which goes with neither piece, as the
    /// next piece starts on a line of its own; within a line too long for a part, after its last space in the run that
    /// fills the part, or where the part is full, but never inside a surrogate pair; and after the most runs.
    /// </summary>
    private static IEnumerable<TPiece> Pieces<T, TPiece>(
        IEnumerable<(string Text, T Run)> runs,
        Func<List<(string Text, T Run)>, bool, TPiece> piece
    )
    {
        var current = new List<(string Text, T Run)>();
        int characters = 0;
        int lines = 1;
        bool first = true;
        foreach ((string text, T run) in runs)
        {
            int start = 0;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (IsLineBreak(c))
                {
                    int length = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                    if (lines < MaxLines)
                    {
                        lines++;
                        i += length;
                        continue;
                    }
                    // The next line starts the next piece.
                    Add(start, i);
                    foreach (TPiece done in Flush())
                        yield return done;
                    start = i += length;
                    continue;
                }
                if (characters + i - start < MaxCharacters)
                {
                    i++;
                    continue;
                }
                int space = i > start ? text.LastIndexOf(' ', i - 1, i - start) : -1;
                int cut = space >= start ? space + 1 : i;
                if (cut > start && char.IsHighSurrogate(text[cut - 1]))
                    cut--;
                Add(start, cut);
                foreach (TPiece done in Flush())
                    yield return done;
                start = i = cut;
            }
            Add(start, text.Length);
            if (current.Count >= MaxRuns)
            {
                foreach (TPiece done in Flush())
                    yield return done;
            }

            void Add(int from, int to)
            {
                if (to <= from)
                    return;
                current.Add((text[from..to], run));
                characters += to - from;
            }
        }
        foreach (TPiece done in Flush())
            yield return done;

        IEnumerable<TPiece> Flush()
        {
            List<(string Text, T Run)> done = current;
            current = [];
            characters = 0;
            lines = 1;
            if (done.Count == 0)
                yield break;
            yield return piece(done, first);
            first = false;
        }
    }

    /// <summary>The characters of <paramref name="texts"/>, and the lines they take.</summary>
    private static (int Characters, int Lines) Count(IEnumerable<string> texts)
    {
        int characters = 0;
        int lines = 1;
        bool afterCarriageReturn = false;
        foreach (string text in texts)
        {
            characters += text.Length;
            foreach (char c in text)
            {
                if (IsLineBreak(c) && !(c == '\n' && afterCarriageReturn))
                    lines++;
                afterCarriageReturn = c == '\r';
            }
        }
        return (characters, lines);
    }

    /// <summary>
    /// Whether <paramref name="c"/> breaks a line: at <c>\r\n</c>, <c>\n</c> and <c>\r</c>, as everywhere in the
    /// library, and also where the layout of text breaks them: at a vertical tab, a form feed, and the Unicode line and
    /// paragraph separators.
    /// </summary>
    private static bool IsLineBreak(char c) => c is '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029';
}
