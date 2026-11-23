using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.CSharpScript.Protocol;

/// <summary>The params of the server's compile request.</summary>
/// <param name="Environment">What the script sees.</param>
/// <param name="Text">
/// The text to compile. It comes with the request because the client's snapshot may already be behind the open document,
/// or the document closed.
/// </param>
internal sealed record ScriptCompileParams(ScriptEnvironmentData Environment, string Text)
{
    /// <summary>The request's method.</summary>
    public const string Method = "csharpScript/compile";
}

/// <summary>The answer to a compile request.</summary>
/// <param name="Diagnostics">The script's errors and warnings.</param>
/// <param name="Script">The compiled script; null when it has errors.</param>
internal sealed record ScriptCompileResult(IReadOnlyList<LspDiagnostic> Diagnostics, ScriptImage? Script);

/// <summary>An emitted script, which runs without the server.</summary>
/// <param name="Assembly">The assembly, with its portable PDB embedded.</param>
/// <param name="Name">Its assembly name, unique to this compilation.</param>
/// <param name="EntryType">The metadata name of the type with the script's entry point.</param>
/// <param name="EntryMethod">The metadata name of the entry point, which takes the globals in an <c>object[]</c>.</param>
internal sealed record ScriptImage(byte[] Assembly, string Name, string EntryType, string EntryMethod);
