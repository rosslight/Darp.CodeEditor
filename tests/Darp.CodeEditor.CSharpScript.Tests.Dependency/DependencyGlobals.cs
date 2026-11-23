namespace ScriptTests.Dependency;

/// <summary>A base class of the globals, in an assembly that the app does not mark.</summary>
public class DependencyGlobals
{
    /// <summary>A value that the globals inherit.</summary>
    public int InheritedValue => 21;
}

/// <summary>A type that scripts can't name, because its assembly is not marked.</summary>
public sealed class UnmarkedType;
