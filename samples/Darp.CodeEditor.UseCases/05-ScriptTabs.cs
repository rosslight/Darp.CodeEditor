using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using UseCases.Startup;

namespace UseCases.Tabs;

// Scenario: a script workbench with tabs. The user opens, edits, saves and closes scripts.
//
// - Each tab's document lives in the view model, so switching tabs keeps its text and undo history.
// - The caret and the scroll position belong to the editor, as a list's scroll position does. A view that keeps one
//   editor per open tab, such as an ItemsControl that shows only the selected one, keeps them without code. A view
//   with one editor for all tabs saves and restores them, as SharedEditor below does.
// - Closing a tab disposes its document. Closing the workbench disposes the rest.
// - The error count covers every open tab, also those not shown: documents stay open in the server.
// - A tab title shows "*" while its document has unsaved changes.
// - The tab keeps the file's path and does the reading and writing; the document only learns what was saved, with
//   MarkUnmodified. The C# server does not need the path, so the document has none.

/// <summary>The script workbench.</summary>
public sealed partial class ScriptTabsViewModel(CSharpScriptServer scripts) : ObservableObject, IDisposable
{
    private readonly CSharpScriptServer _scripts = scripts;

    /// <summary>The open scripts.</summary>
    public ObservableCollection<ScriptTab> Tabs { get; } = [];

    /// <summary>The tab the user sees.</summary>
    [ObservableProperty]
    public partial ScriptTab? SelectedTab { get; set; }

    /// <summary>The errors in all open scripts, for the status bar.</summary>
    public int ErrorCount =>
        Tabs.Sum(tab => tab.Document.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));

    /// <summary>Opens a script in a new tab.</summary>
    public async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        string text = await File.ReadAllTextAsync(path, cancellationToken);
        var tab = new ScriptTab(_scripts.CreateDocument(ScriptEnvironments.Default, text), path);
        tab.Document.PropertyChanged += OnDocumentChanged;
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    /// <summary>Saves a tab's script. Edits made while it saves keep the tab modified.</summary>
    public async Task SaveAsync(ScriptTab tab, CancellationToken cancellationToken)
    {
        TextSnapshot snapshot = tab.Document.CurrentSnapshot;
        await File.WriteAllTextAsync(tab.FilePath, snapshot.GetText(), cancellationToken);
        tab.Document.MarkUnmodified(snapshot);
    }

    /// <summary>Closes a tab for good.</summary>
    public void Close(ScriptTab tab)
    {
        Tabs.Remove(tab);
        tab.Document.PropertyChanged -= OnDocumentChanged;
        tab.Document.Dispose();
        OnPropertyChanged(nameof(ErrorCount));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (ScriptTab tab in Tabs.ToList())
            Close(tab);
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeDocument.Diagnostics))
            OnPropertyChanged(nameof(ErrorCount));
    }
}

/// <summary>One open script. Its view is a <see cref="CodeEditor"/> bound to <see cref="Document"/>.</summary>
public sealed partial class ScriptTab : ObservableObject
{
    /// <summary>Creates the tab of <paramref name="document"/>, saved in <paramref name="filePath"/>.</summary>
    public ScriptTab(CodeDocument document, string filePath)
    {
        Document = document;
        FilePath = filePath;
        document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CodeDocument.IsModified))
                OnPropertyChanged(nameof(Title));
        };
    }

    /// <summary>The script.</summary>
    public CodeDocument Document { get; }

    /// <summary>The file the script is saved in.</summary>
    public string FilePath { get; }

    /// <summary>The file name, with "*" while there are unsaved changes.</summary>
    public string Title => Path.GetFileName(FilePath) + (Document.IsModified ? " *" : "");
}

/// <summary>
/// For a view that shows every tab in one editor: shows a tab's document where the user left it. Call
/// <see cref="Show"/> when the selected tab changes, instead of binding the editor's document.
/// </summary>
public sealed class SharedEditor
{
    private readonly CodeEditor _editor;
    private readonly Dictionary<CodeDocument, CodeEditorViewState> _places = [];

    /// <summary>Switches the documents of <paramref name="editor"/>.</summary>
    public SharedEditor(CodeEditor editor) => _editor = editor;

    /// <summary>Shows <paramref name="document"/>, at its caret and scroll position from last time.</summary>
    public void Show(CodeDocument document)
    {
        if (_editor.Document is { } shown)
            _places[shown] = _editor.SaveViewState();
        _editor.Document = document;
        if (_places.TryGetValue(document, out CodeEditorViewState? place))
            _editor.RestoreViewState(place);
    }

    /// <summary>Forgets the place of a document whose tab was closed.</summary>
    public void Forget(CodeDocument document) => _places.Remove(document);
}
