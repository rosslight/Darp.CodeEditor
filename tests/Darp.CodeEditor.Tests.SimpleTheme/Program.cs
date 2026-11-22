// Starts an app whose only theme is Avalonia's Simple theme, as such an app starts, in a process of its own: the tests'
// app has Fluent. Shows an editor and its completion list, and exits with 0 when the editor shows its text in the color
// theme of the app's theme variant, the list its frame in that theme's suggest background, and its scroll bar the
// popups' own. The arguments are light or dark, then the command line of a fake language server that answers completion.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Darp.CodeEditor;

if (args is not [var variant and ("light" or "dark"), var serverFileName, .. var serverArguments])
    return Fail("Pass light or dark, and the fake language server's command line.");
bool dark = variant == "dark";
AppBuilder.Configure<SimpleApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

var server = new LanguageServer(new LanguageServerOptions { FileName = serverFileName, Arguments = serverArguments });
CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "word");
var editor = new CodeEditor { Document = document };
var window = new Window
{
    Width = 400,
    Height = 300,
    Content = editor,
};
window.Show();

// The text area in Light+ or Dark+, as the theme variant picks them.
Color background = dark ? Color.Parse("#1E1E1E") : Colors.White;
if (
    !RunUntil(() =>
        editor.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault()
            is { Background: ISolidColorBrush { Color: var color } }
        && color == background
    )
)
    return Fail($"The editor does not show its text in the {variant} color theme.");
if (!RunUntil(() => document.Diagnostics.Count > 0))
    return Fail("The server did not serve the document.");

// Completion's list at the caret, in VS Code's suggest background of the color theme.
Color suggest = dark ? Color.Parse("#252526") : Color.Parse("#F3F3F3");
editor.Focus();
editor.Selection = new TextSelection(0);
window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
window.KeyRelease(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
if (!RunUntil(() => ListBackground(window) == suggest))
    return Fail($"The completion list did not show in the color theme's suggest background: {ListBackground(window)}.");

// More items than the list shows: its bar is the popups' own, whose width no theme changes.
if (!RunUntil(() => BarWidth(window) == 10))
    return Fail($"The completion list did not show its scroll bar: {BarWidth(window)}.");

window.Close();
document.Dispose();
server.Dispose();
Console.WriteLine("Passed.");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

// The background of the frame of completion's list; null while it is closed.
static Color? ListBackground(Window window) =>
    OverlayLayer
        .GetOverlayLayer(window)
        ?.GetVisualDescendants()
        .OfType<Border>()
        .FirstOrDefault(frame => frame.Name == "CompletionList")
        ?.Background
        is ISolidColorBrush { Color: var color }
        ? color
        : null;

// The width of the scroll bar of completion's list; null while it does not show.
static double? BarWidth(Window window) =>
    OverlayLayer
        .GetOverlayLayer(window)
        ?.GetVisualDescendants()
        .OfType<ScrollBar>()
        .FirstOrDefault(bar => bar.IsEffectivelyVisible)
        ?.Bounds.Width;

// Runs the UI thread's jobs until a condition holds, for at most 30 s.
static bool RunUntil(Func<bool> condition)
{
    for (int i = 0; i < 3000 && !condition(); i++)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(10);
    }
    return condition();
}

/// <summary>An app with Avalonia's Simple theme and the editor's styles, and nothing else.</summary>
internal sealed class SimpleApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
        Styles.Add(new CodeEditorStyles());
    }
}
