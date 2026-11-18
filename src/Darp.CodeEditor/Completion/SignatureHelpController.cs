using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Signature help in one editor's view of a document: the signature of the call around the caret, with the parameter
/// at the caret in bold and its documentation, in a popup that looks like the app's tooltips, above the caret's line,
/// so that completion's list below it shows too.
/// </summary>
/// <remarks>
/// <para>
/// Its triggers are VS Code's (<c>ParameterHintsModel</c>): one of the server's trigger characters, such as <c>(</c>,
/// and while it is active, one of its retrigger characters, such as <c>,</c>, each edit and each caret move by the
/// keyboard. Each restarts a pause of 120 ms, VS Code's; once it passes, it asks, for the triggers since the last
/// request: the last character typed, or an edit. Asking by key asks at once. It is active while it shows, while a
/// request is under way and while it waits to ask: asking then is asking again, which the server learns, with the
/// signature that shows, or showed before the request under way. A newer request cancels the one under way, and the
/// answer to a request that a trigger came after is dropped. Up and Down show the other signatures, while completion's
/// list is closed.
/// </para>
/// <para>
/// <see cref="Close"/> ends it, for every reason it ends: Escape, leaving the call, a click or drag that moves the caret,
/// when the server answers with none, the editor becoming read-only, the editor losing the keyboard focus, a server that
/// starts or stops serving the document, new colors, the caret's line scrolling out of view, and the editor no longer
/// showing the document.
/// </para>
/// </remarks>
internal sealed class SignatureHelpController : IDisposable
{
    /// <summary>The narrowest the popup's most width gets: VS Code's.</summary>
    private const double MinMaxWidth = 440;

    /// <summary>The lowest the popup's most height gets: VS Code's.</summary>
    private const double MinMaxHeight = 250;

    /// <summary>The pause after an edit or a caret move before signature help is asked again: VS Code's.</summary>
    private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(120);

    private readonly CodeEditor _editor;
    private readonly TextArea _textArea;
    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly PopupScroller _content = new();
    private readonly PopupFrame _frame;
    private readonly EditorPopup _popup;
    private TextAreaColors? _colors;

    /// <summary>The signature help that shows; null while none does.</summary>
    private SignatureHelpInfo? _shown;

    /// <summary>Cancels the request under way; null for none.</summary>
    private CancellationTokenSource? _request;

    /// <summary>The pause before asking again; null while none runs.</summary>
    private ITimer? _pause;

    /// <summary>Cancels the reading of the documentation that is about to show.</summary>
    private CancellationTokenSource? _reading;

    /// <summary>The triggers since the last request, for the next one.</summary>
    private readonly List<(int Kind, char? Character)> _triggers = [];

    /// <summary>Counts the triggers: an answer to a request that a trigger came after is outdated.</summary>
    private int _triggerCount;

    public SignatureHelpController(
        CodeEditor editor,
        TextArea textArea,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter
    )
    {
        _editor = editor;
        _textArea = textArea;
        _textView = textArea.TextView;
        _buffer = textArea.Document;
        _document = document;
        _highlighter = highlighter;
        _frame = new PopupFrame
        {
            Name = "SignatureHelp",
            Child = _content,
            Padding = new Avalonia.Thickness(4, 4, 2, 4),
        };
        _popup = new EditorPopup(editor, _frame);
        _textArea.TextEntered += OnTextEntered;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
        _buffer.Changed += OnEditOrCaretMove;
        _document.LanguageFeaturesChanged += OnClosingChange;
        _popup.LeftView += OnClosingChange;
    }

    /// <summary>The colors of the color theme, for the signature, once it is loaded. New colors end signature help.</summary>
    public TextAreaColors? Colors
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

    /// <summary>Asks for signature help at the caret at once, as the user asks for it by key.</summary>
    public void Trigger()
    {
        if (_document.Features?.SignatureHelp is not null)
            Schedule(SignatureHelpTriggerKind.Invoked, null, TimeSpan.Zero);
    }

    /// <summary>Ends signature help: closes the popup, and cancels what is under way.</summary>
    /// <returns>Whether the popup showed.</returns>
    public bool Close()
    {
        bool wasOpen = _shown is not null;
        _shown = null;
        _pause?.Dispose();
        _pause = null;
        _request?.Cancel();
        _request = null;
        _triggers.Clear();
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
        _buffer.Changed -= OnEditOrCaretMove;
        _document.LanguageFeaturesChanged -= OnClosingChange;
        _popup.LeftView -= OnClosingChange;
    }

    /// <summary>Asks on the server's trigger characters, and on its retrigger characters while active, after the pause.</summary>
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } text || _document.Features?.SignatureHelp is not { } provider)
            return;
        char typed = text[^1];
        if (provider.TriggerCharacters.Contains(typed) || (IsActive && provider.RetriggerCharacters.Contains(typed)))
            Schedule(SignatureHelpTriggerKind.TriggerCharacter, typed, s_pause);
    }

    /// <summary>
    /// While active, an edit, also one that leaves the caret where it was, or a caret move outdates what is asked, and
    /// asks again after the pause.
    /// </summary>
    private void OnEditOrCaretMove(object? sender, EventArgs e)
    {
        if (IsActive)
            Schedule(SignatureHelpTriggerKind.ContentChange, null, s_pause);
    }

    /// <summary>A caret moved by the pointer, as by a click, ends signature help, as in VS Code.</summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_editor.PointerMovesCaret)
            Close();
        else
            OnEditOrCaretMove(sender, e);
    }

    /// <summary>
    /// Adds a trigger, and asks once <paramref name="delay"/> has passed without another one, as VS Code's throttled
    /// delayer does; at once without a delay.
    /// </summary>
    private void Schedule(int kind, char? character, TimeSpan delay)
    {
        _triggerCount++;
        _triggers.Add((kind, character));
        _pause?.Dispose();
        _pause = null;
        if (delay <= TimeSpan.Zero)
        {
            Ask();
            return;
        }
        ITimer? pause = null;
        pause = _document.TimeProvider.CreateTimer(
            _ =>
                _editor.Dispatcher.Post(() =>
                {
                    if (_pause == pause)
                        Ask();
                }),
            null,
            delay,
            Timeout.InfiniteTimeSpan
        );
        _pause = pause;
    }

    /// <summary>
    /// Asks for the triggers since the last request, as VS Code's <c>doTrigger</c>: asking by key wins over what came
    /// before it, a character typed over an edit; it asks again while signature help shows or a request is under way,
    /// with the signature that shows, or showed before that request.
    /// </summary>
    private void Ask()
    {
        _pause?.Dispose();
        _pause = null;
        bool isRetrigger = _shown is not null || _request is not null;
        _request?.Cancel();
        _request = null;
        if (_triggers.Count == 0)
            return;
        (int kind, char? character) = _triggers.Aggregate(
            (previous, current) => current.Kind == SignatureHelpTriggerKind.ContentChange ? previous : current
        );
        _triggers.Clear();
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
            new SignatureHelpTrigger(kind, character, isRetrigger, _shown),
            cancellation,
            _triggerCount
        );
    }

    /// <summary>
    /// Shows the server's answer, unless signature help ended, a newer request replaced it, or a trigger came after it;
    /// closes when the server has none there.
    /// </summary>
    private async void AskAsync(
        TextSnapshot snapshot,
        int caret,
        SignatureHelpTrigger trigger,
        CancellationTokenSource cancellation,
        int triggerCount
    )
    {
        try
        {
            SignatureHelpInfo? help = await _document.SignatureHelpAsync(snapshot, caret, trigger, cancellation.Token);
            // Outdated: the request stays under way, as VS Code's does, until the next one replaces it.
            if (_request != cancellation || _triggerCount != triggerCount)
                return;
            _request = null;
            // Changed elsewhere meanwhile, as in another editor: the answer is about older text.
            if (snapshot != _document.CurrentSnapshot)
            {
                Schedule(SignatureHelpTriggerKind.ContentChange, null, s_pause);
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
    /// Shows <paramref name="help"/>'s signatures above the caret, once the documentation of the active one is read off
    /// the UI thread, as the hover reads its Markdown.
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
        try
        {
            IReadOnlyList<HoverBlock> blocks =
                documentation.Length == 0
                    ? []
                    : await Task.Run(
                        () => HoverBlocks.Of(new HoverInfo(documentation, null), grammar, reading.Token),
                        reading.Token
                    );
            if (_reading != reading)
                return;
            PopupColors colors = _colors?.Popups ?? PopupColors.Default;
            _frame.Paint(colors.HoverBackground, colors.HoverBorder, colors.HoverForeground, colors.Shadow);
            _content.SliderBrushes = (colors.Slider, colors.SliderHover, colors.SliderActive);
            // As VS Code: at least 440 pixels wide, wider in wide editors, and scrolling beyond a quarter of the
            // editor's height, at least 250 pixels.
            Avalonia.Size editor = _editor.Bounds.Size;
            _frame.MaxWidth = Math.Max(MinMaxWidth, Math.Floor(editor.Width / 2));
            _frame.MaxHeight = Math.Max(MinMaxHeight, Math.Floor(editor.Height / 4));
            double room = _frame.MaxWidth - _frame.Padding.Left - _frame.Padding.Right - PopupScroller.SliderWidth - 10;
            int columns = Math.Max(20, (int)(room / Math.Max(1, _textView.WideSpaceWidth)));
            _content.Child = SignatureView.Create(
                help,
                blocks,
                new PopupLook(_editor.FontFamily, _editor.FontSize, colors),
                columns,
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
