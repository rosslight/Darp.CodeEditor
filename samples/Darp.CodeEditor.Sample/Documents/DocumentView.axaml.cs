using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Darp.CodeEditor;
using FluentAvalonia.UI.Controls;
using Sample.Localization;

namespace Sample.Documents;

/// <summary>
/// The document page: one editor for every open document, the commands of a text editor, and a status bar. The
/// commands live here, not in a view model, because they work with the editor, file pickers and dialogs.
/// </summary>
public sealed partial class DocumentView : UserControl
{
    private static readonly Indentation[] s_indentations =
    [
        Indentation.Spaces(2),
        Indentation.Spaces(4),
        Indentation.Tabs(4),
    ];

    private Workspace? _workspace;

    /// <summary>The document the editor shows.</summary>
    private OpenDocument? _shown;

    public DocumentView()
    {
        InitializeComponent();
        // The number box handles Enter itself, to commit its value.
        LineNumber.AddHandler(KeyUpEvent, OnLineNumberKeyUp, handledEventsToo: true);
        // The app's shortcuts. The editor handles its own keys, such as Ctrl+F, and leaves these to the app.
        AddShortcut("Ctrl+N", () => New(FileType.Text));
        AddShortcut("Ctrl+O", OpenAsync);
        AddShortcut("Ctrl+S", () => SaveCurrentAsync(saveAs: false));
        AddShortcut("Ctrl+Shift+S", () => SaveCurrentAsync(saveAs: true));
        AddShortcut("Ctrl+W", CloseCurrentAsync);
        AddShortcut("Ctrl+G", OpenGoToLine);
    }

    /// <summary>The status bar's text for a document's indentation: the texts for spaces and tabs, and the indentation.</summary>
    public static FuncMultiValueConverter<object?, string?> IndentationText { get; } =
        new(values =>
            values.ToList() is [string spaces, string tabs, Indentation indentation]
                ? Strings.Current.Format(indentation.UseTabs ? tabs : spaces, indentation.Size)
                : null
        );

    private static FilePickerFileType AllFiles => new(Strings.Current.AllFiles) { Patterns = ["*"] };

    private TopLevel Window =>
        TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("The view isn't shown.");

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
    }

    private void New(FileType type)
    {
        _workspace?.New(type);
        Editor.Focus();
    }

    private void OnNewMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not FAMenuFlyout menu)
            return;
        menu.Items.Clear();
        foreach (FileType type in FileType.All)
        {
            var item = new FAMenuFlyoutItem { Text = type.Name };
            item.Click += (_, _) => New(type);
            menu.Items.Add(item);
        }
    }

    private async Task OpenAsync()
    {
        if (_workspace is not { } workspace)
            return;
        IReadOnlyList<IStorageFile> files = await Window.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = Strings.Current.Open,
                AllowMultiple = true,
                FileTypeFilter = [AllFiles, .. FileType.All.Select(type => type.ToPickerType())],
            }
        );
        foreach (IStorageFile file in files)
        {
            try
            {
                await workspace.OpenAsync(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await ShowErrorAsync(Strings.Current.Format(Strings.Current.OpenFailed, file.Name), e.Message);
            }
        }
        Editor.Focus();
    }

    private async Task SaveCurrentAsync(bool saveAs)
    {
        if (_workspace?.Current is { } document)
            await SaveAsync(document, saveAs);
        Editor.Focus();
    }

    /// <summary>Saves <paramref name="document"/>, asking for a file if it has none yet or <paramref name="saveAs"/> is set.</summary>
    /// <returns>Whether the document was saved.</returns>
    private async Task<bool> SaveAsync(OpenDocument document, bool saveAs)
    {
        IStorageFile? file = saveAs ? null : document.File;
        if (file is null)
        {
            // The document keeps its language, also if the file gets another extension.
            string extension = document.Type.Extensions[0];
            file = await Window.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = Strings.Current.SaveAs,
                    SuggestedFileName = document.File?.Name ?? document.Name + extension,
                    DefaultExtension = extension,
                    FileTypeChoices = [document.Type.ToPickerType(), AllFiles],
                }
            );
            if (file is null)
                return false;
        }
        try
        {
            await document.SaveAsync(file);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (file != document.File)
                file.Dispose();
            await ShowErrorAsync(Strings.Current.Format(Strings.Current.SaveFailed, file.Name), e.Message);
            return false;
        }
    }

    /// <summary>Closes the current document, after asking to save its changes.</summary>
    private async Task CloseCurrentAsync()
    {
        if (_workspace is not { Current: { } document } workspace)
            return;
        if (document.Document.IsModified)
        {
            var dialog = new FAContentDialog
            {
                Title = Strings.Current.Format(Strings.Current.SaveChanges, document.Name),
                Content = Strings.Current.SaveChangesMessage,
                PrimaryButtonText = Strings.Current.Save,
                SecondaryButtonText = Strings.Current.DontSave,
                CloseButtonText = Strings.Current.Cancel,
                DefaultButton = FAContentDialogButton.Primary,
            };
            FAContentDialogResult result = await dialog.ShowAsync(Window);
            if (result == FAContentDialogResult.None)
                return;
            // A document that wasn't saved stays open, and so does one that was edited while it was saved.
            if (
                result == FAContentDialogResult.Primary
                && (!await SaveAsync(document, saveAs: false) || document.Document.IsModified)
            )
                return;
        }
        workspace.Close(document);
        Editor.Focus();
    }

    private Task ShowErrorAsync(string title, string message) =>
        new FAContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = Strings.Current.Ok,
        }.ShowAsync(Window);

    private void OpenGoToLine() => CaretButton.Flyout?.ShowAt(CaretButton);

    private void OnGoToLineOpened(object? sender, EventArgs e)
    {
        if (Editor.Document is not { } document)
            return;
        TextSnapshot snapshot = document.CurrentSnapshot;
        LineNumber.Maximum = snapshot.GetPosition(snapshot.Length).Line;
        LineNumber.Value = Editor.CaretPosition.Line;
        // Once the flyout shows, which is after it opened.
        Dispatcher.Post(() => LineNumber.Focus());
    }

    private void OnLineNumberKeyUp(object? sender, KeyEventArgs e)
    {
        // The key going down committed the value.
        if (e.Key == Key.Enter)
            GoToLine();
    }

    private void OnGoToLineConfirmed(object? sender, RoutedEventArgs e) => GoToLine();

    private void GoToLine()
    {
        CaretButton.Flyout?.Hide();
        if (Editor.Document is not { } document || double.IsNaN(LineNumber.Value))
            return;
        int offset = document.CurrentSnapshot.GetOffset(new TextPosition((int)LineNumber.Value, 1));
        Editor.Selection = new TextSelection(offset);
        Editor.ScrollTo(new TextRange(offset, 0));
        Editor.Focus();
    }

    private void OnIndentationMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not FAMenuFlyout menu || Editor.Document is not { } document)
            return;
        menu.Items.Clear();
        foreach (Indentation indentation in s_indentations)
        {
            var item = new FARadioMenuFlyoutItem
            {
                Text = Strings.Current.Format(
                    indentation.UseTabs ? Strings.Current.IndentTabs : Strings.Current.IndentSpaces,
                    indentation.Size
                ),
                IsChecked = document.Indentation == indentation,
            };
            // Every editor that shows the document indents so from now on. The text stays as it is.
            item.Click += (_, _) => document.Indentation = indentation;
            menu.Items.Add(item);
        }
    }

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        if (sender is Control button)
            FlyoutBase.ShowAttachedFlyout(button);
    }

    private async void OnOpen(object? sender, RoutedEventArgs e) => await OpenAsync();

    private async void OnSave(object? sender, RoutedEventArgs e) => await SaveCurrentAsync(saveAs: false);

    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveCurrentAsync(saveAs: true);

    private async void OnClose(object? sender, RoutedEventArgs e) => await CloseCurrentAsync();

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        Editor.Document?.Undo();
        Editor.Focus();
    }

    private void OnRedo(object? sender, RoutedEventArgs e)
    {
        Editor.Document?.Redo();
        Editor.Focus();
    }

    private void OnCut(object? sender, RoutedEventArgs e) => Editor.Cut();

    private void OnCopy(object? sender, RoutedEventArgs e) => Editor.Copy();

    private void OnPaste(object? sender, RoutedEventArgs e) => Editor.Paste();

    private void OnSelectAll(object? sender, RoutedEventArgs e) => Editor.SelectAll();

    private void OnFind(object? sender, RoutedEventArgs e) => Editor.OpenSearch();

    private void OnReplace(object? sender, RoutedEventArgs e) => Editor.OpenSearch(replace: true);

    private void OnGoToLine(object? sender, RoutedEventArgs e) => OpenGoToLine();

    private void OnInsertDateTime(object? sender, RoutedEventArgs e)
    {
        // Replaces the selection, as typing does, in one step that the user can undo.
        Editor.Document?.Replace(Editor.Selection.Range, DateTime.Now.ToString("g", Strings.Current.Culture));
        Editor.Focus();
    }

    private void AddShortcut(string gesture, Action action) =>
        KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse(gesture), Command = new RelayCommand(action) });

    private void AddShortcut(string gesture, Func<Task> action) =>
        KeyBindings.Add(
            new KeyBinding { Gesture = KeyGesture.Parse(gesture), Command = new AsyncRelayCommand(action) }
        );
}
