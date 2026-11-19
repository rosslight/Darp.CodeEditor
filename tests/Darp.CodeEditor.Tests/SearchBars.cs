using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Tests;

/// <summary>The search bar of an editor under test: its keys, its buttons, and the wait for its searches.</summary>
internal static class SearchBars
{
    /// <summary>The modifiers of the search bar's toggles: Alt, and Alt+Cmd on macOS.</summary>
    public static KeyModifiers Toggle =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Alt | KeyModifiers.Meta : KeyModifiers.Alt;

    public static SearchBar BarOf(EditorWindow window) =>
        window.Editor.GetVisualDescendants().OfType<SearchBar>().Single();

    /// <summary>The bar's buttons and toggles in its order, not those of its parts' templates.</summary>
    public static Button[] ButtonsOf(SearchBar bar) =>
        [.. bar.GetVisualDescendants().OfType<Button>().Where(button => button.TemplatedParent is null)];

    /// <summary>Clicks a button of the bar, also a disabled one, as a command could.</summary>
    public static void Click(SearchBar bar, string content) =>
        bar.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Content as string == content)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Clicks a toggle, which turns it on or off and then raises its click, as a click with the pointer does.</summary>
    public static void Click(ToggleButton toggle)
    {
        toggle.IsChecked = toggle.IsChecked != true;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>
    /// Completes once the search's latest work in the background, and any it started, has shown its result, or at once
    /// without work under way.
    /// </summary>
    public static async Task SearchedAsync(SearchBar bar)
    {
        Task latest;
        do
        {
            latest = bar.LatestSearch;
            await Wait.ForAsync(latest);
        } while (latest != bar.LatestSearch);
    }
}
