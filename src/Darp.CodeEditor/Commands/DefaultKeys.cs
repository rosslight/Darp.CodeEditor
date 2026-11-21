using System.Windows.Input;
using Avalonia.Input;

namespace Darp.CodeEditor.Commands;

/// <summary>
/// The editor's default keys: ordinary <see cref="KeyBinding"/>s in <see cref="CodeEditor"/>'s <c>KeyBindings</c>, which
/// go behind those of the app's XAML when it is first attached, so that the app's run first.
/// </summary>
internal static class DefaultKeys
{
    /// <summary>The bindings of this operating system, for <paramref name="editor"/>.</summary>
    public static IEnumerable<KeyBinding> For(CodeEditor editor) =>
        Table(OperatingSystem.IsMacOS(), OperatingSystem.IsLinux())
            .Select(key => new KeyBinding { Gesture = key.Gesture, Command = new KeyCommand(editor, key.Id) });

    /// <summary>Puts the defaults that remain behind the app's bindings, in their order.</summary>
    public static void MoveLast(List<KeyBinding> bindings)
    {
        KeyBinding[] defaults = [.. bindings.Where(IsDefault)];
        bindings.RemoveAll(IsDefault);
        bindings.AddRange(defaults);
    }

    private static bool IsDefault(KeyBinding binding) => binding.Command is KeyCommand;

    /// <summary>
    /// The commands and their keys: Cmd takes Ctrl's place on macOS, except for Ctrl+Space, as Cmd+Space is the system's
    /// search. Where a key has two commands, the first that applies runs.
    /// </summary>
    internal static IEnumerable<(string Id, KeyGesture Gesture)> Table(bool mac, bool linux)
    {
        KeyModifiers command = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        yield return (CodeEditorCommands.TriggerSuggest, new KeyGesture(Key.Space, KeyModifiers.Control));
        yield return (CodeEditorCommands.TriggerSuggest, new KeyGesture(Key.I, command));
        if (mac)
            yield return (CodeEditorCommands.TriggerSuggest, new KeyGesture(Key.Escape, KeyModifiers.Alt));
        yield return (
            CodeEditorCommands.TriggerParameterHints,
            new KeyGesture(Key.Space, command | KeyModifiers.Shift)
        );

        yield return (CodeEditorCommands.Find, new KeyGesture(Key.F, command));
        yield return (
            CodeEditorCommands.Replace,
            mac ? new KeyGesture(Key.F, KeyModifiers.Alt | command) : new KeyGesture(Key.H, command)
        );
        yield return (CodeEditorCommands.FindNext, new KeyGesture(Key.F3));
        yield return (CodeEditorCommands.FindPrevious, new KeyGesture(Key.F3, KeyModifiers.Shift));
        if (mac)
        {
            yield return (CodeEditorCommands.FindNext, new KeyGesture(Key.G, command));
            yield return (CodeEditorCommands.FindPrevious, new KeyGesture(Key.G, command | KeyModifiers.Shift));
        }

        yield return (CodeEditorCommands.CommentLine, new KeyGesture(Key.Oem2, command));
        yield return (CodeEditorCommands.CommentLine, new KeyGesture(Key.Divide, command));
        // On X11, the "/" of a German layout, Shift+7, arrives as the 7 key with Shift.
        if (linux)
            yield return (CodeEditorCommands.CommentLine, new KeyGesture(Key.D7, command | KeyModifiers.Shift));

        yield return (CodeEditorCommands.Undo, new KeyGesture(Key.Z, command));
        if (!mac)
            yield return (CodeEditorCommands.Redo, new KeyGesture(Key.Y, command));
        yield return (CodeEditorCommands.Redo, new KeyGesture(Key.Z, command | KeyModifiers.Shift));

        yield return (CodeEditorCommands.Cut, new KeyGesture(Key.X, command));
        yield return (CodeEditorCommands.Copy, new KeyGesture(Key.C, command));
        yield return (CodeEditorCommands.Paste, new KeyGesture(Key.V, command));
        yield return (CodeEditorCommands.SelectAll, new KeyGesture(Key.A, command));
    }

    /// <summary>
    /// What a default binding runs. Where its command does not apply, <see cref="CanExecute"/> says so, and Avalonia goes
    /// on to the next binding of the key and, after them all, raises the key as usual.
    /// </summary>
    private sealed class KeyCommand : ICommand
    {
        private readonly CodeEditor _editor;
        private readonly string _id;

        public KeyCommand(CodeEditor editor, string id)
        {
            _editor = editor;
            _id = id;
        }

        /// <summary>Never raised: Avalonia asks at each key.</summary>
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => _editor.View?.CanRun(_id) == true;

        public void Execute(object? parameter) => _editor.View?.RunKey(_id);
    }
}
