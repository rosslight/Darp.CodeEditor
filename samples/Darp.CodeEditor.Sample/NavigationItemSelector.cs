using Avalonia.Controls;
using Avalonia.Controls.Templates;
using FluentAvalonia.UI.Controls;
using Sample.Documents;

namespace Sample;

/// <summary>
/// Builds the navigation pane's items for the open documents. The pane's other items, such as Settings, are items
/// already, and stay as they are: with a plain item template, the navigation view would build them from the
/// documents' template too.
/// </summary>
public sealed class NavigationItemSelector : FADataTemplateSelector
{
    private static readonly IDataTemplate s_itself = new FuncDataTemplate<Control>((control, _) => control);

    public IDataTemplate? Document { get; set; }

    protected override IDataTemplate SelectTemplateCore(object item) =>
        item is OpenDocument ? Document ?? throw new InvalidOperationException("Document has no template.") : s_itself;
}
