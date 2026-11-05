namespace Darp.CodeEditor;

/// <summary>Adds <c>CodeLanguage.CSharp</c> to the editor's languages.</summary>
/// <remarks>
/// <c>CodeLanguage.CSharp</c> is a C# 14 extension member. XAML's <c>x:Static</c> does not find extension members; script
/// documents come from <see cref="CSharpScript.CSharpScriptServer.CreateDocument"/> in code, which needs none.
/// </remarks>
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
        /// C#, from VS Code's C# extension: colors and editing rules. Script documents get it from
        /// <see cref="CSharpScript.CSharpScriptServer.CreateDocument"/>, which adds the server and the environment.
        /// </summary>
        public static CodeLanguage CSharp => CSharpLanguage;
    }
}
