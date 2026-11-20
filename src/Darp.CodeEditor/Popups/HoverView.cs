using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The controls that show a hover's blocks, as VS Code's hover shows Markdown: prose in the popup's font and foreground,
/// links in <c>textLink.foreground</c>, code spans on <c>textCodeBlock.background</c>, code blocks on the popup's
/// background in the editor's font and token colors, and a line between parts in the hover's border color. In a look
/// that says so, as the hover's, all text is selectable, and Ctrl+C copies the selection.
/// </summary>
internal static class HoverView
{
    /// <summary>How far each level of a list is indented.</summary>
    private const double ListIndent = 16;

    /// <summary>The space between two blocks, unless the second goes on from the first.</summary>
    public const double Spacing = 6;

    /// <summary>The hand over links, made the first time it is needed.</summary>
    private static Cursor? s_hand;

    /// <summary>
    /// The controls of <paramref name="blocks"/>, all of them: as many as a part takes (<see cref="HoverParts"/>) at
    /// once, and the next parts in the frames after it, while they show; once <paramref name="after"/> is built in full,
    /// where given.
    /// </summary>
    public static HoverContent Create(IEnumerable<HoverBlock> blocks, PopupLook look, HoverContent? after = null) =>
        new(blocks, look, after);

    /// <summary>The control of a block.</summary>
    internal static Control Block(HoverBlock block, PopupLook look) =>
        block switch
        {
            HoverParagraph paragraph => Paragraph(paragraph, look),
            HoverCode codeBlock => Code(codeBlock, look),
            HoverRule => new PopupLine { Height = 1, Background = look.Colors.HoverBorder },
            HoverDiagnostic diagnostic => Diagnostic(diagnostic, look),
            HoverHeading heading => Heading(heading, look),
            _ => throw new UnreachableException(),
        };

    /// <summary>
    /// Prose in the app's font and the popup's foreground, set on the text itself: an app's styles for text would beat
    /// what it inherits.
    /// </summary>
    public static TextBlock Prose(PopupLook look)
    {
        TextBlock text = Text(look);
        text.FontFamily = look.TextFont;
        text.FontSize = look.TextSize;
        text.Foreground = look.Foreground;
        return text;
    }

    /// <summary>Code in the editor's font, size and token colors, on the popup's background.</summary>
    public static TextBlock Code(InlineCollection inlines, PopupLook look)
    {
        TextBlock text = Text(look);
        text.Inlines = inlines;
        text.FontFamily = look.CodeFont;
        text.FontSize = look.CodeSize;
        text.Foreground = look.Colors.CodeForeground;
        text.TextWrapping = TextWrapping.Wrap;
        return text;
    }

    /// <summary>Text of the popup's kind: selectable where the look says so.</summary>
    private static TextBlock Text(PopupLook look) =>
        look.Selectable ? new PopupSelectableText(look.Colors.Selection) : new PopupText();

    private static Control Paragraph(HoverParagraph paragraph, PopupLook look)
    {
        TextBlock text = Prose(look);
        Show(text, paragraph.Spans, look);
        text.TextWrapping = TextWrapping.Wrap;
        if (paragraph.Marker is not { } marker)
        {
            text.Margin = new Thickness(ListIndent * paragraph.Level, 0, 0, 0);
            return text;
        }
        var item = new PopupDock { Margin = new Thickness(ListIndent * (paragraph.Level - 1), 0, 0, 0) };
        TextBlock markerText = Prose(look);
        markerText.Text = marker;
        markerText.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(markerText, Dock.Left);
        item.Children.Add(markerText);
        item.Children.Add(text);
        return item;
    }

    /// <summary>
    /// A diagnostic in VS Code's geometry (<see cref="HoverDiagnostic"/>): the icon 8 pixels from the text, and each
    /// related place 8 pixels below the one before, in the link color where the editor can go there.
    /// </summary>
    private static Control Diagnostic(HoverDiagnostic diagnostic, PopupLook look)
    {
        var column = new PopupStack();
        if (!diagnostic.Continues || diagnostic.Line.Count > 0)
        {
            TextBlock line = CodeText(look);
            Show(line, diagnostic.Line, look);
            column.Children.Add(line);
        }
        foreach (HoverRelated related in diagnostic.Related)
        {
            TextBlock place = CodeText(look);
            place.Margin = new Thickness(0, related.Continues ? 0 : 8, 0, 0);
            var where = new Run(related.Place);
            place.Inlines = [where, new Run(related.Message)];
            // Only a place that the editor can go to looks like a link.
            if (related.Open is { } open)
            {
                where.Foreground = look.Colors.Link;
                OpenLinks(place, [(0, related.Place.Length, _ => open())]);
            }
            column.Children.Add(place);
        }
        if (diagnostic.Icon == HoverIcon.None)
            return column;
        Control icon =
            diagnostic.Icon == HoverIcon.Shown
                ? new SeverityIcon(diagnostic.Severity, look.Colors)
                : new Control { Width = SeverityIcon.Size, Height = SeverityIcon.Size };
        icon.Margin = new Thickness(0, 1, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        return new PopupDock { Children = { icon, column } };
    }

    /// <summary>
    /// Shows <paramref name="spans"/> in <paramref name="text"/>: code in the editor's font at the text's size, so that
    /// it fits its line, links in the link color, and dimmed text at VS Code's 60% opacity.
    /// </summary>
    private static void Show(TextBlock text, IReadOnlyList<HoverSpan> spans, PopupLook look)
    {
        var inlines = new InlineCollection();
        var links = new List<(int Start, int End, Action<TopLevel> Open)>();
        int length = 0;
        foreach (HoverSpan span in spans)
        {
            var run = new Run(span.Text);
            if (span.Style.HasFlag(SpanStyle.Bold))
                run.FontWeight = FontWeight.Bold;
            if (span.Style.HasFlag(SpanStyle.Italic))
                run.FontStyle = FontStyle.Italic;
            if (span.Style.HasFlag(SpanStyle.Underline))
                run.TextDecorations = TextDecorations.Underline;
            if (span.Style.HasFlag(SpanStyle.Code))
            {
                run.FontFamily = look.CodeFont;
                run.Background = look.Colors.CodeSpanBackground;
            }
            IBrush? foreground = span.Link is null ? null : look.Colors.Link;
            if (span.Style.HasFlag(SpanStyle.Dimmed))
                foreground = Dimmed(foreground ?? look.Foreground);
            if (foreground is not null)
                run.Foreground = foreground;
            if (span.Link is { } uri)
                links.Add((length, length + span.Text.Length, topLevel => Open(topLevel, uri)));
            inlines.Add(run);
            length += span.Text.Length;
        }
        text.Inlines = inlines;
        if (links.Count > 0)
            OpenLinks(text, links);
    }

    /// <summary>The heading of the problem that F8 went to, as VS Code's widget's title: the icon 4 pixels from the text.</summary>
    private static Control Heading(HoverHeading heading, PopupLook look)
    {
        var icon = new SeverityIcon(heading.Severity, look.Colors)
        {
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(icon, Dock.Left);
        TextBlock text = Prose(look);
        text.VerticalAlignment = VerticalAlignment.Center;
        var count = new Run(heading.Count) { Foreground = look.Colors.Description };
        text.Inlines = heading.Name is { } name ? [new Run(name), new Run("  "), count] : [count];
        return new PopupDock { Children = { icon, text } };
    }

    /// <summary>Text in the editor's font and size, in the popup's foreground, that wraps.</summary>
    private static TextBlock CodeText(PopupLook look)
    {
        TextBlock text = Text(look);
        text.FontFamily = look.CodeFont;
        text.FontSize = look.CodeSize;
        text.Foreground = look.Foreground;
        text.TextWrapping = TextWrapping.Wrap;
        return text;
    }

    /// <summary><paramref name="brush"/> at VS Code's 60% opacity of a hover's dimmed parts.</summary>
    private static IBrush Dimmed(IBrush brush) =>
        brush is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, solid.Opacity * 0.6) : brush;

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
    /// Opens the web page of a link that is clicked without selecting, through the app's launcher, and shows the hand
    /// over links.
    /// </summary>
    private static void OpenLinks(TextBlock text, List<(int Start, int End, Action<TopLevel> Open)> links)
    {
        Cursor? cursor = text.Cursor;
        text.PointerMoved += (_, e) =>
            text.Cursor = LinkAt(text, links, e) is null ? cursor : s_hand ??= new(StandardCursorType.Hand);
        // Selectable text handles the press and release itself, to select.
        text.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, e) =>
            {
                if (
                    e.InitialPressMouseButton == MouseButton.Left
                    && text is not SelectableTextBlock { SelectedText.Length: > 0 }
                    && LinkAt(text, links, e) is { } open
                    && TopLevel.GetTopLevel(text) is { } topLevel
                )
                {
                    open(topLevel);
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true
        );
    }

    private static Action<TopLevel>? LinkAt(
        TextBlock text,
        List<(int Start, int End, Action<TopLevel> Open)> links,
        PointerEventArgs e
    )
    {
        Point point = e.GetPosition(text) - new Point(text.Padding.Left, text.Padding.Top);
        TextHitTestResult hit = text.TextLayout.HitTestPoint(point);
        if (!hit.IsInside)
            return null;
        foreach ((int start, int end, Action<TopLevel> open) in links)
        {
            if (hit.TextPosition >= start && hit.TextPosition < end)
                return open;
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
    /// <summary>Whether its text is selectable, as a hover's; false by default.</summary>
    public bool Selectable { get; init; }

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
