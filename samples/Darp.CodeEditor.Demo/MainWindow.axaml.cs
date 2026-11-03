using Avalonia.Controls;
using Avalonia.Threading;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.Demo;

public sealed partial class MainWindow : Window, IBenchHost
{
    public MainWindow()
        : this(DemoOptions.Parse([])) { }

    internal MainWindow(DemoOptions options)
    {
        EditorsCreatedMs = ProcessClock.ElapsedMs;
        InitializeComponent();
        ThemeButton.Click += (_, _) => Ui.ToggleThemeVariant();
        CSharpEditor.Text = SampleDocuments.CSharpScript;
        LuauEditor.Text = SampleDocuments.LuauScript;
        JsonEditor.Text = SampleDocuments.Json;
        SetLuauGrammar();
        if (options.NoLanguageService)
            return;

        // The editors are fully usable now. Opening the C# document starts the C# server in the background.
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

    /// <summary>The C# language service, shared by the C# editors; null with <c>--no-language-service</c>.</summary>
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

    private void SetLuauGrammar()
    {
        // TextMateSharp.Grammars has no Luau grammar; use the MIT-licensed one if it is next to the app, else Lua.
        string grammar = Path.Combine(AppContext.BaseDirectory, "Grammars", "Luau.tmLanguage.json");
        LuauEditor.LanguageConfiguration = LuauLanguage.Configuration;
        if (File.Exists(grammar))
            LuauEditor.SetGrammarFile(grammar);
        else
            LuauEditor.LanguageId = "lua";
    }
}
