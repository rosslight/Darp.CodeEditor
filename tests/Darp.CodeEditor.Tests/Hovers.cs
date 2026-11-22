using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Tests;

/// <summary>Shows, waits for and reads the hover in the hover's tests.</summary>
internal static class Hovers
{
    /// <summary>How long the pointer rests before its hover shows.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Moves the pointer away, then rests it on the character at <paramref name="offset"/> for the hover's delay on the
    /// clock of <paramref name="time"/>, and waits for the popup with <paramref name="text"/>.
    /// </summary>
    internal static Task<PopupFrame> HoverAsync(EditorWindow window, int offset, string text, ManualTime time)
    {
        bool showing = Popup(window) is not null;
        window.MoveMouse(window.PointAt(offset) + new Point(0, 200));
        // A hover that shows stays this long, for the pointer to reach it.
        if (showing)
            Pass(window, time, Delay);
        window.MoveMouse(window.PointAt(offset));
        Pass(window, time, Delay);
        return WaitForPopupAsync(window, text);
    }

    /// <summary>
    /// Moves the clock of <paramref name="time"/> on by <paramref name="span"/>, and runs the UI thread's work that its
    /// timers posted, so that what they do has happened.
    /// </summary>
    internal static void Pass(EditorWindow window, ManualTime time, TimeSpan span)
    {
        time.Advance(span);
        window.Layout();
    }

    internal static async Task<PopupFrame> WaitForPopupAsync(EditorWindow window, string text)
    {
        PopupFrame? popup = null;
        await Wait.UntilAsync(() =>
        {
            window.Render();
            popup = Popup(window);
            return popup is not null && Texts(popup).Any(t => t.Contains(text, StringComparison.Ordinal));
        });
        return popup ?? throw new InvalidOperationException();
    }

    /// <summary>Presses this platform's hover key: Ctrl+Q on Windows, F1 on Linux and macOS.</summary>
    internal static void PressHoverKey(EditorWindow window)
    {
        if (OperatingSystem.IsWindows())
            window.Press(Key.Q, KeyModifiers.Control);
        else
            window.Press(Key.F1);
    }

    /// <summary>The paragraph of the server's hover, "… is a word in …".</summary>
    internal static SelectableTextBlock Paragraph(PopupFrame popup) =>
        popup
            .GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .First(block => TextOf(block).Contains("is a word in", StringComparison.Ordinal));

    /// <summary>The hover's popup in the window's overlay layer; null while it is closed.</summary>
    internal static PopupFrame? Popup(EditorWindow window) => PopupFrames.Find(window, "Hover");

    internal static Rect BoundsInWindow(Visual visual) =>
        visual.GetTransformedBounds() is { } bounds
            ? bounds.Bounds.TransformToAABB(bounds.Transform)
            : throw new InvalidOperationException("Not shown.");

    internal static Control Content(PopupFrame popup) => PopupFrames.Content(popup);

    internal static IEnumerable<string> Texts(PopupFrame popup) =>
        popup.GetVisualDescendants().OfType<TextBlock>().Select(TextOf);

    internal static string TextOf(Control control) =>
        control is TextBlock { Inlines: { } inlines } ? inlines.Text ?? "" : ((TextBlock)control).Text ?? "";
}
