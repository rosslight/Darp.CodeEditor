using Darp.CodeEditor.CSharp.Execution.TestApi;
using Darp.CodeEditor.CSharp.Hosting;

namespace Darp.CodeEditor.CSharp.Execution.TestHost;

/// <summary>The environment of test scripts, built the way a NativeAOT app builds it: from files next to the app.</summary>
internal static class TestEnvironment
{
    public static CSharpScriptEnvironment Create() =>
        new()
        {
            Globals = new CSharpScriptGlobals(
                Path.Combine(AppContext.BaseDirectory, "Darp.CodeEditor.CSharp.Execution.TestApi.dll"),
                typeof(TestGlobals).FullName!
            ),
            FrameworkReferences = ["System.Collections", "System.Console", "System.Diagnostics.Process", "System.Linq"],
            Imports =
            [
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Threading",
                "System.Threading.Tasks",
                "Darp.CodeEditor.CSharp.Execution.TestApi",
            ],
        };
}
