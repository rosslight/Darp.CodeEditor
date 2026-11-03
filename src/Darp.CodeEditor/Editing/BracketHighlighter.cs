using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Theming;

namespace Darp.CodeEditor.Editing;

/// <summary>Marks the bracket next to the caret and its match, in the theme's bracket match colors.</summary>
internal sealed class BracketHighlighter : IBackgroundRenderer, IThemedRenderer
{
    private readonly CodeEditor _editor;

    public BracketHighlighter(CodeEditor editor)
    {
        _editor = editor;
        editor.TextArea.Caret.PositionChanged += (_, _) => Update();
        editor.DocumentChanged += (_, _) => Update();
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public EditorTheme EditorTheme { private get; set; } = EditorTheme.Default;

    /// <summary>The offsets of the marked brackets, or null.</summary>
    public (int Open, int Close)? Pair { get; private set; }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Pair is not { } pair || !textView.VisualLinesValid)
            return;
        foreach (int offset in (ReadOnlySpan<int>)[pair.Open, pair.Close])
        {
            var segment = new TextSegment { StartOffset = offset, Length = 1 };
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                drawingContext.DrawRectangle(EditorTheme.BracketMatchBackground, EditorTheme.BracketMatchBorder, rect);
        }
    }

    private void Update()
    {
        (int Open, int Close)? pair = BracketMatcher.FindPairAt(
            _editor.Document,
            _editor.TextArea.Caret.Offset,
            _editor.LanguageConfiguration.Brackets,
            _editor.TokenKindAt
        );
        if (pair == Pair)
            return;
        Pair = pair;
        _editor.TextArea.TextView.InvalidateLayer(Layer);
    }
}
