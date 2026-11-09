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
/// It opens when the user types one of the server's trigger characters, such as <c>(</c>, and asks again at once on a
/// retrigger character, such as <c>,</c>, while it is open. While it is open, each edit and caret move restarts a pause
/// of 120 ms, VS Code's; once it passes, it asks again, telling the server the signature that shows. A newer request,
/// and every edit, cancels the request under way, and an answer about older text is dropped. Up and Down show the
/// other signatures, while completion's list is closed.
/// </para>
/// <para>
/// <see cref="Close"/> ends it, for every reason it ends: Escape, leaving the call, when the server answers with none,
/// the editor becoming read-only, the editor losing the keyboard focus, a server that starts or stops serving the
/// document, new colors, the caret's line scrolling out of view, and the editor no longer showing the document.
/// </para>
/// </remarks>
internal sealed class SignatureHelpController : IDisposable
{
    /// <summary>The pause after an edit or a caret move before signature help is asked again: VS Code's.</summary>
    private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(120);

    private readonly CodeEditor _editor;
    private readonly TextArea _textArea;
    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly ContentControl _content = new();
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
        _popup = new EditorPopup(editor, new AppToolTip { Content = _content });
        _textArea.TextEntered += OnTextEntered;
        _textArea.Caret.PositionChanged += OnEditOrCaretMove;
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

    /// <summary>Ends signature help: closes the popup, and cancels what is under way.</summary>
    /// <returns>Whether the popup showed.</returns>
    public bool Close()
    {
        bool wasOpen = _shown is not null;
        _shown = null;
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
        _textArea.Caret.PositionChanged -= OnEditOrCaretMove;
        _buffer.Changed -= OnEditOrCaretMove;
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
            Ask(SignatureHelpTriggerKind.TriggerCharacter, typed);
    }

    /// <summary>
    /// While active, an edit, also one that leaves the caret where it was, or a caret move outdates what is asked, and
    /// restarts the pause.
    /// </summary>
    private void OnEditOrCaretMove(object? sender, EventArgs e)
    {
        if (IsActive)
            Wait();
    }

    /// <summary>Cancels the request under way, and asks again once the pause has passed.</summary>
    private void Wait()
    {
        StopWaiting();
        ITimer? pause = null;
        pause = _document.TimeProvider.CreateTimer(
            _ =>
                _editor.Dispatcher.Post(() =>
                {
                    if (_pause == pause)
                        Ask(SignatureHelpTriggerKind.ContentChange, null);
                }),
            null,
            s_pause,
            Timeout.InfiniteTimeSpan
        );
        _pause = pause;
    }

    private void StopWaiting()
    {
        _pause?.Dispose();
        _pause = null;
        _request?.Cancel();
        _request = null;
    }

    private void Ask(int kind, char? character)
    {
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
            new SignatureHelpTrigger(kind, character, _shown),
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
                Wait();
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
    /// Shows <paramref name="help"/>'s active signature above the caret, once its documentation is read off the UI
    /// thread, as the hover reads its Markdown.
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
        // As much as a hover shows, the signature first: a server's long signature or documentation costs the UI thread
        // no more than the largest hover.
        var budget = new HoverBudget();
        string label = budget.Take(signature.Label) ?? "";
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
            var look = new CodeLook(_editor.FontFamily, _editor.FontSize, _colors?.Background, _colors?.Foreground);
            _content.Content = View(help, signature, label, blocks, look);
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

    /// <summary>
    /// The signature as code, in the editor's font, colors and background, with the active parameter in bold; before
    /// it which signature of how many, when there are more; below it the documentation.
    /// </summary>
    /// <param name="help">The signature help.</param>
    /// <param name="signature">The signature that shows.</param>
    /// <param name="label">Its label, as much of it as the budget left, with "…" where it was cut.</param>
    /// <param name="documentation">The documentation.</param>
    /// <param name="look">How code looks.</param>
    private static Control View(
        SignatureHelpInfo help,
        SignatureInfo signature,
        string label,
        IReadOnlyList<HoverBlock> documentation,
        CodeLook look
    )
    {
        var inlines = new InlineCollection();
        // The active parameter, in what is left of the label.
        int kept = label.Length == signature.Label.Length ? label.Length : label.Length - 1;
        (int start, int end) =
            signature.ActiveParameter >= 0 && signature.Parameters[signature.ActiveParameter] is var active
                ? (Math.Min(active.Start, kept), Math.Min(active.Start + active.Length, kept))
                : (0, 0);
        if (end > start)
        {
            inlines.Add(new Run(label[..start]));
            inlines.Add(new Run(label[start..end]) { FontWeight = FontWeight.Bold });
            inlines.Add(new Run(label[end..]));
        }
        else
        {
            inlines.Add(new Run(label));
        }
        var code = new Border
        {
            Background = look.Background,
            Padding = new Avalonia.Thickness(6, 4),
            Child = new TextBlock
            {
                Inlines = inlines,
                FontFamily = look.FontFamily,
                FontSize = look.FontSize,
                Foreground = look.Foreground,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        var line = new DockPanel();
        if (help.Signatures.Count > 1)
        {
            var counter = new TextBlock
            {
                Text = $"{help.ActiveSignature + 1}/{help.Signatures.Count}",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
            };
            DockPanel.SetDock(counter, Dock.Left);
            line.Children.Add(counter);
        }
        line.Children.Add(code);
        var panel = new StackPanel { Spacing = 6, Children = { line } };
        if (documentation.Count > 0)
            panel.Children.Add(HoverView.Create(documentation, look));
        return panel;
    }

    private void OnClosingChange(object? sender, EventArgs e) => Close();
}
