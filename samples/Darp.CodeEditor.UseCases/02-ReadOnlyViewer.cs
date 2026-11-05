using Darp.CodeEditor;

namespace UseCases.Viewer;

// Scenario: a dialog shows JSON that the app produced, such as a device's configuration: colored, searchable,
// read-only. No language server.
//
// The document has no server, so it holds nothing that needs disposing; the dialog disposes it anyway, so that the
// code stays right if the language gets a server later.
//
// Questions:
// - Is "dispose every document" a fair rule, or should documents without a server not be disposable at all?

/// <summary>A read-only JSON view.</summary>
public sealed class JsonViewer : IDisposable
{
    private readonly CodeDocument _document;

    /// <summary>Creates the view of <paramref name="json"/>.</summary>
    public JsonViewer(string json)
    {
        _document = new CodeDocument(CodeLanguage.Json, json);
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
