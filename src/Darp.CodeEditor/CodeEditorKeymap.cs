using System.Text.Json;
using Avalonia.Input;

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
    /// <item><term>f3, shift+f3 (also cmd+g, shift+cmd+g)</term><description><see cref="CodeEditorCommands.FindNext"/>, <see cref="CodeEditorCommands.FindPrevious"/></description></item>
    /// <item><term>alt+c, alt+w, alt+r, alt+p, alt+l (alt+cmd+c, …)</term><description><see cref="CodeEditorCommands.ToggleFindCaseSensitive"/>, <see cref="CodeEditorCommands.ToggleFindWholeWord"/>, <see cref="CodeEditorCommands.ToggleFindRegex"/>, <see cref="CodeEditorCommands.TogglePreserveCase"/>, <see cref="CodeEditorCommands.ToggleFindInSelection"/></description></item>
    /// <item><term>ctrl+/ (cmd+/)</term><description><see cref="CodeEditorCommands.CommentLine"/></description></item>
    /// <item><term>ctrl+z (cmd+z)</term><description><see cref="CodeEditorCommands.Undo"/></description></item>
    /// <item><term>ctrl+y, ctrl+shift+z (shift+cmd+z)</term><description><see cref="CodeEditorCommands.Redo"/></description></item>
    /// <item><term>ctrl+left, ctrl+shift+left (alt+left, shift+alt+left)</term><description><see cref="CodeEditorCommands.CursorWordLeft"/>, <see cref="CodeEditorCommands.CursorWordLeftSelect"/></description></item>
    /// <item><term>ctrl+right, ctrl+shift+right (alt+right, shift+alt+right)</term><description><see cref="CodeEditorCommands.CursorWordEndRight"/>, <see cref="CodeEditorCommands.CursorWordEndRightSelect"/></description></item>
    /// <item><term>ctrl+backspace, ctrl+delete (alt+backspace, alt+delete)</term><description><see cref="CodeEditorCommands.DeleteWordLeft"/>, <see cref="CodeEditorCommands.DeleteWordRight"/></description></item>
    /// <item><term>ctrl+d (cmd+d)</term><description><see cref="CodeEditorCommands.AddSelectionToNextFindMatch"/></description></item>
    /// <item><term>alt+up, alt+down</term><description><see cref="CodeEditorCommands.MoveLinesUp"/>, <see cref="CodeEditorCommands.MoveLinesDown"/></description></item>
    /// <item><term>shift+alt+up, shift+alt+down (Linux: ctrl+shift+alt+up, ctrl+shift+alt+down)</term><description><see cref="CodeEditorCommands.CopyLinesUp"/>, <see cref="CodeEditorCommands.CopyLinesDown"/></description></item>
    /// <item><term>ctrl+shift+k (shift+cmd+k)</term><description><see cref="CodeEditorCommands.DeleteLines"/></description></item>
    /// <item><term>ctrl+enter, ctrl+shift+enter (cmd+enter, shift+cmd+enter)</term><description><see cref="CodeEditorCommands.InsertLineAfter"/>, <see cref="CodeEditorCommands.InsertLineBefore"/></description></item>
    /// <item><term>ctrl+], ctrl+[ (cmd+], cmd+[)</term><description><see cref="CodeEditorCommands.IndentLines"/>, <see cref="CodeEditorCommands.OutdentLines"/></description></item>
    /// <item><term>ctrl+l (cmd+l)</term><description><see cref="CodeEditorCommands.ExpandLineSelection"/></description></item>
    /// <item><term>shift+alt+a (Linux: ctrl+shift+a)</term><description><see cref="CodeEditorCommands.BlockComment"/></description></item>
    /// <item><term>ctrl+shift+\ (shift+cmd+\)</term><description><see cref="CodeEditorCommands.JumpToBracket"/></description></item>
    /// <item><term>shift+alt+right, shift+alt+left (ctrl+shift+cmd+right, ctrl+shift+cmd+left)</term><description><see cref="CodeEditorCommands.SmartSelectExpand"/>, <see cref="CodeEditorCommands.SmartSelectShrink"/></description></item>
    /// <item><term>f7, shift+f7</term><description><see cref="CodeEditorCommands.WordHighlightNext"/>, <see cref="CodeEditorCommands.WordHighlightPrevious"/></description></item>
    /// </list>
    /// </remarks>
    public static CodeEditorKeymap Default { get; } = new(DefaultBindings(OperatingSystem.IsMacOS()));

    /// <summary>A keymap without bindings: the editor runs no command by key.</summary>
    public static CodeEditorKeymap Empty { get; } = new([]);

    /// <summary>The bindings, as given, removals included.</summary>
    public IReadOnlyList<CodeEditorKeyBinding> Bindings { get; }

    /// <summary>The bindings that remain once the removals applied, in order.</summary>
    internal IReadOnlyList<CodeEditorKeyBinding> Effective { get; }

    /// <summary>
    /// The strokes of <paramref name="command"/>'s latest binding, which a menu or a tip shows as its keys; none
    /// without a binding.
    /// </summary>
    internal IReadOnlyList<KeyGesture> GesturesOf(string command) =>
        Effective.LastOrDefault(binding => binding.Command == command) is { First: { } first } binding
            ? binding.Second is { } second
                ? [first.ToGesture(), second.ToGesture()]
                : [first.ToGesture()]
            : [];

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
            string keys = "";
            if (item.TryGetProperty("key", out JsonElement key))
            {
                // A removal without keys removes every binding of its command, so a key of another type is no omission.
                keys =
                    key.ValueKind == JsonValueKind.String
                        ? key.GetString() ?? ""
                        : throw new JsonException("A binding's key is a string.");
            }
            bindings.Add(new CodeEditorKeyBinding(keys, command.GetString() ?? ""));
        }
        return (basedOn ?? Default).With(bindings);
    }

    /// <summary>VS Code's keys on macOS when <paramref name="mac"/> is true, else on Windows and Linux.</summary>
    internal static IEnumerable<CodeEditorKeyBinding> DefaultBindings(bool mac)
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
        // Later, as the latest binding of a command is the one that a menu shows.
        if (mac)
        {
            yield return new("cmd+g", CodeEditorCommands.FindNext);
            yield return new("shift+cmd+g", CodeEditorCommands.FindPrevious);
        }
        // Cmd joins Alt on macOS, where Alt with a letter types a character.
        string toggle = mac ? "alt+cmd" : "alt";
        yield return new($"{toggle}+c", CodeEditorCommands.ToggleFindCaseSensitive);
        yield return new($"{toggle}+w", CodeEditorCommands.ToggleFindWholeWord);
        yield return new($"{toggle}+r", CodeEditorCommands.ToggleFindRegex);
        yield return new($"{toggle}+p", CodeEditorCommands.TogglePreserveCase);
        yield return new($"{toggle}+l", CodeEditorCommands.ToggleFindInSelection);
        yield return new($"{command}+/", CodeEditorCommands.CommentLine);
        yield return new($"{command}+z", CodeEditorCommands.Undo);
        if (!mac)
            yield return new("ctrl+y", CodeEditorCommands.Redo);
        yield return new($"{command}+shift+z", CodeEditorCommands.Redo);
        string words = mac ? "alt" : "ctrl";
        yield return new($"{words}+left", CodeEditorCommands.CursorWordLeft);
        yield return new($"{words}+shift+left", CodeEditorCommands.CursorWordLeftSelect);
        yield return new($"{words}+right", CodeEditorCommands.CursorWordEndRight);
        yield return new($"{words}+shift+right", CodeEditorCommands.CursorWordEndRightSelect);
        yield return new($"{words}+backspace", CodeEditorCommands.DeleteWordLeft);
        yield return new($"{words}+delete", CodeEditorCommands.DeleteWordRight);
        yield return new($"{command}+d", CodeEditorCommands.AddSelectionToNextFindMatch);
        foreach (CodeEditorKeyBinding binding in LineBindings(mac, linux: !mac && OperatingSystem.IsLinux()))
            yield return binding;
        yield return new($"{command}+shift+\\", CodeEditorCommands.JumpToBracket);
        string smartSelect = mac ? "ctrl+shift+cmd" : "shift+alt";
        yield return new($"{smartSelect}+right", CodeEditorCommands.SmartSelectExpand);
        yield return new($"{smartSelect}+left", CodeEditorCommands.SmartSelectShrink);
        yield return new("f7", CodeEditorCommands.WordHighlightNext);
        yield return new("shift+f7", CodeEditorCommands.WordHighlightPrevious);
    }

    /// <summary>
    /// The keys of the commands on whole lines and of the block comment: Linux has keys of its own where its desktops
    /// take the others.
    /// </summary>
    internal static IEnumerable<CodeEditorKeyBinding> LineBindings(bool mac, bool linux)
    {
        string command = mac ? "cmd" : "ctrl";
        string copy = linux ? "ctrl+shift+alt" : "shift+alt";
        yield return new("alt+up", CodeEditorCommands.MoveLinesUp);
        yield return new("alt+down", CodeEditorCommands.MoveLinesDown);
        yield return new($"{copy}+up", CodeEditorCommands.CopyLinesUp);
        yield return new($"{copy}+down", CodeEditorCommands.CopyLinesDown);
        yield return new($"{command}+shift+k", CodeEditorCommands.DeleteLines);
        yield return new($"{command}+enter", CodeEditorCommands.InsertLineAfter);
        yield return new($"{command}+shift+enter", CodeEditorCommands.InsertLineBefore);
        yield return new($"{command}+]", CodeEditorCommands.IndentLines);
        yield return new($"{command}+[", CodeEditorCommands.OutdentLines);
        yield return new($"{command}+l", CodeEditorCommands.ExpandLineSelection);
        yield return new(linux ? "ctrl+shift+a" : "shift+alt+a", CodeEditorCommands.BlockComment);
    }
}
