using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The controls that show a hover's blocks, as VS Code's hover shows Markdown: prose in the app's font at one size, in
/// the popup's foreground; code blocks on the popup's background in the editor's font and token colors; code spans on
/// <c>textCodeBlock.background</c>; links in <c>textLink.foreground</c>. All text is selectable, and Ctrl+C copies the
/// selection.
/// </summary>
internal static class HoverView
{
    /// <summary>How far each level of a list is indented.</summary>
    private const double ListIndent = 16;

    /// <summary>
    /// The editor's own look of selectable text, so that an app's theme for <see cref="SelectableTextBlock"/> never
    /// reaches popups: an I-beam, and nothing else.
    /// </summary>
    private static readonly ControlTheme s_selectableTheme = new(typeof(SelectableTextBlock))
    {
        Setters = { new Setter(InputElement.CursorProperty, new Cursor(StandardCursorType.Ibeam)) },
    };

    /// <summary>The hand over links, made the first time it is needed.</summary>
    private static Cursor? s_hand;

    public static Control Create(IReadOnlyList<HoverBlock> blocks, PopupLook look)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (HoverBlock block in blocks)
        {
            panel.Children.Add(
                block switch
                {
                    HoverParagraph paragraph => Paragraph(paragraph, look),
                    HoverCode codeBlock => Code(codeBlock, look),
                    HoverRule => Rule(look),
                    _ => throw new UnreachableException(),
                }
            );
        }
        return panel;
    }

    /// <summary>Selectable text in the editor's own look, with the color theme's selection.</summary>
    public static SelectableTextBlock Text(PopupLook look) =>
        new()
        {
            Theme = s_selectableTheme,
            SelectionBrush = look.Colors.Selection,
            TextWrapping = TextWrapping.Wrap,
        };

    /// <summary>A line between two parts, in the hover's border color.</summary>
    public static Control Rule(PopupLook look) =>
        new Border
        {
            Height = 1,
            Background = look.Colors.HoverBorder,
            Margin = new Thickness(0, 2),
        };

    /// <summary>Code in the editor's font, size and token colors, on the popup's background.</summary>
    public static SelectableTextBlock Code(HoverCode block, PopupLook look)
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
        SelectableTextBlock code = Text(look);
        code.Inlines = inlines;
        code.FontFamily = look.CodeFont;
        code.FontSize = look.CodeSize;
        code.Foreground = look.Colors.CodeForeground;
        return code;
    }

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
        SelectableTextBlock text = Text(look);
        text.Inlines = inlines;
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

    /// <summary>
    /// Opens the web page of a link that is clicked without selecting, through the app's launcher, and shows the hand
    /// over links.
    /// </summary>
    private static void OpenLinks(SelectableTextBlock text, List<(int Start, int End, Uri Uri)> links)
    {
        text.PointerMoved += (_, e) =>
            text.Cursor = LinkAt(text, links, e) is null ? null : s_hand ??= new(StandardCursorType.Hand);
        // The text handles the press and release itself, to select.
        text.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, e) =>
            {
                if (
                    e.InitialPressMouseButton == MouseButton.Left
                    && text.SelectionStart == text.SelectionEnd
                    && LinkAt(text, links, e) is { } uri
                    && TopLevel.GetTopLevel(text) is { } topLevel
                )
                {
                    Open(topLevel, uri);
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true
        );
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

/// <summary>How a popup looks: code as in the editor, everything else in the popup colors of its color theme.</summary>
/// <param name="CodeFont">The editor's font.</param>
/// <param name="CodeSize">The editor's font size.</param>
/// <param name="Colors">The popups' colors.</param>
internal sealed record PopupLook(FontFamily CodeFont, double CodeSize, PopupColors Colors);
