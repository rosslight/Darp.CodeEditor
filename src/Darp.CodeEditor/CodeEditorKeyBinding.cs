using Darp.CodeEditor.Commands;

namespace Darp.CodeEditor;

/// <summary>
/// A binding of a <see cref="CodeEditorKeymap"/>: a key sequence of one or two strokes and the command it runs, or
/// with <c>-</c> before the command, the removal of earlier bindings of that command.
/// </summary>
public sealed record CodeEditorKeyBinding
{
    /// <summary>Binds <paramref name="keys"/>, such as <c>ctrl+k ctrl+i</c>, to <paramref name="command"/>.</summary>
    /// <param name="keys">
    /// One or two strokes, apart by a space. A stroke is modifiers and a key joined by <c>+</c>, in any case: the
    /// modifiers <c>ctrl</c>, <c>shift</c>, <c>alt</c> and <c>cmd</c> (also <c>meta</c> or <c>win</c>), and a key such as
    /// <c>a</c>, <c>1</c>, <c>f3</c>, <c>space</c>, <c>enter</c>, <c>pageup</c>, <c>numpad1</c> or <c>/</c>. A punctuation
    /// key is the one that types its character, on any keyboard layout. Empty only for a removal, which then removes
    /// every binding of its command.
    /// </param>
    /// <param name="command">One of <see cref="CodeEditorCommands"/>, or one with <c>-</c> before it.</param>
    /// <exception cref="FormatException"><paramref name="keys"/> is no key sequence of that syntax.</exception>
    public CodeEditorKeyBinding(string keys, string command)
    {
        Keys = keys;
        Command = command;
        (First, Second) = KeyStroke.ParseSequence(keys);
        if (First is null && !IsRemoval)
            throw new FormatException("A binding needs keys.");
    }

    /// <summary>The keys, as given.</summary>
    public string Keys { get; }

    /// <summary>The command, with <c>-</c> before it for a removal.</summary>
    public string Command { get; }

    /// <summary>The first stroke; null only for a removal without keys.</summary>
    internal KeyStroke? First { get; }

    /// <summary>The second stroke of a sequence of two; null for one of one.</summary>
    internal KeyStroke? Second { get; }

    internal bool IsRemoval => Command.StartsWith('-');
}
