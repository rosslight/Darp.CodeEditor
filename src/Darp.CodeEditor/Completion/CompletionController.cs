using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Theming;
using CompletionList = Darp.CodeEditor.LanguageServices.CompletionList;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Runs completion sessions. A session fetches its list once, off the UI thread. While the user keeps typing the
/// word, <see cref="CompletionPopup"/> filters that list locally. Descriptions load lazily for the selected item,
/// and commit replaces the word with text the service already computed.
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

    public void ApplyTheme(EditorTheme theme) => _popup?.ApplyTheme(theme);

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
        if (_session is not { } session || !session.Shown)
            return;
        _session = null;
        session.Cancellation.Cancel();
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_session is null || e.Text is not [char c] || IsIdentifierPart(c))
            return;
        if (
            _popup is { IsOpen: true } popup
            && _features.Service is { } service
            && service.Capabilities.CompletionCommitCharacters.Contains(c)
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
        if (_session is not null)
        {
            // Typing on in the same word keeps the session; the list is filtered locally.
            if (IsIdentifierPart(c))
                return;
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

    private async void Start(ILanguageService service, CompletionTrigger trigger)
    {
        Cancel();
        var session = new Session();
        _session = session;
        (DocumentSnapshot snapshot, ITextSourceVersion checkpoint) = _features.CreateSnapshot();
        int position = _editor.CaretOffset;
        long start = Stopwatch.GetTimestamp();
        CancellationToken cancellationToken = session.Cancellation.Token;

        CompletionList? list = await _features.RequestAsync(
            () => service.GetCompletionsAsync(snapshot, position, trigger, cancellationToken),
            cancellationToken
        );
        if (_session != session)
            return;
        if (list is null || list.Items.Count == 0)
        {
            _session = null;
            _editor.ReportTiming("completion-empty", Stopwatch.GetElapsedTime(start));
            return;
        }

        // The user may have typed on while the list was computed: map the word forward and keep the session only
        // while the caret is still in that word.
        TextDocument document = _editor.Document;
        int wordStart = checkpoint.MoveOffsetTo(
            document.Version,
            list.ApplicableRange.Start,
            AnchorMovementType.BeforeInsertion
        );
        int wordEnd = checkpoint.MoveOffsetTo(document.Version, list.ApplicableRange.End, AnchorMovementType.Default);
        int caret = _editor.CaretOffset;
        if (caret < wordStart || !IsIdentifierRun(document, wordStart, caret))
        {
            _session = null;
            _editor.ReportTiming("completion-dropped", Stopwatch.GetElapsedTime(start));
            return;
        }

        long uiStart = Stopwatch.GetTimestamp();
        bool shown = Popup.Open(
            list.Items,
            wordStart,
            Math.Max(caret, wordEnd),
            closeWhenCaretAtStart: trigger.Kind == CompletionTriggerKind.Insertion,
            index => LoadDescription(session, new CompletionItemReference(snapshot.DocumentId, list.ResultId, index))
        );
        if (!shown)
        {
            _session = null;
            _editor.ReportTiming("completion-dropped", Stopwatch.GetElapsedTime(start));
            return;
        }
        session.Shown = true;
        _editor.ReportTiming("completion-ui", Stopwatch.GetElapsedTime(uiStart), list.Items.Count);
        _editor.ReportTiming("completion", Stopwatch.GetElapsedTime(start), list.Items.Count);
    }

    private TextBlock LoadDescription(Session session, CompletionItemReference reference)
    {
        var block = new TextBlock
        {
            Text = "…",
            MaxWidth = 480,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _editor.EditorTheme.WidgetForeground,
            FontFamily = _editor.FontFamily,
        };
        if (_features.Service is { } service)
            _ = LoadAsync(service);
        return block;

        async Task LoadAsync(ILanguageService service)
        {
            CancellationToken cancellationToken = session.Cancellation.Token;
            string? description = await _features.RequestAsync(
                () => service.GetCompletionDescriptionAsync(reference, cancellationToken),
                cancellationToken
            );
            block.Text = description ?? "";
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

    private sealed class Session
    {
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>True once its list is shown; until then the popup may still show the previous session.</summary>
        public bool Shown { get; set; }
    }
}
