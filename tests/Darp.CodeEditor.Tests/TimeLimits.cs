using System.Runtime.CompilerServices;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The time limits of what a key does, set so that a slow or busy machine can't make them run out and change what it
/// does: indentation's clock stands still, and a match, which runs on the system's clock, gets a second. Tests of the
/// limits pass a clock that they advance, or match a pattern that backtracks for longer.
/// </summary>
internal static class TimeLimits
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        IndentationRules.Clock = new ManualTime();
        LanguageConfiguration.PatternTimeout = TimeSpan.FromSeconds(1);
    }
}
