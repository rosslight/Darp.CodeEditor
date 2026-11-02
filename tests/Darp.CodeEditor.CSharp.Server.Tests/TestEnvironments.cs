using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.CSharp.TestGlobals;

namespace Darp.CodeEditor.CSharp.Server.Tests;

/// <summary>Script environments built the way a host builds them: from the path and name of a contract assembly.</summary>
internal static class TestEnvironments
{
    public static readonly string[] DefaultImports =
    [
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Threading.Tasks",
    ];

    public static readonly string[] DefaultFrameworkReferences =
    [
        "System.Collections",
        "System.Console",
        "System.Linq",
    ];

    public static CSharpScriptEnvironment For<TGlobals>() => For(typeof(TGlobals));

    public static CSharpScriptEnvironment For(Type? globalsType) =>
        new()
        {
            Globals = globalsType is null
                ? null
                : new CSharpScriptGlobals(
                    globalsType.Assembly.Location,
                    globalsType.FullName ?? throw new ArgumentException("The type has no name.", nameof(globalsType))
                ),
            FrameworkReferences = DefaultFrameworkReferences,
            Imports = DefaultImports,
        };

    public static CSharpScriptEnvironment Device => For<DeviceGlobals>();

    /// <summary>The prototype demo's environment: its globals, references and imports.</summary>
    public static CSharpScriptEnvironment Sample =>
        For<Darp.CodeEditor.Demo.ScriptGlobals>() with
        {
            FrameworkReferences = ["System.Linq"],
            Imports = ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks"],
        };
}
