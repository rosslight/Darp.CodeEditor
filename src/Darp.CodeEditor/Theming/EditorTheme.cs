using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Darp.CodeEditor.Theming;

/// <summary>Colors of the editor's own UI: popups, squiggles and markers. Immutable, so any thread may create one.</summary>
internal sealed class EditorTheme
{
    /// <summary>VS Code's Dark+ colors.</summary>
    public static EditorTheme Default { get; } = new();

    public IBrush WidgetBackground { get; init; } = Brush("#252526");
    public IBrush WidgetBorder { get; init; } = Brush("#454545");
    public IBrush WidgetForeground { get; init; } = Brush("#CCCCCC");

    /// <summary>Details next to completion items and other secondary text in popups.</summary>
    public IBrush WidgetSecondaryForeground { get; init; } = Brush("#8C8C8C");

    public IBrush ListSelectionBackground { get; init; } = Brush("#04395E");
    public IBrush ListSelectionForeground { get; init; } = Brush("#FFFFFF");

    public IBrush SymbolMethod { get; init; } = Brush("#B180D7");
    public IBrush SymbolType { get; init; } = Brush("#EE9D28");
    public IBrush SymbolKeyword { get; init; } = Brush("#CCCCCC");
    public IBrush SymbolVariable { get; init; } = Brush("#75BEFF");

    internal static ImmutableSolidColorBrush Brush(string color) => new(Color.Parse(color));
}
