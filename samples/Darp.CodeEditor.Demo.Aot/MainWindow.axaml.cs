using Avalonia.Controls;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.Demo.Aot;

public sealed partial class MainWindow : Window, IBenchHost
{
    public MainWindow()
    {
        InitializeComponent();
        LuauEditor.Text = SampleDocuments.LuauScript;
        JsonEditor.Text = SampleDocuments.Json.Replace("\"file\": null", "\"file\": null,");
        CSharpEditor.Text = SampleDocuments.CSharpScript;
        // A small language service without Roslyn: word completion and JSON parse errors, all AOT-safe.
        JsonEditor.LanguageService = new JsonLanguageService();
        PacketScriptRun.Attach(
            this,
            RunButton,
            RunOutput,
            PacketEditor,
            () => Tabs.SelectedItem = PacketTab,
            Program.Options
        );

        string grammar = Path.Combine(AppContext.BaseDirectory, "Grammars", "Luau.tmLanguage.json");
        if (File.Exists(grammar))
            LuauEditor.SetGrammarFile(grammar);
        else
            LuauEditor.LanguageId = "lua";
    }

    TopLevel IBenchHost.TopLevel => this;

    public Task ShowAsync(CodeEditor editor)
    {
        BenchTab.Content = editor;
        BenchTab.IsVisible = true;
        Tabs.SelectedItem = BenchTab;
        return Ui.NextFrameAsync(this);
    }

    public Task ShowTabAsync(int index)
    {
        Tabs.SelectedIndex = index;
        return Ui.NextFrameAsync(this);
    }
}
