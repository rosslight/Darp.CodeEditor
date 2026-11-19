using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Darp.CodeEditor.Localization;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The editor's default context menu, as Avalonia's text box has one: a flyout, which takes the keyboard while it is
/// open. The clipboard and Select All; Toggle Line Comment; Find and Replace.
/// </summary>
/// <remarks>
/// As the menu opens, each item takes its text in the UI culture, its keys and whether it applies: the keymap's keys
/// for the editor's commands, the platform's for AvaloniaEdit's clipboard keys. The menu takes the app's menu look, or
/// the editor's fallback where the app's theme has none for Avalonia's menu flyouts.
/// </remarks>
internal sealed class EditorMenu : MenuFlyout
{
    private readonly CodeEditor _editor;
    private readonly Control[] _controls;
    private readonly List<Item> _items = [];

    public EditorMenu(CodeEditor editor)
    {
        _editor = editor;
        PlatformHotkeyConfiguration? hotkeys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        Func<bool> showsText = () => editor.Document is { IsDisposed: false };
        Func<bool> editable = () => showsText() && !editor.IsReadOnly;
        _controls =
        [
            Add(() => Strings.Cut, Platform(hotkeys?.Cut), editable, editor.Cut),
            Add(() => Strings.Copy, Platform(hotkeys?.Copy), showsText, editor.Copy),
            Add(() => Strings.Paste, Platform(hotkeys?.Paste), editable, editor.Paste),
            Add(() => Strings.SelectAll, Platform(hotkeys?.SelectAll), showsText, editor.SelectAll),
            new Separator(),
            Command(() => Strings.CommentLine, CodeEditorCommands.CommentLine, editable),
            new Separator(),
            Command(() => Strings.Find, CodeEditorCommands.Find, showsText),
            Command(() => Strings.Replace, CodeEditorCommands.Replace, editable),
        ];
        ItemsSource = _controls;
        return;

        // After the menu has closed and given the keyboard back: the commands act where it is.
        MenuItem Command(Func<string> text, string command, Func<bool> applies) =>
            Add(
                text,
                () => editor.Keymap.GesturesOf(command),
                applies,
                () => editor.Dispatcher.Post(() => editor.RunFromMenu(command))
            );

        static Func<IReadOnlyList<KeyGesture>> Platform(List<KeyGesture>? gestures)
        {
            IReadOnlyList<KeyGesture> first = gestures is [KeyGesture gesture, ..] ? [gesture] : [];
            return () => first;
        }
    }

    /// <summary>
    /// Where the menu opens next: at <paramref name="point"/> of the editor, as for a menu opened by key, or at the
    /// pointer where null.
    /// </summary>
    public void OpenAt(Point? point)
    {
        // Avalonia shows a context flyout opened by key at its placement, by default under the whole editor.
        Placement = PlacementMode.AnchorAndGravity;
        PlacementAnchor = PopupAnchor.TopLeft;
        PlacementGravity = PopupGravity.BottomRight;
        // Pointer placement adds the offsets too.
        HorizontalOffset = point?.X ?? 0;
        VerticalOffset = point?.Y ?? 0;
    }

    protected override void OnOpening(CancelEventArgs args)
    {
        // Before the base applies the presenter's theme.
        FlyoutPresenterTheme = Fallback(typeof(MenuFlyoutPresenter), "CodeEditorMenuFlyoutPresenterTheme");
        ControlTheme? separator = Fallback(typeof(Separator), "CodeEditorMenuSeparatorTheme");
        foreach (Separator line in _controls.OfType<Separator>())
            line.Theme = separator;
        foreach ((MenuItem item, Func<string> text, Func<IReadOnlyList<KeyGesture>> keys, Func<bool> applies) in _items)
        {
            item.Header = text();
            item.IsEnabled = applies();
            // A menu item shows one stroke: a sequence of two shows none rather than half of it.
            item.InputGesture = keys() is [KeyGesture key] ? key : null;
        }
        base.OnOpening(args);
    }

    /// <summary>The editor's theme for <paramref name="type"/>, where the app's theme has none.</summary>
    private ControlTheme? Fallback(Type type, string key) =>
        _editor.TryFindResource(type, _editor.ActualThemeVariant, out _)
            ? null
            : _editor.FindResource(_editor.ActualThemeVariant, key) as ControlTheme;

    /// <summary>
    /// An item whose text in the UI culture, keys and whether it applies are read as the menu opens, and which runs
    /// <paramref name="run"/>.
    /// </summary>
    private MenuItem Add(Func<string> text, Func<IReadOnlyList<KeyGesture>> keys, Func<bool> applies, Action run)
    {
        var item = new MenuItem();
        item.Click += (_, _) => run();
        _items.Add(new Item(item, text, keys, applies));
        return item;
    }

    private sealed record Item(
        MenuItem Control,
        Func<string> Text,
        Func<IReadOnlyList<KeyGesture>> Keys,
        Func<bool> Applies
    );
}
