using Avalonia.Controls;
using FluentAvalonia.UI.Controls;

namespace Sample.Settings;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>
    /// Shows the hover's delay again when the box is left blank, which gives NaN. The binding can't: it skips a value
    /// equal to the one it read last, which the delay still is.
    /// </summary>
    private void OnHoverDelayChanged(FANumberBox sender, FANumberBoxValueChangedEventArgs e)
    {
        if (double.IsNaN(e.NewValue))
            sender.Value = e.OldValue;
    }
}
