using System.Reflection;
using Darp.CodeEditor.CSharp.Execution.Protocol;

namespace Darp.CodeEditor.CSharp.Runner;

/// <summary>
/// Types of the app's script API. The runner loads their assemblies from files next to the app, which can be another
/// build than the one the app was compiled with; calls between the two would then fail in obscure ways.
/// </summary>
internal static class AppTypes
{
    /// <summary>Loads the type by its assembly's simple name and checks that it is the app's build.</summary>
    public static Type Load(RunnerTypeName name)
    {
        string simpleName =
            new AssemblyName(name.Assembly).Name
            ?? throw new ArgumentException($"\"{name.Assembly}\" is not an assembly name.", nameof(name));
        Type type = Assembly.Load(new AssemblyName(simpleName)).GetType(name.FullName, throwOnError: true)!;
        Verify(type, name);
        return type;
    }

    /// <exception cref="ArgumentException">The runner loaded another build of the type's assembly than the app has.</exception>
    public static void Verify(Type type, RunnerTypeName name)
    {
        AssemblyName loaded = type.Assembly.GetName();
        Guid module = type.Module.ModuleVersionId;
        if (loaded.FullName == new AssemblyName(name.Assembly).FullName && module == name.ModuleVersionId)
            return;
        throw new ArgumentException(
            $"The app was built with {name.Assembly} (module {name.ModuleVersionId}), but the runner loaded "
                + $"{loaded.FullName} (module {module}) from {type.Assembly.Location}. Ship the {loaded.Name} assembly "
                + "the app was built with next to the app."
        );
    }
}
