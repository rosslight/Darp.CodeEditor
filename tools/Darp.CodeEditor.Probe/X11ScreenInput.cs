using System.Runtime.InteropServices;

namespace Darp.CodeEditor.Probe;

/// <summary>XTest input and XGetImage reads of the root window, which on a server without a compositor is the screen.</summary>
internal sealed unsafe partial class X11ScreenInput : IKeyInput
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

    /// <summary>Shift, Control, Alt (Mod1), Super (Mod4), AltGr (Mod5) and mouse buttons 1-5; not Caps or Num Lock.</summary>
    private static readonly (uint Bit, string Name)[] s_modifiers =
    [
        (1 << 0, "Shift"),
        (1 << 2, "Ctrl"),
        (1 << 3, "Alt"),
        (1 << 6, "the Super key"),
        (1 << 7, "AltGr"),
        (1 << 8, "the left mouse button"),
        (1 << 9, "the middle mouse button"),
        (1 << 10, "the right mouse button"),
        (1 << 11, "mouse button 4"),
        (1 << 12, "mouse button 5"),
    ];

    private static int s_failedRequests;

    public X11ScreenInput()
    {
        _display = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException("Cannot open the X display; set DISPLAY (and XAUTHORITY).");
        _root = XDefaultRootWindow(_display);
        _m = XKeysymToKeycode(_display, 'm');
        _backspace = XKeysymToKeycode(_display, XkBackSpace);
        XSetErrorHandler(&OnError);
        _activeWindowAtom = XInternAtom(_display, "_NET_ACTIVE_WINDOW", 0);
        _pidAtom = XInternAtom(_display, "_NET_WM_PID", 0);
    }

    public void Key(bool backspace)
    {
        byte key = backspace ? _backspace : _m;
        bool pressed = XTestFakeKeyEvent(_display, key, 1, 0) != 0;
        bool released = XTestFakeKeyEvent(_display, key, 0, 0) != 0;
        // A key down without its key up would stay pressed: release the probe's own key.
        if (pressed && !released)
            XTestFakeKeyEvent(_display, key, 0, 0);
        XFlush(_display);
        if (!pressed || !released)
            throw new UnsafeInputException("XTest did not take the key events");
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

    /// <summary>
    /// The target window must be the window the manager marks as active (EWMH) and hold the X input focus itself or
    /// through a child window.
    /// </summary>
    public string? CheckFocus(TargetWindow target)
    {
        var window = (IntPtr)target.Window;
        s_failedRequests = 0;
        bool exists = XGetWindowAttributes(_display, window, out _) && s_failedRequests == 0;
        if (!exists)
            return "the demo's window no longer exists";
        if (Cardinal(window, _pidAtom) is not { } processId || (int)processId != target.ProcessId)
            return "the demo's window belongs to another process now";
        if (Cardinal(_root, _activeWindowAtom) is { } active && active != window)
            return $"the active window is 0x{active:X}, not the demo's window 0x{window:X}";
        XGetInputFocus(_display, out IntPtr focus, out _);
        for (IntPtr current = focus; current != IntPtr.Zero && current != 1 && current != _root; )
        {
            if (current == window)
                return null;
            if (XQueryTree(_display, current, out _, out IntPtr parent, out IntPtr children, out _) == 0)
                break;
            if (children != IntPtr.Zero)
                XFree(children);
            current = parent;
        }
        // 0 is None and 1 is PointerRoot: keys would go to whatever is under the pointer.
        return $"the input focus is on window 0x{focus:X}, not in the demo's window 0x{window:X}";
    }

    public string? HeldModifier()
    {
        XQueryPointer(_display, _root, out _, out _, out _, out _, out _, out _, out uint mask);
        foreach ((uint bit, string name) in s_modifiers)
        {
            if ((mask & bit) != 0)
                return name;
        }
        return null;
    }

    /// <summary>
    /// Counts failed requests instead of Xlib's default, which ends the process; a window that went away while the
    /// probe looks at it is an expected failure.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int OnError(IntPtr display, IntPtr error)
    {
        s_failedRequests++;
        return 0;
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

    [LibraryImport(LibX11)]
    private static partial IntPtr XSetErrorHandler(delegate* unmanaged<IntPtr, IntPtr, int> handler);

    [LibraryImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool XQueryPointer(
        IntPtr display,
        IntPtr window,
        out IntPtr root,
        out IntPtr child,
        out int rootX,
        out int rootY,
        out int windowX,
        out int windowY,
        out uint mask
    );

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


}
