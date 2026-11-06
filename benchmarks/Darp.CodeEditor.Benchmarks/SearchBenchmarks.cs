// Left out of the baseline's build while the target branch has no search: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// Searching 5,000 lines of JSON in the background, as the search bar does on every change of the query or the text:
/// the measurements behind the search's time limit and its limit on matches.
/// </summary>
[MemoryDiagnoser]
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class SearchBenchmarks
{
    private TextSnapshot? _snapshot;

    [GlobalSetup]
    public void SetUp() => _snapshot = TextSnapshot.FromString(HeadlessEditor.Json(5000));

    /// <summary>A word on every line: about 5,000 matches.</summary>
    [Benchmark]
    public int FindAWordOnEveryLine() =>
        Find(new SearchQuery("value", MatchCase: false, WholeWord: true, UseRegex: false));

    /// <summary>A regular expression with two matches on every line: about 10,000 matches, the limit.</summary>
    [Benchmark]
    public int FindARegularExpression() =>
        Find(new SearchQuery("\\d+", MatchCase: true, WholeWord: false, UseRegex: true));

    /// <summary>A word on no line: the search reads all of the text.</summary>
    [Benchmark]
    public int FindNothing() => Find(new SearchQuery("missing", MatchCase: false, WholeWord: false, UseRegex: false));

    private int Find(SearchQuery query)
    {
        SearchResult result = TextSearch.Find(
            _snapshot ?? throw new InvalidOperationException("Not set up."),
            query,
            null,
            CancellationToken.None
        );
        return result.IsComplete ? result.Matches.Count : throw new InvalidOperationException("The search stopped.");
    }
}

/// <summary>
/// Replace All on the UI thread, with the frame that follows: about 10,000 matches in 5,000 lines of JSON, close to the
/// most a search keeps. Calls replace every "e" with "E" and back in turn, so that each one starts from one of two
/// texts and its replacements can be checked.
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
[WarmupCount(2)]
[IterationCount(10)]
public class ReplaceAllBenchmarks
{
    private Window? _window;
    private SearchBar? _bar;
    private CodeDocument? _document;

    /// <summary>Whether the call replaces "e" with "E", rather than back.</summary>
    private bool _upper;

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        _document = CodeDocument.Create(CodeLanguage.Json, HeadlessEditor.Json(5000));
        var editor = new CodeEditor { Document = _document };
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.RenderUntilColored(_window, editor, HeadlessEditor.JsonKey);
        editor.OpenSearch(replace: true);
        _bar = editor.GetVisualDescendants().OfType<SearchBar>().Single();
        _bar.MatchCaseToggle.IsChecked = true;
    }

    /// <summary>Searches for the call's letter, and waits for the result, as the user would before clicking.</summary>
    [IterationSetup]
    public void Search()
    {
        SearchBar bar = Bar;
        _upper = !_upper;
        bar.FindBox.Text = _upper ? "e" : "E";
        bar.ReplaceBox.Text = _upper ? "E" : "e";
        long start = Stopwatch.GetTimestamp();
        while (!bar.ReplaceAllButton.IsEnabled)
        {
            Dispatcher.UIThread.RunJobs();
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("The search did not complete.");
        }
        if (bar.Count is null || bar.Message is not null)
            throw new InvalidOperationException($"The search did not find every match: {bar.Count}, {bar.Message}");
        HeadlessEditor.Render(_window ?? throw new InvalidOperationException("Not set up."));
    }

    [Benchmark]
    public void ReplaceAll10000Matches()
    {
        Bar.ReplaceAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HeadlessEditor.Render(_window ?? throw new InvalidOperationException("Not set up."));
    }

    /// <summary>Fails the run if Replace All left a letter, instead of measuring a click that did nothing.</summary>
    [IterationCleanup]
    public void CheckTheReplacements()
    {
        string text = (_document ?? throw new InvalidOperationException("Not set up.")).Text;
        if (text.Contains(_upper ? 'e' : 'E', StringComparison.Ordinal))
            throw new InvalidOperationException("Replace All did not replace every match.");
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();

    private SearchBar Bar => _bar ?? throw new InvalidOperationException("Not set up.");
}
#endif
