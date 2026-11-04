using Avalonia;
using Avalonia.Controls.Primitives;

namespace Darp.CodeEditor;

/// <summary>
/// Shows and edits a <see cref="CodeDocument"/>: syntax highlighting, search and, when the document's language has a
/// server, completion, hover, signature help and diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// The editor is a view. The app owns the document, and the document keeps the text, the undo history and the
/// diagnostics. Setting <see cref="Document"/> shows another document; the editor brings back that document's caret,
/// selection and scroll position from the last time an editor showed it.
/// </para>
/// <para>
/// Use it on the UI thread. It never waits for a language server: text and colors come first, and language features
/// follow when the server answers. Out of the visual tree, the editor holds no subscriptions on its document, so a
/// discarded editor is collected even while its document lives on.
/// </para>
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

    /// <summary>Defines the <see cref="IndentationSize"/> property.</summary>
    public static readonly StyledProperty<int> IndentationSizeProperty = AvaloniaProperty.Register<CodeEditor, int>(
        nameof(IndentationSize),
        defaultValue: 4
    );

    /// <summary>Defines the <see cref="ConvertTabsToSpaces"/> property.</summary>
    public static readonly StyledProperty<bool> ConvertTabsToSpacesProperty = AvaloniaProperty.Register<
        CodeEditor,
        bool
    >(nameof(ConvertTabsToSpaces), defaultValue: true);

    /// <summary>Defines the <see cref="LightTheme"/> property.</summary>
    public static readonly StyledProperty<CodeEditorTheme> LightThemeProperty = AvaloniaProperty.Register<
        CodeEditor,
        CodeEditorTheme
    >(nameof(LightTheme), defaultValue: CodeEditorTheme.LightPlus);

    /// <summary>Defines the <see cref="DarkTheme"/> property.</summary>
    public static readonly StyledProperty<CodeEditorTheme> DarkThemeProperty = AvaloniaProperty.Register<
        CodeEditor,
        CodeEditorTheme
    >(nameof(DarkTheme), defaultValue: CodeEditorTheme.DarkPlus);

    /// <summary>Defines the <see cref="CaretOffset"/> property.</summary>
    public static readonly DirectProperty<CodeEditor, int> CaretOffsetProperty = AvaloniaProperty.RegisterDirect<
        CodeEditor,
        int
    >(nameof(CaretOffset), editor => editor.CaretOffset, (editor, value) => editor.CaretOffset = value);

    /// <summary>Defines the <see cref="CaretPosition"/> property.</summary>
    public static readonly DirectProperty<CodeEditor, TextPosition> CaretPositionProperty =
        AvaloniaProperty.RegisterDirect<CodeEditor, TextPosition>(
            nameof(CaretPosition),
            editor => editor.CaretPosition
        );

    /// <summary>Defines the <see cref="Selection"/> property.</summary>
    public static readonly DirectProperty<CodeEditor, TextRange> SelectionProperty = AvaloniaProperty.RegisterDirect<
        CodeEditor,
        TextRange
    >(nameof(Selection), editor => editor.Selection, (editor, value) => editor.Selection = value);

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

    /// <summary>The columns of one indentation level, and the width of a tab. Default: 4.</summary>
    public int IndentationSize
    {
        get => GetValue(IndentationSizeProperty);
        set => SetValue(IndentationSizeProperty, value);
    }

    /// <summary>Whether Tab and auto-indent insert spaces rather than tabs. Default: true.</summary>
    public bool ConvertTabsToSpaces
    {
        get => GetValue(ConvertTabsToSpacesProperty);
        set => SetValue(ConvertTabsToSpacesProperty, value);
    }

    /// <summary>
    /// The colors while the app's theme variant is light. Default: <see cref="CodeEditorTheme.LightPlus"/>.
    /// </summary>
    public CodeEditorTheme LightTheme
    {
        get => GetValue(LightThemeProperty);
        set => SetValue(LightThemeProperty, value);
    }

    /// <summary>
    /// The colors while the app's theme variant is dark. Default: <see cref="CodeEditorTheme.DarkPlus"/>.
    /// </summary>
    public CodeEditorTheme DarkTheme
    {
        get => GetValue(DarkThemeProperty);
        set => SetValue(DarkThemeProperty, value);
    }

    /// <summary>The caret's offset in the document's text.</summary>
    public int CaretOffset
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>The caret's line and column, for a status bar.</summary>
    public TextPosition CaretPosition => throw new NotImplementedException();

    /// <summary>The selected text; empty at the caret when nothing is selected.</summary>
    public TextRange Selection
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>Scrolls the line of <paramref name="offset"/> into view, centered when it is far away.</summary>
    public void ScrollTo(int offset) => throw new NotImplementedException();

    /// <summary>Opens the search bar, as Ctrl+F does, or the replace bar, as Ctrl+H does.</summary>
    public void OpenSearch(bool replace = false) => throw new NotImplementedException();
}
