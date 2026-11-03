namespace Darp.CodeEditor.CSharp.Execution;

/// <summary>A runner ended without being disposed. Handlers it returned no longer work.</summary>
public sealed class ScriptRunnerExitedEventArgs(ScriptRunnerExitReason reason, int? exitCode, string standardError)
    : EventArgs
{
    public ScriptRunnerExitReason Reason { get; } = reason;

    /// <summary>The exit code of the runner process, or null when it is unknown.</summary>
    public int? ExitCode { get; } = exitCode;

    /// <summary>The last lines the runner wrote to its console, such as the message of a fatal error.</summary>
    public string StandardError { get; } = standardError;
}

public enum ScriptRunnerExitReason
{
    /// <summary>The runner process ended or broke the connection, for example because a script crashed it.</summary>
    Crashed,

    /// <summary>A call did not end within the grace period after its deadline, so the runner was killed.</summary>
    Killed,
}
