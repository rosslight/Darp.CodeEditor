using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darp.CodeEditor.Web.Assets;

/// <summary>
/// Runs code on the GLib main loop that Avalonia's WebView runs WebKitGTK on (its own thread). GTK and WebKit objects
/// may only be used there.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class GlibThread
{
    public static void Post(Action action)
    {
        IntPtr state = GCHandle.ToIntPtr(GCHandle.Alloc(action));
        g_timeout_add(0, &RunOnce, state);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RunOnce(IntPtr state)
    {
        GCHandle handle = GCHandle.FromIntPtr(state);
        var action = (Action)handle.Target!;
        handle.Free();
        action();
        return 0; // G_SOURCE_REMOVE
    }

    [LibraryImport("libglib-2.0.so.0")]
    private static partial uint g_timeout_add(
        uint interval,
        delegate* unmanaged[Cdecl]<IntPtr, int> function,
        IntPtr data
    );
}
