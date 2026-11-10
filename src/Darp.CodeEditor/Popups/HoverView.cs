using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The controls that show a hover's blocks: text in the popup's look, with the app's link and separator looks, and code
/// in the editor's font, colors and background.
/// </summary>
internal static class HoverView
{
    /// <summary>How far each level of a list is indented.</summary>
    private const double ListIndent = 16;

    /// <summary>The hand over links, made the first time it is needed.</summary>
    private static Cursor? s_hand;

    public static Control Create(IReadOnlyList<HoverBlock> blocks, CodeLook code)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (HoverBlock block in blocks)
        {
            panel.Children.Add(
                block switch
                {
                    HoverParagraph paragraph => Paragraph(paragraph, code),
                    HoverCode codeBlock => Code(codeBlock, code),
                    HoverRule => new Separator(),
                    _ => throw new UnreachableException(),
                }
            );
        }
        return panel;
    }

    private static Control Paragraph(HoverParagraph paragraph, CodeLook code)
    {
        var inlines = new InlineCollection();
        var links = new List<(int Start, int End, Uri Uri)>();
        int length = 0;
        foreach (HoverSpan span in paragraph.Spans)
        {
            var run = new Run(span.Text);
            if (span.Style.HasFlag(SpanStyle.Bold))
                run.FontWeight = FontWeight.Bold;
            if (span.Style.HasFlag(SpanStyle.Italic))
                run.FontStyle = FontStyle.Italic;
            // A small code span, in the editor's look at the paragraph's size, so that it fits its line.
            if (span.Style.HasFlag(SpanStyle.Code))
            {
                run.FontFamily = code.FontFamily;
                run.Background = code.Background;
                if (code.Foreground is { } foreground)
                    run.Foreground = foreground;
            }
            if (span.Link is { } uri)
            {
                run.TextDecorations = TextDecorations.Underline;
                links.Add((length, length + span.Text.Length, uri));
            }
            inlines.Add(run);
            length += span.Text.Length;
        }
        var text = new TextBlock { Inlines = inlines, TextWrapping = TextWrapping.Wrap };
        if (links.Count > 0)
            OpenLinks(text, links);
        if (paragraph.Marker is not { } marker)
        {
            text.Margin = new Thickness(ListIndent * paragraph.Level, 0, 0, 0);
            return text;
        }
        var item = new DockPanel { Margin = new Thickness(ListIndent * (paragraph.Level - 1), 0, 0, 0) };
        var markerText = new TextBlock { Text = marker, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(markerText, Dock.Left);
        item.Children.Add(markerText);
        item.Children.Add(text);
        return item;
    }

    private static Control Code(HoverCode block, CodeLook look)
    {
        var inlines = new InlineCollection();
        foreach (CodeRun run in block.Runs)
        {
            // A run without a color of its own takes the block's, the editor's foreground.
            var text = new Run(run.Text);
            if (run.Foreground is { } foreground)
                text.Foreground = foreground;
            inlines.Add(text);
        }
        return new Border
        {
            Background = look.Background,
            Padding = new Thickness(6, 4),
            Child = new TextBlock
            {
                Inlines = inlines,
                FontFamily = look.FontFamily,
                FontSize = look.FontSize,
                Foreground = look.Foreground,
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    /// <summary>
    /// Opens the web page of a link that is clicked, through the app's launcher, and shows the hand over links. The text
    /// takes no keyboard focus, so the editor keeps it.
    /// </summary>
    private static void OpenLinks(TextBlock text, List<(int Start, int End, Uri Uri)> links)
    {
        text.PointerMoved += (_, e) =>
            text.Cursor = LinkAt(text, links, e) is null ? null : s_hand ??= new(StandardCursorType.Hand);
        text.PointerReleased += (_, e) =>
        {
            if (
                e.InitialPressMouseButton == MouseButton.Left
                && LinkAt(text, links, e) is { } uri
                && TopLevel.GetTopLevel(text) is { } topLevel
            )
            {
                Open(topLevel, uri);
            }
        };
    }

    private static Uri? LinkAt(TextBlock text, List<(int Start, int End, Uri Uri)> links, PointerEventArgs e)
    {
        Point point = e.GetPosition(text) - new Point(text.Padding.Left, text.Padding.Top);
        TextHitTestResult hit = text.TextLayout.HitTestPoint(point);
        if (!hit.IsInside)
            return null;
        foreach ((int start, int end, Uri uri) in links)
        {
            if (hit.TextPosition >= start && hit.TextPosition < end)
                return uri;
        }
        return null;
    }

    /// <summary>Opens a web page; a failure of the launcher goes to the dispatcher, as for Avalonia's own links.</summary>
    private static async void Open(TopLevel topLevel, Uri uri) => await topLevel.Launcher.LaunchUriAsync(uri);
}

/// <summary>How code looks in a popup: as in the editor.</summary>
/// <param name="FontFamily">The editor's font.</param>
/// <param name="FontSize">The editor's font size.</param>
/// <param name="Background">The text area's background; null while the color theme loads.</param>
/// <param name="Foreground">The text area's foreground; null while the color theme loads.</param>
internal sealed record CodeLook(FontFamily FontFamily, double FontSize, IBrush? Background, IBrush? Foreground);
