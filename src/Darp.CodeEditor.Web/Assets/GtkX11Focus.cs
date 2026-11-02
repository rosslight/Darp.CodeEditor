using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darp.CodeEditor.Web.Assets;

/// <summary>
/// Moves the X input focus between the Avalonia window and the web view's GTK window.
/// </summary>
/// <remarks>
/// Avalonia's WebView reparents a GTK window into the Avalonia window and only emulates GTK focus, so the X input
/// focus stays on the Avalonia window. X then delivers keystrokes to the window under the pointer: with the pointer
/// over any other part of the Avalonia window, keys typed into the editor were lost.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static partial class GtkX11Focus
{
    private const int RevertToParent = 2;
    private const IntPtr CurrentTime = 0;

    /// <summary>Gives the X input focus to the GTK window that holds the web view.</summary>
    public static void FocusWebView(IntPtr webKitWebView) =>
        GlibThread.Post(() =>
        {
            IntPtr window = gtk_widget_get_window(gtk_widget_get_toplevel(webKitWebView));
            if (window == IntPtr.Zero)
                return;
            SetInputFocus(gdk_x11_window_get_xid(window));
        });

    /// <summary>Gives the X input focus back to the Avalonia window.</summary>
    public static void FocusHost(IntPtr hostWindow) => GlibThread.Post(() => SetInputFocus(hostWindow));

    private static void SetInputFocus(IntPtr window)
    {
        IntPtr display = gdk_x11_display_get_xdisplay(gdk_display_get_default());
        XSetInputFocus(display, window, RevertToParent, CurrentTime);
        XFlush(display);
    }

    [LibraryImport("libgtk-3.so.0")]
    private static partial IntPtr gtk_widget_get_toplevel(IntPtr widget);

    [LibraryImport("libgtk-3.so.0")]
    private static partial IntPtr gtk_widget_get_window(IntPtr widget);

    [LibraryImport("libgdk-3.so.0")]
    private static partial IntPtr gdk_display_get_default();

    [LibraryImport("libgdk-3.so.0")]
    private static partial IntPtr gdk_x11_display_get_xdisplay(IntPtr display);

    [LibraryImport("libgdk-3.so.0")]
    private static partial IntPtr gdk_x11_window_get_xid(IntPtr window);

    [LibraryImport("libX11.so.6")]
    private static partial int XSetInputFocus(IntPtr display, IntPtr window, int revertTo, IntPtr time);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(IntPtr display);
}
