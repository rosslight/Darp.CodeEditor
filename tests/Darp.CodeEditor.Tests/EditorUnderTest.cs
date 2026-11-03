using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit.Editing;
using Shouldly;

namespace Darp.CodeEditor.Tests;

/// <summary>A shown, focused editor with text, caret and selection from marked text.</summary>
internal sealed class EditorUnderTest : IDisposable
{
    private readonly Window _window;

    public EditorUnderTest(string marked, LanguageConfiguration? configuration = null, string? languageId = null)
    {
        Editor = new CodeEditor { LanguageId = languageId };
        if (configuration is not null)
            Editor.LanguageConfiguration = configuration;
        _window = new Window
        {
            Width = 600,
            Height = 400,
            Content = Editor,
        };
        _window.Show();
        Editor.TextArea.Focus();
        int selectionStart = marked.IndexOf('«');
        string text = marked.Replace("«", "").Replace("»", "").Replace("|", "");
        Editor.Text = text;
        if (selectionStart >= 0)
        {
            int selectionEnd = marked.IndexOf('»') - 1;
            Editor.TextArea.Selection = Selection.Create(Editor.TextArea, selectionStart, selectionEnd);
            Editor.CaretOffset = selectionEnd;
        }
        else
        {
            Editor.CaretOffset = marked.IndexOf('|');
        }
        Dispatcher.UIThread.RunJobs();
    }

    public CodeEditor Editor { get; }

    /// <summary>The text with the caret and selection marked.</summary>
    public string Text
    {
        get
        {
            string text = Editor.Text;
            if (Editor.TextArea.Selection.IsEmpty)
                return text.Insert(Editor.CaretOffset, "|");
            int start = Editor.SelectionStart;
            int end = start + Editor.SelectionLength;
            return text.Insert(end, "»").Insert(start, "«");
        }
    }

    public string Plain => Editor.Text;

    /// <summary>
    /// Waits until the language's configuration is loaded and every shown line has tokens. Typing then finds the
    /// context of a character however long that takes, rather than within the few milliseconds of real use, so that
    /// a slow machine does not change what is auto-closed.
    /// </summary>
    public async Task WaitForLanguageAsync(string lineComment)
    {
        for (int i = 0; i < 1000; i++)
        {
            if (Editor.LanguageConfiguration.LineComment == lineComment && Editor.VisibleLinesWithoutHighlighting == 0)
            {
                Highlighting.BackgroundTextMateHighlighter highlighter = Editor.Highlighter.ShouldNotBeNull();
                highlighter.ContextTimeLimit = TimeSpan.FromSeconds(30);
                // A headless input can run another layout after this wait and mark the shared grammar too slow.
                highlighter.AllowSlowContext = true;
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("The language did not load.");
    }

    public void Type(string text)
    {
        foreach (char c in text)
            _window.KeyTextInput(c.ToString());
        Dispatcher.UIThread.RunJobs();
    }

    public void Press(Key key, KeyModifiers modifiers = KeyModifiers.None, string? symbol = null)
    {
        var raw = (RawInputModifiers)modifiers;
        _window.KeyPress(key, raw, PhysicalKey.None, symbol);
        _window.KeyRelease(key, raw, PhysicalKey.None, symbol);
        Dispatcher.UIThread.RunJobs();
    }

    public void Dispose() => _window.Close();
}
