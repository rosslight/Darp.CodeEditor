using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Sample.Settings;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // Ahead of the button that has the keyboard, which would click on Space or Enter.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    private EditorHotkeys? Hotkeys => (DataContext as MainViewModel)?.Settings.Hotkeys;

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (Hotkeys is not { Recording: { } command } hotkeys)
            return;
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            hotkeys.Recording = null;
        else if (command.CanTake(e.Key, e.KeyModifiers))
            hotkeys.Assign(new KeyGesture(e.Key, e.KeyModifiers));
    }

    private void OnRecordKey(object? sender, RoutedEventArgs e)
    {
        if (Hotkeys is { } hotkeys && CommandOf(sender) is { } command)
            hotkeys.Recording = hotkeys.Recording == command ? null : command;
    }

    private void OnRecordKeyLostFocus(object? sender, RoutedEventArgs e)
    {
        if (Hotkeys is { } hotkeys && hotkeys.Recording == CommandOf(sender))
            hotkeys.Recording = null;
    }

    private void OnRemoveKey(object? sender, RoutedEventArgs e) => CommandOf(sender)?.Clear();

    private void OnResetKey(object? sender, RoutedEventArgs e)
    {
        if (CommandOf(sender) is { } command)
            Hotkeys?.Reset(command);
    }

    private void OnResetAllKeys(object? sender, RoutedEventArgs e) => Hotkeys?.ResetAll();

    private static CommandHotkey? CommandOf(object? sender) => (sender as Control)?.DataContext as CommandHotkey;
}
