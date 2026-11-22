using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The controls that show a hover's blocks: text in the popup's look, links in <c>textLink.foreground</c>, code spans on
/// <c>textCodeBlock.background</c>, and code blocks in the editor's font, colors and background. A hover's text is
/// selectable.
/// </summary>
internal static class HoverView
{
    /// <summary>How far each level of a list is indented.</summary>
    private const double ListIndent = 16;

    /// <summary>The hand over links, made the first time it is needed.</summary>
    private static Cursor? s_hand;

    /// <param name="blocks">The blocks.</param>
    /// <param name="code">How code looks.</param>
    /// <param name="rule">The color of the line between parts.</param>
    /// <param name="selectable">Whether the user can select and copy the text, as in a hover.</param>
    public static PopupStack Create(
        IReadOnlyList<HoverBlock> blocks,
        CodeLook code,
        IBrush rule,
        bool selectable = false
    )
    {
        var panel = new PopupStack { Spacing = 6 };
        Append(panel, blocks, code, rule, selectable);
        return panel;
    }

    /// <summary>Adds blocks below those that show, which keep their selection and scroll position.</summary>
    public static void Append(
        PopupStack panel,
        IReadOnlyList<HoverBlock> blocks,
        CodeLook code,
        IBrush rule,
        bool selectable
    )
    {
        foreach (HoverBlock block in blocks)
        {
            panel.Children.Add(
                block switch
                {
                    HoverParagraph paragraph => Paragraph(paragraph, code, selectable),
                    HoverCode codeBlock => Code(codeBlock, code, selectable),
                    HoverRule => new PopupLine { Background = rule },
                    _ => throw new UnreachableException(),
                }
            );
        }
    }

    private static TextBlock Text(CodeLook look, bool selectable) =>
        selectable ? new PopupSelectableText(look.Colors.Selection) : new PopupText();

    private static Control Paragraph(HoverParagraph paragraph, CodeLook code, bool selectable)
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
                run.FontFamily = code.FontFamily;
                run.Background = code.Colors.CodeSpanBackground;
            }
            if (span.Link is { } uri)
            {
                run.Foreground = code.Colors.Link;
                run.TextDecorations = TextDecorations.Underline;
                links.Add((length, length + span.Text.Length, uri));
            }
            if (span.Style.HasFlag(SpanStyle.Dimmed))
                run.Foreground = Dimmed(run.Foreground ?? code.Colors.HoverForeground);
            inlines.Add(run);
            length += span.Text.Length;
        }
        TextBlock text = Text(code, selectable);
        text.Inlines = inlines;
        text.TextWrapping = TextWrapping.Wrap;
        if (links.Count > 0)
            OpenLinks(text, links);
        if (paragraph.Severity is { } severity)
        {
            var icon = new SeverityIcon(severity, code.Colors)
            {
                Margin = new Thickness(0, 1, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(icon, Dock.Left);
            return new PopupDock { Children = { icon, text } };
        }
        if (paragraph.Marker is not { } marker)
        {
            text.Margin = new Thickness(ListIndent * paragraph.Level, 0, 0, 0);
            return text;
        }
        var item = new PopupDock { Margin = new Thickness(ListIndent * (paragraph.Level - 1), 0, 0, 0) };
        var markerText = new PopupText { Text = marker, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(markerText, Dock.Left);
        item.Children.Add(markerText);
        item.Children.Add(text);
        return item;
    }

    /// <summary><paramref name="brush"/> at VS Code's 60% opacity of a hover's dimmed parts.</summary>
    private static IBrush Dimmed(IBrush brush) =>
        brush is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, solid.Opacity * 0.6) : brush;

    private static Control Code(HoverCode block, CodeLook look, bool selectable)
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
        TextBlock code = Text(look, selectable);
        code.Inlines = inlines;
        code.FontFamily = look.FontFamily;
        code.FontSize = look.FontSize;
        code.Foreground = look.Foreground;
        code.TextWrapping = TextWrapping.Wrap;
        return new PopupBox
        {
            Background = look.Background,
            Padding = new Thickness(6, 4),
            Child = code,
        };
    }

    /// <summary>
    /// Opens the web page of a link that is clicked without selecting, through the app's launcher, and shows the hand
    /// over links.
    /// </summary>
    private static void OpenLinks(TextBlock text, List<(int Start, int End, Uri Uri)> links)
    {
        Cursor? cursor = text.Cursor;
        text.PointerMoved += (_, e) =>
            text.Cursor = LinkAt(text, links, e) is null ? cursor : s_hand ??= new(StandardCursorType.Hand);
        // Selectable text handles the release itself.
        text.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, e) =>
            {
                if (
                    e.InitialPressMouseButton == MouseButton.Left
                    && text is not SelectableTextBlock { SelectedText.Length: > 0 }
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

/// <summary>How code looks in a popup: as in the editor.</summary>
/// <param name="FontFamily">The editor's font.</param>
/// <param name="FontSize">The editor's font size.</param>
/// <param name="Colors">The popups' colors.</param>
internal sealed record CodeLook(FontFamily FontFamily, double FontSize, PopupColors Colors)
{
    /// <summary>The text area's background.</summary>
    public IBrush Background => Colors.CodeBackground;

    /// <summary>The text area's foreground.</summary>
    public IBrush Foreground => Colors.CodeForeground;
}
