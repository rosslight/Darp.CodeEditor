using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darp.CodeEditor.Web.Assets;

/// <summary>
/// Serves the editor assets to WebKitGTK from a custom <c>app://</c> URI scheme, registered as secure and CORS-enabled,
/// so the pages get a real origin: module scripts, web workers and fetch work as on https.
/// </summary>
/// <remarks>
/// WebKitGTK objects belong to the GLib main loop that Avalonia's WebView runs on its own thread. Registration and
/// every request run there; a request reads one file synchronously.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static unsafe partial class GtkAppScheme
{
    public const string Scheme = "app";
    private const string LibGLib = "libglib-2.0.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";
    private const string LibGio = "libgio-2.0.so.0";
    private const string LibWebKit = "libwebkit2gtk-4.1.so.0";
    private const string LibSoup = "libsoup-3.0.so.0";

    // Contexts that already have the scheme; touched only on the GLib thread.
    private static readonly HashSet<IntPtr> s_registeredContexts = [];
    private static string s_root = "";

    /// <summary>Registers the scheme on the web view's context, once per context.</summary>
    public static Task RegisterAsync(IntPtr webKitWebView, string root)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GlibThread.Post(() =>
        {
            try
            {
                s_root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
                IntPtr context = webkit_web_view_get_context(webKitWebView);
                if (s_registeredContexts.Add(context))
                {
                    webkit_web_context_register_uri_scheme(context, Scheme, &OnRequest, IntPtr.Zero, IntPtr.Zero);
                    IntPtr security = webkit_web_context_get_security_manager(context);
                    webkit_security_manager_register_uri_scheme_as_secure(security, Scheme);
                    webkit_security_manager_register_uri_scheme_as_cors_enabled(security, Scheme);
                }
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRequest(IntPtr request, IntPtr userData)
    {
        string path = Marshal.PtrToStringUTF8(webkit_uri_scheme_request_get_path(request)) ?? "/";
        string file = Path.GetFullPath(Path.Combine(s_root, Uri.UnescapeDataString(path).TrimStart('/')));
        byte[]? content = null;
        if (file.StartsWith(s_root, StringComparison.Ordinal) && File.Exists(file))
        {
            try
            {
                content = File.ReadAllBytes(file);
            }
            catch (IOException) { }
        }
        if (content is null)
        {
            IntPtr error = g_error_new_literal(
                g_quark_from_static_string("darp-app-scheme"),
                404,
                $"Not found: {path}"
            );
            webkit_uri_scheme_request_finish_error(request, error);
            g_error_free(error);
            return;
        }

        IntPtr bytes;
        fixed (byte* data = content)
            bytes = g_bytes_new(data, (nuint)content.Length);
        IntPtr stream = g_memory_input_stream_new_from_bytes(bytes);
        IntPtr response = webkit_uri_scheme_response_new(stream, content.Length);
        webkit_uri_scheme_response_set_content_type(response, AssetContentTypes.Get(file));
        // Cross-origin isolation: the pages load nothing from elsewhere, and it gives them precise timers.
        IntPtr headers = soup_message_headers_new(1); // SOUP_MESSAGE_HEADERS_RESPONSE
        soup_message_headers_append(headers, "Cross-Origin-Opener-Policy", "same-origin");
        soup_message_headers_append(headers, "Cross-Origin-Embedder-Policy", "require-corp");
        webkit_uri_scheme_response_set_http_headers(response, headers);
        webkit_uri_scheme_request_finish_with_response(request, response);
        g_object_unref(response);
        g_object_unref(stream);
        g_bytes_unref(bytes);
    }

    [LibraryImport(LibGLib)]
    private static partial IntPtr g_bytes_new(byte* data, nuint size);

    [LibraryImport(LibGLib)]
    private static partial void g_bytes_unref(IntPtr bytes);

    [LibraryImport(LibGLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint g_quark_from_static_string(string value);

    [LibraryImport(LibGLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr g_error_new_literal(uint domain, int code, string message);

    [LibraryImport(LibGLib)]
    private static partial void g_error_free(IntPtr error);

    [LibraryImport(LibGObject)]
    private static partial void g_object_unref(IntPtr instance);

    [LibraryImport(LibGio)]
    private static partial IntPtr g_memory_input_stream_new_from_bytes(IntPtr bytes);

    [LibraryImport(LibWebKit)]
    private static partial IntPtr webkit_web_view_get_context(IntPtr webView);

    [LibraryImport(LibWebKit, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void webkit_web_context_register_uri_scheme(
        IntPtr context,
        string scheme,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> callback,
        IntPtr userData,
        IntPtr userDataDestroy
    );

    [LibraryImport(LibWebKit)]
    private static partial IntPtr webkit_web_context_get_security_manager(IntPtr context);

    [LibraryImport(LibWebKit, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void webkit_security_manager_register_uri_scheme_as_secure(IntPtr manager, string scheme);

    [LibraryImport(LibWebKit, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void webkit_security_manager_register_uri_scheme_as_cors_enabled(
        IntPtr manager,
        string scheme
    );

    [LibraryImport(LibWebKit)]
    private static partial IntPtr webkit_uri_scheme_request_get_path(IntPtr request);

    [LibraryImport(LibWebKit)]
    private static partial IntPtr webkit_uri_scheme_response_new(IntPtr stream, long length);

    [LibraryImport(LibWebKit, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void webkit_uri_scheme_response_set_content_type(IntPtr response, string contentType);

    /// <remarks>Takes ownership of <paramref name="headers"/>.</remarks>
    [LibraryImport(LibWebKit)]
    private static partial void webkit_uri_scheme_response_set_http_headers(IntPtr response, IntPtr headers);

    [LibraryImport(LibWebKit)]
    private static partial void webkit_uri_scheme_request_finish_with_response(IntPtr request, IntPtr response);

    [LibraryImport(LibSoup)]
    private static partial IntPtr soup_message_headers_new(int type);

    [LibraryImport(LibSoup, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void soup_message_headers_append(IntPtr headers, string name, string value);

    [LibraryImport(LibWebKit)]
    private static partial void webkit_uri_scheme_request_finish_error(IntPtr request, IntPtr error);
}
