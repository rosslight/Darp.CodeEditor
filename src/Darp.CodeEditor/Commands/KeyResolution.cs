namespace Darp.CodeEditor.Commands;

/// <summary>
/// What a key does by a keymap: the commands it tries, in order, and if none applies, whether it waits for the second
/// stroke of a sequence. The editor runs the first command that applies (<see cref="CommandTable.TryRun"/>).
/// </summary>
/// <param name="Commands">
/// The commands to try: those of the sequences that the waiting key and this one complete, the latest binding first,
/// then those of this key alone, the latest first, down to the latest sequence that starts with it.
/// </param>
/// <param name="Waits">
/// Whether the key starts a sequence, and so waits for a second stroke once none of the commands applied.
/// </param>
internal readonly record struct KeyResolution(IReadOnlyList<string> Commands, bool Waits)
{
    /// <summary>
    /// Resolves <paramref name="key"/> by <paramref name="keymap"/>, after the key that waits, <paramref name="pending"/>,
    /// if any: the second stroke of every sequence that starts with it counts.
    /// </summary>
    public static KeyResolution Of(CodeEditorKeymap keymap, PressedKey? pending, PressedKey key)
    {
        IReadOnlyList<CodeEditorKeyBinding> bindings = keymap.Effective;
        List<string>? commands = null;
        if (pending is { } waiting)
        {
            for (int i = bindings.Count - 1; i >= 0; i--)
            {
                if (
                    bindings[i] is { First: { } first, Second: { } second }
                    && first.Matches(waiting)
                    && second.Matches(key)
                )
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
                return new KeyResolution(commands ?? [], Waits: true);
            (commands ??= []).Add(binding.Command);
        }
        return new KeyResolution(commands ?? [], Waits: false);
    }
}
