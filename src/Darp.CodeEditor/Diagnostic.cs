namespace Darp.CodeEditor;

/// <summary>An error, warning or hint about a span of text, from a language server or a compiler.</summary>
/// <param name="Snapshot">
/// The text the diagnostic is about: the document's text that the server analyzed, or the compiled text. To find the
/// range in the current text, pass both to <see cref="CodeDocument.MapToCurrent"/>.
/// </param>
/// <param name="Range">The span the diagnostic is about, in <paramref name="Snapshot"/>.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Code">The tool's identifier for the kind of problem, such as <c>CS0103</c>, if it has one.</param>
/// <param name="Message">The description for users.</param>
public sealed record Diagnostic(
    TextSnapshot Snapshot,
    TextRange Range,
    DiagnosticSeverity Severity,
    string? Code,
    string Message
)
{
    /// <summary>The line and column where <see cref="Range"/> starts, for lists and logs.</summary>
    public TextPosition Start => Snapshot.GetPosition(Range.Start);
}

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
