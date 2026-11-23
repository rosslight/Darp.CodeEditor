#:project ../../src/Darp.CodeEditor/Darp.CodeEditor.csproj
#:package Avalonia.Desktop
#:package Avalonia.Themes.Fluent
#:package CommunityToolkit.Mvvm

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using CommunityToolkit.Mvvm.Input;
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
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new InvalidOperationException("This example needs a desktop app.");

        CodeDocument document = CodeDocument.Create(CodeLanguage.Json, """{ "name": "example" }""");

        // --8<-- [start:options]
        var editor = new CodeEditor
        {
            Document = document,
            WordWrap = true,
            ShowLineNumbers = true,
            AutoClosing = false,
            FontSize = 14,
        };
        // --8<-- [end:options]

        // --8<-- [start:scope]
        var darkEditor = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = editor };
        // --8<-- [end:scope]

        // --8<-- [start:command]
        var comment = new Button { Content = "Comment" };
        comment.Click += (_, _) => editor.RunCommand(CodeEditorCommands.CommentLine);
        // --8<-- [end:command]

        var find = new Button { Content = "Replace" };
        // --8<-- [start:search]
        find.Click += (_, _) => editor.OpenSearch(replace: true);
        // --8<-- [end:search]

        // --8<-- [start:rebind]
        KeyBinding deleteLines = editor.KeyBindings.First(binding =>
            binding.CommandParameter is CodeEditorCommands.DeleteLines
        );
        deleteLines.Gesture = new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift);
        // --8<-- [end:rebind]

        // --8<-- [start:remove]
        editor.KeyBindings.RemoveAll(binding => binding.CommandParameter is CodeEditorCommands.ExpandLineSelection);
        // --8<-- [end:remove]

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { comment, find },
        };
        DockPanel.SetDock(bar, Dock.Top);
        var window = new Window
        {
            Title = "Customizing",
            Width = 720,
            Height = 420,
            Content = new DockPanel { Children = { bar, darkEditor } },
        };

        // --8<-- [start:app-key]
        window.KeyBindings.Add(
            new KeyBinding
            {
                Gesture = new KeyGesture(Key.S, KeyModifiers.Control),
                Command = new RelayCommand(() => window.Title = "Saved"),
            }
        );
        // --8<-- [end:app-key]

        window.Closed += (_, _) => document.Dispose();
        desktop.MainWindow = window;

        base.OnFrameworkInitializationCompleted();
    }
}
