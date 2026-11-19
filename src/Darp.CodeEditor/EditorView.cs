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
internal sealed record EditorContext(
    TemplatedControl Editor,
    TextArea Area,
    CodeDocument Document,
    Func<CodeEditorKeymap> Keymap,
    Func<CodeEditorOptions> Options,
    Func<SearchBar?> SearchBar,
    Func<SearchBar?> CreateSearchBar,
    Action<TextRange> Reveal
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

    public EditorView(EditorContext context)
    {
        _context = context;
        TextArea area = context.Area;
        CodeDocument document = context.Document;
        Highlighter = new BackgroundTextMateHighlighter(area.TextView, area.Document, context.Editor.Dispatcher, null);
        _changes = new TextChanges(area);
        _pairs = new AutoClosingPairs(area, document, Highlighter, _changes, context.Options);
        _typing = new Typing(area, document, Highlighter, _changes, _pairs);
        _clipboard = new Clipboard(area, document);
        _brackets = new BracketMatching(area, document, Highlighter, _changes, context.Options);
        _occurrences = new OccurrenceHighlighter(area, document, _changes, context.Options);
        _whitespace = new WhitespaceLayer(area);
        _squiggles = new SquiggleLayer(area, document);
        SemanticHighlighter = new SemanticHighlighter(area.TextView, document, context.Editor.Dispatcher);
        _hover = new HoverController(context.Editor, area, document, _squiggles, Highlighter);
        _snippets = new SnippetController(area, document);
        // After typing: it sees typed text first, so that a typed "(" has its ")" when completion asks.
        _completion = new CompletionController(context.Editor, area, document, Highlighter, _snippets);
        _signatureHelp = new SignatureHelpController(context.Editor, area, document, Highlighter);
        _search = new FindController(context, _changes);
        _features =
        [
            Highlighter,
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
        _commands = new CommandTable(area);
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
        KeyResolution resolution = KeyResolution.Of(_context.Keymap(), _pending, key);
        _pending = null;
        foreach (string command in resolution.Commands)
        {
            if (_commands.TryRun(command))
                return true;
        }
        if (resolution.Waits)
        {
            _pending = key;
            return true;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && Dismiss())
            return true;
        return _context.Area.IsFocused
            && (
                _completion.OnKeyDown(e)
                || _signatureHelp.OnKeyDown(e)
                || _snippets.OnKeyDown(e)
                || _typing.OnKeyDown(e)
                || CaretKeys.OnKeyDown(_context.Area, e)
            );
    }

    /// <summary>
    /// Typed text, before AvaloniaEdit's text area takes it: a commit character commits completion's item, and the text
    /// goes in by the rules of typing.
    /// </summary>
    public void OnTextInput(TextInputEventArgs e)
    {
        if (!_context.Area.IsFocused)
            return;
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
    public bool RunCommand(string id) => _commands.TryRun(id);

    public void Cut() => _clipboard.Cut();

    public void Copy() => _clipboard.Copy();

    /// <summary>As the key pastes, through the command, whose bindings paste a copied line as a line.</summary>
    public void Paste() => AvaloniaEdit.ApplicationCommands.Paste.Execute(null, _context.Area);

    /// <summary>
    /// Ends the wait for a sequence's second stroke, as a click, the focus leaving or a new keymap does.
    /// </summary>
    public void EndWait() => _pending = null;

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
        _completion.Colors = colors;
        _completion.Icons = icons;
        _snippets.Colors = colors;
        _signatureHelp.Colors = colors;
        _brackets.Colors = colors;
        _occurrences.Colors = colors;
        _whitespace.Colors = colors;
        _squiggles.Colors = colors;
        _hover.Colors = colors;
        _search.Colors = colors;
        SemanticHighlighter.Theme = theme;
    }

    public void Dispose()
    {
        for (int i = _features.Length - 1; i >= 0; i--)
            _features[i].Dispose();
    }
}
