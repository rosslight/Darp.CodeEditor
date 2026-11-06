using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Draws a box around the bracket next to the caret and around its match, in the color theme's bracket match colors,
/// as VS Code does.
/// </summary>
/// <remarks>
/// The pair is found when the text view draws, after the caret moved, the text changed, the lines were drawn again or
/// the background changed lines' tokens, also out of view: once per frame, however many changes came before it.
/// </remarks>
internal sealed class BracketHighlighter : IBackgroundRenderer, IDisposable
{
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private TextAreaColors? _colors;
    private (int Open, int Close)? _pair;

    /// <summary>Whether the caret, the text or the lines' tokens changed since the pair was found.</summary>
    private bool _stale = true;

    public BracketHighlighter(TextArea textArea, CodeDocument document, BackgroundTextMateHighlighter highlighter)
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        _highlighter = highlighter;
        textArea.Caret.PositionChanged += OnChanged;
        _buffer.TextChanged += OnChanged;
        textArea.TextView.VisualLinesChanged += OnVisualLinesChanged;
        highlighter.TokensChanged += OnChanged;
        textArea.TextView.BackgroundRenderers.Add(this);
    }

    /// <summary>Behind the selection, in the layer that the text view draws itself whenever a layer changes.</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            _textArea.TextView.InvalidateLayer(Layer);
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_stale)
        {
            _stale = false;
            _pair = BracketMatcher.FindPair(_buffer, _textArea.Caret.Offset, Brackets, _highlighter.TokensOf);
        }
        if (_pair is not { } pair || _colors is not { } colors)
            return;
        var builder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            BorderThickness = colors.BracketMatchBorder.Thickness,
        };
        builder.AddSegment(textView, new TextSegment { StartOffset = pair.Open, Length = 1 });
        // Two boxes, also for brackets side by side.
        builder.CloseFigure();
        builder.AddSegment(textView, new TextSegment { StartOffset = pair.Close, Length = 1 });
        if (builder.CreateGeometry() is { } geometry)
            drawingContext.DrawGeometry(colors.BracketMatchBackground, colors.BracketMatchBorder, geometry);
    }

    public void Dispose()
    {
        _textArea.Caret.PositionChanged -= OnChanged;
        _buffer.TextChanged -= OnChanged;
        _textArea.TextView.VisualLinesChanged -= OnVisualLinesChanged;
        _highlighter.TokensChanged -= OnChanged;
        _textArea.TextView.BackgroundRenderers.Remove(this);
    }

    /// <summary>The lines were built again, with new tokens maybe: the frame that follows finds the pair again.</summary>
    private void OnVisualLinesChanged(object? sender, EventArgs e) => _stale = true;

    /// <summary>Draws again where a pair shows or may show now: next to a bracket, or instead of the last pair.</summary>
    private void OnChanged(object? sender, EventArgs e)
    {
        _stale = true;
        int caret = _textArea.Caret.Offset;
        if (_pair is not null || IsBracketCharacter(caret) || IsBracketCharacter(caret - 1))
            _textArea.TextView.InvalidateLayer(Layer);
    }

    /// <summary>The language's brackets; none while they load.</summary>
    private IReadOnlyList<CharacterPair> Brackets =>
        _document.Language.Loaded is { IsCompletedSuccessfully: true } loaded
            ? loaded.Result.Configuration.Brackets
            : [];

    private bool IsBracketCharacter(int offset)
    {
        if (offset < 0 || offset >= _buffer.TextLength)
            return false;
        char c = _buffer.GetCharAt(offset);
        return Brackets.Any(pair => pair is { Open: [char open], Close: [char close] } && (c == open || c == close));
    }
}
