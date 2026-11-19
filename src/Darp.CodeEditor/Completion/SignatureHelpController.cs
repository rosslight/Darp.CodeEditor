using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Signature help in one editor's view of a document, as VS Code asks for it: every signature of the call around the
/// caret (<see cref="SignatureView"/>), with the parameter at the caret in bold and its documentation, in the editor's
/// own popup in the color theme's hover colors, above the caret's line, so that completion's list below it shows too.
/// </summary>
/// <remarks>
/// <para>
/// Its command asks at once. Typing one of the server's trigger characters, such as <c>(</c>, asks after a pause of
/// 120 ms, and so does a retrigger character, such as <c>,</c>, while signature help shows, waits for an answer or for
/// the pause; each edit and caret move meanwhile restarts the pause. Once it passes, signature help asks again, for the
/// trigger character typed last, or by the command, and tells the server the signature that shows. A newer request
/// cancels the one under way, and an answer about older text is dropped. Up and Down choose the active signature, while
/// completion's list is closed.
/// </para>
/// <para>
/// <see cref="Close"/> ends it, for every reason it ends: Escape, leaving the call, a click or drag that moves the caret,
/// when the server answers with none,
/// the editor becoming read-only, the editor losing the keyboard focus, a server that starts or stops serving the
/// document, new colors, the caret's line scrolling out of view, and the editor no longer showing the document.
/// </para>
/// </remarks>
internal sealed class SignatureHelpController : IEditorFeature
{
    /// <summary>The pause after a typed trigger, an edit or a caret move before signature help asks.</summary>
    private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(120);

    /// <summary>The most width of the popup, unless half the editor is wider.</summary>
    private const double MaxWidth = 440;

    /// <summary>The most height of the popup, unless a quarter of the editor is taller: beyond it, it scrolls.</summary>
    private const double MaxHeight = 250;

    private readonly TemplatedControl _editor;
    private readonly TextArea _textArea;
    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly CaretMoves _caretMoves;
    private readonly PopupScroller _content = new();
    private readonly EditorPopup _popup;
    private PopupColors _colors = PopupColors.Default;

    /// <summary>The signature help that shows; null while none does.</summary>
    private SignatureHelpInfo? _shown;

    /// <summary>Cancels the request under way; null for none.</summary>
    private CancellationTokenSource? _request;

    /// <summary>The pause before asking; null while none runs.</summary>
    private ITimer? _pause;

    /// <summary>How the next request asks, from what asked within the pause; null while nothing waits to ask.</summary>
    private (int Kind, char? Character)? _trigger;

    /// <summary>Cancels the reading of the documentation that is about to show.</summary>
    private CancellationTokenSource? _reading;

    public SignatureHelpController(
        TemplatedControl editor,
        TextArea textArea,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        CaretMoves caretMoves
    )
    {
        _editor = editor;
        _caretMoves = caretMoves;
        _textArea = textArea;
        _textView = textArea.TextView;
        _buffer = textArea.Document;
        _document = document;
        _highlighter = highlighter;
        _popup = new EditorPopup(editor, _content, "SignatureHelp");
        _popup.Frame.Padding = new Avalonia.Thickness(8, 4);
        _textArea.TextEntered += OnTextEntered;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
        _buffer.Changed += OnEdit;
        _document.LanguageFeaturesChanged += OnClosingChange;
        _popup.LeftView += OnClosingChange;
    }

    /// <summary>The popup's colors, from the color theme once it is loaded. New colors end signature help.</summary>
    public PopupColors Colors
    {
        set
        {
            if (value == _colors)
                return;
            _colors = value;
            Close();
        }
    }

    /// <summary>Whether signature help shows, waits for an answer, or waits to ask again.</summary>
    private bool IsActive => _shown is not null || _request is not null || _pause is not null;

    public void AddCommands(CommandTable commands) =>
        commands.Add(CodeEditorCommands.TriggerParameterHints, Trigger, CommandNeeds.TextFocus | CommandNeeds.Writable);

    /// <summary>Asks for signature help, where the server has it and the user can type.</summary>
    private bool Trigger()
    {
        if (
            _document.Features?.SignatureHelp is null
            || !_textArea.ReadOnlySectionProvider.CanInsert(_textArea.Caret.Offset)
        )
            return false;
        Trigger(SignatureHelpTriggerKind.Invoked, null, TimeSpan.Zero);
        return true;
    }

    /// <summary>Up and Down, from the editor's key handler, while more than one signature shows: the others.</summary>
    /// <returns>Whether the key was signature help's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        if (
            _shown is not { Signatures.Count: > 1 } shown
            || e.KeyModifiers != KeyModifiers.None
            || e.Key is not (Key.Up or Key.Down)
        )
        {
            return false;
        }
        int count = shown.Signatures.Count;
        Show(shown with { ActiveSignature = (shown.ActiveSignature + (e.Key == Key.Down ? 1 : count - 1)) % count });
        return true;
    }

    /// <summary>Ends signature help: closes the popup, and cancels what is under way.</summary>
    /// <returns>Whether the popup showed.</returns>
    public bool Close()
    {
        bool wasOpen = _shown is not null;
        _shown = null;
        _trigger = null;
        StopWaiting();
        _reading?.Cancel();
        _reading = null;
        _popup.Hide();
        return wasOpen;
    }

    public void Dispose()
    {
        Close();
        _textArea.TextEntered -= OnTextEntered;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _buffer.Changed -= OnEdit;
        _document.LanguageFeaturesChanged -= OnClosingChange;
        _popup.LeftView -= OnClosingChange;
    }

    /// <summary>Asks on the server's trigger characters, and on its retrigger characters while active.</summary>
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } text || _document.Features?.SignatureHelp is not { } provider)
            return;
        char typed = text[^1];
        if (provider.TriggerCharacters.Contains(typed) || (IsActive && provider.RetriggerCharacters.Contains(typed)))
            Trigger(SignatureHelpTriggerKind.TriggerCharacter, typed, s_pause);
    }

    /// <summary>While active, an edit, also one that leaves the caret where it was, outdates what is asked.</summary>
    private void OnEdit(object? sender, EventArgs e)
    {
        if (IsActive)
            Trigger(SignatureHelpTriggerKind.ContentChange, null, s_pause);
    }

    /// <summary>A caret that the pointer moves ends signature help; any other move outdates what is asked.</summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_caretMoves.Mover == CaretMover.Pointer)
            Close();
        else if (IsActive)
            Trigger(SignatureHelpTriggerKind.ContentChange, null, s_pause);
    }

    /// <summary>
    /// Asks once <paramref name="pause"/> has passed, restarting the pause under way: for the command or the trigger
    /// character that asked last within it, as an edit asks only for itself, without either.
    /// </summary>
    private void Trigger(int kind, char? character, TimeSpan pause)
    {
        _trigger =
            kind == SignatureHelpTriggerKind.ContentChange && _trigger is { } earlier ? earlier : (kind, character);
        _pause?.Dispose();
        _pause = null;
        if (pause == TimeSpan.Zero)
        {
            Ask();
            return;
        }
        ITimer? timer = null;
        timer = _document.TimeProvider.CreateTimer(
            _ =>
                _editor.Dispatcher.Post(() =>
                {
                    if (_pause == timer)
                        Ask();
                }),
            null,
            pause,
            Timeout.InfiniteTimeSpan
        );
        _pause = timer;
    }

    private void StopWaiting()
    {
        _pause?.Dispose();
        _pause = null;
        _request?.Cancel();
        _request = null;
    }

    /// <summary>
    /// Asks as <see cref="_trigger"/> says, in place of the request under way: again, while signature help shows or is
    /// asked for.
    /// </summary>
    private void Ask()
    {
        if (_trigger is not { } trigger)
            return;
        _trigger = null;
        bool again = _shown is not null || _request is not null;
        StopWaiting();
        // Nothing to help with where the user cannot type.
        if (!_textArea.ReadOnlySectionProvider.CanInsert(_textArea.Caret.Offset))
        {
            Close();
            return;
        }
        var cancellation = new CancellationTokenSource();
        _request = cancellation;
        AskAsync(
            _document.CurrentSnapshot,
            _textArea.Caret.Offset,
            new SignatureHelpTrigger(trigger.Kind, trigger.Character, again, _shown),
            cancellation
        );
    }

    /// <summary>Shows the server's answer, unless it is outdated; closes when the server has none there.</summary>
    private async void AskAsync(
        TextSnapshot snapshot,
        int caret,
        SignatureHelpTrigger trigger,
        CancellationTokenSource cancellation
    )
    {
        try
        {
            SignatureHelpInfo? help = await _document.SignatureHelpAsync(snapshot, caret, trigger, cancellation.Token);
            if (_request != cancellation)
                return;
            _request = null;
            // Changed elsewhere meanwhile, as in another editor: the answer is about older text.
            if (snapshot != _document.CurrentSnapshot)
            {
                Trigger(SignatureHelpTriggerKind.ContentChange, null, s_pause);
                return;
            }
            if (help is null)
                Close();
            else
                Show(help);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, AssetFiles.LogArea)?.Log(this, "Signature help failed: {Exception}", e);
        }
    }

    /// <summary>
    /// Shows <paramref name="help"/> above the caret, once its documentation is read off the UI thread, as the hover
    /// reads its Markdown: as much as a hover shows, the active signature first, then the others and the documentation,
    /// so that a server's long signatures or documentation cost the UI thread no more than the largest hover.
    /// </summary>
    private async void Show(SignatureHelpInfo help)
    {
        _shown = help;
        _reading?.Cancel();
        var reading = new CancellationTokenSource();
        _reading = reading;
        SignatureInfo signature = help.Signatures[help.ActiveSignature];
        HoverText[] documentation =
        [
            .. new[]
            {
                signature.ActiveParameter >= 0 ? signature.Parameters[signature.ActiveParameter].Documentation : null,
                signature.Documentation,
            }.OfType<HoverText>(),
        ];
        TextMateGrammar? grammar = _highlighter.Grammar;
        var budget = new HoverBudget();
        string?[] labels = new string?[help.Signatures.Count];
        labels[help.ActiveSignature] = budget.Take(signature.Label);
        for (int i = 0; i < labels.Length; i++)
        {
            if (i != help.ActiveSignature)
                labels[i] = budget.Take(help.Signatures[i].Label);
        }
        try
        {
            IReadOnlyList<HoverBlock> blocks =
                documentation.Length == 0
                    ? []
                    : await Task.Run(
                        () => HoverBlocks.Of(new HoverInfo(documentation, null), grammar, budget, reading.Token),
                        reading.Token
                    );
            if (_reading != reading)
                return;
            _popup.Frame.Paint(_colors.HoverBackground, _colors.HoverBorder, _colors.HoverForeground, _colors.Shadow);
            _content.SliderBrushes = _colors.Slider;
            // Wider in a wide editor, and taller in a tall one, as VS Code sizes it.
            _popup.Frame.MaxWidth = Math.Max(MaxWidth, Math.Floor(_editor.Bounds.Width / 2));
            _popup.Frame.MaxHeight = Math.Max(MaxHeight, Math.Floor(_editor.Bounds.Height / 4));
            Avalonia.Thickness padding = _popup.Frame.Padding;
            double room = _popup.Frame.MaxWidth - padding.Left - padding.Right - PopupSlider.Thickness;
            _content.Child = SignatureView.Create(
                help,
                labels,
                blocks,
                PopupLook.Of(_editor, _colors, _colors.HoverForeground),
                (int)(room / Math.Max(1, _textView.WideSpaceWidth)),
                out Control active
            );
            _content.ScrollIntoView(active);
            _popup.Show(
                _textView,
                () =>
                    TextViewBoxes.CharacterAt(
                        _textView,
                        Math.Min(_textArea.Caret.Offset, _textArea.Document.TextLength)
                    )
            );
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Showing signature help failed: {Exception}", e);
        }
    }

    private void OnClosingChange(object? sender, EventArgs e) => Close();
}
