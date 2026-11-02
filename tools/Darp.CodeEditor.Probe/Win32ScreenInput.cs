using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darp.CodeEditor.Probe;

/// <summary>
/// SendInput and GDI reads of the screen. With DWM, BitBlt from the screen returns the composed desktop, so a change is
/// seen once DWM has presented the frame.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class Win32ScreenInput : IScreenInput
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint KeyUp = 0x0002;
    private const ushort VkBack = 0x08;
    private const ushort VkM = 0x4D;
    private const uint SrcCopy = 0x00CC0020;

    private readonly IntPtr _screen = GetDC(IntPtr.Zero);
    private readonly IntPtr _memory;

    public Win32ScreenInput()
    {
        _memory = CreateCompatibleDC(_screen);
    }

    public void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Input* inputs = stackalloc Input[2];
        inputs[0] = new Input
        {
            Type = InputMouse,
            Mouse = new MouseInput { Flags = MouseLeftDown },
        };
        inputs[1] = new Input
        {
            Type = InputMouse,
            Mouse = new MouseInput { Flags = MouseLeftUp },
        };
        SendInput(2, inputs, sizeof(Input));
    }

    public void Key(bool backspace)
    {
        ushort key = backspace ? VkBack : VkM;
        Input* inputs = stackalloc Input[2];
        inputs[0] = new Input
        {
            Type = InputKeyboard,
            Keyboard = new KeyboardInput { VirtualKey = key },
        };
        inputs[1] = new Input
        {
            Type = InputKeyboard,
            Keyboard = new KeyboardInput { VirtualKey = key, Flags = KeyUp },
        };
        SendInput(2, inputs, sizeof(Input));
    }

    public byte[] Capture(int x, int y, int width, int height)
    {
        var info = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };
        IntPtr bitmap = CreateDIBSection(_memory, &info, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr previous = SelectObject(_memory, bitmap);
        try
        {
            BitBlt(_memory, 0, 0, width, height, _screen, x, y, SrcCopy);
            return new ReadOnlySpan<byte>((void*)bits, width * height * 4).ToArray();
        }
        finally
        {
            SelectObject(_memory, previous);
            DeleteObject(bitmap);
        }
    }

    public (int X, int Y)? FindWindow(string titlePrefix)
    {
        (int X, int Y)? found = null;
        var state = GCHandle.Alloc(
            (
                titlePrefix,
                new Action<IntPtr>(window =>
                {
                    var origin = new Point();
                    if (ClientToScreen(window, ref origin))
                        found = (origin.X, origin.Y);
                })
            )
        );
        try
        {
            EnumWindows(&OnWindow, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }
        return found;
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(IntPtr window, IntPtr data)
    {
        var (prefix, found) = ((string, Action<IntPtr>))GCHandle.FromIntPtr(data).Target!;
        if (!IsWindowVisible(window))
            return 1;
        char* title = stackalloc char[256];
        int length = GetWindowTextW(window, title, 256);
        if (!new string(title, 0, length).StartsWith(prefix, StringComparison.Ordinal))
            return 1;
        found(window);
        return 0;
    }

    public void Dispose()
    {
        DeleteDC(_memory);
        ReleaseDC(IntPtr.Zero, _screen);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public MouseInput Mouse;

        [FieldOffset(8)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    private static partial int EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> callback, IntPtr data);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextW(IntPtr window, char* text, int length);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr window, ref Point point);

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, Input* inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(
        IntPtr dc,
        BitmapInfoHeader* info,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset
    );

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(
        IntPtr destination,
        int x,
        int y,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint rop
    );
}
