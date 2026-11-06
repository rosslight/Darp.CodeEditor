using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Tests;

/// <summary>A shown window with an editor, and what tests read from it: the rendered lines, their colors and pixels.</summary>
internal sealed class EditorWindow : IDisposable
{
    /// <summary>
    /// Shows <paramref name="editor"/>, inside <paramref name="content"/> if given, in a new window, and gives it the
    /// keyboard focus.
    /// </summary>
    public EditorWindow(CodeEditor editor, Control? content = null, double width = 800, double height = 600)
    {
        Editor = editor;
        Window = new Window
        {
            Width = width,
            Height = height,
            Content = content ?? editor,
        };
        Window.Show();
        Layout();
        editor.Focus();
    }

    /// <summary>
    /// The key that commands hold: Cmd on macOS and Ctrl elsewhere, as AvaloniaEdit decides by the operating system.
    /// The headless platform reports Ctrl on every system, so its hotkey settings can't tell.
    /// </summary>
    public static KeyModifiers Command { get; } = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public Window Window { get; }

    public CodeEditor Editor { get; }

    /// <summary>AvaloniaEdit's text view in <paramref name="editor"/>.</summary>
    public static TextView TextViewOf(CodeEditor editor) => PartOf(editor).TextArea.TextView;

    /// <summary>AvaloniaEdit's editor in <paramref name="editor"/>'s template.</summary>
    public static TextEditor PartOf(CodeEditor editor) => editor.GetVisualDescendants().OfType<TextEditor>().Single();

    public TextView TextView => TextViewOf(Editor);

    /// <summary>Runs pending jobs and a layout pass, without rendering.</summary>
    public void Layout()
    {
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
    }

    /// <summary>Renders a frame, after the jobs and layout before it.</summary>
    public void Render() => Window.CaptureRenderedFrame()?.Dispose();

    /// <summary>Renders a frame, and counts its pixels of <paramref name="color"/>, give or take a little.</summary>
    public int RenderAndCountPixels(Color color, int tolerance = 8)
    {
        using WriteableBitmap frame =
            Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        return CountPixels(frame, color, tolerance);
    }

    public void Type(string text)
    {
        foreach (char c in text)
            Window.KeyTextInput(c.ToString());
        Dispatcher.UIThread.RunJobs();
    }

    public void Press(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        Window.KeyPress(key, (RawInputModifiers)modifiers, PhysicalKey.None, null);
        Window.KeyRelease(key, (RawInputModifiers)modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Clicks <paramref name="button"/> at <paramref name="point"/> of the window.</summary>
    public void Click(Point point, MouseButton button)
    {
        Window.MouseDown(point, button);
        Window.MouseUp(point, button);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The text colors that the rendered line of <paramref name="lineNumber"/> uses, if it is rendered.</summary>
    public IReadOnlySet<Color> ColorsOfLine(int lineNumber) =>
        TextView.VisualLinesValid && TextView.GetVisualLine(lineNumber) is { } line
            ? line
                .TextLines.SelectMany(t => t.TextRuns)
                .Select(run => run.Properties?.ForegroundBrush)
                .OfType<ISolidColorBrush>()
                .Select(brush => brush.Color)
                .ToHashSet()
            : new HashSet<Color>();

    /// <summary>The text colors that the rendered lines use.</summary>
    public IReadOnlySet<Color> RenderedColors() =>
        TextView.VisualLinesValid
            ? TextView
                .VisualLines.SelectMany(line => line.TextLines)
                .SelectMany(t => t.TextRuns)
                .Select(run => run.Properties?.ForegroundBrush)
                .OfType<ISolidColorBrush>()
                .Select(brush => brush.Color)
                .ToHashSet()
            : new HashSet<Color>();

    private static int CountPixels(WriteableBitmap frame, Color color, int tolerance = 8)
    {
        using ILockedFramebuffer buffer = frame.Lock();
        bool bgra = buffer.Format == PixelFormat.Bgra8888;
        var row = new byte[buffer.RowBytes];
        int count = 0;
        for (int y = 0; y < buffer.Size.Height; y++)
        {
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, row, 0, row.Length);
            for (int x = 0; x < buffer.Size.Width; x++)
            {
                int i = x * 4;
                (byte r, byte b) = bgra ? (row[i + 2], row[i]) : (row[i], row[i + 2]);
                if (
                    Math.Abs(r - color.R) <= tolerance
                    && Math.Abs(row[i + 1] - color.G) <= tolerance
                    && Math.Abs(b - color.B) <= tolerance
                )
                    count++;
            }
        }
        return count;
    }

    public void Dispose() => Window.Close();
}
