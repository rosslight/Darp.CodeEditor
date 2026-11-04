namespace Darp.CodeEditor;

/// <summary>An error, warning or hint about a span of text, from a language server or a compiler.</summary>
/// <param name="Range">The span the diagnostic is about.</param>
/// <param name="Start">The line and column where <paramref name="Range"/> starts, for lists and logs.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Code">The tool's identifier for the kind of problem, such as <c>CS0103</c>, if it has one.</param>
/// <param name="Message">The description for users.</param>
public sealed record Diagnostic(
    TextRange Range,
    TextPosition Start,
    DiagnosticSeverity Severity,
    string? Code,
    string Message
);

/// <summary>How serious a <see cref="Diagnostic"/> is.</summary>
public enum DiagnosticSeverity
{
    /// <summary>A suggestion, shown subtly.</summary>
    Hint,

    /// <summary>Information, not a problem.</summary>
    Information,

    /// <summary>A probable problem that does not stop a script from running.</summary>
    Warning,

    /// <summary>A problem that stops a script from compiling or running.</summary>
    Error,
}
