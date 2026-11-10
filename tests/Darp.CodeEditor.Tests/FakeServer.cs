using System.Collections.Concurrent;
using System.Diagnostics;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The fake language server, <c>Darp.CodeEditor.Tests.FakeServer</c>: what it writes to standard error, line by line,
/// and the options to start it. Its program says which arguments and commands it takes.
/// </summary>
internal sealed class FakeServer
{
    /// <summary>Documents of this language hold the fake server's commands.</summary>
    public static readonly CodeLanguage Commands = new("fake-command");

    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>The options that start the fake server with <paramref name="arguments"/>, with a log of its lines.</summary>
    public FakeServer(params IEnumerable<string> arguments)
    {
        (string fileName, IReadOnlyList<string> commandLine) = Fixtures.CommandLine(
            "Darp.CodeEditor.Tests.FakeServer",
            arguments
        );
        Options = new LanguageServerOptions
        {
            FileName = fileName,
            Arguments = commandLine,
            StandardError = _lines.Enqueue,
        };
    }

    public LanguageServerOptions Options { get; }

    /// <summary>The lines that the processes wrote to standard error so far, in order.</summary>
    public IReadOnlyList<string> Lines => [.. _lines];

    /// <summary>The lines that start with <paramref name="prefix"/>.</summary>
    public IReadOnlyList<string> LinesStartingWith(string prefix) => [.. _lines.Where(l => l.StartsWith(prefix))];

    /// <summary>Waits until <paramref name="count"/> lines start with <paramref name="prefix"/>, and returns the last.</summary>
    public async Task<string> WaitForAsync(string prefix, int count = 1)
    {
        await Wait.UntilAsync(() => LinesStartingWith(prefix).Count >= count);
        return LinesStartingWith(prefix)[count - 1];
    }

    /// <summary>The process ids of the processes started so far.</summary>
    public IReadOnlyList<int> ProcessIds => [.. LinesStartingWith("pid ").Select(l => int.Parse(l["pid ".Length..]))];

    /// <summary>Whether the process with <paramref name="id"/> has ended.</summary>
    public static bool HasEnded(int id)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
