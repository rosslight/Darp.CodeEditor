using Avalonia.Media;
using TextMateSharp.Grammars;

namespace Darp.CodeEditor.Highlighting;

/// <summary>A loaded TextMate grammar and the brushes of the theme it was loaded with, indexed by color id.</summary>
internal sealed class TextMateGrammar(IGrammar grammar, IBrush?[] brushes)
{
    public IGrammar Grammar { get; } = grammar;

    public IBrush?[] Brushes { get; } = brushes;

    /// <summary>How long background tokenization yields to the UI thread at most, before it takes the grammar anyway.</summary>
    private static readonly TimeSpan s_maxYield = TimeSpan.FromMilliseconds(20);

    /// <summary>Held while tokenizing: a grammar compiles and caches its expressions as it goes, and editors share it.</summary>
    private readonly object _gate = new();

    /// <summary>
    /// True while the UI thread waits for or holds the grammar. Monitor is not fair: a thread-pool loop that takes the
    /// grammar for one line after another would otherwise keep the UI thread waiting past its frame budget.
    /// </summary>
    private volatile bool _uiThreadWaiting;

    /// <summary>
    /// Set on the UI thread when one line took much longer than the frame budget there: TextMate cannot stop inside
    /// one regex search, so a grammar with such an expression is tokenized only on the thread pool from then on.
    /// </summary>
    public bool TooSlowForUiThread { get; set; }

    /// <summary>Takes the grammar on the UI thread, ahead of background tokenization; false after the timeout.</summary>
    public bool TryEnterOnUiThread(TimeSpan timeout)
    {
        _uiThreadWaiting = true;
        if (Monitor.TryEnter(_gate, timeout))
            return true;
        _uiThreadWaiting = false;
        return false;
    }

    public void ExitOnUiThread()
    {
        Monitor.Exit(_gate);
        _uiThreadWaiting = false;
    }

    /// <summary>Tokenizes one line on a thread-pool thread, after the UI thread when it is waiting.</summary>
    public ITokenizeLineResult2 TokenizeInBackground(string line, IStateStack? state, TimeSpan timeLimit)
    {
        if (_uiThreadWaiting)
            SpinWait.SpinUntil(() => !_uiThreadWaiting, s_maxYield);
        lock (_gate)
            return Grammar.TokenizeLine2(new LineText(line.AsMemory()), state, timeLimit);
    }
}
