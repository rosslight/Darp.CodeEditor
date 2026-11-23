using System.Text;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// One version of an open script: an immutable Roslyn document and its line positions. A request captures the snapshot
/// it arrived for, so later edits neither change its answer nor wait for it.
/// </summary>
/// <param name="Document">The Roslyn document, in a solution of its own.</param>
/// <param name="Text">The document's text.</param>
/// <param name="Lines">Converts between offsets and the client's positions.</param>
/// <param name="Version">The client's version of the text.</param>
/// <param name="EnvironmentError">Why the document's environment could not be fully loaded, if so.</param>
internal sealed record ScriptSnapshot(
    Document Document,
    SourceText Text,
    LineMap Lines,
    int Version,
    string? EnvironmentError
)
{
    /// <summary>This script after the client's <paramref name="changes"/>, each in the text the one before left.</summary>
    public ScriptSnapshot WithChanges(IReadOnlyList<TextDocumentContentChangeEvent> changes, int version)
    {
        SourceText text = Text;
        LineMap lines = Lines;
        foreach (TextDocumentContentChangeEvent change in changes)
        {
            if (change.Range is not { } range)
            {
                text = SourceText.From(change.Text, Encoding.UTF8);
                lines = LineMap.Of(text, change.Text);
                continue;
            }
            var span = TextSpan.FromBounds(lines.ToOffset(range.Start), lines.ToOffset(range.End));
            text = text.WithChanges(new TextChange(span, change.Text));
            lines = lines.WithChange(text, change.Text);
        }
        return this with { Document = Document.WithText(text), Text = text, Lines = lines, Version = version };
    }
}
