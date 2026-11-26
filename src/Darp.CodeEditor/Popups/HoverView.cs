using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The controls that show the blocks of every popup of code and docs, so that they look alike: the hover, with or
/// without problems, completion's details and signature help's card. Code sits on the popup's background in the
/// editor's font, its tokens in the editor's colors; the code that opens a part, as a signature, has a line below it,
/// between it and the docs; the docs are text in the popup's own, muted color, with links in
/// <c>textLink.foreground</c> and code spans on <c>textCodeBlock.background</c>. Lines are in <c>editorHoverWidget.border</c>, the border of these popups
/// (<see cref="PopupFrame.PaintDocs"/>). A hover's text is selectable.
/// </summary>
internal static class HoverView
{
    /// <summary>How far each level of a list is indented.</summary>
    private const double ListIndent = 16;

    /// <summary>
    /// What code shows for a space. Code fills each line and breaks at any character, as VS Code's completion details:
    /// by Unicode's rules a line of code breaks only at a space, so a qualified name too long for what follows
    /// <c>enum </c> went whole to a line of its own and was cut there still. A no-break space leaves code no place to
    /// break, and a line that doesn't fit breaks where it is full.
    /// </summary>
    private const char NoBreakSpace = ' ';

    /// <summary>The hand over links, made the first time it is needed.</summary>
    private static Cursor? s_hand;

    /// <param name="blocks">The blocks.</param>
    /// <param name="code">How code looks.</param>
    /// <param name="selectable">Whether the user can select and copy the text, as in a hover.</param>
    public static PopupStack Create(IReadOnlyList<HoverBlock> blocks, CodeLook code, bool selectable = false)
    {
        var panel = new PopupStack { Spacing = 6 };
        Append(panel, blocks, code, selectable);
        return panel;
    }

    /// <summary>
    /// Adds blocks below those that show, which keep their selection and scroll position. The blocks start a part where
    /// nothing shows yet.
    /// </summary>
    public static void Append(PopupStack panel, IReadOnlyList<HoverBlock> blocks, CodeLook code, bool selectable)
    {
        IBrush rule = code.Colors.HoverBorder;
        bool opensPart = panel.Children.Count == 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            HoverBlock block = blocks[i];
            panel.Children.Add(
                block switch
                {
                    HoverParagraph paragraph => Paragraph(paragraph, code, selectable),
                    HoverCode codeBlock => Code(codeBlock, code, selectable),
                    HoverRule => new PopupLine { Background = rule },
                    _ => throw new UnreachableException(),
                }
            );
            // The code that opens a part, as a signature, stands apart from the docs below it.
            if (opensPart && block is HoverCode && i + 1 < blocks.Count && blocks[i + 1] is not HoverRule)
                panel.Children.Add(new PopupLine { Background = rule });
            opensPart = block is HoverRule;
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

    private static TextBlock Code(HoverCode block, CodeLook look, bool selectable)
    {
        var inlines = new InlineCollection();
        foreach (CodeRun run in block.Runs)
        {
            // A run without a color of its own takes the block's, the editor's foreground.
            var text = new Run(run.Text.Replace(' ', NoBreakSpace));
            if (run.Foreground is { } foreground)
                text.Foreground = foreground;
            if (run.Bold)
                text.FontWeight = FontWeight.Bold;
            inlines.Add(text);
        }
        TextBlock code = Text(look, selectable);
        code.Inlines = inlines;
        code.FontFamily = look.FontFamily;
        code.FontSize = look.FontSize;
        code.Foreground = look.Colors.CodeForeground;
        code.TextWrapping = TextWrapping.Wrap;
        if (code is SelectableTextBlock copied)
        {
            string original = string.Concat(block.Runs.Select(run => run.Text));
            copied.CopyingToClipboard += (_, e) => Copy(copied, original, e);
        }
        return code;
    }

    /// <summary>
    /// Copies the selected part of <paramref name="original"/>, the code that <paramref name="code"/> shows with its
    /// spaces as no-break spaces: the two have the same length.
    /// </summary>
    private static async void Copy(SelectableTextBlock code, string original, RoutedEventArgs e)
    {
        e.Handled = true;
        int start = Math.Clamp(Math.Min(code.SelectionStart, code.SelectionEnd), 0, original.Length);
        int end = Math.Clamp(Math.Max(code.SelectionStart, code.SelectionEnd), start, original.Length);
        string text = original[start..end];
        try
        {
            if (TopLevel.GetTopLevel(code)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception exception)
        {
            // The platform's clipboard fails now and then; the text is then not copied, as in the editor.
            Logger
                .TryGet(LogEventLevel.Warning, AssetFiles.LogArea)
                ?.Log(code, "Writing to the clipboard failed: {Exception}", exception);
        }
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

/// <summary>How code looks in a popup: in the editor's font.</summary>
/// <param name="FontFamily">The editor's font.</param>
/// <param name="FontSize">The editor's font size.</param>
/// <param name="Colors">The popups' colors.</param>
internal sealed record CodeLook(FontFamily FontFamily, double FontSize, PopupColors Colors);
