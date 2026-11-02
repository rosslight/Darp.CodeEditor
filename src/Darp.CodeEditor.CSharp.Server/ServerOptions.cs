namespace Darp.CodeEditor.CSharp.Server;

/// <summary>Command line options of the server.</summary>
/// <param name="DiagnosticsDelay">How long the text must stay unchanged before diagnostics are computed.</param>
/// <param name="WarmUp">Whether to run every feature once in the background after the environment loaded.</param>
internal sealed record ServerOptions(TimeSpan DiagnosticsDelay, bool WarmUp, CompletionListLimits CompletionListLimits)
{
    public static ServerOptions Default { get; } =
        new(TimeSpan.FromMilliseconds(200), WarmUp: true, CompletionListLimits.Default);

    /// <summary>
    /// Reads <c>--diagnostics-delay &lt;ms&gt;</c>, <c>--no-warm-up</c>, <c>--completion-filter-above &lt;items&gt;</c>
    /// and <c>--completion-limit &lt;items&gt;</c>. Ignores other arguments, such as <c>--stdio</c>.
    /// </summary>
    public static ServerOptions Parse(IReadOnlyList<string> args)
    {
        ServerOptions options = Default;
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--diagnostics-delay" when i + 1 < args.Count && int.TryParse(args[i + 1], out int delay):
                    options = options with { DiagnosticsDelay = TimeSpan.FromMilliseconds(delay) };
                    i++;
                    break;
                case "--no-warm-up":
                    options = options with { WarmUp = false };
                    break;
                case "--completion-filter-above" when i + 1 < args.Count && int.TryParse(args[i + 1], out int above):
                    options = options with
                    {
                        CompletionListLimits = options.CompletionListLimits with { FilterAbove = above },
                    };
                    i++;
                    break;
                case "--completion-limit" when i + 1 < args.Count && int.TryParse(args[i + 1], out int limit):
                    options = options with
                    {
                        CompletionListLimits = options.CompletionListLimits with { MaxItems = limit },
                    };
                    i++;
                    break;
            }
        }
        return options;
    }
}

/// <summary>When the server, not the client, filters completion lists by the typed text.</summary>
/// <param name="FilterAbove">Lists with more items are filtered by the typed text and marked incomplete.</param>
/// <param name="MaxItems">The most items a filtered list keeps: the best matches.</param>
/// <remarks>
/// A word list after one letter has over 3,000 items; sending them all costs about as much as computing them. With
/// both limits at 1,000 it keeps up to 1,000 matches, and the client asks again for each further letter.
/// </remarks>
internal sealed record CompletionListLimits(int FilterAbove, int MaxItems)
{
    public static CompletionListLimits Default { get; } = new(1000, 1000);
}
