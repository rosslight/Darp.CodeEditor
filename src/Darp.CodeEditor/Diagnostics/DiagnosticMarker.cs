using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Diagnostics;

/// <summary>A diagnostic that editors show, where it is in the current text.</summary>
internal sealed class DiagnosticMarker(Diagnostic diagnostic, int rank) : TextSegment
{
    public Diagnostic Diagnostic { get; } = diagnostic;

    /// <summary>Its place in a hover: the most severe first, then in the server's order.</summary>
    public int Rank { get; } = rank;
}
