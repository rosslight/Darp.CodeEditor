using Sample.Documents;
using Sample.Localization;
using Sample.Settings;

namespace Sample;

/// <summary>What the main window shows: the open documents and the settings, in the app's language.</summary>
public sealed class MainViewModel(Workspace workspace, AppSettings settings)
{
    public Workspace Workspace { get; } = workspace;

    public AppSettings Settings { get; } = settings;

    public Strings Strings => Strings.Current;
}
