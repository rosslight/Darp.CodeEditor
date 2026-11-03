using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Document;
using Darp.CodeEditor.LanguageServices;
using CompletionList = Darp.CodeEditor.LanguageServices.CompletionList;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Runs completion sessions. A session fetches its list off the UI thread and <see cref="CompletionPopup"/> filters it
/// locally while the user keeps typing the word; a list the service marked incomplete is fetched again for each
/// further letter and replaces the shown one when it arrives. The selected item is resolved lazily for its description
/// and additional edits, and commit inserts text the service already computed.
/// </summary>
internal sealed class CompletionController
{
    private readonly CodeEditor _editor;
    private readonly LanguageFeatures _features;
    private Session? _session;

    public CompletionController(CodeEditor editor, LanguageFeatures features)
    {
        _editor = editor;
        _features = features;
        editor.TextArea.TextEntering += OnTextEntering;
        editor.TextArea.TextEntered += OnTextEntered;
        editor.DocumentChanged += (_, _) => Cancel();
        editor.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public void Cancel()
    {
        if (_session is not { } session)
            return;
        _session = null;
        session.Cancellation.Cancel();
        session.Window?.Hide();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control && _features.Service is { } service)
        {
            e.Handled = true;
            Start(service, new CompletionTrigger(CompletionTriggerKind.Invoke));
        }
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_session is not { } session || e.Text is not [char c] || IsIdentifierPart(c))
            return;
        if (
            session.Window is { SelectedItem: { } item } window
            && _features.Service is { } service
            && (item.CommitCharacters ?? service.Capabilities.CompletionCommitCharacters).Contains(c)
        )
        {
            window.Commit();
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

    /// <summary>Fetches a list and shows it in place of the session's current one.</summary>
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
            if (session.Window is null)
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
            if (session.Window is null)
                End(session, "completion-dropped", start);
            return;
        }

        long uiStart = Stopwatch.GetTimestamp();
        var shown = new ShownList(service, _features, list, checkpoint, document.DocumentId);
        var window = new CompletionPopup(
            _editor.TextArea,
            list.Items,
            index => LoadDescription(shown, index),
            (index, from, to) => Commit(shown, index, from, to)
        )
        {
            StartOffset = wordStart,
            EndOffset = Math.Max(caret, wordEnd),
            CloseWhenCaretAtBeginning = session.Kind == CompletionTriggerKind.Insertion,
        };
        window.Closed += (_, _) =>
        {
            // Replacing an incomplete list closes the old window; that does not end the session.
            if (_session != session || session.Window != window)
                return;
            _session = null;
            session.Cancellation.Cancel();
        };
        if (!window.Filter())
        {
            window.Hide();
            if (session.Window is null)
                End(session, "completion-dropped", start);
            return;
        }
        CompletionPopup? previous = session.Window;
        session.Shown = shown;
        session.Window = window;
        window.Show();
        previous?.Hide();
        string name = trigger.Kind == CompletionTriggerKind.IncompleteList ? "completion-refined" : "completion";
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
    /// Replaces the word with the item's text, and applies its additional edits, such as a <c>using</c> directive:
    /// at once when the item is resolved, otherwise when the resolve arrives.
    /// </summary>
    private void Commit(ShownList shown, int index, int wordStart, int wordEnd)
    {
        CompletionItem item = shown.List.Items[index];
        TextDocument document = _editor.Document;
        if (!shown.Checkpoint.BelongsToSameDocumentAs(document.Version))
            return;
        if (item.Range is { } range)
        {
            int itemStart = shown.Checkpoint.MoveOffsetTo(
                document.Version,
                range.Start,
                AnchorMovementType.BeforeInsertion
            );
            wordStart = Math.Min(wordStart, itemStart);
        }
        Task<CompletionItemDetails?> details = shown.Resolve(index);
        document.BeginUpdate();
        try
        {
            document.Replace(wordStart, wordEnd - wordStart, item.InsertText);
            if (details.IsCompleted)
                ApplyEdits(details.Result?.AdditionalEdits ?? item.AdditionalEdits, shown.Checkpoint);
        }
        finally
        {
            document.EndUpdate();
        }
        if (!details.IsCompleted)
            _ = ApplyEditsWhenResolvedAsync(details, item, shown.Checkpoint);
    }

    private async Task ApplyEditsWhenResolvedAsync(
        Task<CompletionItemDetails?> details,
        CompletionItem item,
        ITextSourceVersion checkpoint
    )
    {
        IReadOnlyList<TextEdit>? edits = (await details)?.AdditionalEdits ?? item.AdditionalEdits;
        if (edits is not { Count: > 0 } || !checkpoint.BelongsToSameDocumentAs(_editor.Document.Version))
            return;
        _editor.Document.BeginUpdate();
        try
        {
            ApplyEdits(edits, checkpoint);
        }
        finally
        {
            _editor.Document.EndUpdate();
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
            Foreground = Brushes.Gainsboro,
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

        /// <summary>The list on screen and its window; null until the first list arrived.</summary>
        public ShownList? Shown { get; set; }

        public CompletionPopup? Window { get; set; }
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
