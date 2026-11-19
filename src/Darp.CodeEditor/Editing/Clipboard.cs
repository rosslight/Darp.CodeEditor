using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Logging;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Copy, cut and paste in one editor's view of a document, in place of AvaloniaEdit's, for their line breaks: copied
/// text takes the clipboard's (<see cref="LineBreaks.OnClipboard"/>), and pasted text the document's. As in
/// AvaloniaEdit, copy and cut without a selection take the caret's line with its line break.
/// </summary>
internal sealed class Clipboard : IDisposable
{
    private readonly TextArea _textArea;
    private readonly CodeDocument _document;
    private readonly RoutedCommandBinding[] _bindings;
    private bool _disposed;

    public Clipboard(TextArea textArea, CodeDocument document)
    {
        _textArea = textArea;
        _document = document;
        _bindings =
        [
            new(ApplicationCommands.Copy, (_, _) => Copy()),
            new(ApplicationCommands.Cut, (_, _) => Cut()),
            new(ApplicationCommands.Paste, (_, _) => Paste()),
        ];
        // First, as the first binding of a command runs, and AvaloniaEdit's came with the text area.
        for (int i = 0; i < _bindings.Length; i++)
            textArea.CommandBindings.Insert(i, _bindings[i]);
    }

    private TextDocument Buffer => _textArea.Document;

    public void Dispose()
    {
        _disposed = true;
        foreach (RoutedCommandBinding binding in _bindings)
            _textArea.CommandBindings.Remove(binding);
    }

    private void Copy()
    {
        if (Copied() is { } copied)
            SetClipboard(copied);
    }

    private void Cut()
    {
        if (_textArea.IsReadOnly || Copied() is not { } copied)
            return;
        SetClipboard(copied);
        Selection cut = _textArea.Selection;
        if (cut.IsEmpty)
        {
            DocumentLine line = Buffer.GetLineByOffset(_textArea.Caret.Offset);
            cut = Selection.Create(_textArea, line.Offset, line.Offset + line.TotalLength);
        }
        // Through a selection, which leaves read-only text and a long line's collapsed rest.
        cut.ReplaceSelectionWithText("");
        _textArea.Caret.BringCaretToView();
    }

    /// <summary>Pastes once the clipboard has given its text, in one step; nothing in a read-only editor.</summary>
    private async void Paste()
    {
        if (_textArea.IsReadOnly || TopLevel.GetTopLevel(_textArea)?.Clipboard is not { } clipboard)
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
        if (_disposed || _textArea.IsReadOnly || string.IsNullOrEmpty(text))
            return;
        using (Buffer.RunUpdate())
            _textArea.Selection.ReplaceSelectionWithText(TextUtilities.NormalizeNewLines(text, _document.LineBreak));
        _textArea.Caret.BringCaretToView();
    }

    /// <summary>The selection, or without one the caret's line with its line break; null for an empty last line.</summary>
    private string? Copied()
    {
        string text;
        if (_textArea.Selection.IsEmpty)
        {
            DocumentLine line = Buffer.GetLineByOffset(_textArea.Caret.Offset);
            text = Buffer.GetText(line.Offset, line.TotalLength);
        }
        else
            text = _textArea.Selection.GetText();
        return text.Length == 0
            ? null
            : TextUtilities.NormalizeNewLines(text, LineBreaks.OnClipboard(_document.LineBreak));
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
