namespace Darp.CodeEditor;

/// <summary>Adds <c>CodeLanguage.CSharp</c> to the editor's languages.</summary>
public static class CSharpCodeLanguage
{
    private static readonly CodeLanguage CSharpLanguage = new(
        "csharp",
        new Uri("avares://Darp.CodeEditor.CSharpScript/Languages/csharp.tmLanguage.json"),
        new Uri("avares://Darp.CodeEditor.CSharpScript/Languages/csharp.language-configuration.json")
    );

    extension(CodeLanguage)
    {
        /// <summary>
        /// C#, from VS Code's C# extension, without a language server: colors and editing rules. For scripts,
        /// <see cref="CSharpScript.CSharpScriptServer.CreateLanguage"/> adds the server and a script environment.
        /// </summary>
        public static CodeLanguage CSharp => CSharpLanguage;
    }
}
