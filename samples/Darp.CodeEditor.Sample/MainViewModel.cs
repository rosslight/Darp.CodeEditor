using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sample.Documents;
using Sample.Localization;
using Sample.Settings;

namespace Sample;

/// <summary>What the main window shows: the open documents or the settings, in the app's language.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(Workspace workspace, AppSettings settings)
    {
        Workspace = workspace;
        Settings = settings;
        // The workspace lives as long as this view model.
        Workspace.PropertyChanged += OnWorkspaceChanged;
    }

    public Workspace Workspace { get; }

    public AppSettings Settings { get; }

    public Strings Strings => Strings.Current;

    /// <summary>Whether the settings page shows instead of the current document.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownDocument))]
    public partial bool ShowsSettings { get; set; }

    /// <summary>The document the page shows, which the title bar's commands act on; null while the settings show.</summary>
    public OpenDocument? ShownDocument => ShowsSettings ? null : Workspace.Current;

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.Current))
            OnPropertyChanged(nameof(ShownDocument));
    }
}
