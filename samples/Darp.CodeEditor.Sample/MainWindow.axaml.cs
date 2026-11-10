using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Darp.CodeEditor;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Sample.Documents;
using Sample.Localization;
using Sample.Settings;

namespace Sample;

/// <summary>
/// The title bar with the commands, the open documents in the pane, the page, and the status bar. The page is the
/// document page, which shows every document in one editor, or the settings. The window does what involves files and
/// dialogs; the document page does what involves the editor.
/// </summary>
public sealed partial class MainWindow : FAAppWindow
{
    private static readonly Indentation[] s_indentations =
    [
        Indentation.Spaces(2),
        Indentation.Spaces(4),
        Indentation.Tabs(4),
    ];

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
        TitleBar.Height = 44;
        // The number box handles Enter itself, to commit its value.
        LineNumber.AddHandler(KeyUpEvent, OnLineNumberKeyUp, handledEventsToo: true);
        // The app's shortcuts. The editor handles its own keys, such as Ctrl+F, and leaves these to the app.
        AddShortcut("Ctrl+N", () => New(FileType.Text));
        AddShortcut("Ctrl+O", OpenAsync);
        AddShortcut("Ctrl+S", () => SaveShownAsync(saveAs: false));
        AddShortcut("Ctrl+Shift+S", () => SaveShownAsync(saveAs: true));
        AddShortcut("Ctrl+W", () => ShownDocument is { } document ? CloseAsync(document) : Task.CompletedTask);
        AddShortcut("Ctrl+G", OpenGoToLine);
        AddShortcut("Alt+Z", ToggleWordWrap);
        AddShortcut("Ctrl+OemComma", () => Navigation.SelectedItem = SettingsItem);
    }

    /// <summary>
    /// The status bar's text for a document's indentation. Its values are the texts for spaces and for tabs, which
    /// change with the language, and the indentation.
    /// </summary>
    public static FuncMultiValueConverter<object?, string?> IndentationText { get; } =
        new(values => values.ToList() is [_, _, Indentation indentation] ? Describe(indentation) : null);

    /// <summary>
    /// The title bar's text after a document's name for its state. Its values are the state and the texts for no file
    /// and for unsaved changes, which change with the language.
    /// </summary>
    public static FuncMultiValueConverter<object?, string> StateText { get; } =
        new(values =>
            values.ToList() switch
            {
                [DocumentState.NoFile, string noFile, _] => $" · {noFile}",
                [DocumentState.Unsaved, _, string unsaved] => $" · {unsaved}",
                _ => "",
            }
        );

    /// <summary>
    /// The status bar's text for a document's language server. Its values are the texts for no server and for a
    /// server that isn't installed, which change with the language, the name of the type's server, and the server's
    /// state, null without a server.
    /// </summary>
    public static FuncMultiValueConverter<object?, string> ServerText { get; } =
        new(values =>
            values.ToList() switch
            {
                [string noServer, _, null, _] => noServer,
                [_, _, string name, LanguageServerState state] => $"{name}: {Strings.Current.Describe(state)}",
                [_, string notInstalled, string name, _] => Strings.Current.Format(notInstalled, name),
                _ => "",
            }
        );

    private static FilePickerFileType AllFiles => new(Strings.Current.AllFiles) { Patterns = ["*"] };

    private Workspace? Workspace => _viewModel?.Workspace;

    private AppSettings? Settings => _viewModel?.Settings;

    private OpenDocument? ShownDocument => _viewModel?.ShownDocument;

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
        PaneButton.IsVisible = e.DisplayMode != FANavigationViewDisplayMode.Expanded;

    private void OnPaneButtonClick(object? sender, RoutedEventArgs e) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    private void New(FileType type) => Workspace?.New(type);

    private void OnNewMenuOpening(object? sender, EventArgs e)
    {
        if (sender is FAMenuFlyout menu)
            FillNewMenu(menu.Items);
    }

    private void FillNewMenu(System.Collections.IList items)
    {
        items.Clear();
        foreach (FileType type in FileType.All)
            items.Add(MenuItem(type.Name, icon: null, () => New(type)));
    }

    /// <summary>
    /// Fills the ⋯ menu: the commands that have no button, and in narrow windows also those whose buttons are
    /// hidden.
    /// </summary>
    private void OnMoreMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not FAMenuFlyout menu)
            return;
        Strings strings = Strings.Current;
        bool shows = ShownDocument is not null;
        menu.Items.Clear();
        if (!SaveButton.IsVisible)
        {
            var newItem = new FAMenuFlyoutSubItem
            {
                Text = strings.New,
                IconSource = new FASymbolIconSource { Symbol = FASymbol.Add },
            };
            FillNewMenu(newItem.Items);
            menu.Items.Add(newItem);
            menu.Items.Add(MenuItem(strings.Open, FASymbol.OpenFile, async () => await OpenAsync()));
            menu.Items.Add(
                MenuItem(strings.Save, FASymbol.Save, async () => await SaveShownAsync(saveAs: false), shows)
            );
            menu.Items.Add(new FAMenuFlyoutSeparator());
            menu.Items.Add(
                MenuItem(strings.Undo, FASymbol.Undo, DocumentPage.Undo, ShownDocument?.Document.CanUndo == true)
            );
            menu.Items.Add(
                MenuItem(strings.Redo, FASymbol.Redo, DocumentPage.Redo, ShownDocument?.Document.CanRedo == true)
            );
            menu.Items.Add(MenuItem(strings.Find, FASymbol.Find, DocumentPage.Find, shows));
            var viewItem = new FAMenuFlyoutSubItem
            {
                Text = strings.View,
                IconSource = new FASymbolIconSource { Symbol = FASymbol.View },
            };
            FillViewMenu(viewItem.Items);
            menu.Items.Add(viewItem);
            menu.Items.Add(new FAMenuFlyoutSeparator());
        }
        menu.Items.Add(
            MenuItem(strings.SaveAs, FASymbol.SaveAs, async () => await SaveShownAsync(saveAs: true), shows)
        );
        menu.Items.Add(MenuItem(strings.Replace, FASymbol.Switch, DocumentPage.Replace, shows));
        menu.Items.Add(MenuItem(strings.GoToLine, FASymbol.Go, OpenGoToLine, shows));
        menu.Items.Add(MenuItem(strings.InsertDateTime, FASymbol.Calendar, DocumentPage.InsertDateTime, shows));
    }

    private void ToggleWordWrap()
    {
        if (Settings is { } settings)
            settings.WordWrap = !settings.WordWrap;
    }

    private void OnViewMenuOpening(object? sender, EventArgs e)
    {
        if (sender is FAMenuFlyout menu)
            FillViewMenu(menu.Items);
    }

    /// <summary>The view's options: how every editor shows its text. They last as long as the app runs.</summary>
    private void FillViewMenu(System.Collections.IList items)
    {
        items.Clear();
        if (Settings is not { } settings)
            return;
        var wordWrap = new FAToggleMenuFlyoutItem
        {
            Text = Strings.Current.WordWrap,
            IsChecked = settings.WordWrap,
            InputGesture = KeyGesture.Parse("Alt+Z"),
        };
        wordWrap.Click += (_, _) => ToggleWordWrap();
        var lineNumbers = new FAToggleMenuFlyoutItem
        {
            Text = Strings.Current.LineNumbers,
            IsChecked = settings.ShowLineNumbers,
        };
        lineNumbers.Click += (_, _) => settings.ShowLineNumbers = !settings.ShowLineNumbers;
        var fontSize = new FAMenuFlyoutSubItem { Text = Strings.Current.FontSize };
        foreach (double size in settings.FontSizes)
        {
            var item = new FARadioMenuFlyoutItem
            {
                Text = size.ToString(Strings.Current.Culture),
                IsChecked = settings.FontSize == size,
            };
            item.Click += (_, _) => settings.FontSize = size;
            fontSize.Items.Add(item);
        }
        items.Add(wordWrap);
        items.Add(lineNumbers);
        items.Add(new FAMenuFlyoutSeparator());
        items.Add(fontSize);
    }

    private static FAMenuFlyoutItem MenuItem(string text, FASymbol? icon, Action action, bool isEnabled = true)
    {
        var item = new FAMenuFlyoutItem
        {
            Text = text,
            IconSource = icon is { } symbol ? new FASymbolIconSource { Symbol = symbol } : null,
            IsEnabled = isEnabled,
        };
        item.Click += (_, _) => action();
        return item;
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

    private Task SaveShownAsync(bool saveAs) =>
        ShownDocument is { } document ? OneAtATimeAsync(() => SaveAsync(document, saveAs)) : Task.CompletedTask;

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
            if (file.TryGetLocalPath() is { } path && Workspace?.IsOpenInAnother(document, path) == true)
            {
                await ShowErrorAsync(
                    Strings.Current.Format(Strings.Current.SaveFailed, file.Name),
                    Strings.Current.OpenInAnother
                );
                file.Dispose();
                return false;
            }
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

    private void OnNew(object? sender, RoutedEventArgs e) => ShowAttachedFlyout(sender);

    private void OnMore(object? sender, RoutedEventArgs e) => ShowAttachedFlyout(sender);

    private void OnView(object? sender, RoutedEventArgs e) => ShowAttachedFlyout(sender);

    private static void ShowAttachedFlyout(object? sender)
    {
        if (sender is Control button)
            FlyoutBase.ShowAttachedFlyout(button);
    }

    private async void OnOpen(object? sender, RoutedEventArgs e) => await OpenAsync();

    private async void OnSave(object? sender, RoutedEventArgs e) => await SaveShownAsync(saveAs: false);

    private void OnUndo(object? sender, RoutedEventArgs e) => DocumentPage.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => DocumentPage.Redo();

    private void OnFind(object? sender, RoutedEventArgs e) => DocumentPage.Find();

    // The status bar.

    private void OpenGoToLine()
    {
        if (ShownDocument is not null)
            CaretButton.Flyout?.ShowAt(CaretButton);
    }

    private void OnGoToLineOpened(object? sender, EventArgs e)
    {
        if (ShownDocument is not { } document)
            return;
        TextSnapshot snapshot = document.Document.CurrentSnapshot;
        LineNumber.Maximum = snapshot.GetPosition(snapshot.Length).Line;
        LineNumber.Value = DocumentPage.CaretPosition.Line;
        // Once the flyout shows, which is after it opened.
        Dispatcher.Post(() => LineNumber.Focus());
    }

    private void OnLineNumberKeyUp(object? sender, KeyEventArgs e)
    {
        // The key going down committed the value.
        if (e.Key != Key.Enter || double.IsNaN(LineNumber.Value))
            return;
        CaretButton.Flyout?.Hide();
        DocumentPage.GoToLine((int)LineNumber.Value);
    }

    private void OnIndentationMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not FAMenuFlyout menu || ShownDocument?.Document is not { } document)
            return;
        menu.Items.Clear();
        foreach (Indentation indentation in s_indentations)
        {
            var item = new FARadioMenuFlyoutItem
            {
                Text = Describe(indentation),
                IsChecked = document.Indentation == indentation,
            };
            // Every editor that shows the document indents so from now on. The text stays as it is.
            item.Click += (_, _) => document.Indentation = indentation;
            menu.Items.Add(item);
        }
    }

    private static string Describe(Indentation indentation) =>
        Strings.Current.Format(
            indentation.UseTabs ? Strings.Current.IndentTabs : Strings.Current.IndentSpaces,
            indentation.Size
        );

    // A document's own commands: its right-click menu and a middle click on it.

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
