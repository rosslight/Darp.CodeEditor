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
/// Replace All on the UI thread, with the frame that follows: about 10,000 matches in 5,000 lines of JSON, the most a
/// search keeps. Each match is replaced with its own text, so that every call starts from the same text.
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
[WarmupCount(2)]
[IterationCount(10)]
public class ReplaceAllBenchmarks
{
    private Window? _window;
    private SearchBar? _bar;

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.Json, HeadlessEditor.Json(5000)) };
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.RenderUntilColored(_window, editor, HeadlessEditor.JsonKey);
        editor.OpenSearch(replace: true);
        _bar = editor.GetVisualDescendants().OfType<SearchBar>().Single();
        _bar.RegexToggle.IsChecked = true;
        _bar.ReplaceBox.Text = "$0";
        _bar.FindBox.Text = "\\d+";
    }

    /// <summary>Waits for the search after the last replacement, as the user would before clicking.</summary>
    [IterationSetup]
    public void Search()
    {
        SearchBar bar = _bar ?? throw new InvalidOperationException("Not set up.");
        long start = Stopwatch.GetTimestamp();
        while (!bar.ReplaceAllButton.IsEnabled)
        {
            Dispatcher.UIThread.RunJobs();
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("The search did not complete.");
        }
        if (bar.Count is null || bar.Message is not null)
            throw new InvalidOperationException($"The search did not find every match: {bar.Count}, {bar.Message}");
    }

    [Benchmark]
    public void ReplaceAll10000Matches()
    {
        SearchBar bar = _bar ?? throw new InvalidOperationException("Not set up.");
        bar.ReplaceAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        HeadlessEditor.Render(_window ?? throw new InvalidOperationException("Not set up."));
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();
}
#endif
