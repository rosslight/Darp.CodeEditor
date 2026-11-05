using Avalonia;
using Avalonia.Markup.Xaml;

namespace UseCases.Themes;

/// <summary>The app, with its editor colors and styles.</summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
