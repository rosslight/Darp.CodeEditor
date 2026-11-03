using Avalonia.Controls;
using Avalonia.Threading;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Luau;
using Darp.CodeEditor.Samples;

namespace Darp.CodeEditor.Demo.Aot;

public sealed partial class MainWindow : Window, IBenchHost
{
    /// <summary>The tab of the Luau editor.</summary>
    public const int LuauTab = 0;

    /// <summary>The tab of the C# editor.</summary>
    public const int CSharpTab = 2;

    public MainWindow()
        : this(DemoOptions.Parse([])) { }

    internal MainWindow(DemoOptions options)
    {
        EditorsCreatedMs = ProcessClock.ElapsedMs;
        InitializeComponent();
        ThemeButton.Click += (_, _) => Ui.ToggleThemeVariant();
        LuauEditor.Text = SampleDocuments.LuauScript;
        JsonEditor.Text = SampleDocuments.Json.Replace("\"file\": null", "\"file\": null,");
        CSharpEditor.Text = SampleDocuments.CSharpScript;
        // A small in-process language service: word completion and JSON parse errors.
        JsonEditor.LanguageService = new JsonLanguageService();
        PacketScriptRun.Attach(this, RunButton, RunOutput, PacketEditor, () => Tabs.SelectedItem = PacketTab, options);

        LuauLanguage.Configure(LuauEditor);
        if (options.NoLanguageService)
            return;

        // C# through the same language server as in the JIT demo, Luau through luau-lsp with the app's Luau API.
        // Opening a document starts its server in the background.
        LspLanguageService csharp = DemoCSharp.CreateService(options);
        LspLanguageService luau = DemoLuau.CreateService(options);
        CSharp = csharp;
        Luau = luau;
        csharp.StateChanged += (_, _) =>
        {
            double at = ProcessClock.ElapsedMs;
            Dispatcher.UIThread.Post(() =>
            {
                _csharpStatus = DemoCSharp.Describe(csharp, at);
                ShowStatus();
            });
        };
        luau.StateChanged += (_, _) => Dispatcher.UIThread.Post(ShowStatus);
        CSharpEditor.LanguageService = csharp;
        LuauEditor.LanguageService = luau;
        Closed += (_, _) =>
        {
            _ = csharp.DisposeAsync().AsTask();
            _ = luau.DisposeAsync().AsTask();
        };
    }

    /// <summary>When the window started to create its editors, in milliseconds since the process started.</summary>
    public double EditorsCreatedMs { get; }

    /// <summary>The C# language service; null with <c>--no-language-service</c>.</summary>
    public LspLanguageService? CSharp { get; }

    /// <summary>The Luau language service; null with <c>--no-language-service</c>.</summary>
    public LspLanguageService? Luau { get; }

    private string _csharpStatus = "";

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

    private void ShowStatus() =>
        Status.Text = Luau is { } luau ? $"{_csharpStatus} · {DemoLuau.Describe(luau)}" : _csharpStatus;
}
