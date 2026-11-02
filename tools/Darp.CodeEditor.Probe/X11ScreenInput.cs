using System.Runtime.InteropServices;

namespace Darp.CodeEditor.Probe;

/// <summary>XTest input and XGetImage reads of the root window, which on a server without a compositor is the screen.</summary>
internal sealed unsafe partial class X11ScreenInput : IScreenInput
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";
    private const int ZPixmap = 2;
    private static readonly nuint s_allPlanes = nuint.MaxValue;
    private const ulong XkBackSpace = 0xff08;

    private readonly IntPtr _display;
    private readonly IntPtr _root;
    private readonly byte _m;
    private readonly byte _backspace;
    private readonly IntPtr _activeWindowAtom;
    private readonly IntPtr _pidAtom;

    public X11ScreenInput()
    {
        _display = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException("Cannot open the X display; set DISPLAY (and XAUTHORITY).");
        _root = XDefaultRootWindow(_display);
        _m = XKeysymToKeycode(_display, 'm');
        _backspace = XKeysymToKeycode(_display, XkBackSpace);
        _activeWindowAtom = XInternAtom(_display, "_NET_ACTIVE_WINDOW", 0);
        _pidAtom = XInternAtom(_display, "_NET_WM_PID", 0);
    }

    public void Click(int x, int y)
    {
        XTestFakeMotionEvent(_display, -1, x, y, 0);
        XTestFakeButtonEvent(_display, 1, 1, 0);
        XTestFakeButtonEvent(_display, 1, 0, 0);
        XFlush(_display);
    }

    public void Key(bool backspace)
    {
        byte key = backspace ? _backspace : _m;
        XTestFakeKeyEvent(_display, key, 1, 0);
        XTestFakeKeyEvent(_display, key, 0, 0);
        XFlush(_display);
    }

    public byte[] Capture(int x, int y, int width, int height)
    {
        XImage* image = XGetImage(_display, _root, x, y, (uint)width, (uint)height, s_allPlanes, ZPixmap);
        if (image == null)
            throw new InvalidOperationException("XGetImage failed.");
        try
        {
            return new ReadOnlySpan<byte>(image->Data, image->BytesPerLine * image->Height).ToArray();
        }
        finally
        {
            // XDestroyImage is a macro that calls the image's own destroy function.
            image->DestroyImage(image);
        }
    }

    public (int X, int Y)? FindWindow(string titlePrefix)
    {
        IntPtr window = Find(_root, titlePrefix);
        if (
            window == IntPtr.Zero
            || !XGetWindowAttributes(_display, window, out WindowAttributes attributes)
            || attributes.MapState != 2
        )
            return null;
        XTranslateCoordinates(_display, window, _root, 0, 0, out int x, out int y, out _);
        return (x, y);
    }

    public int? ProcessAt(int x, int y)
    {
        // With a reparenting window manager the root's child is the frame; the application's window is inside it.
        IntPtr window = _root;
        while (XTranslateCoordinates(_display, _root, window, x, y, out _, out _, out IntPtr child) && child != IntPtr.Zero)
        {
            if (Cardinal(child, _pidAtom) is { } processId)
                return (int)processId;
            window = child;
        }
        return null;
    }

    /// <summary>
    /// The process whose window has the X input focus, where keys go, if the window manager also marks it as active
    /// (EWMH). Null when either is unknown or they differ.
    /// </summary>
    public int? ForegroundProcess()
    {
        int? active =
            Cardinal(_root, _activeWindowAtom) is { } window && window != 0 && Cardinal(window, _pidAtom) is { } pid
                ? (int)pid
                : null;
        XGetInputFocus(_display, out IntPtr focus, out _);
        int? focused = ProcessOfWindowOrAncestor(focus);
        return active == focused ? focused : null;
    }

    /// <summary>The process of a window or of the nearest ancestor that names one; focus may be on a child window.</summary>
    private int? ProcessOfWindowOrAncestor(IntPtr window)
    {
        // 0 is None and 1 is PointerRoot: keys would go to whatever is under the pointer.
        while (window != IntPtr.Zero && window != 1 && window != _root)
        {
            if (Cardinal(window, _pidAtom) is { } processId)
                return (int)processId;
            if (XQueryTree(_display, window, out _, out IntPtr parent, out IntPtr children, out _) == 0)
                return null;
            if (children != IntPtr.Zero)
                XFree(children);
            window = parent;
        }
        return null;
    }

    private IntPtr? Cardinal(IntPtr window, IntPtr property)
    {
        if (
            XGetWindowProperty(
                _display,
                window,
                property,
                0,
                1,
                0,
                IntPtr.Zero,
                out _,
                out int format,
                out nuint count,
                out _,
                out IntPtr data
            ) != 0
            || data == IntPtr.Zero
        )
            return null;
        try
        {
            // Format 32 properties are arrays of C longs.
            return format == 32 && count > 0 ? *(IntPtr*)data : null;
        }
        finally
        {
            XFree(data);
        }
    }

    private IntPtr Find(IntPtr window, string titlePrefix)
    {
        if (XFetchName(_display, window, out IntPtr name) != 0 && name != IntPtr.Zero)
        {
            string? title = Marshal.PtrToStringUTF8(name);
            XFree(name);
            if (title is not null && title.StartsWith(titlePrefix, StringComparison.Ordinal))
                return window;
        }
        if (XQueryTree(_display, window, out _, out _, out IntPtr children, out uint count) == 0)
            return IntPtr.Zero;
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr found = Find(((IntPtr*)children)[i], titlePrefix);
                if (found != IntPtr.Zero)
                    return found;
            }
            return IntPtr.Zero;
        }
        finally
        {
            if (children != IntPtr.Zero)
                XFree(children);
        }
    }

    public void Dispose() => XCloseDisplay(_display);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowAttributes
    {
        public int X,
            Y,
            Width,
            Height,
            BorderWidth,
            Depth;
        public IntPtr Visual,
            Root;
        public int Class,
            BitGravity,
            WinGravity,
            BackingStore;
        public nuint BackingPlanes,
            BackingPixel;
        public int SaveUnder;
        public IntPtr Colormap;
        public int MapInstalled,
            MapState;
        public nint AllEventMasks,
            YourEventMask,
            DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Screen;
    }

    [LibraryImport(LibX11)]
    private static partial int XFetchName(IntPtr display, IntPtr window, out IntPtr name);

    [LibraryImport(LibX11)]
    private static partial int XFree(IntPtr data);

    [LibraryImport(LibX11)]
    private static partial int XGetInputFocus(IntPtr display, out IntPtr focus, out int revertTo);

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XInternAtom(IntPtr display, string name, int onlyIfExists);

    [LibraryImport(LibX11)]
    private static partial int XGetWindowProperty(
        IntPtr display,
        IntPtr window,
        IntPtr property,
        nint offset,
        nint length,
        int delete,
        IntPtr requestedType,
        out IntPtr actualType,
        out int actualFormat,
        out nuint count,
        out nuint bytesAfter,
        out IntPtr data
    );

    [LibraryImport(LibX11)]
    private static partial int XQueryTree(
        IntPtr display,
        IntPtr window,
        out IntPtr root,
        out IntPtr parent,
        out IntPtr children,
        out uint count
    );

    [LibraryImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool XGetWindowAttributes(IntPtr display, IntPtr window, out WindowAttributes attributes);

    [LibraryImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool XTranslateCoordinates(
        IntPtr display,
        IntPtr source,
        IntPtr destination,
        int x,
        int y,
        out int destinationX,
        out int destinationY,
        out IntPtr child
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int Width;
        public int Height;
        public int XOffset;
        public int Format;
        public byte* Data;
        public int ByteOrder;
        public int BitmapUnit;
        public int BitmapBitOrder;
        public int BitmapPad;
        public int Depth;
        public int BytesPerLine;
        public int BitsPerPixel;
        public nuint RedMask;
        public nuint GreenMask;
        public nuint BlueMask;
        public IntPtr ObData;
        public IntPtr CreateImage;
        public delegate* unmanaged[Cdecl]<XImage*, int> DestroyImage;
    }

    [LibraryImport(LibX11)]
    private static partial IntPtr XOpenDisplay(IntPtr name);

    [LibraryImport(LibX11)]
    private static partial int XCloseDisplay(IntPtr display);

    [LibraryImport(LibX11)]
    private static partial IntPtr XDefaultRootWindow(IntPtr display);

    [LibraryImport(LibX11)]
    private static partial byte XKeysymToKeycode(IntPtr display, ulong keysym);

    [LibraryImport(LibX11)]
    private static partial int XFlush(IntPtr display);

    [LibraryImport(LibX11)]
    private static partial XImage* XGetImage(
        IntPtr display,
        IntPtr drawable,
        int x,
        int y,
        uint width,
        uint height,
        nuint planeMask,
        int format
    );

    [LibraryImport(LibXtst)]
    private static partial int XTestFakeKeyEvent(IntPtr display, uint keycode, int isPress, ulong delay);

    [LibraryImport(LibXtst)]
    private static partial int XTestFakeButtonEvent(IntPtr display, uint button, int isPress, ulong delay);

    [LibraryImport(LibXtst)]
    private static partial int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);
}
