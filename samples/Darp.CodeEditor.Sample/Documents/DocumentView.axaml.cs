using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Darp.CodeEditor;
using Sample.Localization;

namespace Sample.Documents;

/// <summary>
/// The document page: one editor for every open document. Its public members are what the title bar and the status
/// bar need from the editor.
/// </summary>
public sealed partial class DocumentView : UserControl
{
    /// <summary>Defines the <see cref="CaretPosition"/> property.</summary>
    public static readonly DirectProperty<DocumentView, TextPosition> CaretPositionProperty =
        AvaloniaProperty.RegisterDirect<DocumentView, TextPosition>(nameof(CaretPosition), view => view.CaretPosition);

    /// <summary>Defines the <see cref="PendingKeys"/> property.</summary>
    public static readonly DirectProperty<DocumentView, KeyGesture?> PendingKeysProperty =
        AvaloniaProperty.RegisterDirect<DocumentView, KeyGesture?>(nameof(PendingKeys), view => view.PendingKeys);

    private Workspace? _workspace;

    /// <summary>The document the editor shows.</summary>
    private OpenDocument? _shown;

    public DocumentView()
    {
        InitializeComponent();
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == CodeEditor.CaretPositionProperty)
                RaisePropertyChanged(CaretPositionProperty, default, CaretPosition);
            else if (e.Property == CodeEditor.PendingKeysProperty)
                RaisePropertyChanged(PendingKeysProperty, (KeyGesture?)e.OldValue, PendingKeys);
        };
    }

    /// <summary>The caret's line and column, for the status bar.</summary>
    public TextPosition CaretPosition => Editor.CaretPosition;

    /// <summary>The first key of a sequence while the editor waits for the second, for the status bar.</summary>
    public KeyGesture? PendingKeys => Editor.PendingKeys;

    public void Undo()
    {
        Editor.Document?.Undo();
        Editor.Focus();
    }

    public void Redo()
    {
        Editor.Document?.Redo();
        Editor.Focus();
    }

    public void Find() => Editor.OpenSearch();

    public void Replace() => Editor.OpenSearch(replace: true);

    public void InsertDateTime()
    {
        // Replaces the selection, as typing does, in one step that the user can undo.
        Editor.Document?.Replace(Editor.Selection.Range, DateTime.Now.ToString("g", Strings.Default.Culture));
        Editor.Focus();
    }

    /// <summary>Puts the caret at the start of <paramref name="line"/>, counted from 1, and scrolls it into view.</summary>
    public void GoToLine(int line)
    {
        if (Editor.Document is not { } document)
            return;
        int offset = document.CurrentSnapshot.GetOffset(new TextPosition(line, 1));
        Editor.Selection = new TextSelection(offset);
        Editor.ScrollTo(new TextRange(offset, 0));
        Editor.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_workspace is not null)
            _workspace.PropertyChanged -= OnWorkspaceChanged;
        _workspace = (DataContext as MainViewModel)?.Workspace;
        if (_workspace is not null)
            _workspace.PropertyChanged += OnWorkspaceChanged;
        Show(_workspace?.Current);
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.Current))
            Show(_workspace?.Current);
    }

    /// <summary>
    /// Shows <paramref name="document"/> where the user left it. The caret, the selection and the scroll position
    /// belong to the editor, so the view saves them for the document it leaves and restores them for the one it shows.
    /// </summary>
    private void Show(OpenDocument? document)
    {
        if (document == _shown)
            return;
        if (_shown is not null)
            _shown.ViewState = Editor.SaveViewState();
        _shown = document;
        Editor.Document = document?.Document;
        if (document?.ViewState is { } state)
            Editor.RestoreViewState(state);
        Editor.Focus();
    }
}
