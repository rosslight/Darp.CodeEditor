namespace Darp.CodeEditor.Completion;

/// <summary>
/// Page Down and Page Up in completion's list, as VS Code's lists page (<c>focusNextPage</c>, <c>focusPreviousPage</c>):
/// the first press selects the last or the first of the rows that show, and the next one goes a page of rows on.
/// </summary>
internal static class CompletionPaging
{
    /// <summary>The item that Page Down selects.</summary>
    /// <param name="selected">The selected item's index.</param>
    /// <param name="first">The index of the item in the first row that shows.</param>
    /// <param name="rows">How many rows show.</param>
    /// <param name="count">How many items the list has.</param>
    public static int Down(int selected, int first, int rows, int count)
    {
        int last = Math.Min(first + rows, count) - 1;
        return selected < last ? last : Math.Min(count - 1, selected + rows);
    }

    /// <summary>The item that Page Up selects.</summary>
    /// <param name="selected">The selected item's index.</param>
    /// <param name="first">The index of the item in the first row that shows.</param>
    /// <param name="rows">How many rows show.</param>
    public static int Up(int selected, int first, int rows) => selected > first ? first : Math.Max(0, selected - rows);
}
