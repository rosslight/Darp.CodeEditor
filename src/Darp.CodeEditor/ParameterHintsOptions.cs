namespace Darp.CodeEditor;

/// <summary>
/// How signature help shows the parameters of the call around the caret, as VS Code's <c>editor.parameterHints</c>. Set
/// it as <see cref="CodeEditor.ParameterHints"/>.
/// </summary>
/// <param name="Enabled">
/// Whether signature help opens as the user types, on the server's trigger characters, such as <c>(</c>, as VS Code's
/// <c>enabled</c>. Asking for it by key, Ctrl+Shift+Space, and a completion item's command open it either way.
/// </param>
/// <param name="Cycle">
/// Whether Up and Down go on from the last signature to the first and from the first to the last, as VS Code's
/// <c>cycle</c>; otherwise signature help closes there.
/// </param>
public readonly record struct ParameterHintsOptions(bool Enabled, bool Cycle)
{
    /// <summary>VS Code's default: both.</summary>
    public static ParameterHintsOptions Default => new(Enabled: true, Cycle: true);
}
