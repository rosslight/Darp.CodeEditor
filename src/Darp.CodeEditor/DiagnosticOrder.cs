namespace Darp.CodeEditor;

/// <summary>
/// The order in which the editor goes to the next and previous diagnostic, as F8 and Shift+F8 do: VS Code's
/// <c>problems.sortOrder</c>.
/// </summary>
public enum DiagnosticOrder
{
    /// <summary>Errors first, then warnings, then information, each by position: VS Code's default.</summary>
    Severity,

    /// <summary>By position, the most severe first among those that start and end alike.</summary>
    Position,
}
