using Avalonia.Controls;
using Avalonia.Threading;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Luau;
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
        LuauLanguage.Configure(LuauEditor);
        PacketScriptRun.Attach(this, RunButton, RunOutput, PacketEditor, () => Tabs.SelectedItem = PacketTab, options);
        if (options.NoLanguageService)
            return;

        // The editors are fully usable now. Opening the C# document starts the C# server in the background, opening
        // the Luau document luau-lsp with the app's Luau API.
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

    /// <summary>The C# language service, shared by the C# editors; null with <c>--no-language-service</c>.</summary>
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
