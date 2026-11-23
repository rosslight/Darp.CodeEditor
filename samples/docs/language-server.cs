#:project ../../src/Darp.CodeEditor/Darp.CodeEditor.csproj
#:package Avalonia.Desktop
#:package Avalonia.Themes.Fluent

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Darp.CodeEditor;

// Needs a JSON language server on PATH: npm install -g vscode-langservers-extracted
// On Windows, set FileName to the full path of the .cmd script that npm installs.
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
            const string text = """{ "name": "example", "name": "duplicate" }""";
            string filePath = Path.Combine(Path.GetTempPath(), "darp-codeeditor-example.json");

            // --8<-- [start:connect]
            var server = new LanguageServer(
                new LanguageServerOptions
                {
                    FileName = "vscode-json-language-server",
                    Arguments = ["--stdio"],
                    Settings = GetSettings,
                    StandardError = line => Trace.WriteLine(line, "language-server"),
                }
            );
            CodeDocument document = server.CreateDocument(CodeLanguage.Json, text, filePath);
            // --8<-- [end:connect]

            var editor = new CodeEditor { Document = document };
            var status = new TextBlock { Text = server.State.ToString() };

            // --8<-- [start:state]
            server.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LanguageServer.State))
                    status.Text = server.State.ToString();
            };
            // --8<-- [end:state]

            var goToProblem = new Button { Content = "Go to first problem" };
            goToProblem.Click += (_, _) =>
            {
                if (document.Diagnostics is [var first, ..])
                    Select(editor, document, first);
            };

            var bar = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                Children = { goToProblem, status },
            };
            DockPanel.SetDock(bar, Dock.Top);
            var window = new Window
            {
                Title = "Language server",
                Width = 720,
                Height = 420,
                Content = new DockPanel { Children = { bar, editor } },
            };

            // --8<-- [start:exit]
            desktop.Exit += (_, _) =>
            {
                document.Dispose();
                server.Dispose();
            };
            // --8<-- [end:exit]
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    // --8<-- [start:settings]
    private static JsonNode? GetSettings(LanguageServerSettingsRequest request) =>
        request.Section switch
        {
            null => new JsonObject { ["json"] = JsonSettings() },
            "json" => JsonSettings(),
            _ => null,
        };

    private static JsonObject JsonSettings() => new() { ["validate"] = new JsonObject { ["enable"] = true } };

    // --8<-- [end:settings]

    // --8<-- [start:diagnostic]
    private static void Select(CodeEditor editor, CodeDocument document, Diagnostic diagnostic)
    {
        TextRange range = document.MapToCurrent(diagnostic.Snapshot, diagnostic.Range);
        editor.Selection = new TextSelection(range.Start, range.End);
        editor.ScrollTo(range);
    }

    // --8<-- [end:diagnostic]

    // --8<-- [start:stop]
    internal static async Task StopAsync(LanguageServer server, IEnumerable<CodeDocument> documents)
    {
        foreach (CodeDocument document in documents)
            document.Dispose();
        await server.DisposeAsync();
    }

    // --8<-- [end:stop]
}
