using Avalonia.Controls.Primitives;
using Avalonia.Input;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Completion;
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Popups;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor;

/// <summary>What the features of an editor's view get; never the <see cref="CodeEditor"/> itself.</summary>
/// <param name="Editor">The control that the popups belong to, and whose font code takes.</param>
/// <param name="Area">AvaloniaEdit's text area, which shows <paramref name="Document"/>.</param>
/// <param name="Document">The document shown.</param>
/// <param name="Keymap">The editor's keymap, as it is now.</param>
/// <param name="SearchBar">
/// The editor's search bar, made the first time it is asked for, and kept across documents; null without a place for
/// it in the template.
/// </param>
internal sealed record EditorContext(
    TemplatedControl Editor,
    TextArea Area,
    CodeDocument Document,
    Func<CodeEditorKeymap> Keymap,
    Func<SearchBar?> SearchBar
);

/// <summary>
/// Everything an editor does for the document it shows: created when it shows the document, and disposed when it stops.
/// It builds the features, which add their commands, and runs them by the keymap.
/// </summary>
internal sealed class EditorView : IDisposable
{
    private readonly EditorContext _context;
    private readonly CommandTable _commands;
    private readonly EditingHelpers _helpers;
    private readonly Clipboard _clipboard;
    private readonly BracketHighlighter _brackets;
    private readonly WhitespaceLayer _whitespace;
    private readonly SquiggleLayer _squiggles;
    private readonly HoverController _hover;
    private readonly SnippetController _snippets;
    private readonly CompletionController _completion;
    private readonly SignatureHelpController _signatureHelp;
    private readonly FindCommands _search;

    /// <summary>The first stroke of a sequence while the view waits for its second; null otherwise.</summary>
    private KeyStroke? _pending;

    public EditorView(EditorContext context)
    {
        _context = context;
        TextArea area = context.Area;
        CodeDocument document = context.Document;
        Highlighter = new BackgroundTextMateHighlighter(area.TextView, area.Document, context.Editor.Dispatcher, null);
        _helpers = new EditingHelpers(area, document, Highlighter);
        _clipboard = new Clipboard(area, document);
        _brackets = new BracketHighlighter(area, document, Highlighter);
        _whitespace = new WhitespaceLayer(area);
        _squiggles = new SquiggleLayer(area, document);
        SemanticHighlighter = new SemanticHighlighter(area.TextView, document, context.Editor.Dispatcher);
        _hover = new HoverController(context.Editor, area, document, _squiggles, Highlighter);
        _snippets = new SnippetController(area, document);
        // After the editing helpers: they see typed text first, so that a typed "(" has its ")" when it asks.
        _completion = new CompletionController(context.Editor, area, document, Highlighter, _snippets);
        _signatureHelp = new SignatureHelpController(context.Editor, area, document, Highlighter);
        _search = new FindCommands(area, context.SearchBar);
        _commands = new CommandTable(area);
        foreach (IEditorFeature feature in (IEditorFeature[])[_helpers, _completion, _signatureHelp, _search])
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
    /// Escape, which dismisses, and in the text the keys of completion's list, of signature help, of a snippet's stops
    /// and of the editing helpers.
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
        KeyResolution resolution = KeyResolution.Of(_context.Keymap(), _pending, e);
        _pending = null;
        foreach (string command in resolution.Commands)
        {
            if (_commands.TryRun(command))
                return true;
        }
        if (resolution.Sequence is { } first)
        {
            _pending = first;
            return true;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && Dismiss())
            return true;
        return _context.Area.IsFocused
            && (
                _completion.OnKeyDown(e)
                || _signatureHelp.OnKeyDown(e)
                || _snippets.OnKeyDown(e)
                || _helpers.OnKeyDown(e)
            );
    }

    /// <summary>
    /// Typed text, before AvaloniaEdit's text area takes it: a commit character commits completion's item.
    /// </summary>
    public void OnTextInput(TextInputEventArgs e)
    {
        if (_context.Area.IsFocused)
            _completion.OnTextInput(e);
    }

    /// <summary>Runs a command, as its key does, where it applies.</summary>
    /// <returns>Whether it applied.</returns>
    public bool RunCommand(string id) => _commands.TryRun(id);

    /// <summary>
    /// Ends the wait for a sequence's second stroke, as a click, the focus leaving or a new keymap does.
    /// </summary>
    public void EndWait() => _pending = null;

    /// <summary>
    /// What Escape closes, one at a time: completion, signature help, a snippet's stops while the text area has the
    /// keyboard, and the hover, in the order of VS Code's keys. Each also ends what still waits for its server.
    /// </summary>
    /// <returns>Whether it closed something the user saw.</returns>
    public bool Dismiss() =>
        _completion.Close() || _signatureHelp.Close() || (_context.Area.IsFocused && _snippets.End()) || _hover.Close();

    /// <summary>Closes the popups, once the keyboard has left the editor.</summary>
    public void OnFocusLeft()
    {
        _hover.Close();
        _completion.Close();
        _signatureHelp.Close();
    }

    /// <summary>Nothing completes, asks for signatures or tabs through a snippet where the user cannot type.</summary>
    public void OnReadOnly()
    {
        _completion.Close();
        _signatureHelp.Close();
        _snippets.End();
    }

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public void SetColors(ColorThemeFile? theme, TextAreaColors? colors, SymbolIconColors? icons)
    {
        _completion.Colors = colors;
        _completion.Icons = icons;
        _snippets.Colors = colors;
        _signatureHelp.Colors = colors;
        _brackets.Colors = colors;
        _whitespace.Colors = colors;
        _squiggles.Colors = colors;
        _hover.Colors = colors;
        SemanticHighlighter.Theme = theme;
    }

    public void Dispose()
    {
        _helpers.Dispose();
        _clipboard.Dispose();
        _brackets.Dispose();
        _whitespace.Dispose();
        _hover.Dispose();
        _completion.Dispose();
        _snippets.Dispose();
        _signatureHelp.Dispose();
        _search.Dispose();
        _squiggles.Dispose();
        SemanticHighlighter.Dispose();
        Highlighter.Dispose();
    }
}
