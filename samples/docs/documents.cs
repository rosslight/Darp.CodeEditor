#:project ../../src/Darp.CodeEditor/Darp.CodeEditor.csproj
#:package Avalonia.Desktop
#:package Avalonia.Themes.Fluent

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
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
            string path = Path.Combine(Path.GetTempPath(), "darp-codeeditor-example.json");
            CodeDocument json = CodeDocument.Create(CodeLanguage.Json, """{ "name": "first" }""");
            CodeDocument yaml = CodeDocument.Create(CodeLanguage.Yaml, "name: second\n");
            var editor = new CodeEditor { Document = json };

            var showJson = new Button { Content = "JSON" };
            var showYaml = new Button { Content = "YAML" };
            var save = new Button { Content = "Save JSON" };
            var insert = new Button { Content = "Insert" };
            var lines = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            showJson.Click += (_, _) => Show(editor, json);
            showYaml.Click += (_, _) => Show(editor, yaml);
            save.Click += async (_, _) => await SaveAsync(json, path);
            insert.Click += (_, _) => InsertAtSelection(editor, "text");
            json.TextChanged += async (_, _) => lines.Text = $"{await CountLinesAsync(json)} lines";

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { showJson, showYaml, save, insert, lines },
            };
            DockPanel.SetDock(buttons, Dock.Top);
            var window = new Window
            {
                Title = "Documents",
                Width = 720,
                Height = 420,
                Content = new DockPanel { Children = { buttons, editor } },
            };
            window.Closed += (_, _) =>
            {
                json.Dispose();
                yaml.Dispose();
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    // --8<-- [start:tabs]
    private readonly Dictionary<CodeDocument, CodeEditorViewState> _places = [];

    private void Show(CodeEditor editor, CodeDocument document)
    {
        if (editor.Document is { } current)
            _places[current] = editor.SaveViewState();

        editor.Document = document;

        if (_places.TryGetValue(document, out CodeEditorViewState? place))
            editor.RestoreViewState(place);
    }

    // --8<-- [end:tabs]

    // --8<-- [start:save]
    private static async Task SaveAsync(CodeDocument document, string path)
    {
        TextSnapshot snapshot = document.CurrentSnapshot;
        await File.WriteAllTextAsync(path, snapshot.GetText());
        document.MarkUnmodified(snapshot);
    }

    // --8<-- [end:save]

    // --8<-- [start:replace]
    private static void InsertAtSelection(CodeEditor editor, string text)
    {
        TextRange selection = editor.Selection.Range;
        editor.Document?.Replace(selection, text);
    }

    // --8<-- [end:replace]

    // --8<-- [start:background]
    private static async Task<int> CountLinesAsync(CodeDocument document)
    {
        TextSnapshot snapshot = document.CurrentSnapshot;
        return await Task.Run(() => snapshot.GetPosition(snapshot.Length).Line);
    }

    // --8<-- [end:background]
}
