#:project ../../src/Darp.CodeEditor/Darp.CodeEditor.csproj
#:package Avalonia.Desktop
#:package Avalonia.Themes.Fluent

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Darp.CodeEditor;

AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);

sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new CodeEditorStyles());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new InvalidOperationException("This example needs a desktop app.");

        string folder = Path.Combine((string)AppContext.GetData("EntryPointFileDirectoryPath")!, "assets");

        // --8<-- [start:custom]
        var ini = new CodeLanguage(
            "ini",
            grammar: new Uri(Path.Combine(folder, "ini.tmLanguage.json")),
            configuration: new Uri(Path.Combine(folder, "ini.language-configuration.json"))
        );
        // --8<-- [end:custom]

        // --8<-- [start:adjust]
        var yaml = new CodeLanguage("yaml", CodeLanguage.Yaml.Grammar, CodeLanguage.Yaml.Configuration)
        {
            AutoIndent = AutoIndent.Keep,
            QuickSuggestions = new QuickSuggestions(Other: true, Comments: false, Strings: true),
        };
        // --8<-- [end:adjust]

        CodeDocument iniDocument = CodeDocument.Create(ini, "; settings\n[editor]\nfont = mono\n");
        CodeDocument yamlDocument = CodeDocument.Create(yaml, "name: example\n");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var right = new CodeEditor { Document = yamlDocument };
        Grid.SetColumn(right, 1);
        grid.Children.Add(new CodeEditor { Document = iniDocument });
        grid.Children.Add(right);

        var window = new Window
        {
            Title = "Languages",
            Width = 900,
            Height = 420,
            Content = grid,
        };
        window.Closed += (_, _) =>
        {
            iniDocument.Dispose();
            yamlDocument.Dispose();
        };
        desktop.MainWindow = window;

        base.OnFrameworkInitializationCompleted();
    }
}
