using System.Reflection;

namespace Darp.CodeEditor.CSharp.Execution.TestDependency;

/// <summary>Used by the test globals at run time only, through two levels of dependencies.</summary>
/// <remarks>
/// The project also has an en satellite assembly, which a NativeAOT publish must ship the way a build does. The runner
/// uses invariant globalization, so it never loads it.
/// </remarks>
public static class TestDependency
{
    /// <summary>Names a type of System.Reflection.MetadataLoadContext, which loads that assembly.</summary>
    public static string Describe() => typeof(MetadataLoadContext).Name;
}
