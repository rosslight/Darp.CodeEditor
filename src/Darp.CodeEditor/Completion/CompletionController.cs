using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Darp.CodeEditor.LanguageServices;
using CompletionList = Darp.CodeEditor.LanguageServices.CompletionList;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Runs completion sessions. A session fetches its list off the UI thread and <see cref="CompletionPopup"/> filters it
/// locally while the user keeps typing the word; a list the service marked incomplete is fetched again for each
/// further letter and replaces the items in the open list when it arrives. The selected item is resolved lazily for
/// its description and additional edits, and commit inserts text the service already computed.
/// </summary>
internal sealed class CompletionController
{
    private readonly CodeEditor _editor;
    private readonly LanguageFeatures _features;
    private CompletionPopup? _popup;
    private Session? _session;

    public CompletionController(CodeEditor editor, LanguageFeatures features)
    {
        _editor = editor;
        _features = features;
        editor.TextArea.TextEntering += OnTextEntering;
        editor.TextArea.TextEntered += OnTextEntered;
        editor.DocumentChanged += (_, _) => Cancel();
        editor.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        editor.TextArea.TextView.VisualLinesChanged += OnFirstLayout;
        editor.EditorThemeChanged += (_, _) => _popup?.ApplyTheme(editor.EditorTheme);
    }

    /// <summary>True while the completion list is shown; it takes the navigation keys then.</summary>
    public bool IsOpen => _popup?.IsOpen == true;

    /// <summary>Created after the first frame, so it adds nothing to the time until the editor shows text.</summary>
    private CompletionPopup Popup
    {
        get
        {
            if (_popup is null)
            {
                _popup = new CompletionPopup(_editor);
                _popup.ApplyTheme(_editor.EditorTheme);
                _popup.Closed += (_, _) => OnPopupClosed();
            }
            return _popup;
        }
    }

    public void Cancel()
    {
        if (_session is not { } session)
            return;
        _session = null;
        session.Cancellation.Cancel();
        _popup?.Hide();
    }

    private void OnFirstLayout(object? sender, EventArgs e)
    {
        _editor.TextArea.TextView.VisualLinesChanged -= OnFirstLayout;
        Dispatcher.UIThread.Post(() => _ = WarmUpAsync(), DispatcherPriority.Background);
    }

    private async Task WarmUpAsync()
    {
        try
        {
            await Popup.WarmUpAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Warming up the completion list failed: {exception}");
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control && _features.Service is { } service)
        {
            e.Handled = true;
            Start(service, new CompletionTrigger(CompletionTriggerKind.Invoke));
        }
        else if (_popup is { IsOpen: true } popup && e.KeyModifiers == KeyModifiers.None && popup.HandleKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnPopupClosed()
    {
        // The list closed itself (commit, Escape, caret left the word): end the session and its requests.
        if (_session is not { Shown: not null } session)
            return;
        _session = null;
        session.Cancellation.Cancel();
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_session is null || e.Text is not [char c] || IsIdentifierPart(c))
            return;
        if (
            _popup is { IsOpen: true, SelectedItem: { } item } popup
            && _features.Service is { } service
            && (item.CommitCharacters ?? service.Capabilities.CompletionCommitCharacters).Contains(c)
        )
        {
            popup.Commit();
        }
        Cancel();
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_features.Service is not { } service || e.Text is not [char c])
            return;
        if (_session is { } session)
        {
            // Typing on in the same word keeps the session; the list is filtered locally, or fetched again.
            if (IsIdentifierPart(c))
            {
                if (session.Shown?.List.IsIncomplete == true)
                    _ = ShowListAsync(service, session, new CompletionTrigger(CompletionTriggerKind.IncompleteList, c));
                return;
            }
            Cancel();
        }
        if (service.Capabilities.CompletionTriggerCharacters.Contains(c))
        {
            Start(service, new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, c));
        }
        else if (IsIdentifierStart(c) && !IsIdentifierPart(CharAt(_editor.CaretOffset - 2)))
        {
            Start(service, new CompletionTrigger(CompletionTriggerKind.Insertion, c));
        }
    }

    private void Start(ILanguageService service, CompletionTrigger trigger)
    {
        Cancel();
        var session = new Session(trigger.Kind);
        _session = session;
        _ = ShowListAsync(service, session, trigger);
    }

    /// <summary>Fetches a list and shows it, in place of the session's current one if there is one.</summary>
    private async Task ShowListAsync(ILanguageService service, Session session, CompletionTrigger trigger)
    {
        // A newer request for an incomplete list replaces an older one.
        session.Request?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token);
        session.Request = request;
        CancellationToken cancellationToken = request.Token;
        (DocumentVersion document, ITextSourceVersion checkpoint) = _features.CreateSnapshot();
        int position = _editor.CaretOffset;
        long start = Stopwatch.GetTimestamp();

        CompletionList? list = await _features.RequestAsync(
            () => service.GetCompletionsAsync(document, position, trigger, cancellationToken),
            cancellationToken
        );
        if (_session != session || cancellationToken.IsCancellationRequested)
            return;
        if (list is null || list.Items.Count == 0)
        {
            // A refined list that found nothing leaves the shown one to the local filter.
            if (session.Shown is null)
                End(session, "completion-empty", start);
            return;
        }

        // The user may have typed on while the list was computed: map the word forward and keep the session only
        // while the caret is still in that word.
        TextDocument text = _editor.Document;
        int wordStart = checkpoint.MoveOffsetTo(
            text.Version,
            list.ApplicableRange.Start,
            AnchorMovementType.BeforeInsertion
        );
        int wordEnd = checkpoint.MoveOffsetTo(text.Version, list.ApplicableRange.End, AnchorMovementType.Default);
        int caret = _editor.CaretOffset;
        if (caret < wordStart || !IsIdentifierRun(text, wordStart, caret))
        {
            if (session.Shown is null)
                End(session, "completion-dropped", start);
            return;
        }

        long uiStart = Stopwatch.GetTimestamp();
        var shown = new ShownList(service, _features, list, checkpoint, document.DocumentId);
        Func<int, Control> createDescription = index => LoadDescription(shown, index);
        Action<int, int, int> commit = (index, from, to) => Commit(shown, index, from, to);
        bool refining = session.Shown is not null;
        // A list asked again replaces the items in the open list; it keeps the word the first list started.
        bool open = refining
            ? Popup.ReplaceItems(list.Items, createDescription, commit)
            : Popup.Open(
                list.Items,
                wordStart,
                Math.Max(caret, wordEnd),
                closeWhenCaretAtStart: session.Kind == CompletionTriggerKind.Insertion,
                createDescription,
                commit
            );
        if (!open)
        {
            if (!refining)
                End(session, "completion-dropped", start);
            return;
        }
        session.Shown = shown;
        string name = refining ? "completion-refined" : "completion";
        _editor.ReportTiming($"{name}-ui", Stopwatch.GetElapsedTime(uiStart), list.Items.Count);
        _editor.ReportTiming(name, Stopwatch.GetElapsedTime(start), list.Items.Count);
    }

    private void End(Session session, string timing, long start)
    {
        _session = null;
        session.Cancellation.Cancel();
        _editor.ReportTiming(timing, Stopwatch.GetElapsedTime(start));
    }

    /// <summary>
    /// Replaces the item's range, or else the word, with the item's text, and applies its additional edits, such as a
    /// <c>using</c> directive, in the same undo step.
    /// </summary>
    /// <remarks>
    /// Commit stays synchronous: a commit character is inserted right after it, and waiting for the server would hold
    /// the keystroke. The selected item is usually resolved by then, for its description. When its resolve arrives
    /// later, its edits join the commit's undo step, but only while the document is still exactly as the commit left
    /// it and that step is still the last one; after any other change or undo step, an undo included, they no longer
    /// belong to the commit and are dropped.
    /// </remarks>
    private void Commit(ShownList shown, int index, int wordStart, int wordEnd)
    {
        CompletionItem item = shown.List.Items[index];
        TextDocument document = _editor.Document;
        if (!shown.Checkpoint.BelongsToSameDocumentAs(document.Version))
            return;
        if (item.Range is { } range)
        {
            // Typing at the end of the range, as in an insert range that ends at the caret, extends it.
            wordStart = shown.Checkpoint.MoveOffsetTo(
                document.Version,
                range.Start,
                AnchorMovementType.BeforeInsertion
            );
            wordEnd = shown.Checkpoint.MoveOffsetTo(document.Version, range.End, AnchorMovementType.AfterInsertion);
        }
        Task<CompletionItemDetails?> details = shown.Resolve(index);
        // Names the commit's undo step, so that late edits can tell whether it is still the last one.
        var step = new object();
        document.UndoStack.StartUndoGroup(step);
        document.BeginUpdate();
        try
        {
            document.Replace(wordStart, Math.Max(0, wordEnd - wordStart), item.InsertText);
            if (details.IsCompleted)
                ApplyEdits(details.Result?.AdditionalEdits ?? item.AdditionalEdits, shown.Checkpoint);
        }
        finally
        {
            document.EndUpdate();
            document.UndoStack.EndUndoGroup();
        }
        if (!details.IsCompleted)
            _ = ApplyEditsWhenResolvedAsync(details, item, shown.Checkpoint, document.Version, step);
    }

    /// <param name="committed">The document's version right after the commit.</param>
    /// <param name="step">The name of the commit's undo step.</param>
    private async Task ApplyEditsWhenResolvedAsync(
        Task<CompletionItemDetails?> details,
        CompletionItem item,
        ITextSourceVersion checkpoint,
        ITextSourceVersion committed,
        object step
    )
    {
        IReadOnlyList<TextEdit>? edits = (await details)?.AdditionalEdits ?? item.AdditionalEdits;
        TextDocument document = _editor.Document;
        // An undo step that changes no text, such as one an app pushed, keeps the version: the step's name tells.
        if (
            edits is not { Count: > 0 }
            || !committed.BelongsToSameDocumentAs(document.Version)
            || committed.CompareAge(document.Version) != 0
            || document.UndoStack.LastGroupDescriptor != step
        )
        {
            return;
        }
        // The text is as the commit left it and its undo step is the last: the edits continue that step.
        document.UndoStack.StartContinuedUndoGroup(step);
        try
        {
            document.BeginUpdate();
            try
            {
                ApplyEdits(edits, checkpoint);
            }
            finally
            {
                document.EndUpdate();
            }
        }
        finally
        {
            document.UndoStack.EndUndoGroup();
        }
    }

    /// <summary>Applies edits in the coordinates of the list's version, each mapped to the current text.</summary>
    private void ApplyEdits(IReadOnlyList<TextEdit>? edits, ITextSourceVersion checkpoint)
    {
        TextDocument document = _editor.Document;
        foreach (TextEdit edit in edits ?? [])
        {
            int start = checkpoint.MoveOffsetTo(document.Version, edit.Range.Start, AnchorMovementType.BeforeInsertion);
            int end = checkpoint.MoveOffsetTo(document.Version, edit.Range.End, AnchorMovementType.BeforeInsertion);
            document.Replace(start, Math.Max(0, end - start), edit.NewText);
        }
    }

    private TextBlock LoadDescription(ShownList shown, int index)
    {
        var block = new TextBlock
        {
            Text = "…",
            MaxWidth = 480,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _editor.EditorTheme.WidgetForeground,
        };
        _ = LoadAsync();
        return block;

        async Task LoadAsync()
        {
            CompletionItemDetails? details = await shown.Resolve(index);
            if (details is { Description.Count: > 0 })
                MarkupText.Show(block, details.Description, _editor.FontFamily);
            else
                block.Text = "";
        }
    }

    private char CharAt(int offset) =>
        offset >= 0 && offset < _editor.Document.TextLength ? _editor.Document.GetCharAt(offset) : ' ';

    private static bool IsIdentifierRun(TextDocument document, int start, int end)
    {
        for (int offset = start; offset < end; offset++)
        {
            if (!IsIdentifierPart(document.GetCharAt(offset)))
                return false;
        }
        return true;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <param name="kind">How the session started; refining an incomplete list keeps it.</param>
    private sealed class Session(CompletionTriggerKind kind)
    {
        public CompletionTriggerKind Kind { get; } = kind;

        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>The request in flight, for the first list or a refined one.</summary>
        public CancellationTokenSource? Request { get; set; }

        /// <summary>The list on screen; null until the first one is shown, while the popup may show another session.</summary>
        public ShownList? Shown { get; set; }
    }

    /// <summary>A list on screen, with the version it was computed for and its items' resolves.</summary>
    private sealed class ShownList(
        ILanguageService service,
        LanguageFeatures features,
        CompletionList list,
        ITextSourceVersion checkpoint,
        string documentId
    )
    {
        private readonly ILanguageService _service = service;
        private readonly LanguageFeatures _features = features;
        private readonly string _documentId = documentId;
        private readonly Dictionary<int, Task<CompletionItemDetails?>> _details = [];

        public CompletionList List { get; } = list;

        public ITextSourceVersion Checkpoint { get; } = checkpoint;

        /// <summary>Resolves an item once. Not cancelled with the session: a commit may still need its edits.</summary>
        public Task<CompletionItemDetails?> Resolve(int index)
        {
            if (!_details.TryGetValue(index, out Task<CompletionItemDetails?>? details))
            {
                var reference = new CompletionItemReference(_documentId, List.ResultId, index);
                details = _features.RequestAsync(
                    () => _service.ResolveCompletionItemAsync(reference, CancellationToken.None),
                    CancellationToken.None
                );
                _details[index] = details;
            }
            return details;
        }
    }
}
