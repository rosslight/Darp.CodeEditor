using System.Runtime.CompilerServices;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The time limits of what a key does, set so that a slow or busy machine can't make them run out and change what it
/// does: indentation's clock stands still, and a match and the tokens around the caret, which run on the system's clock,
/// get a second. Tests of the limits pass a clock that they advance, or match a pattern that backtracks for longer.
/// </summary>
internal static class TimeLimits
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        IndentationRules.Clock = new ManualTime();
        IndentationRules.MatchTimeout = TimeSpan.FromSeconds(1);
        BackgroundTextMateHighlighter.InsertionTimeLimit = TimeSpan.FromSeconds(1);
    }
}
