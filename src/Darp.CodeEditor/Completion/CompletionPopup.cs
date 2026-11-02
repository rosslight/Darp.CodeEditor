using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Popups;
using Darp.CodeEditor.Theming;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// The completion list and the description of its selected item, drawn inside the editor's window. One instance per
/// editor serves every session, so opening a list creates no controls.
/// </summary>
/// <remarks>
/// While open, the list filters the items of its session whenever the typed word changes, with ordinal matching (well
/// under a millisecond for thousands of items), and commits by replacing the word with precomputed text. It closes
/// when the caret leaves the word, the editor loses focus or the document is replaced.
/// </remarks>
internal sealed class CompletionPopup
{
    private const double MaxDescriptionWidth = 480;
    private const double MinDescriptionWidth = 200;

    private readonly CodeEditor _editor;
    private readonly CompletionListView _view;
    private readonly Border _listBorder;
    private readonly Border _descriptionBorder;
    private readonly DockPanel _content;
    private readonly TextPopup _popup;
    private Session? _session;

    public CompletionPopup(CodeEditor editor)
    {
        _editor = editor;
        _view = new CompletionListView(editor.FontFamily, editor.FontSize);
        _view.SelectionChanged += (_, _) => ShowDescription();
        _view.ItemActivated += (_, _) => Commit();
        _listBorder = new Border
        {
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _view,
        };
        _descriptionBorder = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Top,
            MaxHeight = _view.MaxHeight,
            IsVisible = false,
        };
        // List and description share one popup, so they never overlap and move together.
        _content = new DockPanel { Children = { _descriptionBorder, _listBorder } };
        _popup = new TextPopup(editor, _content, above: false);
        _popup.Closed += (_, _) => OnClosed();
        ApplyTheme(EditorTheme.Default);
    }

    public bool IsOpen => _session is not null;

    /// <summary>Raised when a session ends, by commit, cancellation or because the caret left the word.</summary>
    public event EventHandler? Closed;

    /// <summary>Starts a session: shows the items that match the text between the start offset and the caret.</summary>
    /// <param name="createDescription">Creates the description view for the item at an index, loading lazily.</param>
    /// <returns>False, and nothing shown, when no item matches.</returns>
    public bool Open(
        IReadOnlyList<CompletionItem> items,
        int startOffset,
        int endOffset,
        bool closeWhenCaretAtStart,
        Func<int, Control> createDescription
    )
    {
        Hide();
        TextDocument document = _editor.Document;
        var session = new Session(
            items,
            CreateAnchor(document, startOffset, AnchorMovementType.BeforeInsertion),
            CreateAnchor(document, endOffset, AnchorMovementType.AfterInsertion),
            closeWhenCaretAtStart,
            createDescription
        );
        _session = session;
        _view.SetItems(items);
        if (!Filter(session, CurrentQuery(session)))
        {
            _session = null;
            return false;
        }
        TextArea textArea = _editor.TextArea;
        textArea.Caret.PositionChanged += OnCaretPositionChanged;
        textArea.LostFocus += OnLostFocus;
        PlaceDescription(startOffset);
        ShowDescription();
        _popup.Show(startOffset);
        return true;
    }

    public void Hide()
    {
        if (_session is null)
            return;
        _popup.Hide();
        // Closing the list ends the session through Closed, but only if the list was open.
        OnClosed();
    }

    /// <summary>Replaces the word with the selected item.</summary>
    public void Commit()
    {
        if (_session is not { } session || _view.SelectedItem is not { } index)
            return;
        int start = session.Start.Offset;
        int length = session.End.Offset - start;
        Hide();
        _editor.Document.Replace(start, length, session.Items[index].InsertText);
    }

    /// <summary>Handles the keys that navigate, commit or close the open list. Returns false for other keys.</summary>
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Down:
                _view.Move(1);
                return true;
            case Key.Up:
                _view.Move(-1);
                return true;
            case Key.PageDown:
                _view.Move(10);
                return true;
            case Key.PageUp:
                _view.Move(-10);
                return true;
            case Key.Enter or Key.Tab:
                Commit();
                return true;
            case Key.Escape:
                Hide();
                return true;
            default:
                return false;
        }
    }

    public void ApplyTheme(EditorTheme theme)
    {
        _view.EditorTheme = theme;
        _listBorder.BorderBrush = theme.WidgetBorder;
        _descriptionBorder.Background = theme.WidgetBackground;
        _descriptionBorder.BorderBrush = theme.WidgetBorder;
        if (_descriptionBorder.Child is TextBlock description)
            description.Foreground = theme.WidgetForeground;
    }

    private static TextAnchor CreateAnchor(TextDocument document, int offset, AnchorMovementType movement)
    {
        TextAnchor anchor = document.CreateAnchor(offset);
        anchor.MovementType = movement;
        anchor.SurviveDeletion = true;
        return anchor;
    }

    private void OnClosed()
    {
        if (_session is null)
            return;
        _session = null;
        TextArea textArea = _editor.TextArea;
        textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        textArea.LostFocus -= OnLostFocus;
        _descriptionBorder.Child = null;
        _descriptionBorder.IsVisible = false;
        _view.SetItems([]);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The text between the start offset and the caret.</summary>
    private string CurrentQuery(Session session)
    {
        int start = session.Start.Offset;
        return _editor.Document.GetText(start, Math.Max(0, _editor.CaretOffset - start));
    }

    /// <summary>Shows the items that match, best first, in service order among equals. False if none match.</summary>
    private bool Filter(Session session, string query)
    {
        if (query == session.Query)
            return session.Matches.Length > 0;
        // Typing on narrows the previous matches; anything else starts from all items.
        IReadOnlyList<CompletionItem> items = session.Items;
        IEnumerable<int> candidates =
            session.Query is { Length: > 0 } previous && query.StartsWith(previous, StringComparison.Ordinal)
                ? session.Matches
                : Enumerable.Range(0, items.Count);
        session.Matches =
        [
            .. candidates
                .Select(index => (Index: index, Score: CompletionMatcher.Score(items[index].FilterText, query)))
                .Where(m => m.Score > 0)
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.Index)
                .Select(m => m.Index),
        ];
        session.Query = query;
        _view.SetMatches(session.Matches);
        return session.Matches.Length > 0;
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_session is not { } session)
            return;
        int caret = _editor.CaretOffset;
        int start = session.Start.Offset;
        if (
            caret < start
            || caret > session.End.Offset
            || (caret == start && session.CloseWhenCaretAtStart)
            || !Filter(session, CurrentQuery(session))
        )
            Hide();
    }

    private void OnLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        // Focus may move within the editor (the search panel opens, for example); check once it settled.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_editor.TextArea.IsFocused)
                    Hide();
            },
            DispatcherPriority.Background
        );

    private void ShowDescription()
    {
        if (_session is not { } session || _view.SelectedItem is not { } index)
        {
            _descriptionBorder.IsVisible = false;
            return;
        }
        if (!session.Descriptions.TryGetValue(index, out Control? description))
        {
            description = session.CreateDescription(index);
            session.Descriptions[index] = description;
        }
        _descriptionBorder.Child = description;
        _descriptionBorder.IsVisible = true;
    }

    /// <summary>
    /// Puts the description right of the list, or left of it, or below it when the window is too narrow for either,
    /// and moves the popup so that item text starts at the word being completed.
    /// </summary>
    private void PlaceDescription(int wordStart)
    {
        double listWidth = _listBorder.DesiredSize.Width is > 0 and var measured ? measured : _view.Width + 2;
        double listShift = _view.TextLeft + 1;
        TextView textView = _editor.TextArea.TextView;
        double wordX =
            textView.GetVisualPosition(new TextViewPosition(_editor.Document.GetLocation(wordStart)), VisualYPosition.LineTop).X
            - textView.ScrollOffset.X;
        double windowWidth = double.PositiveInfinity;
        double listLeft = wordX - listShift;
        if (TopLevel.GetTopLevel(textView) is { } topLevel && textView.TranslatePoint(new Point(wordX, 0), topLevel) is { } inWindow)
        {
            windowWidth = topLevel.ClientSize.Width;
            // Where the list ends up after the popup slides away from the window edges.
            listLeft = Math.Clamp(inWindow.X - listShift, 0, Math.Max(0, windowWidth - listWidth));
        }
        double right = windowWidth - listLeft - listWidth;
        Dock dock;
        double width;
        if (right >= MinDescriptionWidth)
            (dock, width) = (Dock.Right, Math.Min(MaxDescriptionWidth, right));
        else if (listLeft >= MinDescriptionWidth)
            (dock, width) = (Dock.Left, Math.Min(MaxDescriptionWidth, listLeft));
        else
            (dock, width) = (Dock.Bottom, Math.Min(MaxDescriptionWidth, windowWidth));
        DockPanel.SetDock(_descriptionBorder, dock);
        _descriptionBorder.Width = width;
        _popup.HorizontalOffset = dock == Dock.Left ? -(listShift + width) : -listShift;
    }

    private sealed class Session(
        IReadOnlyList<CompletionItem> items,
        TextAnchor start,
        TextAnchor end,
        bool closeWhenCaretAtStart,
        Func<int, Control> createDescription
    )
    {
        public IReadOnlyList<CompletionItem> Items { get; } = items;
        public TextAnchor Start { get; } = start;
        public TextAnchor End { get; } = end;
        public bool CloseWhenCaretAtStart { get; } = closeWhenCaretAtStart;
        public Func<int, Control> CreateDescription { get; } = createDescription;
        public Dictionary<int, Control> Descriptions { get; } = [];
        public string? Query { get; set; }
        public int[] Matches { get; set; } = [];
    }
}
