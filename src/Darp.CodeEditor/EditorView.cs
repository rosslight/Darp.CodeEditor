using Avalonia.Controls.Primitives;
using Avalonia.Input;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Completion;
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Popups;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor;

/// <summary>What the features of an editor's view get; never the <see cref="CodeEditor"/> itself.</summary>
/// <param name="Editor">The control that the popups belong to, and whose font code takes.</param>
/// <param name="Area">AvaloniaEdit's text area, which shows <paramref name="Document"/>.</param>
/// <param name="Document">The document shown.</param>
/// <param name="Keymap">The editor's keymap, as it is now.</param>
/// <param name="Options">The editor's settings, as they are now.</param>
/// <param name="SearchBar">The editor's search bar, kept across documents; null until it is first needed.</param>
/// <param name="CreateSearchBar">
/// The editor's search bar, made the first time it is needed, as few editors are searched; null without a place for it
/// in the template.
/// </param>
/// <param name="Reveal">
/// Brings a range into view as <see cref="CodeEditor.ScrollTo"/> does: centered unless visible.
/// </param>
/// <param name="Background">Where work off the UI thread runs: the thread pool, or what a test holds.</param>
internal sealed record EditorContext(
    TemplatedControl Editor,
    TextArea Area,
    CodeDocument Document,
    Func<CodeEditorKeymap> Keymap,
    Func<CodeEditorOptions> Options,
    Func<SearchBar?> SearchBar,
    Func<SearchBar?> CreateSearchBar,
    Action<TextRange> Reveal,
    TaskScheduler Background
);

/// <summary>
/// Everything an editor does for the document it shows: created when it shows the document, and disposed when it stops.
/// It builds the features, which add their commands, and runs them by the keymap.
/// </summary>
internal sealed class EditorView : IDisposable
{
    private readonly EditorContext _context;
    private readonly CommandTable _commands;

    /// <summary>Everything the view made, in the order it made them: disposed the other way round.</summary>
    private readonly IDisposable[] _features;

    private readonly CaretMoves _caretMoves;
    private readonly TextChanges _changes;
    private readonly AutoClosingPairs _pairs;
    private readonly Typing _typing;
    private readonly Clipboard _clipboard;
    private readonly BracketMatching _brackets;
    private readonly OccurrenceHighlighter _occurrences;
    private readonly WhitespaceLayer _whitespace;
    private readonly SquiggleLayer _squiggles;
    private readonly HoverController _hover;
    private readonly SnippetController _snippets;
    private readonly CompletionController _completion;
    private readonly SignatureHelpController _signatureHelp;
    private readonly FindController _search;

    /// <summary>The key that started a sequence while the view waits for its second stroke; null otherwise.</summary>
    private PressedKey? _pending;

    /// <summary>
    /// What is left of a key's resolution after a command that decided later to do nothing, while that key is raised
    /// again: the commands after that one, and whether the key starts a sequence; null otherwise.
    /// </summary>
    private KeyResolution? _resumed;

    /// <summary>How many waits ended, so that a key that would go on after one, by an earlier keymap, does not.</summary>
    private int _waitsEnded;

    public EditorView(EditorContext context)
    {
        _context = context;
        TextArea area = context.Area;
        CodeDocument document = context.Document;
        // Before the features, which run each other's commands; they add them once all are made.
        _commands = new CommandTable(area);
        Highlighter = new BackgroundTextMateHighlighter(area.TextView, area.Document, context.Editor.Dispatcher, null);
        _caretMoves = new CaretMoves(context.Editor, area);
        _changes = new TextChanges(area);
        _pairs = new AutoClosingPairs(area, document, Highlighter, _changes, context.Options);
        _typing = new Typing(area, document, Highlighter, _changes, _pairs);
        _clipboard = new Clipboard(area, document);
        _brackets = new BracketMatching(area, document, Highlighter, _changes, context.Options);
        _occurrences = new OccurrenceHighlighter(area, document, _changes, context.Options, context.Reveal);
        _whitespace = new WhitespaceLayer(area);
        _squiggles = new SquiggleLayer(area, document);
        SemanticHighlighter = new SemanticHighlighter(area.TextView, document, context.Editor.Dispatcher);
        _hover = new HoverController(context.Editor, area, document, _squiggles, Highlighter);
        _snippets = new SnippetController(area, document, _changes);
        // After typing: it sees typed text first, so that a typed "(" has its ")" when completion asks.
        _completion = new CompletionController(
            context.Editor,
            area,
            document,
            Highlighter,
            _snippets,
            _changes,
            _caretMoves,
            context.Background,
            RunCommand
        );
        _signatureHelp = new SignatureHelpController(context.Editor, area, document, Highlighter, _caretMoves);
        _search = new FindController(context, _changes);
        _features =
        [
            Highlighter,
            _caretMoves,
            _changes,
            new UndoCommands(area, document),
            new Comments(area, document, _changes),
            new LineCommands(area, document, Highlighter, _changes),
            _pairs,
            _typing,
            _clipboard,
            // After the clipboard: its commands run first.
            new LineClipboard(area, document, _changes, _clipboard, context.Options),
            new WordEditing(area, _changes, _pairs),
            _brackets,
            new SmartSelect(area, document, Highlighter, _changes),
            _occurrences,
            _whitespace,
            _squiggles,
            SemanticHighlighter,
            _hover,
            _snippets,
            _completion,
            _signatureHelp,
            _search,
        ];
        foreach (IEditorFeature feature in _features.OfType<IEditorFeature>())
            feature.AddCommands(_commands);
    }

    /// <summary>Highlights the document by its grammar.</summary>
    public BackgroundTextMateHighlighter Highlighter { get; }

    /// <summary>Colors the server's semantic tokens.</summary>
    public SemanticHighlighter SemanticHighlighter { get; }

    /// <summary>The first stroke of a key sequence while the view waits for its second; null otherwise.</summary>
    public KeyGesture? PendingKeys => _pending?.ToGesture();

    /// <summary>
    /// A key, before AvaloniaEdit's: first the keymap's commands, as <see cref="KeyResolution"/> resolves the key, then
    /// Escape, which dismisses, and in the text the keys of completion's list, of signature help, of a snippet's stops,
    /// of typing and of the caret.
    /// </summary>
    /// <returns>Whether the key was the view's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        // A modifier alone neither completes nor ends a key sequence.
        if (
            e.Key
            is Key.LeftCtrl
                or Key.RightCtrl
                or Key.LeftShift
                or Key.RightShift
                or Key.LeftAlt
                or Key.RightAlt
                or Key.LWin
                or Key.RWin
        )
            return false;
        var key = PressedKey.Of(e);
        KeyResolution resolution = _resumed ?? KeyResolution.Of(_context.Keymap(), _pending, key);
        _pending = null;
        _resumed = null;
        int waitsEnded = _waitsEnded;
        for (int i = 0; i < resolution.Commands.Count; i++)
        {
            var rest = new KeyResolution([.. resolution.Commands.Skip(i + 1)], resolution.Waits);
            if (Run(resolution.Commands[i], () => GoOn(key, rest, waitsEnded)))
                return true;
        }
        if (resolution.Waits)
        {
            _pending = key;
            return true;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && Dismiss())
            return true;
        if (!_context.Area.IsFocused)
            return false;
        _caretMoves.OnKeyboard();
        return _completion.OnKeyDown(e)
            || _signatureHelp.OnKeyDown(e)
            || _snippets.OnKeyDown(e)
            || _typing.OnKeyDown(e)
            || CaretKeys.OnKeyDown(_context.Area, e);
    }

    /// <summary>
    /// Raises <paramref name="key"/> again, as it goes on after a command that decided later to do nothing: by
    /// <paramref name="rest"/>, to the commands after that one, then to the second stroke of a sequence it starts, or
    /// as an ordinary key, as it would have gone on at once. A wait that ended since, as by a new keymap, drops it.
    /// </summary>
    private void GoOn(PressedKey key, KeyResolution rest, int waitsEnded)
    {
        if (waitsEnded != _waitsEnded)
            return;
        _resumed = rest;
        try
        {
            _context.Area.RaiseEvent(
                new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Source = _context.Area,
                    Key = key.Key,
                    KeyModifiers = key.Modifiers,
                    KeySymbol = key.Symbol,
                }
            );
        }
        finally
        {
            _resumed = null;
        }
    }

    /// <summary>
    /// Typed text, before AvaloniaEdit's text area takes it: a commit character commits completion's item, and the text
    /// goes in by the rules of typing.
    /// </summary>
    public void OnTextInput(TextInputEventArgs e)
    {
        if (!_context.Area.IsFocused)
            return;
        _caretMoves.OnKeyboard();
        _completion.OnTextInput(e);
        if (!e.Handled)
            _typing.OnTextInput(e);
    }

    public void OnOptionsChanged(CodeEditorOptions old, CodeEditorOptions now)
    {
        _pairs.OnOptionsChanged(old.AutoClosing, now.AutoClosing);
        _brackets.OnOptionsChanged(old.MatchBrackets, now.MatchBrackets);
        _occurrences.OnOptionsChanged(old.OccurrencesHighlight, now.OccurrencesHighlight);
    }

    /// <summary>Runs a command, as its key does, where it applies.</summary>
    /// <returns>Whether it applied.</returns>
    public bool RunCommand(string id) => Run(id);

    /// <summary>
    /// Runs a command where it applies. One that moves the caret or the selection ends completion, as in VS Code, such
    /// as F7 or a jump to a bracket: its list was for the word at the caret. Completion's own end it as they commit.
    /// </summary>
    /// <param name="id">The command.</param>
    /// <param name="goOn">What the key goes on to after a command that decided later to do nothing.</param>
    /// <returns>Whether it applied.</returns>
    private bool Run(string id, Action? goOn = null)
    {
        TextSelection before = _changes.Selection;
        if (!_commands.TryRun(id, goOn))
            return false;
        if (_changes.Selection != before)
            _completion.Close();
        return true;
    }

    public void Cut() => _clipboard.Cut();

    public void Copy() => _clipboard.Copy();

    /// <summary>As the key pastes, through the command, whose bindings paste a copied line as a line.</summary>
    public void Paste() => AvaloniaEdit.ApplicationCommands.Paste.Execute(null, _context.Area);

    /// <summary>
    /// Ends the wait for a sequence's second stroke, as a click, the focus leaving or a new keymap does, and drops what
    /// a key that waits for a command, as for completion's filter, would go on to.
    /// </summary>
    public void EndWait()
    {
        _pending = null;
        _waitsEnded++;
    }

    /// <summary>
    /// What Escape closes, one at a time: completion, signature help, a snippet's stops while the text area has the
    /// keyboard, the hover and the search bar, in the order of VS Code's keys. Each also ends what still waits for its
    /// server.
    /// </summary>
    /// <returns>Whether it closed something the user saw.</returns>
    public bool Dismiss() =>
        _completion.Close()
        || _signatureHelp.Close()
        || (_context.Area.IsFocused && _snippets.End())
        || _hover.Close()
        || _search.Close();

    /// <summary>The occurrences of the symbol at the caret, for tests.</summary>
    internal IReadOnlyList<Occurrence> Occurrences => _occurrences.Highlights;

    /// <summary>Whether the view asks for the occurrences, for tests.</summary>
    internal bool IsAskingForOccurrences => _occurrences.IsAsking;

    /// <summary>Closes the popups, and clears the occurrences, once the keyboard has left the editor.</summary>
    public void OnFocusLeft()
    {
        _occurrences.Clear();
        _hover.Close();
        _completion.Close();
        _signatureHelp.Close();
    }

    /// <summary>
    /// Nothing completes, asks for signatures or tabs through a snippet where the user cannot type, and the search bar
    /// hides its replace row.
    /// </summary>
    public void OnReadOnly()
    {
        _completion.Close();
        _signatureHelp.Close();
        _snippets.End();
        _search.OnReadOnly();
    }

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public void SetColors(ColorThemeFile? theme, TextAreaColors? colors, SymbolIconColors? icons)
    {
        PopupColors popups =
            theme is not null && colors is not null ? PopupColors.From(theme, colors) : PopupColors.Default;
        _completion.Colors = popups;
        _completion.Icons = icons;
        _snippets.Colors = colors;
        _signatureHelp.Colors = popups;
        _brackets.Colors = colors;
        _occurrences.Colors = colors;
        _whitespace.Colors = colors;
        _squiggles.Colors = colors;
        _hover.Colors = popups;
        _search.Colors = colors;
        SemanticHighlighter.Theme = theme;
    }

    public void Dispose()
    {
        for (int i = _features.Length - 1; i >= 0; i--)
            _features[i].Dispose();
    }
}
