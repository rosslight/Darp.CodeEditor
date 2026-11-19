using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Commands;

/// <summary>What a command needs to apply.</summary>
[Flags]
internal enum CommandNeeds
{
    None = 0,

    /// <summary>The text has the keyboard.</summary>
    TextFocus = 1,

    /// <summary>The editor is not read-only.</summary>
    Writable = 2,
}

/// <summary>
/// The commands of one editor's view of a document, by their id (<see cref="CodeEditorCommands"/>): each feature adds
/// its own, with what it needs to apply, which the table checks before it runs one.
/// </summary>
internal sealed class CommandTable
{
    private readonly TextArea _area;
    private readonly Dictionary<string, (Func<Action?, bool> Run, CommandNeeds Needs)> _commands = new(
        StringComparer.Ordinal
    );

    public CommandTable(TextArea area) => _area = area;

    /// <summary>Adds a command that always applies where its needs are met.</summary>
    public void Add(string id, Action run, CommandNeeds needs = CommandNeeds.TextFocus) =>
        Add(
            id,
            () =>
            {
                run();
                return true;
            },
            needs
        );

    /// <summary>Adds a command that decides itself whether it applies, by what it returns.</summary>
    public void Add(string id, Func<bool> runIfApplies, CommandNeeds needs = CommandNeeds.TextFocus) =>
        _commands.Add(id, (_ => runIfApplies(), needs));

    /// <summary>
    /// Adds a command that decides itself whether it applies, and may decide later: it gets what its key goes on to as
    /// if the command had not applied, to call once it decides that it does nothing; null when no key runs it, as from
    /// a menu.
    /// </summary>
    public void Add(string id, Func<Action?, bool> runIfApplies, CommandNeeds needs = CommandNeeds.TextFocus) =>
        _commands.Add(id, (runIfApplies, needs));

    /// <summary>
    /// Runs the command if it applies; for a key's binding, with <paramref name="goOn"/>, what the key goes on to after
    /// it.
    /// </summary>
    /// <returns>Whether it applied; otherwise its key goes on to the next binding, or on as an ordinary key.</returns>
    public bool TryRun(string id, Action? goOn = null) =>
        _commands.TryGetValue(id, out (Func<Action?, bool> Run, CommandNeeds Needs) command)
        && (!command.Needs.HasFlag(CommandNeeds.TextFocus) || _area.IsFocused)
        && (!command.Needs.HasFlag(CommandNeeds.Writable) || !_area.IsReadOnly)
        && command.Run(goOn);
}
