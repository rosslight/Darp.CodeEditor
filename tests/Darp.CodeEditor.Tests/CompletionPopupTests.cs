using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Darp.CodeEditor.Completion;
using Shouldly;

namespace Darp.CodeEditor.Tests;

public sealed class CompletionPopupTests
{
    /// <summary>
    /// The warm-up shows the list once, invisibly, so the first real list does not pay for its first layout: that
    /// only works if the warm-up list has rows and a description, as a real one does.
    /// </summary>
    [AvaloniaFact]
    public async Task The_warm_up_lays_out_items_and_their_description()
    {
        using var editor = new EditorUnderTest("x|");
        OverlayLayer overlay = OverlayLayer.GetOverlayLayer(editor.Editor).ShouldNotBeNull();
        var shown = new List<(int Matches, bool Description)>();
        // The content is on the overlay for one frame only: look at it when it is added.
        overlay.Children.CollectionChanged += (_, e) =>
        {
            foreach (DockPanel content in e.NewItems?.OfType<DockPanel>() ?? [])
            {
                Border[] parts = [.. content.Children.OfType<Border>()];
                CompletionListView list = parts.Select(p => p.Child).OfType<CompletionListView>().Single();
                Border description = parts.Single(p => p.Child is not CompletionListView);
                shown.Add((list.MatchCount, description.IsVisible && description.Child is not null));
            }
        };

        Task warmUp = new CompletionPopup(editor.Editor).WarmUpAsync();
        for (int i = 0; i < 500 && shown.Count == 0; i++)
            await Task.Delay(5, TestContext.Current.CancellationToken);

        shown.ShouldNotBeEmpty();
        shown[0].Matches.ShouldBeGreaterThan(0);
        shown[0].Description.ShouldBeTrue();
        try
        {
            await warmUp.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            // Without a render loop, the headless platform may not produce the frame the warm-up waits for.
        }
    }
}
