using Avalonia.Controls;
using Darp.CodeEditor;

namespace UseCases.Themes;

/// <summary>A script editor that stays dark.</summary>
public partial class AlwaysDarkView : UserControl
{
    /// <summary>Creates the view.</summary>
    public AlwaysDarkView() => InitializeComponent();

    /// <summary>The script the view shows.</summary>
    public CodeDocument? Script
    {
        get => Editor.Document;
        set => Editor.Document = value;
    }
}
