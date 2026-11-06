using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sample.Documents;
using Sample.Settings;

namespace Sample;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The app owns the open documents for as long as it runs, and disposes them when it exits.
            var workspace = new Workspace();
            workspace.OpenExamples();
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(workspace, new AppSettings()) };
            desktop.Exit += (_, _) => workspace.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
