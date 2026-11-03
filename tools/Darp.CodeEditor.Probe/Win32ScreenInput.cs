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
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort VkBack = 0x08;
    private const ushort VkM = 0x4D;
    private const uint SrcCopy = 0x00CC0020;

    /// <summary>Keys and buttons that turn a typed key into a shortcut or a click into a drag.</summary>
    private static readonly (int Key, string Name)[] s_modifiers =
    [
        (0x10, "Shift"),
        (0x11, "Ctrl"),
        (0x12, "Alt"),
        (0x5B, "the left Windows key"),
        (0x5C, "the right Windows key"),
        (0x01, "the left mouse button"),
        (0x02, "the right mouse button"),
        (0x04, "the middle mouse button"),
        (0x05, "the first extra mouse button"),
        (0x06, "the second extra mouse button"),
    ];

    private readonly IntPtr _screen = GetDC(IntPtr.Zero);
    private readonly IntPtr _memory;

    public Win32ScreenInput()
    {
        _memory = CreateCompatibleDC(_screen);
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
        uint sent = SendInput(2, inputs, sizeof(Input));
        if (sent == 2)
            return;
        int error = Marshal.GetLastPInvokeError();
        // A key down without its key up would stay pressed: release the probe's own key.
        if (sent == 1)
            SendInput(1, inputs + 1, sizeof(Input));
        throw new UnsafeInputException($"SendInput took {sent} of 2 events (error {error}), input may be blocked");
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

    public string? CheckFocus(TargetWindow target)
    {
        var window = (IntPtr)target.Window;
        if (!IsWindow(window))
            return "the demo's window no longer exists";
        if (ProcessOf(window) != target.ProcessId)
            return "the demo's window belongs to another process now";
        IntPtr foreground = GetForegroundWindow();
        return foreground == window
            ? null
            : $"the foreground window is 0x{foreground:X} of process {ProcessOf(foreground)?.ToString() ?? "unknown"}, not the demo's window 0x{window:X}";
    }

    public string? HeldModifier()
    {
        foreach ((int key, string name) in s_modifiers)
        {
            // The high bit: down now.
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                return name;
        }
        return null;
    }

    private static int? ProcessOf(IntPtr window) =>
        GetWindowThreadProcessId(window, out uint processId) == 0 ? null : (int)processId;

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

        // The union's size is that of its largest member, MOUSEINPUT: 32 bytes on 64-bit.
        [FieldOffset(8)]
        public KeyboardInput Keyboard;

        [FieldOffset(8)]
        private fixed byte _union[32];
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
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, Input* inputs, int size);

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
