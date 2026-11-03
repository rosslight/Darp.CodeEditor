namespace Darp.CodeEditor.Theming;

/// <summary>A line transformer or background renderer that draws with theme colors; the editor sets them.</summary>
internal interface IThemedRenderer
{
    EditorTheme EditorTheme { set; }
}
