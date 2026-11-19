using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Darp.CodeEditor.Localization;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The editor's default context menu, as Avalonia's text box has one: a flyout, which takes the keyboard while it is
/// open. Show Hover; the clipboard and Select All; Toggle Line Comment; Find and Replace.
/// </summary>
/// <remarks>
/// As the menu opens, each item takes its text in the UI culture, its keys and whether it applies: the keymap's keys
/// for the editor's commands, the platform's for AvaloniaEdit's clipboard keys. The menu takes the app's menu look, or
/// the editor's fallback where the app's theme has none for Avalonia's menu flyouts.
/// </remarks>
internal sealed class EditorMenu : MenuFlyout
{
    private readonly CodeEditor _editor;
    private readonly Control[] _items;

    public EditorMenu(CodeEditor editor)
    {
        _editor = editor;
        PlatformHotkeyConfiguration? hotkeys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        Func<bool> showsText = () => editor.ShowsText;
        Func<bool> editable = () => editor.ShowsText && !editor.IsReadOnly;
        _items =
        [
            Command(() => Strings.Default.ShowHover, CodeEditorCommands.ShowHover, showsText),
            new Separator(),
            new Item(() => Strings.Default.Cut, Platform(hotkeys?.Cut), editable, editor.Cut),
            new Item(() => Strings.Default.Copy, Platform(hotkeys?.Copy), showsText, editor.Copy),
            new Item(() => Strings.Default.Paste, Platform(hotkeys?.Paste), editable, editor.Paste),
            new Item(() => Strings.Default.SelectAll, Platform(hotkeys?.SelectAll), showsText, editor.SelectAll),
            new Separator(),
            Command(() => Strings.Default.CommentLine, CodeEditorCommands.CommentLine, editable),
            new Separator(),
            Command(() => Strings.Default.Find, CodeEditorCommands.Find, () => true),
            Command(() => Strings.Default.Replace, CodeEditorCommands.Replace, () => !editor.IsReadOnly),
        ];
        ItemsSource = _items;
        return;

        // After the menu has closed and given the keyboard back: the commands act where it is.
        Item Command(Func<string> text, string command, Func<bool> applies) =>
            new(
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
        foreach (Control item in _items)
        {
            if (item is Item menuItem)
                menuItem.Update();
            else
                item.Theme = separator;
        }
        base.OnOpening(args);
    }

    /// <summary>The editor's theme for <paramref name="type"/>, where the app's theme has none.</summary>
    private ControlTheme? Fallback(Type type, string key) =>
        _editor.TryFindResource(type, _editor.ActualThemeVariant, out _)
            ? null
            : _editor.FindResource(_editor.ActualThemeVariant, key) as ControlTheme;

    /// <summary>
    /// A menu item that also shows a sequence of two strokes, such as Ctrl+K Ctrl+I, where <see cref="MenuItem"/>
    /// shows one gesture: the themes of Fluent, Simple and FluentAvalonia show it in their <c>PART_InputGestureText</c>.
    /// </summary>
    private sealed class Item : MenuItem
    {
        private readonly Func<string> _text;
        private readonly Func<IReadOnlyList<KeyGesture>> _keys;
        private readonly Func<bool> _applies;
        private TextBlock? _keysText;
        private string? _sequence;

        public Item(Func<string> text, Func<IReadOnlyList<KeyGesture>> keys, Func<bool> applies, Action run)
        {
            _text = text;
            _keys = keys;
            _applies = applies;
            Click += (_, _) => run();
        }

        protected override Type StyleKeyOverride => typeof(MenuItem);

        public void Update()
        {
            Header = _text();
            IsEnabled = _applies();
            IReadOnlyList<KeyGesture> keys = _keys();
            // The first stroke also tells the theme that the item has keys to show.
            InputGesture = keys.Count > 0 ? keys[0] : null;
            _sequence = keys.Count > 1 ? string.Join(' ', keys.Select(key => key.ToString("p", null))) : null;
            ShowSequence();
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            _keysText = e.NameScope.Find<TextBlock>("PART_InputGestureText");
            ShowSequence();
        }

        private void ShowSequence()
        {
            if (_keysText is null)
                return;
            if (_sequence is null)
                _keysText.ClearValue(TextBlock.TextProperty);
            else
                _keysText.Text = _sequence;
        }
    }
}
