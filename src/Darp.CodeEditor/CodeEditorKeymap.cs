using System.Text.Json;

namespace Darp.CodeEditor;

/// <summary>
/// Which keys run which of the editor's commands (<see cref="CodeEditorCommands"/>), as VS Code's keybindings: key
/// sequences of one or two strokes in its syntax, such as <c>ctrl+space</c> or <c>ctrl+k ctrl+i</c>. Set one as
/// <see cref="CodeEditor.Keymap"/>.
/// </summary>
/// <remarks>
/// <para>
/// Bindings apply in order, as in a <c>keybindings.json</c>: a binding whose command starts with <c>-</c> removes the
/// earlier bindings of that command, only those of its keys when it names keys. Where several bindings share a key, the
/// latest whose command applies runs; the latest sequence that starts with the key, if it comes first, waits for its
/// second stroke. So one key can do different things where each applies.
/// </para>
/// <para>
/// While the editor waits for a sequence's second stroke, <see cref="CodeEditor.PendingKeys"/> shows the first. A
/// second stroke that completes no sequence counts as an ordinary key; a click, losing the keyboard, another document
/// or another keymap ends the wait. Keys that run no command go on to the open popup, such as Up and Down in
/// completion's list, to typing and AvaloniaEdit's editing, and then to the app's key bindings.
/// </para>
/// <para>A keymap is immutable, and editors may share one.</para>
/// </remarks>
public sealed class CodeEditorKeymap
{
    private static readonly JsonDocumentOptions s_keybindingsJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Creates a keymap of <paramref name="bindings"/>, which apply in order.</summary>
    public CodeEditorKeymap(IEnumerable<CodeEditorKeyBinding> bindings)
    {
        Bindings = [.. bindings];
        var effective = new List<CodeEditorKeyBinding>();
        foreach (CodeEditorKeyBinding binding in Bindings)
        {
            if (!binding.IsRemoval)
            {
                effective.Add(binding);
                continue;
            }
            string command = binding.Command[1..];
            effective.RemoveAll(earlier =>
                earlier.Command == command
                && (binding.First is null || (earlier.First, earlier.Second) == (binding.First, binding.Second))
            );
        }
        Effective = effective;
    }

    /// <summary>
    /// VS Code's keys for the editor's commands on this operating system. On macOS, Cmd takes Ctrl's place, except for
    /// completion's Ctrl+Space: Cmd+Space is the system's search.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    /// <listheader><term>Keys (macOS)</term><description>Command</description></listheader>
    /// <item><term>ctrl+space, ctrl+i (cmd+i; also alt+escape)</term><description><see cref="CodeEditorCommands.TriggerSuggest"/></description></item>
    /// <item><term>ctrl+shift+space (shift+cmd+space)</term><description><see cref="CodeEditorCommands.TriggerParameterHints"/></description></item>
    /// <item><term>ctrl+f (cmd+f)</term><description><see cref="CodeEditorCommands.Find"/></description></item>
    /// <item><term>ctrl+h (alt+cmd+f)</term><description><see cref="CodeEditorCommands.Replace"/></description></item>
    /// <item><term>f3, shift+f3</term><description><see cref="CodeEditorCommands.FindNext"/>, <see cref="CodeEditorCommands.FindPrevious"/></description></item>
    /// <item><term>ctrl+/ (cmd+/)</term><description><see cref="CodeEditorCommands.CommentLine"/></description></item>
    /// <item><term>ctrl+z (cmd+z)</term><description><see cref="CodeEditorCommands.Undo"/></description></item>
    /// <item><term>ctrl+y, ctrl+shift+z (shift+cmd+z)</term><description><see cref="CodeEditorCommands.Redo"/></description></item>
    /// </list>
    /// </remarks>
    public static CodeEditorKeymap Default { get; } = new(DefaultBindings(OperatingSystem.IsMacOS()));

    /// <summary>A keymap without bindings: the editor runs no command by key.</summary>
    public static CodeEditorKeymap Empty { get; } = new([]);

    /// <summary>The bindings, as given, removals included.</summary>
    public IReadOnlyList<CodeEditorKeyBinding> Bindings { get; }

    /// <summary>The bindings that remain once the removals applied, in order.</summary>
    internal IReadOnlyList<CodeEditorKeyBinding> Effective { get; }

    /// <summary>This keymap with <paramref name="bindings"/> applied after its own.</summary>
    public CodeEditorKeymap With(params IEnumerable<CodeEditorKeyBinding> bindings) => new([.. Bindings, .. bindings]);

    /// <summary>
    /// Reads a keymap in the shape of a <c>keybindings.json</c>: an array of
    /// <c>{ "key": "ctrl+q", "command": "editor.action.triggerParameterHints" }</c>, with comments and trailing commas. Its
    /// bindings apply after those of <paramref name="basedOn"/>, by default <see cref="Default"/>, as VS Code applies a
    /// user's file after its own; <see cref="Empty"/> starts from nothing. <c>when</c> clauses are skipped.
    /// </summary>
    /// <exception cref="JsonException">The text is not such an array.</exception>
    /// <exception cref="FormatException">A key is not in the syntax of <see cref="CodeEditorKeyBinding"/>.</exception>
    public static CodeEditorKeymap FromJson(string json, CodeEditorKeymap? basedOn = null)
    {
        using JsonDocument document = JsonDocument.Parse(json, s_keybindingsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("A keymap is an array of bindings.");
        var bindings = new List<CodeEditorKeyBinding>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (
                item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("command", out JsonElement command)
                || command.ValueKind != JsonValueKind.String
            )
                throw new JsonException("A binding needs a command.");
            string keys =
                item.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String
                    ? key.GetString() ?? ""
                    : "";
            bindings.Add(new CodeEditorKeyBinding(keys, command.GetString() ?? ""));
        }
        return (basedOn ?? Default).With(bindings);
    }

    private static IEnumerable<CodeEditorKeyBinding> DefaultBindings(bool mac)
    {
        string command = mac ? "cmd" : "ctrl";
        yield return new("ctrl+space", CodeEditorCommands.TriggerSuggest);
        yield return new($"{command}+i", CodeEditorCommands.TriggerSuggest);
        if (mac)
            yield return new("alt+escape", CodeEditorCommands.TriggerSuggest);
        yield return new($"{command}+shift+space", CodeEditorCommands.TriggerParameterHints);
        yield return new($"{command}+f", CodeEditorCommands.Find);
        yield return new(mac ? "alt+cmd+f" : "ctrl+h", CodeEditorCommands.Replace);
        yield return new("f3", CodeEditorCommands.FindNext);
        yield return new("shift+f3", CodeEditorCommands.FindPrevious);
        yield return new($"{command}+/", CodeEditorCommands.CommentLine);
        yield return new($"{command}+z", CodeEditorCommands.Undo);
        if (!mac)
            yield return new("ctrl+y", CodeEditorCommands.Redo);
        yield return new($"{command}+shift+z", CodeEditorCommands.Redo);
    }
}
