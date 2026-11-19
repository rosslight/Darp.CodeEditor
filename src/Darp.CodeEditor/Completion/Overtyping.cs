using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// The selection that typing replaced last, as VS Code's overtyping capturer keeps it for a snippet's
/// <c>TM_SELECTED_TEXT</c> and <c>SELECTION</c>, as completion never commits over a selection: typing that replaces a
/// selection keeps it, and typing that replaces none forgets it, while completion is closed. Once typing opened
/// completion, typing on keeps what the first key replaced.
/// </summary>
internal sealed class Overtyping : IDisposable
{
    /// <summary>The longest selection that is kept: VS Code's limit.</summary>
    private const int MaxLength = 51_200;

    private readonly TextArea _textArea;
    private readonly Func<bool> _completing;

    /// <param name="textArea">The text area that the user types in.</param>
    /// <param name="completing">Whether completion is open, from its first request until it ends.</param>
    public Overtyping(TextArea textArea, Func<bool> completing)
    {
        _textArea = textArea;
        _completing = completing;
        _textArea.TextEntering += OnTextEntering;
    }

    /// <summary>The selection that typing replaced last; null for none.</summary>
    public SelectedText? Last { get; private set; }

    public void Dispose() => _textArea.TextEntering -= OnTextEntering;

    private void OnTextEntering(object? sender, EventArgs e)
    {
        if (_completing())
            return;
        Selection selection = _textArea.Selection;
        Last =
            selection.IsEmpty || selection.Length > MaxLength
                ? null
                : new SelectedText(selection.GetText(), selection.StartPosition.Line != selection.EndPosition.Line);
    }
}

/// <summary>Text that was selected.</summary>
/// <param name="Text">The text.</param>
/// <param name="IsMultiline">Whether the selection spanned lines.</param>
internal readonly record struct SelectedText(string Text, bool IsMultiline);
