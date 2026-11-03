using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Darp.CodeEditor.CSharp.Execution;

/// <summary>The script did not compile. Nothing ran.</summary>
public sealed class ScriptCompilationException : Exception
{
    public ScriptCompilationException(IReadOnlyList<ScriptDiagnostic> diagnostics)
        : base(FormatMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    /// <summary>Every compiler diagnostic of the script, errors and warnings, as the editor shows them.</summary>
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; }

    private static string FormatMessage(IReadOnlyList<ScriptDiagnostic> diagnostics)
    {
        ScriptDiagnostic? first = diagnostics.FirstOrDefault(d => d.Severity == ScriptDiagnosticSeverity.Error);
        return first is null ? "The script did not compile." : $"The script did not compile: {first}";
    }
}

/// <summary>Script code threw an exception, while the script ran or in a call on its handler.</summary>
/// <remarks>
/// The runner keeps running, and the handler stays usable. The exception's type and stack trace come from the runner;
/// the exception object itself does not cross processes.
/// </remarks>
public sealed class ScriptException : RemoteInvocationException
{
    internal ScriptException(string message, int errorCode, object? errorData, CommonErrorData? details)
        : base(message, errorCode, errorData, details)
    {
        ScriptExceptionType = details?.TypeName;
        ScriptStackTrace = details?.StackTrace;
    }

    /// <summary>The full name of the exception type the script threw, such as "System.InvalidOperationException".</summary>
    public string? ScriptExceptionType { get; }

    /// <summary>The stack trace in the runner.</summary>
    public string? ScriptStackTrace { get; }

    public override string ToString() =>
        ScriptStackTrace is null
            ? base.ToString()
            : $"{GetType().FullName}: {ScriptExceptionType}: {Message}{Environment.NewLine}{ScriptStackTrace}";
}
