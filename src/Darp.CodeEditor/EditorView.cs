using Avalonia.Input;
using AvaloniaEdit.Editing;
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
/// </summary>
internal sealed class EditorView : IDisposable
{
    private readonly TextArea _area;
    private readonly Func<SearchBar?> _searchBar;

    /// <summary>Everything the view made, in the order it made them: disposed the other way round.</summary>
    private readonly IDisposable[] _features;

    private readonly EditingHelpers _helpers;
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
    public EditorView(CodeEditor editor, TextArea area, CodeDocument document, Func<SearchBar?> searchBar)
    {
        _area = area;
        _searchBar = searchBar;
        Highlighter = new BackgroundTextMateHighlighter(area.TextView, area.Document, editor.Dispatcher, null);
        _helpers = new EditingHelpers(area, document, Highlighter);
        _brackets = new BracketHighlighter(area, document, Highlighter);
        _whitespace = new WhitespaceLayer(area);
        _squiggles = new SquiggleLayer(area, document);
        SemanticHighlighter = new SemanticHighlighter(area.TextView, document, editor.Dispatcher);
        _hover = new HoverController(editor, area, document, _squiggles, Highlighter);
        _snippets = new SnippetController(area, document);
        // After the editing helpers: they see typed text first, so that a typed "(" has its ")" when it asks.
        _completion = new CompletionController(editor, area, document, Highlighter, _snippets);
        _signatureHelp = new SignatureHelpController(editor, area, document, Highlighter);
        _features =
        [
            Highlighter,
            _helpers,
            _brackets,
            _whitespace,
            _squiggles,
            SemanticHighlighter,
            _hover,
            _snippets,
            _completion,
            _signatureHelp,
        ];
    }

    /// <summary>Highlights the document by its grammar.</summary>
    public BackgroundTextMateHighlighter Highlighter { get; }

    /// <summary>Colors the server's semantic tokens.</summary>
    public SemanticHighlighter SemanticHighlighter { get; }

    /// <summary>
    /// A key, before AvaloniaEdit's: Escape, which dismisses, and in the text the keys of completion's list, of
    /// signature help, of a snippet's stops and of the editing helpers.
    /// </summary>
    /// <returns>Whether the key was the view's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && Dismiss())
            return true;
        if (!_area.IsFocused)
            return false;
        if (_completion.OnKeyDown(e) || _signatureHelp.OnKeyDown(e) || _snippets.OnKeyDown(e))
            return true;
        _helpers.OnKeyDown(e);
        return e.Handled;
    }

    /// <summary>Typed text, before AvaloniaEdit's text area takes it: a commit character commits completion's item.</summary>
    public void OnTextInput(TextInputEventArgs e)
    {
        if (_area.IsFocused)
            _completion.OnTextInput(e);
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

    /// <summary>
    /// What Escape closes, one at a time: completion, signature help, a snippet's stops while the text area has the
    /// keyboard, the hover, the search bar, in the order of VS Code's keys. Each also ends what still waits for its
    /// server.
    /// </summary>
    /// <returns>Whether it closed something the user saw.</returns>
    private bool Dismiss()
    {
        if (
            _completion.Close()
            || _signatureHelp.Close()
            || (_area.IsFocused && _snippets.End())
            || _hover.Close()
        )
        {
            return true;
        }
        if (_searchBar() is not { IsOpen: true } bar)
            return false;
        bar.Close();
        return true;
    }
}
