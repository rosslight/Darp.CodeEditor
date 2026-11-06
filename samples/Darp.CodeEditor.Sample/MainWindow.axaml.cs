using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Sample.Documents;
using Sample.Localization;

namespace Sample;

/// <summary>
/// The title bar with the commands, the open documents in the pane, and the page: the document page, which shows
/// every document in one editor, or the settings. The window does what involves files and dialogs; the document page
/// does what involves the editor.
/// </summary>
public sealed partial class MainWindow : FAAppWindow
{
    private MainViewModel? _viewModel;

    /// <summary>
    /// Whether a save or a close runs. They ask the user and write files, so the window runs one at a time and
    /// ignores the commands meanwhile.
    /// </summary>
    private bool _isSavingOrClosing;

    public MainWindow()
    {
        InitializeComponent();
        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.Height = 48;
        // The app's shortcuts. The editor handles its own keys, such as Ctrl+F, and leaves these to the app.
        AddShortcut("Ctrl+N", () => New(FileType.Text));
        AddShortcut("Ctrl+O", OpenAsync);
        AddShortcut("Ctrl+S", () => SaveCurrentAsync(saveAs: false));
        AddShortcut("Ctrl+Shift+S", () => SaveCurrentAsync(saveAs: true));
        AddShortcut(
            "Ctrl+W",
            () => _viewModel?.ShownDocument is { } document ? CloseAsync(document) : Task.CompletedTask
        );
        AddShortcut("Ctrl+G", () => DocumentPage.OpenGoToLine());
        AddShortcut("Ctrl+OemComma", () => Navigation.SelectedItem = SettingsItem);
    }

    private static FilePickerFileType AllFiles => new(Strings.Current.AllFiles) { Patterns = ["*"] };

    private Workspace? Workspace => _viewModel?.Workspace;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.Workspace.PropertyChanged -= OnWorkspaceChanged;
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Workspace.PropertyChanged += OnWorkspaceChanged;
            Navigation.SelectedItem = _viewModel.Workspace.Current;
        }
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A document that was opened, created or closed changes the current one: show it.
        if (e.PropertyName == nameof(Workspace.Current))
            Navigation.SelectedItem = Workspace?.Current;
    }

    private void OnNavigationSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        if (_viewModel is not { } viewModel)
            return;
        viewModel.ShowsSettings = e.SelectedItem == SettingsItem;
        if (e.SelectedItem is OpenDocument document)
            viewModel.Workspace.Current = document;
    }

    private void OnDisplayModeChanged(object? sender, FANavigationViewDisplayModeChangedEventArgs e) =>
        PaneButton.IsVisible = e.DisplayMode == FANavigationViewDisplayMode.Minimal;

    private void OnPaneButtonClick(object? sender, RoutedEventArgs e) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    private void New(FileType type) => Workspace?.New(type);

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        if (sender is Control button)
            FlyoutBase.ShowAttachedFlyout(button);
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
        if (Workspace is not { } workspace)
            return;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
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
    }

    private Task SaveCurrentAsync(bool saveAs) =>
        _viewModel?.ShownDocument is { } document
            ? OneAtATimeAsync(() => SaveAsync(document, saveAs))
            : Task.CompletedTask;

    /// <summary>Saves <paramref name="document"/>, asking for a file if it has none yet or <paramref name="saveAs"/> is set.</summary>
    /// <returns>Whether the document was saved.</returns>
    private async Task<bool> SaveAsync(OpenDocument document, bool saveAs)
    {
        IStorageFile? file = saveAs ? null : document.File;
        if (file is null)
        {
            // The document keeps its language, also if the file gets another extension.
            string extension = document.Type.Extensions[0];
            file = await StorageProvider.SaveFilePickerAsync(
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

    /// <summary>Closes <paramref name="document"/>, after asking to save its changes.</summary>
    private Task CloseAsync(OpenDocument document) =>
        OneAtATimeAsync(async () =>
        {
            if (Workspace is not { } workspace)
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
                FAContentDialogResult result = await dialog.ShowAsync(this);
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
        });

    private async Task OneAtATimeAsync(Func<Task> operation)
    {
        if (_isSavingOrClosing)
            return;
        _isSavingOrClosing = true;
        try
        {
            await operation();
        }
        finally
        {
            _isSavingOrClosing = false;
        }
    }

    private Task ShowErrorAsync(string title, string message) =>
        new FAContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = Strings.Current.Ok,
        }.ShowAsync(this);

    private async void OnOpen(object? sender, RoutedEventArgs e) => await OpenAsync();

    private async void OnSave(object? sender, RoutedEventArgs e) => await SaveCurrentAsync(saveAs: false);

    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveCurrentAsync(saveAs: true);

    private void OnUndo(object? sender, RoutedEventArgs e) => DocumentPage.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => DocumentPage.Redo();

    private void OnFind(object? sender, RoutedEventArgs e) => DocumentPage.Find();

    private void OnReplace(object? sender, RoutedEventArgs e) => DocumentPage.Replace();

    private void OnGoToLine(object? sender, RoutedEventArgs e) => DocumentPage.OpenGoToLine();

    private void OnInsertDateTime(object? sender, RoutedEventArgs e) => DocumentPage.InsertDateTime();

    // A document's own commands: its right-click menu, its close button and a middle click on it.

    private async void OnDocumentSave(object? sender, RoutedEventArgs e)
    {
        if (DocumentOf(sender) is { } document)
            await OneAtATimeAsync(() => SaveAsync(document, saveAs: false));
    }

    private async void OnDocumentSaveAs(object? sender, RoutedEventArgs e)
    {
        if (DocumentOf(sender) is { } document)
            await OneAtATimeAsync(() => SaveAsync(document, saveAs: true));
    }

    private async void OnDocumentClose(object? sender, RoutedEventArgs e)
    {
        if (DocumentOf(sender) is { } document)
            await CloseAsync(document);
    }

    private async void OnDocumentPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle && DocumentOf(sender) is { } document)
            await CloseAsync(document);
    }

    private async void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        if (DocumentOf(sender)?.File?.TryGetLocalPath() is { } path && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(path);
    }

    private async void OnShowInFolder(object? sender, RoutedEventArgs e)
    {
        if (DocumentOf(sender)?.File is not { } file)
            return;
        using IStorageFolder? folder = await file.GetParentAsync();
        if (folder is not null)
            await Launcher.LaunchFileAsync(folder);
    }

    private static OpenDocument? DocumentOf(object? sender) => (sender as Control)?.DataContext as OpenDocument;

    private void AddShortcut(string gesture, Action action) =>
        KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse(gesture), Command = new RelayCommand(action) });

    private void AddShortcut(string gesture, Func<Task> action) =>
        KeyBindings.Add(
            new KeyBinding { Gesture = KeyGesture.Parse(gesture), Command = new AsyncRelayCommand(action) }
        );
}
