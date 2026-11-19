using Avalonia.Input;

namespace Darp.CodeEditor.Commands;

/// <summary>
/// What a key does by a keymap: the commands it tries, in order, and if none applies, whether it waits for the second
/// stroke of a sequence. The editor runs the first command that applies (<see cref="CommandTable.TryRun"/>).
/// </summary>
/// <param name="Commands">
/// The commands to try: those that a waiting first stroke and the key complete, the latest binding first, then those of
/// the key alone, the latest first, down to the latest sequence that starts with it.
/// </param>
/// <param name="Sequence">The first stroke of that sequence, to wait in once no command applied; null for none.</param>
internal readonly record struct KeyResolution(IReadOnlyList<string> Commands, KeyStroke? Sequence)
{
    /// <summary>
    /// Resolves <paramref name="key"/> by <paramref name="keymap"/>, after <paramref name="pending"/> if given.
    /// </summary>
    public static KeyResolution Of(CodeEditorKeymap keymap, KeyStroke? pending, KeyEventArgs key)
    {
        IReadOnlyList<CodeEditorKeyBinding> bindings = keymap.Effective;
        List<string>? commands = null;
        if (pending is { } waiting)
        {
            for (int i = bindings.Count - 1; i >= 0; i--)
            {
                if (bindings[i] is { First: { } first, Second: { } second } && first == waiting && second.Matches(key))
                    (commands ??= []).Add(bindings[i].Command);
            }
        }
        // Then, as a second stroke that completes no sequence counts as an ordinary key, the key alone.
        for (int i = bindings.Count - 1; i >= 0; i--)
        {
            CodeEditorKeyBinding binding = bindings[i];
            if (binding.First is not { } first || !first.Matches(key))
                continue;
            if (binding.Second is not null)
                return new KeyResolution(commands ?? [], first);
            (commands ??= []).Add(binding.Command);
        }
        return new KeyResolution(commands ?? [], null);
    }
}
