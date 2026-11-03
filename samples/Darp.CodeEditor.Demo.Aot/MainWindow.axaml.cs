using Avalonia.Controls;
using Avalonia.Threading;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.Demo.Aot;

public sealed partial class MainWindow : Window, IBenchHost
{
    /// <summary>The tab of the C# editor.</summary>
    public const int CSharpTab = 2;

    public MainWindow()
        : this(DemoOptions.Parse([])) { }

    internal MainWindow(DemoOptions options)
    {
        EditorsCreatedMs = ProcessClock.ElapsedMs;
        InitializeComponent();
        LuauEditor.Text = SampleDocuments.LuauScript;
        JsonEditor.Text = SampleDocuments.Json.Replace("\"file\": null", "\"file\": null,");
        CSharpEditor.Text = SampleDocuments.CSharpScript;
        // A small in-process language service: word completion and JSON parse errors.
        JsonEditor.LanguageService = new JsonLanguageService();

        string grammar = Path.Combine(AppContext.BaseDirectory, "Grammars", "Luau.tmLanguage.json");
        if (File.Exists(grammar))
            LuauEditor.SetGrammarFile(grammar);
        else
            LuauEditor.LanguageId = "lua";
        if (options.NoLanguageService)
            return;

        // C# through the same language server as in the JIT demo; opening the document starts it in the background.
        LspLanguageService csharp = DemoCSharp.CreateService(options);
        CSharp = csharp;
        csharp.StateChanged += (_, _) =>
        {
            double at = ProcessClock.ElapsedMs;
            Dispatcher.UIThread.Post(() => Status.Text = DemoCSharp.Describe(csharp, at));
        };
        CSharpEditor.LanguageService = csharp;
        Closed += (_, _) => _ = csharp.DisposeAsync().AsTask();
    }

    /// <summary>When the window started to create its editors, in milliseconds since the process started.</summary>
    public double EditorsCreatedMs { get; }

    /// <summary>The C# language service; null with <c>--no-language-service</c>.</summary>
    public LspLanguageService? CSharp { get; }

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
