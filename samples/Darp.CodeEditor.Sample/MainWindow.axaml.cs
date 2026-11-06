using System.ComponentModel;
using Avalonia.Data.Converters;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Sample.Documents;

namespace Sample;

/// <summary>
/// The open documents in the navigation pane, and the page of the selected one: the document page, which shows every
/// document in one editor, or the settings.
/// </summary>
public sealed partial class MainWindow : FAAppWindow
{
    private Workspace? _workspace;

    public MainWindow()
    {
        InitializeComponent();
        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.Height = 44;
    }

    /// <summary>A document's icon in the navigation pane: one for documents in memory, one for files.</summary>
    public static FuncValueConverter<IStorageFile?, FAIconSource> DocumentIcon { get; } =
        new(file => new FASymbolIconSource { Symbol = file is null ? FASymbol.Code : FASymbol.Document });

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_workspace is not null)
            _workspace.PropertyChanged -= OnWorkspaceChanged;
        _workspace = (DataContext as MainViewModel)?.Workspace;
        if (_workspace is not null)
        {
            _workspace.PropertyChanged += OnWorkspaceChanged;
            Navigation.SelectedItem = _workspace.Current;
        }
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A document that was opened, created or closed changes the current one: show it.
        if (e.PropertyName == nameof(Workspace.Current))
            Navigation.SelectedItem = _workspace?.Current;
    }

    private void OnNavigationSelectionChanged(object? sender, FANavigationViewSelectionChangedEventArgs e)
    {
        bool showsSettings = e.SelectedItem == SettingsItem;
        SettingsPage.IsVisible = showsSettings;
        DocumentPage.IsVisible = !showsSettings;
        if (_workspace is not null && e.SelectedItem is OpenDocument document)
            _workspace.Current = document;
    }
}
