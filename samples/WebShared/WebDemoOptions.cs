using Darp.CodeEditor.Samples;
using Darp.CodeEditor.Web;

namespace Darp.CodeEditor.WebDemo;

/// <summary>The shared demo flags plus the WebView demo's own.</summary>
/// <param name="Editor">--editor monaco|codemirror (default monaco).</param>
/// <param name="CloseAfterMs">--close-after ms: closes the window while the editor may still be loading.</param>
/// <param name="Light">--light: starts with the light theme.</param>
/// <param name="DevTools">--devtools: allows the web inspector.</param>
/// <param name="WebViewInfo">--webview-info: prints the web engine's GPU report and exits.</param>
/// <param name="PageQuery">--page-query a=b: extra query parameters for the editor pages, for experiments.</param>
internal sealed record WebDemoOptions(
    DemoOptions Demo,
    WebEditorKind Editor,
    int? CloseAfterMs,
    bool Light,
    bool DevTools,
    bool WebViewInfo,
    string? PageQuery
)
{
    public bool Measure => Demo.Measure || Demo.E2eOutput is not null;

    public static WebDemoOptions Parse(string[] args)
    {
        string? Value(string flag)
        {
            int index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        return new WebDemoOptions(
            DemoOptions.Parse(args),
            Value("--editor") is "codemirror" or "cm" ? WebEditorKind.CodeMirror : WebEditorKind.Monaco,
            Value("--close-after") is { } ms ? int.Parse(ms) : null,
            args.Contains("--light"),
            args.Contains("--devtools"),
            args.Contains("--webview-info"),
            Value("--page-query")
        );
    }
}
