#:project ../../src/Darp.CodeEditor/Darp.CodeEditor.csproj
#:package Avalonia.Desktop
#:package Avalonia.Themes.Fluent

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Darp.CodeEditor;

AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);

sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new CodeEditorStyles());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var editor = new CodeEditor();

            // --8<-- [start:document]
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, """{ "hello": "world" }""");
            editor.Document = document;
            // --8<-- [end:document]

            var window = new Window
            {
                Title = "First editor",
                Width = 720,
                Height = 420,
                Content = editor,
            };
            // --8<-- [start:dispose]
            window.Closed += (_, _) => document.Dispose();
            // --8<-- [end:dispose]
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
