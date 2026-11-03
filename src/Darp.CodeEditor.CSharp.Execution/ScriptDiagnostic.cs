using PolyType;

namespace Darp.CodeEditor.CSharp.Execution;

/// <summary>A compiler diagnostic of a script, as the editor shows it.</summary>
/// <remarks>Lines and characters are zero-based and count UTF-16 code units, as in LSP.</remarks>
[GenerateShape]
public sealed partial record ScriptDiagnostic(
    string Id,
    ScriptDiagnosticSeverity Severity,
    string Message,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter
)
{
    /// <summary>Formats the diagnostic like the compiler does, with one-based line and column.</summary>
    public override string ToString() =>
        $"({StartLine + 1},{StartCharacter + 1}): {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
}

public enum ScriptDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}
