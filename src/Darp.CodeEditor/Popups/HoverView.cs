using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The controls that show a hover's blocks, as VS Code's hover shows Markdown: prose in the popup's font and foreground,
/// links in <c>textLink.foreground</c>, code spans on <c>textCodeBlock.background</c>, code blocks on the popup's
/// background in the editor's font and token colors, and a line between parts in the hover's border color.
/// </summary>
internal static class HoverView
{
    /// <summary>How far each level of a list is indented.</summary>
    private const double ListIndent = 16;

    /// <summary>The hand over links, made the first time it is needed.</summary>
    private static Cursor? s_hand;

    public static Control Create(IReadOnlyList<HoverBlock> blocks, PopupLook look)
    {
        var panel = new PopupStack { Spacing = 6 };
        foreach (HoverBlock block in blocks)
        {
            panel.Children.Add(
                block switch
                {
                    HoverParagraph paragraph => Paragraph(paragraph, look),
                    HoverCode codeBlock => Code(codeBlock, look),
                    HoverRule => new PopupLine { Height = 1, Background = look.Colors.HoverBorder },
                    _ => throw new UnreachableException(),
                }
            );
        }
        return panel;
    }

    /// <summary>
    /// Prose in the app's font and the popup's foreground, set on the text itself: an app's styles for text would beat
    /// what it inherits.
    /// </summary>
    public static PopupText Prose(PopupLook look) =>
        new()
        {
            FontFamily = look.TextFont,
            FontSize = look.TextSize,
            Foreground = look.Foreground,
        };

    /// <summary>Code in the editor's font, size and token colors, on the popup's background.</summary>
    public static TextBlock Code(InlineCollection inlines, PopupLook look) =>
        new PopupText
        {
            Inlines = inlines,
            FontFamily = look.CodeFont,
            FontSize = look.CodeSize,
            Foreground = look.Colors.CodeForeground,
            TextWrapping = TextWrapping.Wrap,
        };

    private static Control Paragraph(HoverParagraph paragraph, PopupLook look)
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
            // A small code span, in the editor's font at the paragraph's size, so that it fits its line.
            if (span.Style.HasFlag(SpanStyle.Code))
            {
                run.FontFamily = look.CodeFont;
                run.Background = look.Colors.CodeSpanBackground;
            }
            if (span.Link is { } uri)
            {
                run.Foreground = look.Colors.Link;
                links.Add((length, length + span.Text.Length, uri));
            }
            inlines.Add(run);
            length += span.Text.Length;
        }
        PopupText text = Prose(look);
        text.Inlines = inlines;
        text.TextWrapping = TextWrapping.Wrap;
        if (links.Count > 0)
            OpenLinks(text, links);
        if (paragraph.Marker is not { } marker)
        {
            text.Margin = new Thickness(ListIndent * paragraph.Level, 0, 0, 0);
            return text;
        }
        var item = new PopupDock { Margin = new Thickness(ListIndent * (paragraph.Level - 1), 0, 0, 0) };
        PopupText markerText = Prose(look);
        markerText.Text = marker;
        markerText.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(markerText, Dock.Left);
        item.Children.Add(markerText);
        item.Children.Add(text);
        return item;
    }

    private static TextBlock Code(HoverCode block, PopupLook look)
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
        return Code(inlines, look);
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

/// <summary>
/// How a popup looks: code in the editor's font, prose in the app's font where the editor is, and the color theme's
/// popup colors.
/// </summary>
/// <param name="CodeFont">The editor's font.</param>
/// <param name="CodeSize">The editor's font size.</param>
/// <param name="Colors">The popups' colors.</param>
/// <param name="TextFont">The app's font where the editor is, not the editor's monospace one.</param>
/// <param name="TextSize">The app's font size there.</param>
/// <param name="Foreground">The popup's foreground, for prose.</param>
internal sealed record PopupLook(
    FontFamily CodeFont,
    double CodeSize,
    PopupColors Colors,
    FontFamily TextFont,
    double TextSize,
    IBrush Foreground
)
{
    /// <summary>The look of <paramref name="editor"/>'s popups, with prose in <paramref name="foreground"/>.</summary>
    public static PopupLook Of(TemplatedControl editor, PopupColors colors, IBrush foreground)
    {
        // Outside a window, where no popup opens, the editor's own.
        AvaloniaObject around = editor.GetVisualParent() ?? (Visual)editor;
        return new PopupLook(
            editor.FontFamily,
            editor.FontSize,
            colors,
            around.GetValue(TextElement.FontFamilyProperty),
            around.GetValue(TextElement.FontSizeProperty),
            foreground
        );
    }
}
