using System.Diagnostics;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// An app whose only theme is Avalonia's Simple theme, started in a process of its own, as the tests' app has Fluent:
/// the editor and its popups need no resource of Fluent's.
/// </summary>
public sealed class SimpleThemeTests
{
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task An_app_with_only_the_Simple_theme_shows_the_editor_and_its_completion_list(string variant)
    {
        (string server, IReadOnlyList<string> arguments) = Fixtures.CommandLine(
            "Darp.CodeEditor.Tests.FakeServer",
            "--completion-items",
            "3"
        );
        using Process process = Fixtures.Start("Darp.CodeEditor.Tests.SimpleTheme", [variant, server, .. arguments]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        process.ExitCode.ShouldBe(0, $"{await output}\n{await errors}");
    }
}
