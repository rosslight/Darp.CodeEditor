using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Darp.CodeEditor.Instrumentation;

namespace Darp.CodeEditor.Demo.Aot;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            UiStallProbe? probe = Program.Options.Measure
                ? new UiStallProbe(Dispatcher.UIThread, TimeSpan.FromMilliseconds(2))
                : null;
            probe?.BeginPhase("startup: until first highlighted frame");
            var window = new MainWindow();
            desktop.MainWindow = window;
            if (probe is not null || Program.Options.Scene)
                DemoRun.Start(window, desktop, probe, Program.Options);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
