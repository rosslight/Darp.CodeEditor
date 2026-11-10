using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Logging;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Collects the library's entries in Avalonia's log, for tests of load errors, and the binding warnings and errors of
/// code that a test runs. Only those, and binding entries as text: an entry keeps its source alive, and tests check
/// that discarded editors are collected.
/// </summary>
internal sealed class TestLog : ILogSink
{
    private static readonly TestLog s_instance = new();

    /// <summary>
    /// The binding entries of the code that <see cref="BindingErrorsOf"/> runs on this thread. Avalonia logs a binding
    /// on the thread that updates it, so tests that run meanwhile on other threads add none.
    /// </summary>
    [ThreadStatic]
    private static List<string>? s_bindingErrors;

    private readonly ConcurrentQueue<(LogEventLevel Level, string Area, object? Source)> _entries = new();

    /// <summary>The entries of <paramref name="source"/>, once the log is installed as Avalonia's sink.</summary>
    public static IReadOnlyList<(LogEventLevel Level, string Area)> EntriesOf(object source) =>
        [.. s_instance._entries.Where(e => e.Source == source).Select(e => (e.Level, e.Area))];

    /// <summary>
    /// Runs <paramref name="action"/>, and returns the binding warnings and errors it logs: their level, the type and
    /// name of the control, and the message.
    /// </summary>
    public static IReadOnlyList<string> BindingErrorsOf(Action action)
    {
        Install();
        List<string> errors = [];
        s_bindingErrors = errors;
        try
        {
            action();
        }
        finally
        {
            s_bindingErrors = null;
        }
        return errors;
    }

    /// <summary>Makes this log Avalonia's sink, before the code under test logs.</summary>
    public static void Install() => Logger.Sink = s_instance;

    public bool IsEnabled(LogEventLevel level, string area) =>
        area == AssetFiles.LogArea
        || (area == LogArea.Binding && level >= LogEventLevel.Warning && s_bindingErrors is not null);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(
        LogEventLevel level,
        string area,
        object? source,
        string messageTemplate,
        params object?[] propertyValues
    )
    {
        if (area != LogArea.Binding)
            _entries.Enqueue((level, area, source));
        else
            s_bindingErrors?.Add(
                $"{level} {source?.GetType().Name} #{(source as StyledElement)?.Name}: {messageTemplate} "
                    + $"[{string.Join(", ", propertyValues)}]"
            );
    }
}
