namespace ScriptTests.Extra;

/// <summary>Tools in a second marked assembly, which an environment can leave out.</summary>
public static class ExtraTools
{
    /// <summary>Doubles a number.</summary>
    /// <param name="value">The number.</param>
    public static int Twice(int value) => value * 2;
}
