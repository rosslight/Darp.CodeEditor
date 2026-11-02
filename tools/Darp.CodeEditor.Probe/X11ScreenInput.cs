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

    public X11ScreenInput()
    {
        _display = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException("Cannot open the X display; set DISPLAY (and XAUTHORITY).");
        _root = XDefaultRootWindow(_display);
        _m = XKeysymToKeycode(_display, 'm');
        _backspace = XKeysymToKeycode(_display, XkBackSpace);
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
