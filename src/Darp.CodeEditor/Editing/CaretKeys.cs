using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The arrow keys and Home where VS Code's <c>cursorLeft</c>, <c>cursorRight</c>, <c>cursorUp</c>, <c>cursorDown</c>
/// and <c>cursorHome</c> move the caret otherwise than AvaloniaEdit: Left and Right end a selection at
/// its start or end; Up and Down end it and go on from its start or end; Up on the text's first row goes to its start
/// and Down on its last row to its end; Home on a line of whitespace only goes to the line's start. Shift selects with
/// Up, Down and Home. Elsewhere, and in a rectangle selection, AvaloniaEdit moves the caret.
/// </summary>
internal static class CaretKeys
{
    /// <summary>A key, while the text has the keyboard, once the open popups and typing left it.</summary>
    /// <returns>Whether the key moved the caret; otherwise AvaloniaEdit takes it.</returns>
    public static bool OnKeyDown(TextArea area, KeyEventArgs e)
    {
        if (area.Selection is RectangleSelection)
            return false;
        return (e.Key, e.KeyModifiers) switch
        {
            (Key.Left or Key.Right, KeyModifiers.None) => Collapse(area, toStart: e.Key == Key.Left),
            (Key.Up or Key.Down, KeyModifiers.None or KeyModifiers.Shift) => MoveVertically(
                area,
                up: e.Key == Key.Up,
                select: e.KeyModifiers == KeyModifiers.Shift
            ),
            _ => LineStartKey(e) is { } select && ToBlankLineStart(area, select),
        };
    }

    /// <summary>Ends the selection, with the caret at its start or its end.</summary>
    /// <returns>Whether there was a selection.</returns>
    private static bool Collapse(TextArea area, bool toStart)
    {
        if (area.Selection.IsEmpty)
            return false;
        ISegment selected = area.Selection.SurroundingSegment;
        area.ClearSelection();
        // Setting the caret also forgets the column that moves up and down keep.
        area.Caret.Offset = toStart ? selected.Offset : selected.EndOffset;
        area.Caret.BringCaretToView();
        return true;
    }

    /// <summary>
    /// Up and Down: without Shift, a selection ends first, at its start for Up and its end for Down. On the text's
    /// first or last row, the caret goes to the text's start or end, and the column it came from stays the one that the
    /// next move goes back to, unless the caret was there already.
    /// </summary>
    /// <returns>Whether the caret went to the text's start or end; otherwise AvaloniaEdit moves it a row.</returns>
    private static bool MoveVertically(TextArea area, bool up, bool select)
    {
        if (!select)
            Collapse(area, toStart: up);
        if (!IsOnEdgeRow(area, up))
            return false;
        Caret caret = area.Caret;
        int target = up ? 0 : area.Document.TextLength;
        double column =
            caret.Offset == target ? double.NaN
            : double.IsNaN(caret.DesiredXPos)
                ? area.TextView.GetVisualPosition(caret.Position, VisualYPosition.LineTop).X
            : caret.DesiredXPos;
        MoveCaret(area, target, select);
        caret.DesiredXPos = column;
        return true;
    }

    /// <summary>
    /// Whether the caret is on the text's first row, going <paramref name="up"/>, or on its last: the first or the
    /// last row of a line that wraps.
    /// </summary>
    private static bool IsOnEdgeRow(TextArea area, bool up)
    {
        Caret caret = area.Caret;
        if (caret.Line != (up ? 1 : area.Document.LineCount))
            return false;
        VisualLine line = area.TextView.GetOrConstructVisualLine(area.Document.GetLineByNumber(caret.Line));
        int row = line.TextLines.IndexOf(line.GetTextLine(caret.VisualColumn, caret.Position.IsAtEndOfLine));
        return up ? row <= 0 : row < 0 || row == line.TextLines.Count - 1;
    }

    /// <summary>
    /// Whether <paramref name="e"/> is the platform's key for the line's start: false for Home, true for Shift+Home,
    /// which selects; null for another key.
    /// </summary>
    private static bool? LineStartKey(KeyEventArgs e)
    {
        PlatformHotkeyConfiguration? keys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (keys is null)
            return null;
        if (keys.MoveCursorToTheStartOfLine.Exists(gesture => gesture.Matches(e)))
            return false;
        if (keys.MoveCursorToTheStartOfLineWithSelection.Exists(gesture => gesture.Matches(e)))
            return true;
        return null;
    }

    /// <summary>
    /// Home on a line of whitespace only: to its start, and there it stays, where AvaloniaEdit goes to its end, as past
    /// the whitespace that indents text.
    /// </summary>
    /// <returns>Whether the caret's line is of whitespace only.</returns>
    private static bool ToBlankLineStart(TextArea area, bool select)
    {
        DocumentLine line = area.Document.GetLineByNumber(area.Caret.Line);
        if (line.Length == 0 || IndentRules.Leading(area.Document.GetText(line)) < line.Length)
            return false;
        MoveCaret(area, line.Offset, select);
        return true;
    }

    /// <summary>Moves the caret to <paramref name="offset"/>, ending the selection, or extending it if <paramref name="select"/>.</summary>
    private static void MoveCaret(TextArea area, int offset, bool select)
    {
        TextViewPosition from = area.Caret.Position;
        if (!select)
            area.ClearSelection();
        area.Caret.Offset = offset;
        if (select)
            area.Selection = area.Selection.StartSelectionOrSetEndpoint(from, area.Caret.Position);
        area.Caret.BringCaretToView();
    }
}
