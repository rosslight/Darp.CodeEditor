using Avalonia;
using Avalonia.Controls.Primitives;

namespace Darp.CodeEditor;

/// <summary>
/// Shows and edits a <see cref="CodeDocument"/>: syntax highlighting, search and, when the document's language has a
/// server, completion, hover, signature help and diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// The editor is a view. The app owns the document, and the document keeps the text, the undo history, the
/// indentation and the diagnostics. Setting <see cref="Document"/> shows another document, starting at the caret,
/// selection and first visible line of the last editor that showed it. Several editors can show one document, each
/// with its own caret.
/// </para>
/// <para>
/// Use it on the UI thread. It never waits for a language server: text and colors come first, and language features
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
/// Built in, with no settings: the current line is highlighted, brackets match, brackets and quotes close as you type,
/// new lines are indented, whitespace shows inside the selection, and the view scrolls past the last line. Hover shows
/// the server's Markdown, with code blocks highlighted by the document's grammar, below the messages of the
/// diagnostics under the pointer. Completion items show VS Code's codicons, in the color theme's symbol colors. The
/// font defaults to a monospace one; set <c>FontFamily</c> and <c>FontSize</c> as for any control.
/// </para>
/// <para>
/// The editor handles only its own keys: editing, the clipboard (Ctrl+X, Ctrl+C, Ctrl+V, Ctrl+A), navigation, search
/// (Ctrl+F, Ctrl+H, F3), completion (Ctrl+Space), comments (Ctrl+/) and indentation (Tab, Shift+Tab). Other keys, such
/// as F5 or Ctrl+S, go on to the app's key bindings. A right click opens the editor's context menu: Cut, Copy, Paste
/// and Select All. For an app's Edit menu, <see cref="Cut"/>, <see cref="Copy"/>, <see cref="Paste"/> and
/// <see cref="SelectAll"/> do the same, and the document's <c>Undo</c> and <c>Redo</c> the rest.
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
    public static readonly StyledProperty<CodeEditorTheme> ColorThemeProperty = AvaloniaProperty.Register<
        CodeEditor,
        CodeEditorTheme
    >(nameof(ColorTheme), defaultValue: CodeEditorTheme.LightPlus);

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

    /// <summary>The document the editor shows. Without one, the editor is empty and read-only.</summary>
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
    /// <see cref="CodeEditorStyles"/> defines as <see cref="CodeEditorTheme.LightPlus"/> for light theme variants and
    /// <see cref="CodeEditorTheme.DarkPlus"/> for dark ones, so editors follow the app's theme variant and any
    /// <c>ThemeVariantScope</c> around them. Where no resource matches, Light+.
    /// </summary>
    /// <remarks>
    /// To give every editor other colors, override <c>CodeEditorColorTheme</c> in the app's theme dictionaries.
    /// Setting the property on one editor fixes its colors whatever the theme variant. To make one editor dark in a
    /// light app, prefer a <c>ThemeVariantScope</c> around it: its popups then turn dark too.
    /// </remarks>
    public CodeEditorTheme ColorTheme
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
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>The caret's line and column, for a status bar.</summary>
    public TextPosition CaretPosition => throw new NotImplementedException();

    /// <summary>
    /// Scrolls <paramref name="range"/> into view: not at all when it is visible, centered when it is not. A range
    /// taller than the view shows from its start.
    /// </summary>
    public void ScrollTo(TextRange range) => throw new NotImplementedException();

    /// <summary>
    /// Opens the search bar, as Ctrl+F does, or the replace bar, as Ctrl+H does. A selection within one line becomes
    /// the search text.
    /// </summary>
    public void OpenSearch(bool replace = false) => throw new NotImplementedException();

    /// <summary>Moves the selection to the clipboard, as Ctrl+X does. No effect in a read-only editor.</summary>
    public void Cut() => throw new NotImplementedException();

    /// <summary>Copies the selection to the clipboard, as Ctrl+C does.</summary>
    public void Copy() => throw new NotImplementedException();

    /// <summary>Replaces the selection with the clipboard's text, as Ctrl+V does. No effect in a read-only editor.</summary>
    public void Paste() => throw new NotImplementedException();

    /// <summary>Selects the whole text, as Ctrl+A does.</summary>
    public void SelectAll() => throw new NotImplementedException();
}
