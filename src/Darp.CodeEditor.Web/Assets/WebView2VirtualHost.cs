using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darp.CodeEditor.Web.Assets;

/// <summary>
/// Serves the editor assets to WebView2 from a virtual https host mapped to a folder
/// (<c>ICoreWebView2_3.SetVirtualHostNameToFolderMapping</c>), WebView2's own mechanism for local content.
/// </summary>
/// <remarks>
/// Avalonia's WebView exposes the <c>ICoreWebView2</c> pointer but no wrapper for this call, so it goes through the
/// COM vtable directly: no runtime marshalling, which keeps it NativeAOT-safe. Must run on the UI thread.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe class WebView2VirtualHost
{
    public const string HostName = "darp-editor.example";

    private static readonly Guid s_iidCoreWebView2_3 = new("A0D6DF20-3B92-416D-AA0C-437A9C727857");

    // IUnknown (3) + ICoreWebView2 (58) + ICoreWebView2_2 (7) + TrySuspend, Resume, get_IsSuspended.
    private const int SetVirtualHostNameToFolderMappingSlot = 71;
    private const int AllowAccess = 1; // COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_ALLOW

    public static void Map(IntPtr coreWebView2, string folder)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(coreWebView2, in s_iidCoreWebView2_3, out IntPtr webView3));
        try
        {
            var setMapping = (delegate* unmanaged[Stdcall]<IntPtr, char*, char*, int, int>)(
                (*(IntPtr**)webView3)[SetVirtualHostNameToFolderMappingSlot]
            );
            fixed (char* host = HostName)
            fixed (char* path = Path.GetFullPath(folder))
                Marshal.ThrowExceptionForHR(setMapping(webView3, host, path, AllowAccess));
        }
        finally
        {
            Marshal.Release(webView3);
        }
    }
}
