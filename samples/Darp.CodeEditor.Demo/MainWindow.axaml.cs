using System.Diagnostics;
using Avalonia.Controls;
using Darp.CodeEditor.CSharp;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.Demo;

public sealed partial class MainWindow : Window, IBenchHost
{
    public MainWindow()
        : this(noLanguageService: false) { }

    public MainWindow(bool noLanguageService)
    {
        InitializeComponent();
        CSharpEditor.Text = SampleDocuments.CSharpScript;
        LuauEditor.Text = SampleDocuments.LuauScript;
        JsonEditor.Text = SampleDocuments.Json;
        SetLuauGrammar();

        // The editors are fully usable now. C# features switch on once the service finished loading.
        CSharpReady = noLanguageService ? Task.FromResult<CSharpScriptLanguageService?>(null) : StartCSharpAsync();
    }

    /// <summary>Completes once the C# service is attached to the C# editor (null when disabled).</summary>
    public Task<CSharpScriptLanguageService?> CSharpReady { get; }

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

    private async Task<CSharpScriptLanguageService?> StartCSharpAsync()
    {
        Status.Text = "Loading the C# language service…";
        long start = Stopwatch.GetTimestamp();
        CSharpScriptLanguageService service = await CSharpScriptLanguageService.CreateAsync(
            new CSharpScriptOptions(
                typeof(ScriptGlobals),
                [typeof(Enumerable).Assembly, typeof(List<>).Assembly, typeof(System.Text.StringBuilder).Assembly],
                ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks"]
            )
        );
        CSharpEditor.LanguageService = service;
        Closed += (_, _) => service.Dispose();
        Status.Text = $"C# language service ready after {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms";
        return service;
    }

    private void SetLuauGrammar()
    {
        // TextMateSharp.Grammars has no Luau grammar; use the MIT-licensed one if it is next to the app, else Lua.
        string grammar = Path.Combine(AppContext.BaseDirectory, "Grammars", "Luau.tmLanguage.json");
        if (File.Exists(grammar))
            LuauEditor.SetGrammarFile(grammar);
        else
            LuauEditor.LanguageId = "lua";
    }
}
