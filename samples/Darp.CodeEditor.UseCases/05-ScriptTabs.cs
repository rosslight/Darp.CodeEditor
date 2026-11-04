using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Darp.CodeEditor;
using UseCases.Startup;

namespace UseCases.Tabs;

// Scenario: a script workbench with tabs. The user opens, edits, saves and closes sequence scripts.
//
// - Each tab's document lives in the view model, so switching tabs keeps its text, undo history, caret and scroll
//   position, even when the tab control recreates the editor.
// - Closing a tab disposes its document. Closing the workbench disposes the rest.
// - The error count covers every open tab, also those not shown: documents stay open in the server.
// - A tab title shows "*" while its document has unsaved changes.
//
// Questions:
// - One editor per tab (the tab control's default), or one editor whose Document switches? Both work.
// - Should a document know its file, with Load and Save, or is FilePath plus MarkUnmodified enough?

/// <summary>The script workbench.</summary>
public sealed partial class ScriptTabsViewModel(ScriptLanguages languages) : ObservableObject, IDisposable
{
    /// <summary>The open scripts.</summary>
    public ObservableCollection<ScriptTab> Tabs { get; } = [];

    /// <summary>The tab the user sees.</summary>
    [ObservableProperty]
    public partial ScriptTab? SelectedTab { get; set; }

    /// <summary>The errors in all open scripts, for the status bar.</summary>
    public int ErrorCount =>
        Tabs.Sum(tab => tab.Document.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));

    /// <summary>Opens a sequence script in a new tab.</summary>
    public async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        string text = await File.ReadAllTextAsync(path, cancellationToken);
        var tab = new ScriptTab(new CodeDocument(languages.Sequences, text, path));
        tab.Document.PropertyChanged += OnDocumentChanged;
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    /// <summary>Saves a tab's script.</summary>
    public async Task SaveAsync(ScriptTab tab, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(tab.Document.FilePath!, tab.Document.Text, cancellationToken);
        tab.Document.MarkUnmodified();
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
    /// <summary>Creates the tab of <paramref name="document"/>.</summary>
    public ScriptTab(CodeDocument document)
    {
        Document = document;
        document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CodeDocument.IsModified))
                OnPropertyChanged(nameof(Title));
        };
    }

    /// <summary>The script.</summary>
    public CodeDocument Document { get; }

    /// <summary>The file name, with "*" while there are unsaved changes.</summary>
    public string Title => Path.GetFileName(Document.FilePath) + (Document.IsModified ? " *" : "");
}
