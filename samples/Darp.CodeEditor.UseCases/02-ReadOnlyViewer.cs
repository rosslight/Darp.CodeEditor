using Darp.CodeEditor;

namespace UseCases.Viewer;

// Scenario: a dialog shows JSON that the app produced, such as a device's configuration: colored, searchable,
// read-only. No language server.
//
// The dialog disposes the document, although it has no server: every document is disposed, one rule for all, so the
// code stays right if the document gets a server later.

/// <summary>A read-only JSON view.</summary>
public sealed class JsonViewer : IDisposable
{
    private readonly CodeDocument _document;

    /// <summary>Creates the view of <paramref name="json"/>.</summary>
    public JsonViewer(string json)
    {
        _document = CodeDocument.Create(CodeLanguage.Json, json);
        Editor = new CodeEditor
        {
            Document = _document,
            IsReadOnly = true,
            WordWrap = true,
        };
    }

    /// <summary>The control the dialog shows.</summary>
    public CodeEditor Editor { get; }

    /// <inheritdoc />
    public void Dispose() => _document.Dispose();
}
