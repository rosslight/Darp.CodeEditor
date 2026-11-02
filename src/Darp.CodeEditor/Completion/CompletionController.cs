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
/// Runs completion sessions. A session fetches its list once, off the UI thread. While the user keeps typing the
/// word, <see cref="CompletionPopup"/> filters that list locally. Descriptions load lazily for the selected item,
/// and commit replaces the word with text the service already computed.
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
            session.Window is { } window
            && _features.Service is { } service
            && service.Capabilities.CompletionCommitCharacters.Contains(c)
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
        var window = new CompletionPopup(
            _editor.TextArea,
            list.Items,
            index => LoadDescription(session, new CompletionItemReference(snapshot.DocumentId, list.ResultId, index))
        )
        {
            StartOffset = wordStart,
            EndOffset = Math.Max(caret, wordEnd),
            CloseWhenCaretAtBeginning = trigger.Kind == CompletionTriggerKind.Insertion,
        };
        window.Closed += (_, _) =>
        {
            if (_session != session)
                return;
            _session = null;
            session.Cancellation.Cancel();
        };
        if (!window.Filter())
        {
            _session = null;
            window.Hide();
            _editor.ReportTiming("completion-dropped", Stopwatch.GetElapsedTime(start));
            return;
        }
        session.Window = window;
        window.Show();
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
            Foreground = Brushes.Gainsboro,
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
        public CompletionPopup? Window { get; set; }
    }
}
