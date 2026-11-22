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
        Table(OperatingSystem.IsMacOS(), OperatingSystem.IsLinux()).Select(key => Binding(editor, key.Id, key.Gesture));

    /// <summary>A binding that names its command in <c>CommandParameter</c>, so that an app can find and move it.</summary>
    private static KeyBinding Binding(CodeEditor editor, string id, KeyGesture gesture)
    {
        var binding = new KeyBinding { Gesture = gesture, CommandParameter = id };
        binding.Command = new KeyCommand(editor, id, binding);
        return binding;
    }

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
    /// search. Linux copies lines with Ctrl+Shift+Alt, as its desktops take Shift+Alt+Up and Down. Where a key has two
    /// commands, the first that applies runs.
    /// </summary>
    internal static IEnumerable<(string Id, KeyGesture Gesture)> Table(bool mac, bool linux = false)
    {
        KeyModifiers command = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        // The details toggle goes first: it applies while the list shows, and the key asks for completion otherwise.
        foreach (KeyGesture gesture in (KeyGesture[])[new(Key.Space, KeyModifiers.Control), new(Key.I, command)])
        {
            yield return (CodeEditorCommands.ToggleSuggestionDetails, gesture);
            yield return (CodeEditorCommands.TriggerSuggest, gesture);
        }
        if (mac)
            yield return (CodeEditorCommands.TriggerSuggest, new KeyGesture(Key.Escape, KeyModifiers.Alt));
        // The hover's key: the details toggle applies only while the list shows.
        KeyGesture hover = mac || linux ? new KeyGesture(Key.F1) : new KeyGesture(Key.Q, KeyModifiers.Control);
        yield return (CodeEditorCommands.ToggleSuggestionDetails, hover);
        yield return (CodeEditorCommands.ShowHover, hover);
        yield return (CodeEditorCommands.NextMarker, new KeyGesture(Key.F8));
        yield return (CodeEditorCommands.PreviousMarker, new KeyGesture(Key.F8, KeyModifiers.Shift));
        yield return (CodeEditorCommands.AcceptSelectedSuggestion, new KeyGesture(Key.Enter));
        yield return (CodeEditorCommands.AcceptAlternativeSelectedSuggestion, new KeyGesture(Key.Tab));
        yield return (
            CodeEditorCommands.AcceptAlternativeSelectedSuggestion,
            new KeyGesture(Key.Enter, KeyModifiers.Shift)
        );
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

        KeyModifiers copyLines =
            KeyModifiers.Shift | KeyModifiers.Alt | (linux ? KeyModifiers.Control : KeyModifiers.None);
        // Ahead of moving lines: they apply only while several signatures show.
        yield return (CodeEditorCommands.ShowPrevParameterHint, new KeyGesture(Key.Up, KeyModifiers.Alt));
        yield return (CodeEditorCommands.ShowNextParameterHint, new KeyGesture(Key.Down, KeyModifiers.Alt));
        yield return (CodeEditorCommands.MoveLinesUp, new KeyGesture(Key.Up, KeyModifiers.Alt));
        yield return (CodeEditorCommands.MoveLinesDown, new KeyGesture(Key.Down, KeyModifiers.Alt));
        yield return (CodeEditorCommands.CopyLinesUp, new KeyGesture(Key.Up, copyLines));
        yield return (CodeEditorCommands.CopyLinesDown, new KeyGesture(Key.Down, copyLines));
        yield return (CodeEditorCommands.DeleteLines, new KeyGesture(Key.K, command | KeyModifiers.Shift));
        yield return (CodeEditorCommands.InsertLineAfter, new KeyGesture(Key.Enter, command));
        yield return (CodeEditorCommands.InsertLineBefore, new KeyGesture(Key.Enter, command | KeyModifiers.Shift));
        yield return (CodeEditorCommands.IndentLines, new KeyGesture(Key.OemCloseBrackets, command));
        yield return (CodeEditorCommands.OutdentLines, new KeyGesture(Key.OemOpenBrackets, command));
        yield return (CodeEditorCommands.ExpandLineSelection, new KeyGesture(Key.L, command));

        yield return (CodeEditorCommands.Undo, new KeyGesture(Key.Z, command));
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
        private readonly KeyBinding _binding;

        public KeyCommand(CodeEditor editor, string id, KeyBinding binding)
        {
            _editor = editor;
            _id = id;
            _binding = binding;
        }

        /// <summary>Never raised: Avalonia asks at each key.</summary>
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => _editor.View?.CanRun(_id) == true;

        public void Execute(object? parameter) => _editor.View?.RunKey(_id, _binding.Gesture);
    }
}
