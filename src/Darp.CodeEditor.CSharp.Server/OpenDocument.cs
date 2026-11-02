using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>A document the client opened: its newest text and the Roslyn document for it.</summary>
/// <remarks>
/// <see cref="Update"/> and <see cref="Close"/> run on the thread that reads messages. Requests read
/// <see cref="Current"/> on that thread too and do their work on a <see cref="DocumentSnapshot"/>.
/// </remarks>
internal sealed class OpenDocument
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _versionSource = new();
    private RoslynVersion? _roslyn;

    public OpenDocument(string key, string clientUri, int version, SourceText text)
    {
        Key = key;
        ClientUri = clientUri;
        Current = new DocumentSnapshot(this, version, text, _versionSource.Token);
    }

    /// <summary>The normalized URI the server identifies the document by.</summary>
    public string Key { get; }

    /// <summary>The URI exactly as the client sent it, for notifications about the document.</summary>
    public string ClientUri { get; }

    public DocumentSnapshot Current { get; private set; }

    /// <summary>
    /// Cancelled when the document closes, or opens again as a new document. Work that may outlive a version, such as
    /// completion, stops then.
    /// </summary>
    public CancellationToken Closed => _lifetime.Token;

    /// <summary>Moves to a new version. Work for older versions sees its <see cref="DocumentSnapshot.Changed"/> token cancelled.</summary>
    public DocumentSnapshot Update(int version, SourceText text)
    {
        _versionSource.Cancel();
        _versionSource = new CancellationTokenSource();
        Current = new DocumentSnapshot(this, version, text, _versionSource.Token);
        return Current;
    }

    public void Close()
    {
        _versionSource.Cancel();
        _lifetime.Cancel();
    }

    /// <summary>
    /// The Roslyn document for <paramref name="snapshot"/>. Requests for the same version share one document and
    /// therefore one compilation; newer versions are derived from the previous one so Roslyn reparses incrementally.
    /// </summary>
    public Document GetDocument(ScriptWorkspace workspace, DocumentSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_roslyn is not { } latest || latest.Workspace != workspace)
            {
                Document created = workspace.CreateDocument(Key).WithText(snapshot.Text);
                _roslyn = new RoslynVersion(workspace, snapshot.Version, created);
                return created;
            }
            if (latest.Version == snapshot.Version)
                return latest.Document;
            Document document = latest.Document.WithText(snapshot.Text);
            // A request for an older version, arriving late, must not replace the newest document.
            if (snapshot.Version > latest.Version)
                _roslyn = new RoslynVersion(workspace, snapshot.Version, document);
            return document;
        }
    }

    private sealed record RoslynVersion(ScriptWorkspace Workspace, int Version, Document Document);
}

/// <summary>One version of an open document.</summary>
internal sealed class DocumentSnapshot
{
    private LineMap? _lines;

    public DocumentSnapshot(OpenDocument owner, int version, SourceText text, CancellationToken changed)
    {
        Owner = owner;
        Version = version;
        Text = text;
        Changed = changed;
    }

    public OpenDocument Owner { get; }

    public int Version { get; }

    public SourceText Text { get; }

    /// <summary>Cancelled when the document moves to a newer version or closes.</summary>
    public CancellationToken Changed { get; }

    public LineMap Lines => _lines ??= LineMap.Create(Text);
}
