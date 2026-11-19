namespace Darp.CodeEditor;

/// <summary>
/// What the search bar's query, or its replacement, recalls with Up and Down, as VS Code's find and replace history:
/// the entries the user searched for, the latest last, at most <see cref="Capacity"/>. The bar adds the query and the
/// replacement once the user has paused for half a second, as VS Code does. Set one as
/// <see cref="CodeEditor.FindHistory"/> or <see cref="CodeEditor.ReplaceHistory"/>.
/// </summary>
/// <remarks>
/// Editors that share a history share its entries, as VS Code's editors share theirs in a workspace
/// (<c>editor.find.history</c> and <c>editor.find.replaceHistory</c>). To keep them, as VS Code keeps them with the
/// workspace, save <see cref="Entries"/>, for example on <see cref="Changed"/>, and create the history with them the next
/// time. Use it on the app's main UI thread, as the editors do.
/// </remarks>
public sealed class SearchHistory
{
    /// <summary>The most entries a history keeps, as VS Code's: adding another drops the oldest.</summary>
    public const int Capacity = 100;

    private readonly List<string> _entries = [];

    /// <summary>Creates an empty history.</summary>
    public SearchHistory() { }

    /// <summary>
    /// Creates a history of <paramref name="entries"/>, the latest last, such as those saved from another: an entry
    /// that comes again moves to the later place, and only the latest <see cref="Capacity"/> stay.
    /// </summary>
    public SearchHistory(IEnumerable<string> entries)
    {
        foreach (string entry in entries)
            Insert(entry);
    }

    /// <summary>Raised when an entry was added or the history was cleared.</summary>
    public event EventHandler? Changed;

    /// <summary>The entries, the latest last; none of them empty or the same as another.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Counts the changes, so that the bars that go through the entries start again after one.</summary>
    internal int Version { get; private set; }

    /// <summary>
    /// Adds <paramref name="entry"/> as the latest, or moves it there if the history has it; drops the oldest beyond
    /// <see cref="Capacity"/>. An empty entry adds nothing.
    /// </summary>
    public void Add(string entry)
    {
        if (entry.Length == 0)
            return;
        Insert(entry);
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes every entry.</summary>
    public void Clear()
    {
        _entries.Clear();
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Insert(string entry)
    {
        if (entry.Length == 0)
            return;
        _entries.Remove(entry);
        _entries.Add(entry);
        if (_entries.Count > Capacity)
            _entries.RemoveRange(0, _entries.Count - Capacity);
    }
}

/// <summary>
/// Where a text box of the search bar is in its history, as Up and Down go through it: a port of VS Code's
/// <c>HistoryInputBox</c> and its <c>HistoryNavigator</c>. A change of the history, also by another editor that shares
/// it, starts again past the latest entry.
/// </summary>
internal sealed class SearchHistoryCursor
{
    private SearchHistory? _history;
    private int _version;

    /// <summary>The entry shown; the entries' count past the latest, and -1 before the first.</summary>
    private int _index;

    /// <summary>
    /// Adds <paramref name="value"/> to <paramref name="history"/> unless it is empty or the entry shown, as VS Code
    /// adds the text after a pause.
    /// </summary>
    public void Add(SearchHistory history, string value)
    {
        if (value.Length > 0 && value != CurrentValue(history))
            history.Add(value);
    }

    /// <summary>What Up shows instead of <paramref name="value"/>: the entry before; null to keep it.</summary>
    public string? Previous(SearchHistory history, string value)
    {
        if (!Entries(history).Contains(value))
            Add(history, value);
        string? previous = PreviousEntry(history);
        if (previous is not null && previous == value)
            previous = PreviousEntry(history);
        return previous;
    }

    /// <summary>What Down shows instead of <paramref name="value"/>: the entry after, or nothing past the latest.</summary>
    public string Next(SearchHistory history, string value)
    {
        if (!Entries(history).Contains(value))
            Add(history, value);
        string? next = NextEntry(history);
        if (next is not null && next == value)
            next = NextEntry(history);
        return next ?? "";
    }

    /// <summary>The entries, from past the latest again if the history is another or changed.</summary>
    private IReadOnlyList<string> Entries(SearchHistory history)
    {
        if (history != _history || history.Version != _version)
        {
            _history = history;
            _version = history.Version;
            _index = history.Entries.Count;
        }
        return history.Entries;
    }

    private string? Current(IReadOnlyList<string> entries) =>
        _index >= 0 && _index < entries.Count ? entries[_index] : null;

    /// <summary>The entry shown; past the latest, the latest, as VS Code compares the text with it.</summary>
    private string? CurrentValue(SearchHistory history)
    {
        IReadOnlyList<string> entries = Entries(history);
        string? current = Current(entries);
        if (current is null)
        {
            current = entries.Count > 0 ? entries[^1] : null;
            _index = entries.Count;
        }
        return current;
    }

    /// <summary>The entry before the one shown, or the first, as VS Code's <c>previous() || first()</c>.</summary>
    private string? PreviousEntry(SearchHistory history)
    {
        IReadOnlyList<string> entries = Entries(history);
        if (_index != 0 || Current(entries) is null)
        {
            _index = Math.Max(_index - 1, -1);
            if (Current(entries) is { } previous)
                return previous;
        }
        _index = 0;
        return Current(entries);
    }

    private string? NextEntry(SearchHistory history)
    {
        IReadOnlyList<string> entries = Entries(history);
        _index = Math.Min(_index + 1, entries.Count);
        return Current(entries);
    }
}
