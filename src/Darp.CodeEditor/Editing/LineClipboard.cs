using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Logging;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Copy, cut and paste without a selection in one editor's view, as VS Code's with <c>editor.emptySelectionClipboard</c>
/// (<see cref="CodeEditorOptions.EmptySelectionClipboard"/>): copy and cut take the caret's line with a line break, the
/// last line too, and a paste of that line, while nothing is selected, goes in above the caret's line and keeps the
/// caret on its own. It runs ahead of AvaloniaEdit's commands and of <see cref="Clipboard"/>, and leaves them the rest.
/// </summary>
/// <remarks>
/// AvaloniaEdit's own copy of the caret's line, <c>CutCopyWholeLine</c>, which the editor turns off, takes the last line
/// without a line break, and an empty last line not at all.
/// </remarks>
internal sealed class LineClipboard : IDisposable
{
    /// <summary>
    /// The line that an editor of the app last put on the clipboard, while no copy of a selection came after it: a paste
    /// of the same text goes in as a line. On the app's UI thread only.
    /// </summary>
    private static string? s_copiedLine;

    private readonly TextArea _area;
    private readonly CodeDocument _document;
    private readonly TextChanges _changes;
    private readonly Func<CodeEditorOptions> _options;
    private readonly RoutedCommandBinding[] _bindings;
    private bool _disposed;

    public LineClipboard(TextArea area, CodeDocument document, TextChanges changes, Func<CodeEditorOptions> options)
    {
        _area = area;
        _document = document;
        _changes = changes;
        _options = options;
        _bindings =
        [
            new RoutedCommandBinding(ApplicationCommands.Copy, (_, _) => Copy(), (_, e) => e.CanExecute = IsLineCopy()),
            new RoutedCommandBinding(ApplicationCommands.Cut, (_, _) => Cut(), (_, e) => e.CanExecute = IsLineCut()),
            new RoutedCommandBinding(
                ApplicationCommands.Paste,
                (_, _) => Paste(),
                (_, e) => e.CanExecute = MayPasteLine()
            ),
        ];
        // First, as the first binding of a command that can execute runs, ahead of AvaloniaEdit's and the clipboard's.
        foreach (RoutedCommandBinding binding in _bindings)
            area.CommandBindings.Insert(0, binding);
        area.TextCopied += OnTextCopied;
    }

    private TextDocument Buffer => _area.Document;

    public void Dispose()
    {
        _disposed = true;
        foreach (RoutedCommandBinding binding in _bindings)
            _area.CommandBindings.Remove(binding);
        _area.TextCopied -= OnTextCopied;
    }

    private bool IsLineCopy() =>
        _options().EmptySelectionClipboard && _area.Selection is { IsEmpty: true } and not RectangleSelection;

    private bool IsLineCut() => IsLineCopy() && !_area.IsReadOnly;

    private bool MayPasteLine() => s_copiedLine is not null && IsLineCut() && CanInsertAtCaret();

    private bool CanInsertAtCaret() => _area.ReadOnlySectionProvider.CanInsert(_area.Caret.Offset);

    private void Copy() => SetClipboard(CaretLine());

    /// <summary>
    /// The caret's line with its line break, or for the last line the line break before it, as the only line without
    /// one after it, in a step of its own; the caret goes to where it was.
    /// </summary>
    private void Cut()
    {
        string line = CaretLine();
        DocumentLine caretLine = Buffer.GetLineByOffset(_area.Caret.Offset);
        TextRange cut =
            caretLine.NextLine is { } next ? TextRange.FromBounds(caretLine.Offset, next.Offset)
            : caretLine.PreviousLine is { } previous ? TextRange.FromBounds(previous.EndOffset, caretLine.EndOffset)
            : TextRange.FromBounds(caretLine.Offset, caretLine.EndOffset);
        SetClipboard(line);
        _changes.Run(
            EditStep.Own,
            () => _changes.Apply([new TextEdit(cut.Start, cut.Length, "")], new TextSelection(cut.Start))
        );
    }

    /// <summary>
    /// Pastes the clipboard's text above the caret's line, in a step of its own, when it is the line copied last, with
    /// the document's line breaks; any other text goes on to the next paste, which reads the clipboard anew.
    /// </summary>
    private async void Paste()
    {
        string? line = s_copiedLine;
        string? text;
        try
        {
            text = TopLevel.GetTopLevel(_area)?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Warning, AssetFiles.LogArea)
                ?.Log(this, "Reading the clipboard failed: {Exception}", e);
            return;
        }
        if (_disposed)
            return;
        if (text is null || text != line)
        {
            s_copiedLine = null;
            ApplicationCommands.Paste.Execute(null, _area);
            return;
        }
        if (!MayPasteLine())
            return;
        int lineStart = Buffer.GetLineByOffset(_area.Caret.Offset).Offset;
        string pasted = TextUtilities.NormalizeNewLines(text, _document.LineBreak);
        _changes.Run(EditStep.Own, () => _changes.Apply([new TextEdit(lineStart, 0, pasted)]));
        _area.Caret.BringCaretToView();
    }

    /// <summary>The caret's line and a line break after it, in the clipboard's line breaks.</summary>
    private string CaretLine() =>
        Buffer.GetText(Buffer.GetLineByOffset(_area.Caret.Offset)) + LineBreaks.OnClipboard(_document.LineBreak);

    /// <summary>AvaloniaEdit's copy of a selection: the clipboard holds no line now.</summary>
    private static void OnTextCopied(object? sender, TextEventArgs e) => s_copiedLine = null;

    /// <summary>The platform's clipboard fails now and then; the line is then not copied, as in AvaloniaEdit.</summary>
    private async void SetClipboard(string line)
    {
        s_copiedLine = line;
        try
        {
            if (TopLevel.GetTopLevel(_area)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(line);
        }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Warning, AssetFiles.LogArea)
                ?.Log(this, "Writing to the clipboard failed: {Exception}", e);
        }
    }
}
