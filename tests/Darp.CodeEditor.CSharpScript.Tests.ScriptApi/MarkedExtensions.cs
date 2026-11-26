namespace ScriptTests.Dependency.Extensions;

/// <summary>
/// An extension method that scripts can name, in the namespace of the unmarked dependency's, with the same name and
/// another receiver.
/// </summary>
public static class MarkedExtensions
{
    /// <summary>The number itself.</summary>
    /// <param name="value">The number.</param>
    public static int Repeated(this int value) => value;
}
