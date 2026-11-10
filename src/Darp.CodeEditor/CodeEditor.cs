using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Search;
using TextMateSharp.Internal.Types;
using EditorSelection = AvaloniaEdit.Editing.Selection;

namespace Darp.CodeEditor;

/// <summary>
/// Shows and edits a <see cref="CodeDocument"/>: syntax highlighting, search and, when the document's language has a
/// server, completion, hover, signature help and diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// The editor is a view. The app owns the document, and the document keeps the text, the undo history, the
/// indentation and the diagnostics. Several editors can show one document, each with its own caret.
/// </para>
/// <para>
/// The caret, the selection and the scroll position belong to the editor, and last as long as it does, as a list's
/// scroll position does. Setting <see cref="Document"/> shows another document from its start, and a tab control may
/// create a new editor each time a tab shows. To keep them, keep the editors, such as one per open tab, or save them
/// with <see cref="SaveViewState"/> before the switch and give them back with <see cref="RestoreViewState"/>.
/// </para>
/// <para>
/// Use it on the app's main UI thread only, because AvaloniaEdit's editor works only there; on another thread it fails
/// at once. The editor posts its own work to its <c>Dispatcher</c>. It never waits for
/// a language server: text and colors come first, and language features
/// follow when the server answers. Out of the visual tree, the editor drops its subscriptions on the document, its
/// pending requests and its rendering resources, so a discarded editor is collected even while its document lives
/// on. <see cref="Document"/> keeps its value, so the editor can come back.
/// </para>
/// <para>
/// The editor needs <see cref="CodeEditorStyles"/> in the app's styles. The text area takes its colors from
/// <see cref="ColorTheme"/>: syntax and language-server colors, background, selection, caret, line numbers, search and
/// bracket matches, and the squiggles under diagnostics. Everything around the text takes the app's look: completion,
/// hover and signature help look like the app's flyouts and tooltips, and the search bar and scroll bars are the app's
/// controls. Code inside popups keeps the editor's colors, background and font.
/// </para>
/// <para>
/// Built in, with no settings: the current line is highlighted, whitespace shows inside the selection, and the view
/// scrolls past the last line. Lines of up to 3,000 characters show and edit in full; a longer line shows its first
/// 3,000 characters and collapses the rest into "...", which can't be edited. Editing follows the language's rules, as
/// VS Code applies them: the bracket next to the caret and its match are boxed, pairs close as you type, typing their
/// closing character steps over it and Backspace deletes an empty pair, opening characters surround the selection, new
/// lines are indented by the brackets and the indentation rules, a line that comes to match the decrease rule outdents,
/// and comments toggle with the line comment, or else the block comment. Brackets in strings and comments don't match,
/// and pairs that the language excludes there don't close. In a rectangle selection, pairs close, step over and delete,
/// and opening characters surround, on every line; Enter breaks only the caret's line and ends the rectangle. Tab and
/// new lines indent by the document's <see cref="CodeDocument.Indentation"/>.
/// Hover shows the server's Markdown, with code blocks highlighted by the document's grammar, below the messages of
/// the diagnostics under the pointer. Completion items show VS Code's codicons, in the color theme's symbol colors.
/// The font defaults to a monospace one; set <c>FontFamily</c> and <c>FontSize</c> as for any control.
/// </para>
/// <para>
/// The search bar searches as the user types, in the background: plain text or a .NET regular expression, line by
/// line, with Match Case and Match Whole Word. A search stops after 1 second, as a regular expression such as
/// <c>(a+)+$</c> can take far longer, and keeps 10,000 matches at most. The bar says so when a search stops early, and
/// Replace All then does nothing: it never replaces only some of the matches.
/// </para>
/// <para>
/// The editor handles only its own keys: editing, the clipboard (Ctrl+X, Ctrl+C, Ctrl+V, Ctrl+A), navigation, search
/// (Ctrl+F, Ctrl+H, F3, Shift+F3), completion (Ctrl+Space), comments (Ctrl+/) and indentation (Tab, Shift+Tab). On
/// macOS, Cmd takes Ctrl's place for the clipboard, search and comments, and Cmd+Alt+F opens the replace bar, as
/// AvaloniaEdit decides by the operating system. Other keys, such as F5 or Ctrl+S, go on to the app's key bindings. A
/// right click opens the editor's context menu: Cut, Copy, Paste and Select All. For an app's Edit menu,
/// <see cref="Cut"/>, <see cref="Copy"/>, <see cref="Paste"/> and <see cref="SelectAll"/> do the same, and the
/// document's <c>Undo</c> and <c>Redo</c> the rest.
/// </para>
/// <para>Members that take offsets or ranges clamp them to the text.</para>
/// </remarks>
public class CodeEditor : TemplatedControl
{
    /// <summary>Defines the <see cref="Document"/> property.</summary>
    public static readonly StyledProperty<CodeDocument?> DocumentProperty = AvaloniaProperty.Register<
        CodeEditor,
        CodeDocument?
    >(nameof(Document));

    /// <summary>Defines the <see cref="IsReadOnly"/> property.</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<CodeEditor, bool>(
        nameof(IsReadOnly)
    );

    /// <summary>Defines the <see cref="WordWrap"/> property.</summary>
    public static readonly StyledProperty<bool> WordWrapProperty = AvaloniaProperty.Register<CodeEditor, bool>(
        nameof(WordWrap)
    );

    /// <summary>Defines the <see cref="ShowLineNumbers"/> property.</summary>
    public static readonly StyledProperty<bool> ShowLineNumbersProperty = AvaloniaProperty.Register<CodeEditor, bool>(
        nameof(ShowLineNumbers),
        defaultValue: true
    );

    /// <summary>Defines the <see cref="ColorTheme"/> property.</summary>
    public static readonly StyledProperty<CodeEditorColorTheme> ColorThemeProperty = AvaloniaProperty.Register<
        CodeEditor,
        CodeEditorColorTheme
    >(nameof(ColorTheme), defaultValue: CodeEditorColorTheme.LightPlus);

    /// <summary>Defines the <see cref="Selection"/> property.</summary>
    public static readonly DirectProperty<CodeEditor, TextSelection> SelectionProperty =
        AvaloniaProperty.RegisterDirect<CodeEditor, TextSelection>(
            nameof(Selection),
            editor => editor.Selection,
            (editor, value) => editor.Selection = value
        );

    /// <summary>Defines the <see cref="CaretPosition"/> property.</summary>
    public static readonly DirectProperty<CodeEditor, TextPosition> CaretPositionProperty =
        AvaloniaProperty.RegisterDirect<CodeEditor, TextPosition>(
            nameof(CaretPosition),
            editor => editor.CaretPosition
        );

    /// <summary>AvaloniaEdit's editor, from the template. The editor wraps it, so that its API stays out of ours.</summary>
    private TextEditor? _part;

    private bool _isAttached;

    /// <summary>
    /// The document the part shows: <see cref="Document"/>, while the editor is in the visual tree, has its template
    /// and the document is not disposed. Null while the part shows nothing.
    /// </summary>
    private CodeDocument? _shown;

    /// <summary>
    /// The part's document while it shows nothing, empty and never edited: the part is read-only meanwhile.
    /// AvaloniaEdit's own editor always has a document: without one, its template logs a binding warning, and a click
    /// throws.
    /// </summary>
    private readonly TextDocument _emptyBuffer = new();

    /// <summary>Highlights the shown document.</summary>
    private BackgroundTextMateHighlighter? _highlighter;

    /// <summary>The editing helpers of the shown document.</summary>
    private EditingHelpers? _helpers;

    /// <summary>Marks the bracket next to the caret in the shown document, and its match.</summary>
    private BracketHighlighter? _brackets;

    /// <summary>Shows the spaces and tabs inside the selection.</summary>
    private WhitespaceLayer? _whitespace;

    /// <summary>The template's panel, which the search bar goes into, over the text.</summary>
    private Panel? _root;

    /// <summary>
    /// The search and replace bar, from the first time it opens: every editor shows, few are searched, so an editor
    /// nobody searches in makes none.
    /// </summary>
    private SearchBar? _searchBar;

    /// <summary>
    /// AvaloniaEdit's search commands, for the part's text area: they open and use the editor's bar, also when code
    /// executes them, instead of AvaloniaEdit's search panel.
    /// </summary>
    private readonly RoutedCommandBinding[] _searchCommands;

    /// <summary>
    /// Where the editor is in <see cref="Document"/> while the part cannot tell: while it shows nothing, and while the
    /// scroll position waits for the first layout.
    /// </summary>
    private CodeEditorViewState? _place;

    /// <summary>A range that <see cref="ScrollTo"/> brings into view once the shown document is laid out.</summary>
    private (TextSnapshot Snapshot, TextRange Range)? _pendingReveal;

    /// <summary>Whether the shown document waits for its first layout, which scrolling needs.</summary>
    private bool _waitingForLayout;

    /// <summary>
    /// The scroll position at the last layout or scroll of the shown document. Out of the visual tree, the view loses
    /// the font size it inherits, and with it the height of its lines, before the editor learns that it left.
    /// </summary>
    private CodeEditorViewState? _laidOut;

    private TextSelection _selection;

    private TextPosition _caretPosition = new(1, 1);

    /// <summary>The theme whose colors the part shows.</summary>
    private ColorThemeFile? _theme;

    /// <summary>The colors of <see cref="_theme"/> besides the tokens.</summary>
    private TextAreaColors? _colors;

    /// <summary>The grammar loaded last, with the theme it was loaded with.</summary>
    private TextMateGrammar? _grammar;

    private GrammarLoad? _loading;

    static CodeEditor()
    {
        FocusableProperty.OverrideDefaultValue<CodeEditor>(true);
        // Tab moves the focus into the text area, so the editor itself is no stop.
        IsTabStopProperty.OverrideDefaultValue<CodeEditor>(false);
    }

    /// <summary>Creates an editor, on the app's main UI thread.</summary>
    /// <exception cref="InvalidOperationException">This is not the app's main UI thread.</exception>
    public CodeEditor()
    {
        // AvaloniaEdit checks Dispatcher.UIThread: on another dispatcher, the editor would fail later and elsewhere.
        if (Application.Current?.Dispatcher != Dispatcher)
            throw new InvalidOperationException("The editor works only on the app's main UI thread.");
        ContextMenu = CreateContextMenu();
        _searchCommands =
        [
            new(ApplicationCommands.Find, (_, _) => OpenSearch()),
            new(ApplicationCommands.Replace, (_, _) => OpenSearch(replace: true)),
            new(SearchCommands.FindNext, (_, _) => FindNext(previous: false)),
            new(SearchCommands.FindPrevious, (_, _) => FindNext(previous: true)),
        ];
        // Ahead of AvaloniaEdit's own keys, which its text area handles as they bubble.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// The document the editor shows. Without one, the editor is empty and read-only.
    /// </summary>
    public CodeDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Whether the user can only read, select, copy and search.</summary>
    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>Whether long lines wrap at the editor's width.</summary>
    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    /// <summary>Whether line numbers show in the left margin. Default: true.</summary>
    public bool ShowLineNumbers
    {
        get => GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    /// <summary>
    /// The colors of the text area: a VS Code color theme. Default: the <c>CodeEditorColorTheme</c> resource, which
    /// <see cref="CodeEditorStyles"/> defines as <see cref="CodeEditorColorTheme.LightPlus"/> for light theme variants and
    /// <see cref="CodeEditorColorTheme.DarkPlus"/> for dark ones, so editors follow the app's theme variant and any
    /// <c>ThemeVariantScope</c> around them. Where no resource matches, Light+.
    /// </summary>
    /// <remarks>
    /// To give every editor other colors, override <c>CodeEditorColorTheme</c> in the app's theme dictionaries.
    /// Setting the property on one editor fixes its colors whatever the theme variant. To make one editor dark in a
    /// light app, prefer a <c>ThemeVariantScope</c> around it: its popups then turn dark too.
    /// </remarks>
    public CodeEditorColorTheme ColorTheme
    {
        get => GetValue(ColorThemeProperty);
        set => SetValue(ColorThemeProperty, value);
    }

    /// <summary>
    /// The selection and the caret. Without a selection, <see cref="TextSelection.Anchor"/> and
    /// <see cref="TextSelection.Caret"/> are the same offset.
    /// </summary>
    public TextSelection Selection
    {
        get => _selection;
        set
        {
            int length = Document?.CurrentSnapshot.Length ?? 0;
            var selection = new TextSelection(Math.Clamp(value.Anchor, 0, length), Math.Clamp(value.Caret, 0, length));
            if (_shown is not null && _part is { } part)
            {
                Select(part, selection);
                return;
            }
            if (Document is not { } document)
                return;
            TextSnapshot snapshot = document.CurrentSnapshot;
            _place = new CodeEditorViewState(document, snapshot, selection, _place?.ToCurrent().Scroll ?? default);
            SetSelection(selection, snapshot.GetPosition(selection.Caret));
        }
    }

    /// <summary>The caret's line and column, for a status bar.</summary>
    public TextPosition CaretPosition => _caretPosition;

    /// <summary>
    /// Scrolls <paramref name="range"/> into view: not at all when it is visible, centered when it is not. A range
    /// taller or wider than the view shows from its start.
    /// </summary>
    public void ScrollTo(TextRange range)
    {
        if (Document is not { } document)
            return;
        TextSnapshot snapshot = document.CurrentSnapshot;
        (int start, int length) = snapshot.Clamp(range);
        if (_shown is not null && _part is { } part && !_waitingForLayout)
        {
            Reveal(part, new TextRange(start, length));
            return;
        }
        // The latest request wins: a restored scroll position that still waits for the layout gives way.
        _pendingReveal = (snapshot, new TextRange(start, length));
        if (_shown is not null)
            _place = null;
    }

    /// <summary>
    /// Saves where the editor is in its document: the caret, the selection and the scroll position. Call it before
    /// the editor shows another document or goes away, and give the state back with <see cref="RestoreViewState"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The editor shows no document.</exception>
    public CodeEditorViewState SaveViewState()
    {
        CodeDocument document = Document ?? throw new InvalidOperationException("The editor shows no document.");
        if (_shown is not null && _part is { } part)
            return CaptureViewState(part, document);
        return _place ?? new CodeEditorViewState(document, document.CurrentSnapshot, _selection, default);
    }

    /// <summary>
    /// Returns to a saved place in the document the editor shows, following the edits made since. An editor that is
    /// not laid out yet scrolls there once it is.
    /// </summary>
    /// <exception cref="ArgumentException">The state belongs to another document than <see cref="Document"/>.</exception>
    public void RestoreViewState(CodeEditorViewState state)
    {
        if (state.Document != Document)
            throw new ArgumentException("The state belongs to another document.", nameof(state));
        CodeEditorViewState place = state.ToCurrent();
        _pendingReveal = null;
        if (_shown is not null && _part is { } part)
        {
            Select(part, place.Selection);
            if (_waitingForLayout)
                _place = place;
            else
                ScrollToPlace(part, place.Scroll);
            return;
        }
        _place = place;
        SetSelection(place.Selection, place.Snapshot.GetPosition(place.Selection.Caret));
    }

    /// <summary>
    /// Opens the search bar, as Ctrl+F does, or the replace bar, as Ctrl+H does, and puts the keyboard in its query. A
    /// selection within one line becomes the search text. A read-only editor opens the search bar either way.
    /// </summary>
    public void OpenSearch(bool replace = false)
    {
        if (_part is { } part && CreatedSearchBar() is { } bar)
            bar.Open(replace && !IsReadOnly, SelectedLineText(part.TextArea));
    }

    /// <summary>Moves the selection to the clipboard, as Ctrl+X does. No effect in a read-only editor.</summary>
    public void Cut()
    {
        if (_shown is not null)
            _part?.Cut();
    }

    /// <summary>Copies the selection to the clipboard, as Ctrl+C does.</summary>
    public void Copy()
    {
        if (_shown is not null)
            _part?.Copy();
    }

    /// <summary>Replaces the selection with the clipboard's text, as Ctrl+V does. No effect in a read-only editor.</summary>
    public void Paste()
    {
        if (_shown is not null)
            _part?.Paste();
    }

    /// <summary>Selects the whole text, as Ctrl+A does.</summary>
    public void SelectAll() => Selection = new TextSelection(0, Document?.CurrentSnapshot.Length ?? 0);

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_part is { } previous)
        {
            if (_shown is { } shown)
            {
                _place = CaptureViewState(previous, shown);
                Hide(previous, shown);
            }
            previous.TextArea.Caret.PositionChanged -= OnPartSelectionChanged;
            previous.TextArea.SelectionChanged -= OnPartSelectionChanged;
            previous.TemplateApplied -= OnPartTemplateApplied;
            foreach (RoutedCommandBinding binding in _searchCommands)
                previous.TextArea.CommandBindings.Remove(binding);
        }
        _part = e.NameScope.Find<TextEditor>("PART_TextEditor");
        _root = e.NameScope.Find<Panel>("PART_Root");
        // The bar of the old template goes with it.
        _searchBar = null;
        // A new part has none of the theme's colors yet.
        _theme = null;
        if (_part is { } part)
        {
            ShowNothing(part);
            TextEditorOptions options = part.Options;
            // AvaloniaEdit's default, until Avalonia's wrapping and font fallback get fast on longer wrapped lines.
            options.LongLineLimit = 3_000;
            options.HighlightCurrentLine = true;
            options.AllowScrollBelowDocument = true;
            // Links would be drawn in AvaloniaEdit's blue, not in the theme's colors.
            options.EnableHyperlinks = false;
            options.EnableEmailHyperlinks = false;
            part.TextArea.Caret.PositionChanged += OnPartSelectionChanged;
            part.TextArea.SelectionChanged += OnPartSelectionChanged;
            part.TemplateApplied += OnPartTemplateApplied;
            foreach (RoutedCommandBinding binding in _searchCommands)
                part.TextArea.CommandBindings.Add(binding);
        }
        UpdateShownDocument();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateShownDocument();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        UpdateShownDocument();
    }

    /// <inheritdoc />
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source != this || _part is not { } part)
            return;
        // The text area takes the keyboard, where the caret is. As in AvaloniaEdit's own editor, the event is handled
        // here: AvaloniaEdit's commands act on the element that got the focus last.
        part.TextArea.Focus(e.NavigationMethod, e.KeyModifiers);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
            SwitchDocument();
        else if (change.Property == IsReadOnlyProperty)
        {
            _part?.IsReadOnly = IsReadOnly || _shown is null;
            _searchBar?.UpdateReplaceRow();
        }
        else if (
            change.Property == ColorThemeProperty
            || change.Property == ThemeVariantScope.ActualThemeVariantProperty
        )
            UpdateHighlighting();
    }

    /// <summary>Whether a theme variant is dark: Dark, or one that inherits from it.</summary>
    private static bool IsDark(ThemeVariant variant)
    {
        for (ThemeVariant? current = variant; current is not null; current = current.InheritVariant)
        {
            if (current == ThemeVariant.Dark)
                return true;
        }
        return false;
    }

    /// <summary>Shows another document from its start.</summary>
    private void SwitchDocument()
    {
        if (_shown is { } shown && _part is { } part)
            Hide(part, shown);
        _place = null;
        _pendingReveal = null;
        SetSelection(new TextSelection(0), new TextPosition(1, 1));
        UpdateShownDocument();
    }

    /// <summary>
    /// Shows <see cref="Document"/> in the part while the editor is in the visual tree, and nothing otherwise, or once
    /// the document is disposed. What the part stops showing, the editor keeps as its place in the document.
    /// </summary>
    private void UpdateShownDocument()
    {
        CodeDocument? target =
            _isAttached && _part is not null && Document is { IsDisposed: false } document ? document : null;
        if (target != _shown && _part is { } part)
        {
            if (_shown is { } shown)
            {
                _place = CaptureViewState(part, shown);
                Hide(part, shown);
            }
            if (target is not null)
            {
                Show(part, target);
                // Disposed meanwhile, before or while the editor started listening: it shows nothing after all.
                if (target.IsDisposed)
                {
                    _place = CaptureViewState(part, target);
                    Hide(part, target);
                }
            }
        }
        UpdateHighlighting();
    }

    private void Show(TextEditor part, CodeDocument document)
    {
        // Before the part shows the buffer, so that no disposal from here on goes unnoticed.
        document.Disposed += OnDocumentDisposed;
        document.PropertyChanged += OnDocumentPropertyChanged;
        ApplyIndentation(part.Options, document.Indentation);
        TextDocument buffer = document.Buffer;
        part.Document = buffer;
        part.IsReadOnly = IsReadOnly;
        part.TextArea.Opacity = 1;
        CodeEditorViewState? place = _place?.ToCurrent();
        _place = place;
        _shown = document;
        Select(part, place?.Selection ?? new TextSelection(0));
        TextView textView = part.TextArea.TextView;
        _highlighter = new BackgroundTextMateHighlighter(textView, buffer, Dispatcher, null);
        _helpers = new EditingHelpers(part.TextArea, document, _highlighter);
        _brackets = new BracketHighlighter(part.TextArea, document, _highlighter) { Colors = _colors };
        _whitespace = new WhitespaceLayer(part.TextArea) { Colors = _colors };
        _searchBar?.Attach(this, part.TextArea, document);
        // The scroll position waits for the first layout: until then the view has no size and the document no height.
        _waitingForLayout = true;
        textView.LayoutUpdated += OnTextViewLayoutUpdated;
        textView.VisualLinesChanged += OnTextViewLaidOut;
        textView.ScrollOffsetChanged += OnTextViewLaidOut;
    }

    private void Hide(TextEditor part, CodeDocument shown)
    {
        shown.Disposed -= OnDocumentDisposed;
        shown.PropertyChanged -= OnDocumentPropertyChanged;
        CancelLoading();
        _helpers?.Dispose();
        _helpers = null;
        _brackets?.Dispose();
        _brackets = null;
        _whitespace?.Dispose();
        _whitespace = null;
        _searchBar?.Detach();
        _highlighter?.Dispose();
        _highlighter = null;
        part.TextArea.TextView.LayoutUpdated -= OnTextViewLayoutUpdated;
        part.TextArea.TextView.VisualLinesChanged -= OnTextViewLaidOut;
        part.TextArea.TextView.ScrollOffsetChanged -= OnTextViewLaidOut;
        _waitingForLayout = false;
        _laidOut = null;
        _shown = null;
        ShowNothing(part);
    }

    /// <summary>
    /// Gives the part <see cref="_emptyBuffer"/>, read-only, and draws its text area transparent, so that the editor
    /// shows only its background and border: no line number, current line or caret. Transparent rather than hidden: a
    /// hidden text area can't keep the keyboard focus, and the line numbers show the lines that AvaloniaEdit lays out
    /// even in a hidden text view.
    /// </summary>
    private void ShowNothing(TextEditor part)
    {
        part.Document = _emptyBuffer;
        part.IsReadOnly = true;
        part.TextArea.Opacity = 0;
    }

    /// <summary>
    /// Raised on the thread that disposes. Posted, also on the UI thread, so that it never runs in the middle of
    /// showing the document.
    /// </summary>
    private void OnDocumentDisposed(object? sender, EventArgs e) => Dispatcher.Post(UpdateShownDocument);

    /// <summary>
    /// The editor's own keys, before AvaloniaEdit's: search, also in the search bar, as AvaloniaEdit decides them by
    /// the operating system, and the editing helpers' keys in the text area. Ctrl+F and Ctrl+H are the editor's even
    /// without a document, so that AvaloniaEdit's own search panel never opens.
    /// </summary>
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        bool shift = e.KeyModifiers == KeyModifiers.Shift;
        if (ApplicationCommands.Find.Gesture.Matches(e) || ApplicationCommands.Replace.Gesture.Matches(e))
        {
            OpenSearch(replace: ApplicationCommands.Replace.Gesture.Matches(e));
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && (e.KeyModifiers == KeyModifiers.None || shift))
            e.Handled = FindNext(previous: shift);
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _searchBar is { IsOpen: true } bar)
        {
            bar.Close();
            e.Handled = true;
        }
        else if (_helpers is { } helpers && _part is { } part && part.TextArea.IsFocused)
            helpers.OnKeyDown(e);
    }

    /// <summary>
    /// Selects the next or previous match, as F3 and Shift+F3 do, opening the bar for the selection if it is closed.
    /// </summary>
    /// <returns>Whether there is a document to search.</returns>
    private bool FindNext(bool previous)
    {
        if (_part is not { } part || CreatedSearchBar() is not { } bar)
            return false;
        string? text = bar.IsOpen ? null : SelectedLineText(part.TextArea);
        if (previous)
            bar.FindPrevious(text);
        else
            bar.FindNext(text);
        return true;
    }

    /// <summary>
    /// The search bar, made over the text the first time it is needed while the editor shows a document; null without
    /// a document or a panel for it in the template.
    /// </summary>
    private SearchBar? CreatedSearchBar()
    {
        if (_searchBar is null && _root is { } root && _shown is { } shown && _part is { } part)
        {
            var bar = new SearchBar { Colors = _colors };
            root.Children.Add(bar);
            bar.Attach(this, part.TextArea, shown);
            _searchBar = bar;
        }
        return _shown is null ? null : _searchBar;
    }

    /// <summary>
    /// Removes the search panel that AvaloniaEdit's editor installs with each template, its input handler and its
    /// subscriptions: the editor's own bar takes its place.
    /// </summary>
    private static void OnPartTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is TextEditor { SearchPanel: { } panel })
            panel.Uninstall();
    }

    /// <summary>The selected text, if it is within one line: what a search starts with.</summary>
    private static string? SelectedLineText(TextArea textArea) =>
        textArea.Selection is { IsEmpty: false, IsMultiline: false } selection ? selection.GetText() : null;

    /// <summary>Applies a new indentation of the shown document at once. Raised on the UI thread.</summary>
    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeDocument.Indentation) && _shown is { } shown && _part is { } part)
            ApplyIndentation(part.Options, shown.Indentation);
    }

    /// <summary>
    /// AvaloniaEdit's indentation: Tab inserts spaces to the next stop or a tab, Shift+Tab removes one level, and a
    /// tab is drawn one level wide.
    /// </summary>
    private static void ApplyIndentation(TextEditorOptions options, Indentation indentation)
    {
        options.IndentationSize = indentation.Size;
        options.ConvertTabsToSpaces = !indentation.UseTabs;
    }

    /// <summary>Scrolls to where the editor was asked to, once the shown document is laid out.</summary>
    private void OnTextViewLayoutUpdated(object? sender, EventArgs e)
    {
        if (_part is not { } part || !part.TextArea.TextView.VisualLinesValid)
            return;
        part.TextArea.TextView.LayoutUpdated -= OnTextViewLayoutUpdated;
        _waitingForLayout = false;
        if (_pendingReveal is { } reveal && _shown is { } shown)
            Reveal(part, shown.MapToCurrent(reveal.Snapshot, reveal.Range));
        else if (_place is { } place)
            ScrollToPlace(part, place.ToCurrent().Scroll);
        _pendingReveal = null;
        _place = null;
    }

    /// <summary>Keeps the scroll position, after a layout or a scroll.</summary>
    private void OnTextViewLaidOut(object? sender, EventArgs e)
    {
        if (!_waitingForLayout && _shown is { } shown && _part is { } part)
            _laidOut = new CodeEditorViewState(
                shown,
                shown.CurrentSnapshot,
                _selection,
                ScrollPlaceOf(part.TextArea.TextView)
            );
    }

    private void OnPartSelectionChanged(object? sender, EventArgs e)
    {
        if (_shown is not null && _part is { } part)
            ReadSelection(part);
    }

    /// <summary>Selects in the part, and takes the result as <see cref="Selection"/>.</summary>
    private void Select(TextEditor part, TextSelection selection)
    {
        TextArea textArea = part.TextArea;
        textArea.Selection = EditorSelection.Create(textArea, selection.Anchor, selection.Caret);
        textArea.Caret.Offset = selection.Caret;
        ReadSelection(part);
    }

    private void ReadSelection(TextEditor part)
    {
        TextArea textArea = part.TextArea;
        TextDocument buffer = textArea.Document;
        int caret = textArea.Caret.Offset;
        int anchor = textArea.Selection.IsEmpty ? caret : buffer.GetOffset(textArea.Selection.StartPosition.Location);
        TextLocation location = buffer.GetLocation(caret);
        SetSelection(new TextSelection(anchor, caret), new TextPosition(location.Line, location.Column));
    }

    private void SetSelection(TextSelection selection, TextPosition caretPosition)
    {
        SetAndRaise(SelectionProperty, ref _selection, selection);
        SetAndRaise(CaretPositionProperty, ref _caretPosition, caretPosition);
    }

    /// <summary>Where the part is in the shown document; a scroll position that waits for the layout counts.</summary>
    private CodeEditorViewState CaptureViewState(TextEditor part, CodeDocument document)
    {
        ScrollPlace scroll =
            _waitingForLayout ? _place?.ToCurrent().Scroll ?? default
            : _isAttached ? ScrollPlaceOf(part.TextArea.TextView)
            : _laidOut?.ToCurrent().Scroll ?? default;
        return new CodeEditorViewState(document, document.CurrentSnapshot, _selection, scroll);
    }

    private static ScrollPlace ScrollPlaceOf(TextView textView)
    {
        Vector offset = textView.ScrollOffset;
        DocumentLine top = textView.GetDocumentLineByVisualTop(offset.Y);
        return new ScrollPlace(top.Offset, offset.Y - textView.GetVisualTopByDocumentLine(top.LineNumber), offset.X);
    }

    private static void ScrollToPlace(TextEditor part, ScrollPlace place)
    {
        TextView textView = part.TextArea.TextView;
        DocumentLine top = textView.Document.GetLineByOffset(place.TopOffset);
        SetScrollOffset(
            part,
            new Vector(place.Horizontal, textView.GetVisualTopByDocumentLine(top.LineNumber) + place.TopDelta)
        );
    }

    /// <summary>
    /// Brings a range into view: centered unless it is visible, from its start when it is taller or wider than the view.
    /// </summary>
    private static void Reveal(TextEditor part, TextRange range)
    {
        TextView textView = part.TextArea.TextView;
        TextDocument buffer = textView.Document;
        Point start = Position(range.Start, VisualYPosition.LineTop);
        double bottom = Position(range.End, VisualYPosition.LineBottom).Y;
        Vector offset = textView.ScrollOffset;
        Size view = textView.Bounds.Size;
        double y = OffsetToShow(start.Y, bottom, start.Y, offset.Y, view.Height);
        // Lines that wrap never scroll across. A range taller than the view shows from its start, so only one that
        // fits needs the left and right edges of every line it covers.
        (double left, double right) = part.WordWrap || bottom - start.Y > view.Height ? (start.X, start.X) : Edges();
        double x = part.WordWrap ? 0 : OffsetToShow(left, right, start.X, offset.X, view.Width);
        SetScrollOffset(part, new Vector(Math.Max(0, x), Math.Max(0, y)));

        Point Position(int textOffset, VisualYPosition at) =>
            textView.GetVisualPosition(new TextViewPosition(buffer.GetLocation(textOffset)), at);

        // From the range's start to the end of its line, the whole of each line between, and the last line to the
        // range's end.
        (double Left, double Right) Edges()
        {
            (double leftmost, double rightmost) = (start.X, start.X);
            DocumentLine last = buffer.GetLineByOffset(range.End);
            for (DocumentLine line = buffer.GetLineByOffset(range.Start); ; line = line.NextLine)
            {
                double from = Position(Math.Max(range.Start, line.Offset), VisualYPosition.LineTop).X;
                double to = Position(Math.Min(range.End, line.EndOffset), VisualYPosition.LineTop).X;
                (leftmost, rightmost) = (Math.Min(leftmost, from), Math.Max(rightmost, to));
                if (line == last)
                    return (leftmost, rightmost);
            }
        }

        // The offset that shows [from, to] in a view of the length: the same when it shows already, centered when it
        // fits, and from the range's start when it does not.
        static double OffsetToShow(double from, double to, double rangeStart, double current, double length) =>
            from >= current && to <= current + length ? current
            : to - from > length ? rangeStart
            : (from + to - length) / 2;
    }

    /// <summary>Scrolls through the part's scroll viewer, which keeps its scroll bars in step with the text.</summary>
    private static void SetScrollOffset(TextEditor part, Vector offset)
    {
        if (part.TextArea.FindAncestorOfType<ScrollViewer>() is { } scrollViewer)
            scrollViewer.Offset = offset;
    }

    /// <summary>
    /// Shows the colors of <see cref="ColorTheme"/>, or of Light+ or Dark+ when its file failed to load, and the
    /// grammar of the shown document's language, loaded with that theme. A grammar that is not loaded yet loads in the
    /// background with the lines in view, and the current colors stay until it is ready, so that the text area and
    /// the tokens change in the same frame.
    /// </summary>
    private void UpdateHighlighting()
    {
        if (!_isAttached || _part is null)
            return;
        Task<ColorThemeFile?> requested = ColorTheme.Loaded;
        if (!requested.IsCompleted)
        {
            WhenCompleted(requested, UpdateHighlighting);
            return;
        }
        Task<ColorThemeFile?> theme = requested.Result is null
            ? (IsDark(ActualThemeVariant) ? CodeEditorColorTheme.DarkPlus : CodeEditorColorTheme.LightPlus).Loaded
            : requested;
        Task<LoadedLanguage>? language = _shown?.Language.Loaded;
        if (!theme.IsCompleted || language is { IsCompleted: false })
        {
            WhenCompleted(language is null ? theme : Task.WhenAll(theme, language), UpdateHighlighting);
            return;
        }
        // Without its colors there is nothing to show; the built-in theme's load error is in the log.
        if (theme.Result is not { } file)
            return;
        if (language?.Result.Grammar is not { } source)
        {
            CancelLoading();
            Highlight(file, null, null);
        }
        else if (_grammar is { } grammar && grammar.Theme == file && grammar.Source == source)
        {
            CancelLoading();
            Highlight(file, grammar, null);
        }
        else if (_loading is not { } loading || loading.Theme != file || loading.Source != source)
            Load(file, source);
    }

    /// <summary>Loads a grammar with its theme, and tokenizes the lines in view, in the background.</summary>
    private void Load(ColorThemeFile theme, IRawGrammar source)
    {
        if (_part is not { } part || _shown is not { } shown)
            return;
        CancelLoading();
        TextView textView = part.TextArea.TextView;
        ITextSource text = shown.Buffer.CreateSnapshot();
        int firstLine =
            textView.VisualLinesValid && textView.VisualLines.Count > 0
                ? textView.VisualLines[0].FirstDocumentLine.LineNumber - 1
                : 0;
        var cancellation = new CancellationTokenSource();
        var loading = new GrammarLoad(
            theme,
            source,
            cancellation,
            Task.Run(() =>
            {
                TextMateGrammar grammar = TextMateGrammar.Load(theme, source);
                return (grammar, BackgroundTextMateHighlighter.Prepare(grammar, text, firstLine, cancellation.Token));
            })
        );
        _loading = loading;
        WhenCompleted(loading.Result, () => OnLoaded(loading));
    }

    /// <summary>Shows a loaded grammar, unless a newer load replaced it. Its failure goes to the log either way.</summary>
    private void OnLoaded(GrammarLoad loading)
    {
        if (loading.Result.Exception is { } exception)
        {
            AssetFiles.LogError(this, $"the grammar {loading.Source.GetScopeName()} with its color theme", exception);
            if (_loading == loading)
                _loading = null;
            return;
        }
        if (_loading != loading)
            return;
        _loading = null;
        (TextMateGrammar grammar, FirstScreen firstScreen) = loading.Result.Result;
        _grammar = grammar;
        Highlight(loading.Theme, grammar, firstScreen);
    }

    private void CancelLoading()
    {
        _loading?.Cancellation.Cancel();
        _loading = null;
    }

    /// <summary>Shows a theme's colors and a grammar's tokens together, in the same frame.</summary>
    private void Highlight(ColorThemeFile theme, TextMateGrammar? grammar, FirstScreen? firstScreen)
    {
        if (_part is not { } part)
            return;
        if (_theme != theme)
        {
            _theme = theme;
            TextAreaColors colors = TextAreaColors.From(theme);
            _colors = colors;
            _brackets?.Colors = colors;
            _whitespace?.Colors = colors;
            _searchBar?.Colors = colors;
            TextArea textArea = part.TextArea;
            part.Background = colors.Background;
            part.Foreground = colors.Foreground;
            part.LineNumbersForeground = colors.LineNumbers;
            textArea.SelectionBrush = colors.Selection;
            textArea.Caret.CaretBrush = colors.Caret;
            textArea.TextView.CurrentLineBackground = colors.CurrentLineBackground;
            textArea.TextView.CurrentLineBorder = colors.CurrentLineBorder;
        }
        if (_highlighter is { } highlighter && (highlighter.Grammar != grammar || firstScreen is not null))
            highlighter.SetGrammar(grammar, firstScreen);
    }

    /// <summary>Runs <paramref name="action"/> on the editor's dispatcher once <paramref name="task"/> has completed.</summary>
    private void WhenCompleted(Task task, Action action) =>
        task.ContinueWith(
            _ => Dispatcher.Post(action),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    /// <summary>The editor's context menu, in the app's look.</summary>
    private ContextMenu CreateContextMenu()
    {
        PlatformHotkeyConfiguration? keys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        MenuItem cut = Item("Cut", keys?.Cut, Cut);
        MenuItem paste = Item("Paste", keys?.Paste, Paste);
        var menu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                cut,
                Item("Copy", keys?.Copy, Copy),
                paste,
                new Separator(),
                Item("Select All", keys?.SelectAll, SelectAll),
            },
        };
        menu.Opening += (_, _) => cut.IsEnabled = paste.IsEnabled = !IsReadOnly && _shown is not null;
        return menu;

        static MenuItem Item(string header, List<KeyGesture>? gestures, Action action)
        {
            var item = new MenuItem { Header = header, InputGesture = gestures?.FirstOrDefault() };
            item.Click += (_, _) => action();
            return item;
        }
    }

    /// <summary>A grammar that loads in the background, with the theme it loads with.</summary>
    private sealed record GrammarLoad(
        ColorThemeFile Theme,
        IRawGrammar Source,
        CancellationTokenSource Cancellation,
        Task<(TextMateGrammar Grammar, FirstScreen FirstScreen)> Result
    );
}
