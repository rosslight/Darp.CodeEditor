using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Popups;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Tests;

/// <summary>Shows, waits for and reads the hover in the hover's tests.</summary>
internal static class Hovers
{
    /// <summary>
    /// Moves the pointer away, then rests it on the character at <paramref name="offset"/> for the hover's delay on the
    /// clock of <paramref name="time"/>, and waits for the popup with <paramref name="text"/>.
    /// </summary>
    internal static Task<PopupFrame> HoverAsync(EditorWindow window, int offset, string text, ManualTime time)
    {
        window.MoveMouse(window.PointAt(offset) + new Point(0, 200));
        window.MoveMouse(window.PointAt(offset));
        Pass(window, time, window.Editor.Options.Hover.Delay);
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

    /// <summary>Where the window shows the character at <paramref name="index"/> of a text block's text.</summary>
    internal static Point TextPointAt(TextBlock text, int index) =>
        text.TranslatePoint(
            text.TextLayout.HitTestTextPosition(index).Center,
            TopLevel.GetTopLevel(text) ?? throw new InvalidOperationException("Not shown.")
        ) ?? throw new InvalidOperationException("Not shown.");

    internal static IEnumerable<string> TextsOf(HoverContent content) =>
        content.GetVisualDescendants().OfType<TextBlock>().Select(TextOf);

    /// <summary>A popup's look with the hover's colors, in a font that every test machine has.</summary>
    internal static PopupLook Look() =>
        new(
            new FontFamily("DejaVu Sans Mono"),
            13,
            PopupColors.Default,
            FontFamily.Default,
            13,
            PopupColors.Default.HoverForeground
        );

    /// <summary>The paragraph of the server's hover, "… is a word in …".</summary>
    internal static SelectableTextBlock Paragraph(PopupFrame popup) =>
        popup
            .GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .First(block => TextOf(block).Contains("is a word in", StringComparison.Ordinal));

    /// <summary>Whether <paramref name="content"/> still shows: a new hover shows new content in the same frame.</summary>
    internal static bool IsShown(Visual content) => TopLevel.GetTopLevel(content) is not null;

    /// <summary>The hover's popup in the window's overlay layer; null while it is closed.</summary>
    internal static PopupFrame? Popup(EditorWindow window) => PopupFrames.Find(window, "Hover");

    internal static Rect BoundsInWindow(Visual visual) =>
        visual.GetTransformedBounds() is { } bounds
            ? bounds.Bounds.TransformToAABB(bounds.Transform)
            : throw new InvalidOperationException("Not shown.");

    internal static Control Content(PopupFrame popup) => PopupFrames.Content(popup);

    /// <summary>The texts of the hover's code blocks and their pieces, in the editor's <paramref name="font"/>.</summary>
    internal static IEnumerable<string> CodeTexts(PopupFrame popup, FontFamily font) =>
        popup.GetVisualDescendants().OfType<TextBlock>().Where(block => block.FontFamily == font).Select(TextOf);

    internal static IEnumerable<string> Texts(PopupFrame popup) =>
        popup.GetVisualDescendants().OfType<TextBlock>().Select(TextOf);

    internal static string TextOf(Control control) =>
        control is TextBlock { Inlines: { } inlines } ? inlines.Text ?? "" : ((TextBlock)control).Text ?? "";
}
