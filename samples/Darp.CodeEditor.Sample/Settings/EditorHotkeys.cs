using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Darp.CodeEditor;
using Sample.Localization;

namespace Sample.Settings;

/// <summary>
/// The keys of the editor's commands, which the settings page changes. They are the editor's own
/// <see cref="KeyBinding"/>s, each naming its command in <c>CommandParameter</c>, and last as long as the app runs.
/// </summary>
public sealed partial class EditorHotkeys : ObservableObject
{
    public EditorHotkeys() =>
        Commands = [
            new(CodeEditorCommands.Undo, strings => strings.Undo),
            new(CodeEditorCommands.Redo, strings => strings.Redo),
            Cut,
            Copy,
            Paste,
            SelectAll,
            new(CodeEditorCommands.Find, strings => strings.Find),
            new(CodeEditorCommands.Replace, strings => strings.Replace),
            new(CodeEditorCommands.FindNext, strings => strings.CommandFindNext),
            new(CodeEditorCommands.FindPrevious, strings => strings.CommandFindPrevious),
            new(CodeEditorCommands.CommentLine, strings => strings.CommandCommentLine),
            new(CodeEditorCommands.MoveLinesUp, strings => strings.CommandMoveLinesUp),
            new(CodeEditorCommands.MoveLinesDown, strings => strings.CommandMoveLinesDown),
            new(CodeEditorCommands.CopyLinesUp, strings => strings.CommandCopyLinesUp),
            new(CodeEditorCommands.CopyLinesDown, strings => strings.CommandCopyLinesDown),
            new(CodeEditorCommands.DeleteLines, strings => strings.CommandDeleteLines),
            new(CodeEditorCommands.InsertLineAfter, strings => strings.CommandInsertLineAfter),
            new(CodeEditorCommands.InsertLineBefore, strings => strings.CommandInsertLineBefore),
            new(CodeEditorCommands.IndentLines, strings => strings.CommandIndentLines),
            new(CodeEditorCommands.OutdentLines, strings => strings.CommandOutdentLines),
            new(CodeEditorCommands.ExpandLineSelection, strings => strings.CommandExpandLineSelection),
            new(CodeEditorCommands.TriggerSuggest, strings => strings.CommandTriggerSuggest),
            new(CodeEditorCommands.ToggleSuggestionDetails, strings => strings.CommandToggleSuggestionDetails),
            new(CodeEditorCommands.AcceptSelectedSuggestion, strings => strings.CommandAcceptSuggestion),
            new(CodeEditorCommands.AcceptAlternativeSelectedSuggestion, strings => strings.CommandAcceptAlternative),
            new(CodeEditorCommands.TriggerParameterHints, strings => strings.CommandTriggerParameterHints),
            new(CodeEditorCommands.ShowPrevParameterHint, strings => strings.CommandPreviousSignature),
            new(CodeEditorCommands.ShowNextParameterHint, strings => strings.CommandNextSignature),
            new(CodeEditorCommands.ShowHover, strings => strings.CommandShowHover),
            new(CodeEditorCommands.NextMarker, strings => strings.CommandNextProblem),
            new(CodeEditorCommands.PreviousMarker, strings => strings.CommandPreviousProblem),
        ];

    /// <summary>Every command, in the order the settings page lists them.</summary>
    public IReadOnlyList<CommandHotkey> Commands { get; }

    public CommandHotkey Cut { get; } = new(CodeEditorCommands.Cut, strings => strings.Cut);

    public CommandHotkey Copy { get; } = new(CodeEditorCommands.Copy, strings => strings.Copy);

    public CommandHotkey Paste { get; } = new(CodeEditorCommands.Paste, strings => strings.Paste);

    public CommandHotkey SelectAll { get; } = new(CodeEditorCommands.SelectAll, strings => strings.SelectAll);

    /// <summary>The command waiting for its new key; null for none.</summary>
    [ObservableProperty]
    public partial CommandHotkey? Recording { get; set; }

    /// <summary>Takes the keys from the app's one editor; what they are now is what a reset goes back to.</summary>
    public void Use(IReadOnlyList<KeyBinding> bindings)
    {
        foreach (CommandHotkey command in Commands)
            command.Use(bindings);
    }

    /// <summary>Makes <paramref name="gesture"/> the recording command's only key.</summary>
    public void Assign(KeyGesture gesture)
    {
        if (Recording is not { } command)
            return;
        Recording = null;
        TakeFromOthers(command, gesture);
        command.Set(gesture);
    }

    /// <summary>Gives <paramref name="command"/> its default keys back, from commands that took them.</summary>
    public void Reset(CommandHotkey command)
    {
        foreach (KeyGesture gesture in command.Defaults)
            TakeFromOthers(command, gesture);
        command.Reset();
    }

    /// <summary>
    /// Removes <paramref name="gesture"/> from the other commands, except those that share it with
    /// <paramref name="command"/> by default, such as moving lines and stepping through signatures.
    /// </summary>
    private void TakeFromOthers(CommandHotkey command, KeyGesture gesture)
    {
        bool isDefault = command.Defaults.Contains(gesture);
        foreach (
            CommandHotkey other in Commands.Where(other =>
                other != command && !(isDefault && other.Defaults.Contains(gesture))
            )
        )
            other.Remove(gesture);
    }

    public void ResetAll()
    {
        foreach (CommandHotkey command in Commands)
            command.Reset();
    }

    partial void OnRecordingChanged(CommandHotkey? oldValue, CommandHotkey? newValue)
    {
        if (oldValue is not null)
            oldValue.IsRecording = false;
        if (newValue is not null)
            newValue.IsRecording = true;
    }
}

/// <summary>One of the editor's commands, with its keys.</summary>
public sealed partial class CommandHotkey : ObservableObject
{
    private readonly string _id;
    private readonly Func<Strings, string> _name;
    private KeyBinding[] _bindings = [];

    public CommandHotkey(string id, Func<Strings, string> name)
    {
        _id = id;
        _name = name;
        // Commands last as long as the app, as the strings do.
        Strings.Default.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public string Name => _name(Strings.Default);

    /// <summary>Whether the next key pressed becomes the command's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText))]
    public partial bool IsRecording { get; set; }

    /// <summary>The command's first key, for a menu; null without one.</summary>
    public KeyGesture? Gesture => _bindings.FirstOrDefault(binding => binding.Gesture is not null)?.Gesture;

    public bool HasKey => Gesture is not null;

    public bool IsChanged => !_bindings.Select(binding => binding.Gesture).SequenceEqual(Defaults);

    /// <summary>The command's keys in words, or what it waits for. Ctrl+/ and the number pad's read as one.</summary>
    public string KeyText =>
        IsRecording ? Strings.Default.PressKey
        : HasKey
            ? string.Join(
                ", ",
                _bindings
                    .Where(binding => binding.Gesture is not null)
                    .Select(binding => Describe(binding.Gesture))
                    .Distinct()
            )
        : Strings.Default.NoKey;

    internal KeyGesture[] Defaults { get; private set; } = [];

    /// <summary>
    /// Whether the command can have this key. Without Ctrl, Alt or Cmd a key types text, so only function keys qualify,
    /// and Enter and Tab for accepting a suggestion, where they apply only while the list shows.
    /// </summary>
    public bool CanTake(Key key, KeyModifiers modifiers) =>
        key
            is not (
                Key.LeftCtrl
                or Key.RightCtrl
                or Key.LeftShift
                or Key.RightShift
                or Key.LeftAlt
                or Key.RightAlt
                or Key.LWin
                or Key.RWin
            )
        && (
            (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0
            || key is >= Key.F1 and <= Key.F24
            || (
                key is Key.Enter or Key.Tab
                && _id
                    is CodeEditorCommands.AcceptSelectedSuggestion
                        or CodeEditorCommands.AcceptAlternativeSelectedSuggestion
            )
        );

    /// <summary>Leaves the command without a key.</summary>
    public void Clear()
    {
        foreach (KeyBinding binding in _bindings)
            binding.ClearValue(KeyBinding.GestureProperty);
        OnPropertyChanged(string.Empty);
    }

    internal void Use(IReadOnlyList<KeyBinding> bindings)
    {
        _bindings = [.. bindings.Where(binding => Equals(binding.CommandParameter, _id))];
        Defaults = [.. _bindings.Select(binding => binding.Gesture)];
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// Makes <paramref name="gesture"/> the command's only key. A default key returns to its own binding: where commands
    /// share a key, the first that applies runs.
    /// </summary>
    internal void Set(KeyGesture gesture)
    {
        foreach (KeyBinding binding in _bindings)
            binding.ClearValue(KeyBinding.GestureProperty);
        _bindings[Math.Max(Array.IndexOf(Defaults, gesture), 0)].Gesture = gesture;
        OnPropertyChanged(string.Empty);
    }

    internal void Remove(KeyGesture gesture)
    {
        // As Avalonia matches keys: to a binding, the minus key and the number pad's are one.
        var pressed = new KeyEventArgs { Key = gesture.Key, KeyModifiers = gesture.KeyModifiers };
        foreach (KeyBinding binding in _bindings.Where(binding => binding.Gesture?.Matches(pressed) == true))
            binding.ClearValue(KeyBinding.GestureProperty);
        OnPropertyChanged(string.Empty);
    }

    internal void Reset()
    {
        for (int i = 0; i < _bindings.Length; i++)
            _bindings[i].Gesture = Defaults[i];
        OnPropertyChanged(string.Empty);
    }

    private static string Describe(KeyGesture gesture) => gesture.ToString("p", null);
}
