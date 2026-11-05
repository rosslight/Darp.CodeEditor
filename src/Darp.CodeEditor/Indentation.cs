namespace Darp.CodeEditor;

/// <summary>How a document indents: with spaces or tabs, and how many columns one level takes.</summary>
/// <param name="Size">The columns of one indentation level, and the width of a tab.</param>
/// <param name="UseTabs">Whether Tab and new lines insert tabs rather than spaces.</param>
public readonly record struct Indentation(int Size, bool UseTabs)
{
    /// <summary>Indents with <paramref name="size"/> spaces per level.</summary>
    public static Indentation Spaces(int size) => new(size, UseTabs: false);

    /// <summary>Indents with tabs, each <paramref name="width"/> columns wide.</summary>
    public static Indentation Tabs(int width = 4) => new(width, UseTabs: true);
}
