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

/// <summary>
/// Everything an editor does for the document it shows: created when it shows the document, and disposed when it stops.
/// It builds the features, which add their commands, and runs those commands for keys and for the app.
/// </summary>
internal sealed class EditorView : IDisposable
{
    private readonly CodeEditor _editor;
    private readonly TextArea _area;
    private readonly Func<SearchBar?> _searchBar;
    private readonly CommandTable _commands;

    /// <summary>Everything the view made, in the order it made them: disposed the other way round.</summary>
    private readonly IDisposable[] _features;

    private readonly TextChanges _changes;
    private readonly AutoClosingPairs _pairs;
    private readonly Typing _typing;
    private readonly BracketHighlighter _brackets;
    private readonly WhitespaceLayer _whitespace;
    private readonly SquiggleLayer _squiggles;
    private readonly HoverController _hover;
    private readonly SnippetController _snippets;
    private readonly CompletionController _completion;
    private readonly SignatureHelpController _signatureHelp;

    /// <param name="editor">The control that the popups belong to.</param>
    /// <param name="area">AvaloniaEdit's text area, which shows <paramref name="document"/>.</param>
    /// <param name="document">The document shown.</param>
    /// <param name="searchBar">The editor's search bar, kept across documents; null until it is first needed.</param>
    /// <param name="createSearchBar">
    /// The editor's search bar, made the first time it is needed, as few editors are searched; null without a place for
    /// it in the template.
    /// </param>
    public EditorView(
        CodeEditor editor,
        TextArea area,
        CodeDocument document,
        Func<SearchBar?> searchBar,
        Func<SearchBar?> createSearchBar
    )
    {
        _editor = editor;
        _area = area;
        _searchBar = searchBar;
        _commands = new CommandTable(area);
        Highlighter = new BackgroundTextMateHighlighter(area.TextView, area.Document, editor.Dispatcher, null);
        _changes = new TextChanges(area);
        _pairs = new AutoClosingPairs(area, document, Highlighter, _changes, () => editor.AutoClosing);
        _typing = new Typing(area, document, Highlighter, _changes, _pairs);
        _brackets = new BracketHighlighter(area, document, Highlighter);
        _whitespace = new WhitespaceLayer(area);
        _squiggles = new SquiggleLayer(area, document);
        SemanticHighlighter = new SemanticHighlighter(area.TextView, document, editor.Dispatcher);
        _hover = new HoverController(editor, area, document, _squiggles, Highlighter);
        _snippets = new SnippetController(area, document);
        // After typing: it sees typed text first, so that a typed "(" has its ")" when completion asks.
        _completion = new CompletionController(editor, area, document, Highlighter, _snippets);
        _signatureHelp = new SignatureHelpController(editor, area, document, Highlighter);
        _features =
        [
            Highlighter,
            _changes,
            _pairs,
            _typing,
            new EditingHelpers(area, document),
            _brackets,
            _whitespace,
            _squiggles,
            SemanticHighlighter,
            _hover,
            _snippets,
            _completion,
            _signatureHelp,
            new Clipboard(area, document),
            new UndoCommands(area, document),
            new FindCommands(area, createSearchBar),
        ];
        foreach (IEditorFeature feature in _features.OfType<IEditorFeature>())
            feature.AddCommands(_commands);
    }

    /// <summary>Highlights the document by its grammar.</summary>
    public BackgroundTextMateHighlighter Highlighter { get; }

    /// <summary>Colors the server's semantic tokens.</summary>
    public SemanticHighlighter SemanticHighlighter { get; }

    /// <summary>Whether a default key can run the command now; otherwise the key goes on.</summary>
    public bool CanRun(string id) => _commands.CanRun(id);

    /// <summary>
    /// Runs a command for its default key. A key that a binding handles skips <see cref="OnKeyDown"/>, so what a key does
    /// to the popups happens here: in the text, it closes the hover.
    /// </summary>
    public void RunKey(string id)
    {
        if (_area.IsFocused)
            _hover.Close();
        Run(id, _commands.TryRun);
    }

    /// <summary>Runs a command for the app, wherever the keyboard is.</summary>
    /// <returns>Whether it applied.</returns>
    public bool RunFromApp(string id) => Run(id, _commands.TryRunFromApp);

    /// <summary>
    /// A key, before AvaloniaEdit's: Escape, which dismisses, and in the text the keys of completion's list, of
    /// signature help, of a snippet's stops and of typing.
    /// </summary>
    /// <returns>Whether the key was the view's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && Dismiss())
            return true;
        if (!_area.IsFocused)
            return false;
        return _completion.OnKeyDown(e)
            || _signatureHelp.OnKeyDown(e)
            || _snippets.OnKeyDown(e)
            || _typing.OnKeyDown(e);
    }

    /// <summary>
    /// Typed text, before AvaloniaEdit's text area takes it: a commit character commits completion's item, and the text
    /// goes in by the rules of typing.
    /// </summary>
    public void OnTextInput(TextInputEventArgs e)
    {
        if (!_area.IsFocused)
            return;
        _completion.OnTextInput(e);
        if (!e.Handled)
            _typing.OnTextInput(e);
    }

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
        for (int i = _features.Length - 1; i >= 0; i--)
            _features[i].Dispose();
    }

    /// <summary>A command that moves the caret or the selection ends completion: its list was for the word at the caret.</summary>
    private bool Run(string id, Func<string, bool> run)
    {
        TextSelection before = _editor.Selection;
        if (!run(id))
            return false;
        if (_editor.Selection != before)
            _completion.Close();
        return true;
    }

    /// <summary>
    /// What Escape closes, one at a time: completion, signature help, a snippet's stops while the text area has the
    /// keyboard, the hover, the search bar, in the order of VS Code's keys. Each also ends what still waits for its
    /// server.
    /// </summary>
    /// <returns>Whether it closed something the user saw.</returns>
    private bool Dismiss()
    {
        if (_completion.Close() || _signatureHelp.Close() || (_area.IsFocused && _snippets.End()) || _hover.Close())
        {
            return true;
        }
        if (_searchBar() is not { IsOpen: true } bar)
            return false;
        bar.Close();
        return true;
    }
}
