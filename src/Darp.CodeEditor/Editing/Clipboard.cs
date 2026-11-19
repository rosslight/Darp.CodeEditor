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
/// Copy, cut, paste and drop in one editor's view of a document, with the document's line breaks. AvaloniaEdit copies
/// with the system's line break, and pastes and drops with that of the line the text goes in, with no seam to give it
/// another. Its copy, cut and drop run as they are, with their events, and the text they put out is corrected after
/// them: the clipboard's to <see cref="LineBreaks.OnClipboard"/>, and the dropped text to the document's, in the drop's
/// undo step. Paste is the editor's own, because AvaloniaEdit's opens its undo step before it waits for the clipboard
/// and closes it on whatever document the editor shows by then; so it raises no <c>TextPasted</c>.
/// </summary>
internal sealed class Clipboard : IDisposable
{
    private readonly TextArea _textArea;
    private readonly CodeDocument _document;
    private readonly RoutedCommandBinding _paste;
    private bool _disposed;

    public Clipboard(TextArea textArea, CodeDocument document)
    {
        _textArea = textArea;
        _document = document;
        _paste = new RoutedCommandBinding(ApplicationCommands.Paste, (_, _) => Paste());
        // First, as the first binding of a command runs, and AvaloniaEdit's came with the text area.
        textArea.CommandBindings.Insert(0, _paste);
        textArea.TextCopied += OnTextCopied;
        // After AvaloniaEdit's handler, which the text area has from its start.
        textArea.AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private TextDocument Buffer => _textArea.Document;

    public void Cut() => ApplicationCommands.Cut.Execute(null, _textArea);

    public void Copy() => ApplicationCommands.Copy.Execute(null, _textArea);

    /// <summary>
    /// Pastes the clipboard's text over the selection in one undo step, once the clipboard has given it, and only into
    /// the document it was asked for in, while the editor still shows it and text can go in at the caret, as AvaloniaEdit
    /// checks. As in AvaloniaEdit, a document that indents with spaces gets spaces for tabs, and a rectangle takes a line
    /// of the text on each of its rows; the line breaks become the document's before that.
    /// </summary>
    public async void Paste()
    {
        if (!CanPaste() || TopLevel.GetTopLevel(_textArea)?.Clipboard is not { } clipboard)
            return;
        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Warning, AssetFiles.LogArea)
                ?.Log(this, "Reading the clipboard failed: {Exception}", e);
            return;
        }
        if (_disposed || !CanPaste() || string.IsNullOrEmpty(text))
            return;
        text = TextUtilities.NormalizeNewLines(text, _document.LineBreak);
        TextEditorOptions options = _textArea.Options;
        if (options.ConvertTabsToSpaces)
            text = text.Replace("\t", new string(' ', options.IndentationSize), StringComparison.Ordinal);
        using (Buffer.RunUpdate())
            _textArea.Selection.ReplaceSelectionWithText(text);
        _textArea.Caret.BringCaretToView();
    }

    private bool CanPaste() => _textArea.ReadOnlySectionProvider.CanInsert(_textArea.Caret.Offset);

    public void Dispose()
    {
        _disposed = true;
        _textArea.CommandBindings.Remove(_paste);
        _textArea.TextCopied -= OnTextCopied;
        _textArea.RemoveHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnTextCopied(object? sender, TextEventArgs e)
    {
        string copied = TextUtilities.NormalizeNewLines(e.Text, LineBreaks.OnClipboard(_document.LineBreak));
        if (copied != e.Text)
            SetClipboard(copied);
    }

    /// <summary>
    /// Gives the text that AvaloniaEdit's drop put in the document's line breaks, in the drop's undo step and with its
    /// descriptor, so that a drag's move continues it. The drop selects the text it put in, unless it put it in as a
    /// rectangle.
    /// </summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        // The drop's change may have shown another document meanwhile, as may the change here.
        if (
            _disposed
            || e.DragEffects == DragDropEffects.None
            || _textArea.Selection is not SimpleSelection { IsEmpty: false } dropped
        )
            return;
        ISegment segment = dropped.SurroundingSegment;
        string text = Buffer.GetText(segment);
        string corrected = TextUtilities.NormalizeNewLines(text, _document.LineBreak);
        if (corrected == text)
            return;
        UndoStack undo = Buffer.UndoStack;
        undo.StartContinuedUndoGroup(undo.LastGroupDescriptor);
        try
        {
            Buffer.Replace(segment.Offset, segment.Length, corrected);
        }
        finally
        {
            undo.EndUndoGroup();
        }
        if (!_disposed)
            _textArea.Selection = Selection.Create(_textArea, segment.Offset, segment.Offset + corrected.Length);
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
