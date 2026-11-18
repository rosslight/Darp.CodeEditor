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

    /// <summary>What reported it, such as <c>Pylance</c> or <c>json</c>; null if it does not say.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// A web page that explains <see cref="Code"/>, as a language server's <c>codeDescription</c> names it: the hover
    /// shows the code as a link to it. Null for none.
    /// </summary>
    public Uri? CodeDescription { get; init; }

    /// <summary>
    /// Other places that the problem concerns, such as an earlier declaration of a name declared twice, which the hover
    /// lists below the message. Empty for none.
    /// </summary>
    public IReadOnlyList<DiagnosticRelatedInformation> RelatedInformation { get; init; } = [];
}

/// <summary>A place that a <see cref="Diagnostic"/> concerns besides its own range, with a message about it.</summary>
/// <param name="Uri">The document of the place, as the language server names it, such as a <c>file:</c> URI.</param>
/// <param name="Start">Where the place starts in that document.</param>
/// <param name="Message">What the place has to do with the problem.</param>
public sealed record DiagnosticRelatedInformation(Uri Uri, TextPosition Start, string Message);

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
