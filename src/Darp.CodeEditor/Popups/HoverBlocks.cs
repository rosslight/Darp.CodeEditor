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
internal sealed record HoverParagraph(IReadOnlyList<HoverSpan> Spans, int Level = 0, string? Marker = null)
    : HoverBlock;

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

    /// <summary>The messages of <paramref name="diagnostics"/>, each with its code, as plain text.</summary>
    public static IReadOnlyList<HoverBlock> Of(IEnumerable<Diagnostic> diagnostics) =>
        [
            .. diagnostics.Select(diagnostic => new HoverParagraph([
                new HoverSpan(diagnostic.Code is { } code ? $"{diagnostic.Message} ({code})" : diagnostic.Message),
            ])),
        ];

    /// <summary>
    /// The server's hover, its parts apart: Markdown with its code blocks highlighted by <paramref name="grammar"/>, and
    /// plain text as it is. Off the UI thread: tokenizing a code block takes the grammar from the background highlighter
    /// for a line at a time.
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
                blocks.AddRange(Markdown.Parse(part.Text, code => Highlight(code, grammar, cancellationToken)));
            else
                blocks.Add(new HoverParagraph([new HoverSpan(part.Text.Trim())]));
        }
        return blocks;
    }

    /// <summary>
    /// A code block in the colors of <paramref name="grammar"/>'s tokens, line by line; a line that runs out of time
    /// keeps the editor's foreground, and the next starts from the start of the grammar. Tokenizing stops between lines
    /// once <paramref name="cancellationToken"/> is cancelled, as the hover's highlighting is no longer needed.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static HoverCode Highlight(string code, TextMateGrammar? grammar, CancellationToken cancellationToken)
    {
        var runs = new List<CodeRun>();
        IStateStack? state = null;
        string[] lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i > 0)
                runs.Add(new CodeRun("\n", null));
            string line = lines[i];
            if (grammar?.TokenizeInBackground(line, state, s_lineTimeLimit) is not { } result)
            {
                runs.Add(new CodeRun(line, null));
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
                    runs.Add(new CodeRun(line[start..end], grammar.Foreground(tokens[t + 1])));
            }
        }
        return new HoverCode(runs);
    }
}
