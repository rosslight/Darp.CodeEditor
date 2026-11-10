using System.Text.Json;
using Avalonia.Input;

namespace Darp.CodeEditor;

/// <summary>
/// Which keys run which of the editor's commands: key sequences of one or two strokes, in VS Code's syntax, such as
/// <c>ctrl+space</c> or <c>ctrl+k ctrl+i</c>, each bound to a command of <see cref="CodeEditorCommands"/>. Set one as
/// <see cref="CodeEditor.Keymap"/>; <see cref="Default"/> follows VS Code's keys.
/// </summary>
/// <remarks>
/// <para>
/// Bindings apply in order, as in VS Code's <c>keybindings.json</c>: a later binding of a key wins over an earlier one,
/// and a binding whose command starts with <c>-</c> removes the earlier bindings of that command, of its keys only when
/// it names keys. Where several bindings share a key, the latest whose command applies runs, so that one key can do
/// different things where they make sense, such as Ctrl+Space, which asks for completion, or toggles the details
/// while the list shows. <c>when</c> clauses are read and ignored.
/// </para>
/// <para>
/// After the first stroke of a two-stroke sequence, the editor waits for the second, and shows that in
/// <see cref="CodeEditor.PendingKeys"/>. A second stroke that completes no sequence counts as an ordinary key; losing the
/// keyboard or a click ends the wait. The keys inside open popups, such as Up, Down, Enter and Escape in completion's
/// list, and the keys of AvaloniaEdit's editing, such as the clipboard's, are not in the keymap.
/// </para>
/// <para>
/// A keymap is immutable, and can be shared between editors.
/// </para>
/// </remarks>
public sealed class CodeEditorKeymap
{
    private static readonly JsonDocumentOptions s_jsonc = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<CodeEditorKeymap> s_default = new(() =>
        new CodeEditorKeymap(DefaultBindings(OperatingSystem.IsMacOS()))
    );

    /// <summary>The bindings in effect: removals applied, the latest last.</summary>
    private readonly IReadOnlyList<CodeEditorKeyBinding> _effective;

    /// <summary>Creates a keymap of <paramref name="bindings"/>, applied in order.</summary>
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
            effective.RemoveAll(b =>
                b.Command == command && (binding.Strokes.Count == 0 || b.Strokes.SequenceEqual(binding.Strokes))
            );
        }
        _effective = effective;
    }

    /// <summary>
    /// VS Code's keys for the editor's commands, on this operating system. On macOS, Cmd takes Ctrl's place, except
    /// for completion's Ctrl+Space: Cmd+Space is the system's search.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    /// <listheader><term>Keys (macOS)</term><description>Command</description></listheader>
    /// <item><term>ctrl+space</term><description><see cref="CodeEditorCommands.TriggerSuggest"/>, and <see cref="CodeEditorCommands.ToggleSuggestionDetails"/> while the list shows</description></item>
    /// <item><term>ctrl+k ctrl+i (cmd+k cmd+i)</term><description><see cref="CodeEditorCommands.ShowHover"/></description></item>
    /// <item><term>ctrl+shift+space (shift+cmd+space)</term><description><see cref="CodeEditorCommands.TriggerParameterHints"/></description></item>
    /// <item><term>ctrl+f (cmd+f)</term><description><see cref="CodeEditorCommands.Find"/></description></item>
    /// <item><term>ctrl+h (alt+cmd+f)</term><description><see cref="CodeEditorCommands.Replace"/></description></item>
    /// <item><term>f3, shift+f3</term><description><see cref="CodeEditorCommands.FindNext"/>, <see cref="CodeEditorCommands.FindPrevious"/></description></item>
    /// <item><term>ctrl+/ (cmd+/)</term><description><see cref="CodeEditorCommands.CommentLine"/></description></item>
    /// </list>
    /// </remarks>
    public static CodeEditorKeymap Default => s_default.Value;

    /// <summary>A keymap without bindings: the editor runs no command by key.</summary>
    public static CodeEditorKeymap Empty { get; } = new([]);

    /// <summary>The bindings, as given, removals included.</summary>
    public IReadOnlyList<CodeEditorKeyBinding> Bindings { get; }

    /// <summary>This keymap with <paramref name="bindings"/> applied after its own.</summary>
    public CodeEditorKeymap With(params IEnumerable<CodeEditorKeyBinding> bindings) => new([.. Bindings, .. bindings]);

    /// <summary>
    /// Reads a keymap in the shape of VS Code's <c>keybindings.json</c>: an array of
    /// <c>{ "key": "ctrl+k ctrl+i", "command": "editor.action.showHover" }</c>, with comments allowed. The bindings
    /// apply after those of <paramref name="basedOn"/>, by default <see cref="Default"/>, as VS Code applies a user's
    /// file after its defaults. Use <see cref="Empty"/> to start from nothing.
    /// </summary>
    /// <exception cref="JsonException">The text is not such an array.</exception>
    /// <exception cref="FormatException">A key is not in VS Code's syntax.</exception>
    public static CodeEditorKeymap FromJson(string json, CodeEditorKeymap? basedOn = null)
    {
        using JsonDocument document = JsonDocument.Parse(json, s_jsonc);
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
            {
                throw new JsonException("A binding needs a command.");
            }
            string keys =
                item.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String
                    ? key.GetString() ?? ""
                    : "";
            string? when =
                item.TryGetProperty("when", out JsonElement value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            bindings.Add(new CodeEditorKeyBinding(keys, command.GetString() ?? "") { When = when });
        }
        return (basedOn ?? Default).With(bindings);
    }

    /// <summary>
    /// The commands bound to <paramref name="key"/> alone, or to <paramref name="pending"/> followed by it: the latest
    /// binding first.
    /// </summary>
    internal IEnumerable<string> CommandsFor(KeyEventArgs key, KeyStroke? pending)
    {
        for (int i = _effective.Count - 1; i >= 0; i--)
        {
            IReadOnlyList<KeyStroke> strokes = _effective[i].Strokes;
            if (pending is { } prefix)
            {
                if (strokes.Count == 2 && strokes[0] == prefix && strokes[1].Matches(key))
                    yield return _effective[i].Command;
            }
            else if (strokes.Count == 1 && strokes[0].Matches(key))
                yield return _effective[i].Command;
        }
    }

    /// <summary>The first stroke of a two-stroke sequence that <paramref name="e"/> is; null if none starts with it.</summary>
    internal KeyStroke? ChordStartedBy(KeyEventArgs e)
    {
        for (int i = _effective.Count - 1; i >= 0; i--)
        {
            IReadOnlyList<KeyStroke> strokes = _effective[i].Strokes;
            if (strokes.Count == 2 && strokes[0].Matches(e))
                return strokes[0];
        }
        return null;
    }

    private static IEnumerable<CodeEditorKeyBinding> DefaultBindings(bool mac)
    {
        string command = mac ? "cmd" : "ctrl";
        yield return new("ctrl+space", CodeEditorCommands.TriggerSuggest);
        yield return new("ctrl+space", CodeEditorCommands.ToggleSuggestionDetails);
        yield return new($"{command}+k {command}+i", CodeEditorCommands.ShowHover);
        yield return new(mac ? "shift+cmd+space" : "ctrl+shift+space", CodeEditorCommands.TriggerParameterHints);
        yield return new($"{command}+f", CodeEditorCommands.Find);
        yield return new(mac ? "alt+cmd+f" : "ctrl+h", CodeEditorCommands.Replace);
        yield return new("f3", CodeEditorCommands.FindNext);
        yield return new("shift+f3", CodeEditorCommands.FindPrevious);
        yield return new($"{command}+/", CodeEditorCommands.CommentLine);
    }
}

/// <summary>
/// A binding of a keymap: a key sequence of one or two strokes in VS Code's syntax, and a command. A command that
/// starts with <c>-</c> removes earlier bindings of that command instead.
/// </summary>
public sealed record CodeEditorKeyBinding
{
    /// <summary>Binds <paramref name="keys"/>, such as <c>ctrl+k ctrl+i</c>, to <paramref name="command"/>.</summary>
    /// <param name="keys">
    /// One or two strokes, apart by a space. A stroke is modifiers and a key joined by <c>+</c>: the modifiers
    /// <c>ctrl</c>, <c>shift</c>, <c>alt</c> and <c>cmd</c> (also <c>meta</c> or <c>win</c>), and a key such as
    /// <c>a</c>, <c>1</c>, <c>f3</c>, <c>space</c>, <c>enter</c>, <c>pageup</c> or <c>/</c>. Empty only for a removal.
    /// </param>
    /// <param name="command">A command of <see cref="CodeEditorCommands"/>, or one with <c>-</c> before it to remove it.</param>
    /// <exception cref="FormatException"><paramref name="keys"/> is not in VS Code's syntax.</exception>
    public CodeEditorKeyBinding(string keys, string command)
    {
        Keys = keys;
        Command = command;
        Strokes = KeyStroke.ParseSequence(keys);
        if (Strokes.Count == 0 && !IsRemoval)
            throw new FormatException("A binding needs keys.");
    }

    /// <summary>The keys, as given.</summary>
    public string Keys { get; }

    /// <summary>The command, with <c>-</c> before it for a removal.</summary>
    public string Command { get; }

    /// <summary>The binding's <c>when</c> clause, as read; the editor ignores it.</summary>
    public string? When { get; init; }

    /// <summary>The strokes as Avalonia's gestures, for showing the keys, such as in a menu.</summary>
    public IReadOnlyList<KeyGesture> Gestures => [.. Strokes.Select(stroke => stroke.ToGesture())];

    internal IReadOnlyList<KeyStroke> Strokes { get; }

    internal bool IsRemoval => Command.StartsWith('-');
}

/// <summary>
/// The names of the editor's commands, for <see cref="CodeEditorKeymap"/>: VS Code's command ids, so that a VS Code
/// <c>keybindings.json</c> binds them.
/// </summary>
public static class CodeEditorCommands
{
    /// <summary>Asks for completion at the caret; with nothing to suggest, says so.</summary>
    public const string TriggerSuggest = "editor.action.triggerSuggest";

    /// <summary>
    /// While completion's list shows, shows or hides the selected item's details beside it; the choice holds for the
    /// app's run.
    /// </summary>
    public const string ToggleSuggestionDetails = "toggleSuggestionDetails";

    /// <summary>Shows the hover at the caret: the diagnostics there and the server's hover. Again, it takes the keyboard.</summary>
    public const string ShowHover = "editor.action.showHover";

    /// <summary>Shows signature help for the call around the caret.</summary>
    public const string TriggerParameterHints = "editor.action.triggerParameterHints";

    /// <summary>Opens the search bar.</summary>
    public const string Find = "actions.find";

    /// <summary>Opens the replace bar.</summary>
    public const string Replace = "editor.action.startFindReplaceAction";

    /// <summary>Selects the next match.</summary>
    public const string FindNext = "editor.action.nextMatchFindAction";

    /// <summary>Selects the previous match.</summary>
    public const string FindPrevious = "editor.action.previousMatchFindAction";

    /// <summary>Comments or uncomments the selected lines.</summary>
    public const string CommentLine = "editor.action.commentLine";
}
