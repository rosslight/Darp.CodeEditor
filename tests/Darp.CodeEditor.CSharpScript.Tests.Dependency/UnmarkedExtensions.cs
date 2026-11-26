namespace ScriptTests.Dependency.Extensions;

/// <summary>Extension methods that scripts can't name, because their assembly is not marked.</summary>
public static class UnmarkedExtensions
{
    /// <summary>The text twice.</summary>
    /// <param name="text">The text.</param>
    public static string Repeated(this string text) => text + text;
}
