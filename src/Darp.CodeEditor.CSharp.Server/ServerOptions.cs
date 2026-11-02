namespace Darp.CodeEditor.CSharp.Server;

/// <summary>Command line options of the server.</summary>
/// <param name="DiagnosticsDelay">How long the text must stay unchanged before diagnostics are computed.</param>
/// <param name="WarmUp">Whether to run every feature once in the background after the environment loaded.</param>
internal sealed record ServerOptions(TimeSpan DiagnosticsDelay, bool WarmUp)
{
    public static ServerOptions Default { get; } = new(TimeSpan.FromMilliseconds(200), WarmUp: true);

    /// <summary>Reads <c>--diagnostics-delay &lt;ms&gt;</c> and <c>--no-warm-up</c>. Ignores other arguments, such as <c>--stdio</c>.</summary>
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
            }
        }
        return options;
    }
}
