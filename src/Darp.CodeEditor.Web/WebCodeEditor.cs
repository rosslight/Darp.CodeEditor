using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Darp.CodeEditor.Web.Assets;

namespace Darp.CodeEditor.Web;

/// <summary>Which browser editor a <see cref="WebCodeEditor"/> runs.</summary>
public enum WebEditorKind
{
    Monaco,
    CodeMirror,
}

public enum WebEditorTheme
{
    Dark,
    Light,
}

/// <summary>
/// A code editor running in the platform web view (WebView2, WebKitGTK): Monaco or CodeMirror 6, with all assets
/// served from the application. Language features come from a language server: the page runs the LSP client, this
/// control only relays its messages (<see cref="LspMessageReceived"/>, <see cref="PostLspMessage"/>).
/// </summary>
/// <remarks>
/// Set the init-only properties before the control is shown. The page starts with an empty editor and asks for its
/// document once its script ran, so the text arrives with one host round trip.
/// </remarks>
public sealed class WebCodeEditor : Decorator
{
    private readonly NativeWebView _webView = new();
    private WebEditorTheme _theme = WebEditorTheme.Dark;
    private bool _pageReady;

    public WebCodeEditor()
    {
        if (OperatingSystem.IsLinux())
        {
            // The assets are served through WebKitGTK's URI scheme API; WPE would need its own registration.
            _webView.AdapterPreference = [WebViewAdapterType.WebKitGtk];
        }
        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.AdapterCreated += OnAdapterCreated;
        _webView.WebMessageReceived += OnWebMessageReceived;
        Child = _webView;
    }

    public WebEditorKind Kind { get; init; }

    /// <summary>The document URI the page uses in LSP messages, such as "file:///script.csx".</summary>
    public string DocumentUri { get; init; } = "file:///untitled.txt";

    /// <summary>"csharp", "luau" or "plaintext".</summary>
    public string LanguageId { get; init; } = "plaintext";

    /// <summary>The text the editor opens with.</summary>
    public string InitialText { get; init; } = "";

    /// <summary>True to start the page's LSP client; the host must then relay its messages to a server.</summary>
    public bool UseLanguageServer { get; init; }

    /// <summary>True to let the page report startup milestones and accept measurement commands.</summary>
    public bool EnableMeasurements { get; init; }

    /// <summary>
    /// On X11 (WebKitGTK): move the X input focus into the web view while it has focus, so typing reaches the editor
    /// wherever the pointer is (see <see cref="GtkX11Focus"/>). True by default.
    /// </summary>
    public bool FixX11KeyboardFocus { get; init; } = true;

    /// <summary>For experiments: extra query parameters for the page, such as "variant=noinline".</summary>
    public string? PageQuery { get; init; }

    /// <summary>True to allow the web inspector (context menu, F12).</summary>
    public bool EnableDevTools { get; init; }

    public WebEditorTheme EditorTheme
    {
        get => _theme;
        set
        {
            if (_theme == value)
                return;
            _theme = value;
            if (_pageReady)
                PostCommand($$"""{"type":"theme","theme":"{{ThemeName(value)}}"}""");
        }
    }

    /// <summary>The web view, for focus handling and diagnostics.</summary>
    public NativeWebView WebView => _webView;

    /// <summary>
    /// Raised on the UI thread for every LSP message the page sends. Handlers must only queue the message: the UI
    /// thread must not parse or process it.
    /// </summary>
    public event Action<string>? LspMessageReceived;

    /// <summary>
    /// For measurements: raised after an LSP message reached the page, with its length and the time from
    /// <see cref="PostLspMessage"/> until the page's synchronous handling of it finished.
    /// </summary>
    public event Action<int, TimeSpan>? LspMessageDelivered;

    /// <summary>Raised on the UI thread for page events: startup milestones, measurement results, shortcuts.</summary>
    public event EventHandler<WebEditorEvent>? PageEvent;

    /// <summary>Raised on the UI thread when the page cannot be served or loaded.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>Sends an LSP message to the page. Can be called from any thread.</summary>
    public void PostLspMessage(string message)
    {
        long start = Stopwatch.GetTimestamp();
        string script = $"__host.lsp({ToScriptString(message)})";
        Dispatcher.UIThread.Post(async () =>
        {
            if (await InvokeAsync(script))
                LspMessageDelivered?.Invoke(message.Length, Stopwatch.GetElapsedTime(start));
        });
    }

    /// <summary>Sends a command object (JSON) to the page. Can be called from any thread.</summary>
    public void PostCommand(string json) => PostScript($"__host.command({json})");

    /// <summary>Moves keyboard focus into the page.</summary>
    public void FocusEditor() => _webView.Focus();

    /// <summary>A JavaScript string literal; everything outside ASCII is escaped, so any text is safe in a script.</summary>
    private static string ToScriptString(string text) =>
        $"\"{JsonEncodedText.Encode(text, JavaScriptEncoder.Default)}\"";

    private static string ThemeName(WebEditorTheme theme) => theme == WebEditorTheme.Dark ? "dark" : "light";

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = EnableDevTools;
    }

    private async void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        try
        {
            string root = Path.Combine(AppContext.BaseDirectory, "editor");
            Uri baseUri = e.TryGetPlatformHandle() switch
            {
                IGtkWebViewPlatformHandle gtk when OperatingSystem.IsLinux() => await RegisterGtkAsync(gtk, root),
                IWindowsWebView2PlatformHandle webView2 when OperatingSystem.IsWindows() => MapWebView2(webView2, root),
                var handle => throw new PlatformNotSupportedException(
                    $"No local asset mechanism for the {handle?.HandleDescriptor ?? "unknown"} web view."
                ),
            };
            string page = Kind == WebEditorKind.Monaco ? "monaco.html" : "codemirror.html";
            string query =
                $"?language={Uri.EscapeDataString(LanguageId)}&uri={Uri.EscapeDataString(DocumentUri)}"
                + $"&theme={ThemeName(_theme)}&lsp={(UseLanguageServer ? 1 : 0)}&measure={(EnableMeasurements ? 1 : 0)}"
                + (PageQuery is { Length: > 0 } extra ? "&" + extra : "");
            _webView.Navigate(new Uri(baseUri, page + query));
        }
        catch (Exception exception)
        {
            Failed?.Invoke(this, exception);
        }

        async Task<Uri> RegisterGtkAsync(IGtkWebViewPlatformHandle gtk, string root)
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException();
            await GtkAppScheme.RegisterAsync(gtk.WebKitWebView, root);
            if (FixX11KeyboardFocus)
                FollowFocusOnX11(gtk.WebKitWebView);
            return new Uri($"{GtkAppScheme.Scheme}://editor/");
        }

        static Uri MapWebView2(IWindowsWebView2PlatformHandle webView2, string root)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException();
            // The handle getter returns a new COM reference each time.
            IntPtr coreWebView2 = webView2.CoreWebView2;
            if (coreWebView2 == IntPtr.Zero)
                throw new InvalidOperationException("WebView2 is not initialized.");
            try
            {
                WebView2VirtualHost.Map(coreWebView2, root);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.Release(coreWebView2);
            }
            return new Uri($"https://{WebView2VirtualHost.HostName}/");
        }
    }

    [SupportedOSPlatform("linux")]
    private void FollowFocusOnX11(IntPtr webKitWebView)
    {
        _webView.GotFocus += (_, _) => GtkX11Focus.FocusWebView(webKitWebView);
        _webView.LostFocus += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() is { HandleDescriptor: "XID" } host)
                GtkX11Focus.FocusHost(host.Handle);
        };
    }

    // Messages are "L" + an LSP message, or "E" + a JSON event object.
    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (e.Body is not { Length: > 0 } body)
            return;
        if (body[0] == 'L')
        {
            LspMessageReceived?.Invoke(body[1..]);
            return;
        }
        if (body[0] != 'E')
            return;
        using JsonDocument json = JsonDocument.Parse(body.AsMemory(1));
        var pageEvent = new WebEditorEvent(
            json.RootElement.GetProperty("type").GetString() ?? "",
            json.RootElement.Clone()
        );
        if (pageEvent.Type == "ready")
        {
            _pageReady = true;
            // The document travels as a JSON string; the page creates its editor with it.
            PostCommand($$"""{"type":"open","text":{{ToScriptString(InitialText)}},"theme":"{{ThemeName(_theme)}}"}""");
        }
        PageEvent?.Invoke(this, pageEvent);
    }

    private void PostScript(string script) => Dispatcher.UIThread.Post(() => _ = InvokeAsync(script));

    private async Task<bool> InvokeAsync(string script)
    {
        try
        {
            await _webView.InvokeScript(script);
            return true;
        }
        catch (Exception exception)
        {
            // The page may be gone (closing window) or not loaded yet; the message is lost either way.
            Trace.TraceWarning($"Posting to the editor page failed: {exception.Message}");
            return false;
        }
    }
}

/// <param name="Type">"ready", "visible", "highlighted", "save", or a measurement event.</param>
/// <param name="Data">The whole event object.</param>
public sealed record WebEditorEvent(string Type, JsonElement Data);
