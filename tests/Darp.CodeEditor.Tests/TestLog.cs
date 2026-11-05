using System.Collections.Concurrent;
using Avalonia.Logging;

namespace Darp.CodeEditor.Tests;

/// <summary>Collects Avalonia's log entries, for tests of load errors.</summary>
internal sealed class TestLog : ILogSink
{
    private static readonly TestLog s_instance = new();

    private readonly ConcurrentQueue<(LogEventLevel Level, string Area, object? Source)> _entries = new();

    /// <summary>The entries of <paramref name="source"/>, once the log is installed as Avalonia's sink.</summary>
    public static IReadOnlyList<(LogEventLevel Level, string Area)> EntriesOf(object source) =>
        [.. s_instance._entries.Where(e => e.Source == source).Select(e => (e.Level, e.Area))];

    /// <summary>Makes this log Avalonia's sink, before the code under test logs.</summary>
    public static void Install() => Logger.Sink = s_instance;

    public bool IsEnabled(LogEventLevel level, string area) => true;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        _entries.Enqueue((level, area, source));

    public void Log(
        LogEventLevel level,
        string area,
        object? source,
        string messageTemplate,
        params object?[] propertyValues
    ) => _entries.Enqueue((level, area, source));
}
