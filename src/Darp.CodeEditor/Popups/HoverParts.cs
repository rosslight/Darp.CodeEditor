namespace Darp.CodeEditor.Popups;

/// <summary>
/// How much of a hover's content the UI thread builds and lays out at a time, so that each part takes a frame at most:
/// <see cref="MaxCharacters"/> characters on <see cref="MaxLines"/> lines, in <see cref="MaxRuns"/> runs. A hover shows
/// all of its content, as VS Code's does: the first part as it opens, the next parts in the frames after it, and a block
/// larger than a part in pieces, each going on from the one before.
/// </summary>
/// <remarks>
/// The UI thread's cost follows the runs, the pieces of text of one style or one color that it builds and shapes one by
/// one, more than the characters: about 10 µs a run and 1 µs a character. A part is as large as the hover was before it
/// showed all of its content, so that its frame costs what that hover's did (the hover benchmark).
/// </remarks>
internal static class HoverParts
{
    public const int MaxCharacters = 2_000;

    public const int MaxLines = 50;

    public const int MaxRuns = 250;

    /// <summary>
    /// <paramref name="block"/> in pieces of at most a part each: a code block, a paragraph or a diagnostic's line between
    /// its lines, or within a line too long for a part, after a space where there is one. Each piece after the first goes
    /// on from the one before; a list item's marker goes with its first piece. Other blocks, and blocks that fit, stay
    /// whole.
    /// </summary>
    public static IEnumerable<HoverBlock> Split(HoverBlock block) =>
        block switch
        {
            HoverCode code when !Fits(Size(code)) => Pieces(
                code.Runs.Select(run => (run.Text, run)),
                (runs, first) =>
                    new HoverCode([.. runs.Select(r => r.Run with { Text = r.Text })])
                    {
                        Continues = !first || code.Continues,
                    }
            ),
            HoverDiagnostic diagnostic when !Fits(Size(diagnostic)) => DiagnosticPieces(diagnostic),
            HoverParagraph paragraph when !Fits(Size(paragraph)) => Pieces(
                paragraph.Spans.Select(span => (span.Text, span)),
                (spans, first) =>
                    new HoverParagraph(
                        [.. spans.Select(s => s.Run with { Text = s.Text })],
                        paragraph.Level,
                        first ? paragraph.Marker : null
                    )
                    {
                        Continues = !first || paragraph.Continues,
                    },
                reservedRuns: paragraph.Marker is null ? 0 : 1
            ),
            _ => [block],
        };

    /// <summary>
    /// The size of <paramref name="block"/> as a part counts it: its characters, lines and runs, a list item's marker
    /// and a diagnostic's icon as a run.
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
                (characters, lines) = Count(diagnostic.Line.Select(span => span.Text));
                (int Characters, int Lines, int Runs) size = (characters, lines, diagnostic.Line.Count + 1);
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
    /// A diagnostic larger than a part in pieces of at most a part each, each going on from the one before: its line,
    /// its message, source and code, with the icon beside the first piece; then its related places, with the last piece
    /// of the line while they fit it and then in pieces of their own, a place larger than a part in pieces too.
    /// </summary>
    private static IEnumerable<HoverBlock> DiagnosticPieces(HoverDiagnostic diagnostic)
    {
        HoverIcon room = diagnostic.Icon == HoverIcon.None ? HoverIcon.None : HoverIcon.Room;
        HoverDiagnostic? current = null;
        foreach (
            HoverDiagnostic piece in Pieces(
                diagnostic.Line.Select(span => (span.Text, span)),
                (spans, first) =>
                    new HoverDiagnostic(
                        diagnostic.Severity,
                        [.. spans.Select(s => s.Run with { Text = s.Text })],
                        [],
                        first ? diagnostic.Icon : room
                    )
                    {
                        Continues = !first || diagnostic.Continues,
                    },
                reservedRuns: 1
            )
        )
        {
            if (current is not null)
                yield return current;
            current = piece;
        }
        current ??= diagnostic with { Line = [], Related = [] };
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
            current = new HoverDiagnostic(diagnostic.Severity, [], [], room) { Continues = true };
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
    /// fills the part, or where the part is full, but never inside a surrogate pair; and after the most runs, less
    /// <paramref name="reservedRuns"/> that the piece takes besides, as a list item's marker.
    /// </summary>
    private static IEnumerable<TPiece> Pieces<T, TPiece>(
        IEnumerable<(string Text, T Run)> runs,
        Func<List<(string Text, T Run)>, bool, TPiece> piece,
        int reservedRuns = 0
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
                    // A line break takes characters of the part too.
                    if (lines < MaxLines && characters + i - start + length <= MaxCharacters)
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
            if (current.Count >= MaxRuns - reservedRuns)
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
    internal static bool IsLineBreak(char c) => c is '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029';
}
