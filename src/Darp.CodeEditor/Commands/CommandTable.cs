using Avalonia.Input;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Commands;

/// <summary>What a command needs to apply.</summary>
[Flags]
internal enum CommandNeeds
{
    None = 0,

    /// <summary>The text has the keyboard. A key needs it; <see cref="CodeEditor.RunCommand"/> does not.</summary>
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
    private readonly Dictionary<string, Command> _commands = new(StringComparer.Ordinal);

    public CommandTable(TextArea area) => _area = area;

    /// <summary>
    /// The key whose command runs now; null while the app runs one. A command that decides later what to do, as an
    /// accept that waits for completion's filter, keeps it to do that key's normal action if it then does nothing.
    /// </summary>
    public KeyGesture? Gesture { get; private set; }

    /// <summary>Adds a command.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="run">What it does.</param>
    /// <param name="needs">What it needs to apply.</param>
    /// <param name="applies">The feature's own check, such as a server that completes; null for none.</param>
    public void Add(string id, Action run, CommandNeeds needs = CommandNeeds.TextFocus, Func<bool>? applies = null) =>
        _commands.Add(id, new Command(run, needs, applies));

    /// <summary>Whether a key can run the command now; otherwise the key goes on.</summary>
    public bool CanRun(string id) =>
        _commands.TryGetValue(id, out Command? command)
        && (!command.Needs.HasFlag(CommandNeeds.TextFocus) || _area.IsFocused)
        && Applies(command);

    /// <summary>Runs the command for the key of <paramref name="gesture"/>, if it can.</summary>
    /// <returns>Whether it ran.</returns>
    public bool TryRun(string id, KeyGesture gesture)
    {
        if (!CanRun(id))
            return false;
        Gesture = gesture;
        try
        {
            _commands[id].Run();
        }
        finally
        {
            Gesture = null;
        }
        return true;
    }

    /// <summary>
    /// Runs the command for the app, if it applies, wherever the keyboard is: on the app's button or menu at that moment.
    /// </summary>
    /// <returns>Whether it ran.</returns>
    public bool TryRunFromApp(string id)
    {
        if (!_commands.TryGetValue(id, out Command? command) || !Applies(command))
            return false;
        command.Run();
        return true;
    }

    private bool Applies(Command command) =>
        (!command.Needs.HasFlag(CommandNeeds.Writable) || !_area.IsReadOnly) && command.Applies?.Invoke() != false;

    private sealed record Command(Action Run, CommandNeeds Needs, Func<bool>? Applies);
}
