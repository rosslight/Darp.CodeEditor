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

    /// <summary>The space between two blocks, unless the second goes on from the first.</summary>
    internal const double Spacing = 6;

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

    /// <summary>
    /// The controls of <paramref name="blocks"/>, all of them: as many as a part of a hover takes
    /// (<see cref="HoverParts"/>) at once, and the next parts in the frames after it, while they show.
    /// </summary>
    public static HoverContent Create(IReadOnlyList<HoverBlock> blocks, PopupLook look) => new(blocks, look);

    /// <summary>The control of a block.</summary>
    internal static Control Block(HoverBlock block, PopupLook look) =>
        block switch
        {
            HoverParagraph paragraph => Paragraph(paragraph, look),
            HoverCode codeBlock => Code(codeBlock, look),
            HoverRule => Rule(look),
            HoverDiagnostic diagnostic => Diagnostic(diagnostic, look),
            HoverHeading heading => Heading(heading, look),
            _ => throw new UnreachableException(),
        };

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
        var links = new List<(int Start, int End, Action<TopLevel> Open)>();
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
                links.Add((length, length + span.Text.Length, topLevel => Open(topLevel, uri)));
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
    /// A diagnostic, as VS Code's hover shows it, in the editor's font (<see cref="HoverDiagnostic"/>): the icon in a
    /// column of its own, 8 pixels from the text; the source and code at VS Code's 60% opacity, after a space; each
    /// related place 8 pixels below the one before.
    /// </summary>
    private static Control Diagnostic(HoverDiagnostic diagnostic, PopupLook look)
    {
        IBrush dimmed = Dimmed(look.Colors.HoverForeground);
        var inlines = new InlineCollection { new Run(diagnostic.Message) };
        var links = new List<(int Start, int End, Action<TopLevel> Open)>();
        int length = diagnostic.Message.Length;
        if (diagnostic.Source is not null || diagnostic.Code is not null)
        {
            Add(" " + diagnostic.Source, dimmed);
            if (diagnostic.Code is { } code && diagnostic.CodeLink is { } page)
            {
                Add("(", dimmed);
                Run link = Add(code, Dimmed(look.Colors.Link));
                link.TextDecorations = TextDecorations.Underline;
                links.Add((length - code.Length, length, topLevel => Open(topLevel, page)));
                Add(")", dimmed);
            }
            else if (diagnostic.Code is { } plain)
                Add($"({plain})", dimmed);
        }
        SelectableTextBlock message = CodeText(look);
        message.Inlines = inlines;
        if (links.Count > 0)
            OpenLinks(message, links);
        var column = new StackPanel { Children = { message } };
        foreach (HoverRelated related in diagnostic.Related)
        {
            SelectableTextBlock place = CodeText(look);
            place.Margin = new Thickness(0, 8, 0, 0);
            place.Inlines = [new Run(related.Place) { Foreground = look.Colors.Link }, new Run(related.Message)];
            if (related.Open is { } open)
                OpenLinks(place, [(0, related.Place.Length, _ => open())]);
            column.Children.Add(place);
        }
        if (diagnostic.Icon == HoverIcon.None)
            return column;
        var row = new DockPanel();
        Control icon =
            diagnostic.Icon == HoverIcon.Shown
                ? new SeverityIcon(diagnostic.Severity, look.Colors)
                : new Control { Width = SeverityIconSize, Height = SeverityIconSize };
        icon.Margin = new Thickness(0, 1, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(column);
        return row;

        Run Add(string text, IBrush foreground)
        {
            var run = new Run(text) { Foreground = foreground };
            inlines.Add(run);
            length += text.Length;
            return run;
        }
    }

    /// <summary>
    /// The heading of the problem that a key went to, as the title of VS Code's widget for it shows it: the icon 4
    /// pixels from the document's name, and which problem it is of how many, dimmed.
    /// </summary>
    private static Control Heading(HoverHeading heading, PopupLook look)
    {
        var icon = new SeverityIcon(heading.Severity, look.Colors)
        {
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(icon, Dock.Left);
        SelectableTextBlock text = Text(look);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Inlines = heading.Name is { } name
            ? [new Run(name), new Run("  " + heading.Count) { Foreground = look.Colors.Description }]
            : [new Run(heading.Count) { Foreground = look.Colors.Description }];
        return new DockPanel { Children = { icon, text } };
    }

    /// <summary>The size of a severity's icon.</summary>
    private const double SeverityIconSize = 16;

    /// <summary>Selectable text in the editor's font and size.</summary>
    private static SelectableTextBlock CodeText(PopupLook look)
    {
        SelectableTextBlock text = Text(look);
        text.FontFamily = look.CodeFont;
        text.FontSize = look.CodeSize;
        return text;
    }

    /// <summary><paramref name="brush"/> at VS Code's 60% opacity of a hover's dimmed parts.</summary>
    private static IBrush Dimmed(IBrush brush) =>
        brush is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, solid.Opacity * 0.6) : brush;

    /// <summary>
    /// Opens a link that is clicked without selecting, such as a web page through the app's launcher, and shows the
    /// hand over links.
    /// </summary>
    private static void OpenLinks(SelectableTextBlock text, List<(int Start, int End, Action<TopLevel> Open)> links)
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

/// <summary>How a popup looks: code as in the editor, everything else in the popup colors of its color theme.</summary>
/// <param name="CodeFont">The editor's font.</param>
/// <param name="CodeSize">The editor's font size.</param>
/// <param name="Colors">The popups' colors.</param>
internal sealed record PopupLook(FontFamily CodeFont, double CodeSize, PopupColors Colors);

/// <summary>
/// The controls of a hover's blocks, all of them, built in parts so that each takes a frame at most, as
/// <see cref="HoverParts"/> measures them: the first part as it is made, and each next part in the next frame while it
/// shows, where a block larger than a part goes in pieces. <see cref="GrewEvent"/> tells the popup around it after
/// each part, so that it places itself again.
/// </summary>
internal sealed class HoverContent : StackPanel
{
    /// <summary>Raised, bubbling, after a part was added in a later frame: the content grew.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> GrewEvent = RoutedEvent.Register<HoverContent, RoutedEventArgs>(
        "Grew",
        RoutingStrategies.Bubble
    );

    private readonly IReadOnlyList<HoverBlock> _blocks;
    private readonly PopupLook _look;

    /// <summary>The index of the next block to build.</summary>
    private int _next;

    /// <summary>The pieces of the block being built, after the ones built; null between blocks.</summary>
    private IEnumerator<HoverBlock>? _pieces;

    /// <summary>A piece that did not fit the last part, to build first in the next.</summary>
    private HoverBlock? _pending;

    /// <summary>Whether the next part is asked for, for the next frame.</summary>
    private bool _requested;

    public HoverContent(IReadOnlyList<HoverBlock> blocks, PopupLook look)
    {
        _blocks = blocks;
        _look = look;
        AddPart();
    }

    /// <summary>Whether all of the blocks are built.</summary>
    public bool IsComplete => _pending is null && _pieces is null && _next >= _blocks.Count;

    /// <summary>Builds the next part now, for tests and for the benchmarks.</summary>
    internal void AddNextPart()
    {
        AddPart();
        RaiseEvent(new RoutedEventArgs(GrewEvent));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestPart();
    }

    /// <summary>Asks for the next part in the next frame, unless all is built, it is asked for, or nothing shows.</summary>
    private void RequestPart()
    {
        if (IsComplete || _requested || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        _requested = true;
        topLevel.RequestAnimationFrame(_ =>
        {
            _requested = false;
            // No longer shown, as when the hover closed: the rest is never built.
            if (TopLevel.GetTopLevel(this) is null)
                return;
            AddNextPart();
            RequestPart();
        });
    }

    /// <summary>Adds the controls of the next blocks and pieces, as many as fit a part, at least one.</summary>
    private void AddPart()
    {
        int characters = 0;
        int lines = 0;
        int runs = 0;
        while (NextPiece() is { } piece)
        {
            (int pieceCharacters, int pieceLines, int pieceRuns) = HoverParts.Size(piece);
            // At least one piece a part, as each fits one.
            if (
                (characters, lines, runs) != (0, 0, 0)
                && !HoverParts.Fits((characters + pieceCharacters, lines + pieceLines, runs + pieceRuns))
            )
            {
                _pending = piece;
                return;
            }
            characters += pieceCharacters;
            lines += pieceLines;
            runs += pieceRuns;
            Control control = HoverView.Block(piece, _look);
            if (Children.Count > 0 && !piece.Continues)
            {
                Thickness margin = control.Margin;
                control.Margin = new Thickness(
                    margin.Left,
                    margin.Top + HoverView.Spacing,
                    margin.Right,
                    margin.Bottom
                );
            }
            Children.Add(control);
        }
    }

    /// <summary>The next piece to build: one that did not fit the last part, the next of a block, or the next block's first.</summary>
    private HoverBlock? NextPiece()
    {
        if (_pending is { } pending)
        {
            _pending = null;
            return pending;
        }
        while (true)
        {
            if (_pieces is { } pieces)
            {
                if (pieces.MoveNext())
                    return pieces.Current;
                pieces.Dispose();
                _pieces = null;
            }
            if (_next >= _blocks.Count)
                return null;
            _pieces = HoverParts.Split(_blocks[_next++]).GetEnumerator();
        }
    }
}
