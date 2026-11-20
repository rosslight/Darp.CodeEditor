using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The controls of a popup's blocks, all of them, built in parts so that each takes a frame at most, as
/// <see cref="HoverParts"/> measures them: the first part as it is made, and each next part in the next frame while it
/// shows, where a block larger than a part goes in pieces. A tail that follows the blocks, as the server's hover follows
/// the diagnostics, can be replaced while the blocks keep their controls. <see cref="GrewEvent"/> tells the popup around
/// it after each part, so that it places itself again.
/// </summary>
internal sealed class HoverContent : StackPanel
{
    /// <summary>Raised, bubbling, after a part was added in a later frame: the content grew.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> GrewEvent = RoutedEvent.Register<HoverContent, RoutedEventArgs>(
        "Grew",
        RoutingStrategies.Bubble
    );

    private readonly PopupLook _look;

    /// <summary>The blocks after the ones read, read as they are built, then the tail's; null once all are read.</summary>
    private IEnumerator<HoverBlock>? _blocks;

    private IEnumerable<HoverBlock> _tail;

    /// <summary>Where the tail's controls start among the children, once the blocks before it are read; null before.</summary>
    private int? _tailStart;

    /// <summary>The pieces of the block being built, after the ones built; null between blocks.</summary>
    private IEnumerator<HoverBlock>? _pieces;

    /// <summary>A piece that did not fit the last part, to build first in the next.</summary>
    private HoverBlock? _pending;

    private bool _requested;

    public HoverContent(IEnumerable<HoverBlock> blocks, PopupLook look, IEnumerable<HoverBlock>? tail = null)
    {
        // Set here, as on every panel of a popup, so that an app's styles for panels don't reach it.
        Spacing = HoverView.Spacing;
        Margin = default;
        _blocks = blocks.GetEnumerator();
        _tail = tail ?? [];
        _look = look;
        AddPart();
    }

    /// <summary>
    /// Makes <paramref name="tail"/> follow the blocks in place of the tail before: the blocks keep their controls, and
    /// the tail is built a part a frame after them.
    /// </summary>
    public void Follow(IEnumerable<HoverBlock> tail)
    {
        _tail = tail;
        if (_tailStart is not { } start)
            return;
        Children.RemoveRange(start, Children.Count - start);
        _pieces?.Dispose();
        _blocks?.Dispose();
        (_pending, _pieces, _blocks) = (null, null, tail.GetEnumerator());
        RequestPart();
    }

    protected override Type StyleKeyOverride => typeof(HoverContent);

    public bool IsComplete => _pending is null && _pieces is null && _blocks is null;

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
            // No longer shown, as when the popup closed: the rest is never built.
            if (TopLevel.GetTopLevel(this) is null)
                return;
            AddNextPart();
            RequestPart();
        });
    }

    /// <summary>Adds the controls of the next blocks and pieces, as many as fit a part, at least one.</summary>
    private void AddPart()
    {
        (int Characters, int Lines, int Runs) part = (0, 0, 0);
        while (NextPiece() is { } piece)
        {
            (int characters, int lines, int runs) = HoverParts.Size(piece);
            (int, int, int) with = (part.Characters + characters, part.Lines + lines, part.Runs + runs);
            // At least one piece a part, as each fits one.
            if (part != (0, 0, 0) && !HoverParts.Fits(with))
            {
                _pending = piece;
                return;
            }
            part = with;
            Control control = HoverView.Block(piece, _look);
            if (piece.Continues && Children.Count > 0)
            {
                Thickness margin = control.Margin;
                control.Margin = new Thickness(
                    margin.Left,
                    margin.Top - HoverView.Spacing,
                    margin.Right,
                    margin.Bottom
                );
            }
            Children.Add(control);
        }
    }

    /// <summary>The next piece: one that did not fit the last part, the next of a block, or the next block's first.</summary>
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
            if (_blocks is not { } blocks)
                return null;
            if (!blocks.MoveNext())
            {
                blocks.Dispose();
                _blocks = _tailStart is null ? _tail.GetEnumerator() : null;
                _tailStart ??= Children.Count;
                continue;
            }
            _pieces = HoverParts.Split(blocks.Current).GetEnumerator();
        }
    }
}
