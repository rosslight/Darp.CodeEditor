using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Logging;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// AvaloniaEdit's copy, cut, paste and drop in one editor's view of a document, with the document's line breaks.
/// AvaloniaEdit copies with the system's line break, and pastes and drops with that of the line the text goes in, and
/// has no seam to give it another. So its commands run as they are, with their events and their own conversions, such
/// as tabs that paste as spaces, and the text they put out is corrected after them: the clipboard's to
/// <see cref="LineBreaks.OnClipboard"/>, and the text that went in to the document's, in the undo step it went in with.
/// </summary>
internal sealed class Clipboard : IDisposable
{
    private readonly TextArea _textArea;
    private readonly CodeDocument _document;

    public Clipboard(TextArea textArea, CodeDocument document)
    {
        _textArea = textArea;
        _document = document;
        textArea.TextCopied += OnTextCopied;
        textArea.TextPasted += OnTextPasted;
        // After AvaloniaEdit's handler, which the text area has from its start.
        textArea.AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private TextDocument Buffer => _textArea.Document;

    public void Cut() => ApplicationCommands.Cut.Execute(null, _textArea);

    public void Copy() => ApplicationCommands.Copy.Execute(null, _textArea);

    public void Paste() => ApplicationCommands.Paste.Execute(null, _textArea);

    public void Dispose()
    {
        _textArea.TextCopied -= OnTextCopied;
        _textArea.TextPasted -= OnTextPasted;
        _textArea.RemoveHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnTextCopied(object? sender, TextEventArgs e)
    {
        string copied = TextUtilities.NormalizeNewLines(e.Text, LineBreaks.OnClipboard(_document.LineBreak));
        if (copied != e.Text)
            SetClipboard(copied);
    }

    /// <summary>AvaloniaEdit's paste leaves the caret after the text it put in.</summary>
    private void OnTextPasted(object? sender, TextEventArgs e)
    {
        int start = _textArea.Caret.Offset - e.Text.Length;
        if (Correct(start, e.Text) is { } length)
            _textArea.Caret.Offset = start + length;
    }

    /// <summary>AvaloniaEdit's drop selects the text it put in, unless it put it in as a rectangle.</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (
            e.DragEffects == DragDropEffects.None
            || _textArea.Selection is not SimpleSelection { IsEmpty: false } dropped
        )
            return;
        ISegment segment = dropped.SurroundingSegment;
        if (Correct(segment.Offset, Buffer.GetText(segment)) is { } length)
            _textArea.Selection = Selection.Create(_textArea, segment.Offset, segment.Offset + length);
    }

    /// <summary>
    /// Gives <paramref name="inserted"/>, the text that went in at <paramref name="start"/>, the document's line breaks,
    /// in the last undo step and with its descriptor, so that a drag's move continues it.
    /// </summary>
    /// <returns>The corrected text's length, or null when it needed none, or did not go in there.</returns>
    private int? Correct(int start, string inserted)
    {
        string corrected = TextUtilities.NormalizeNewLines(inserted, _document.LineBreak);
        if (
            corrected == inserted
            || start < 0
            || start + inserted.Length > Buffer.TextLength
            || Buffer.GetText(start, inserted.Length) != inserted
        )
            return null;
        UndoStack undo = Buffer.UndoStack;
        undo.StartContinuedUndoGroup(undo.LastGroupDescriptor);
        try
        {
            Buffer.Replace(start, inserted.Length, corrected);
        }
        finally
        {
            undo.EndUndoGroup();
        }
        return corrected.Length;
    }

    /// <summary>The platform's clipboard fails now and then; the text is then not copied, as in AvaloniaEdit.</summary>
    private async void SetClipboard(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(_textArea)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Warning, AssetFiles.LogArea)
                ?.Log(this, "Writing to the clipboard failed: {Exception}", e);
        }
    }
}
