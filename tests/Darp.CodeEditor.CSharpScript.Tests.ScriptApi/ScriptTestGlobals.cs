using ScriptTests.Dependency;

namespace ScriptTests.Api;

/// <summary>The globals of the tests' scripts.</summary>
public class ScriptTestGlobals : DependencyGlobals
{
    /// <summary>The greeting that scripts read.</summary>
    public string Greeting => "hello";

    /// <summary>Adds two numbers.</summary>
    /// <param name="left">The first number.</param>
    /// <param name="right">The second number.</param>
    public int Add(int left, int right) => left + right;

    /// <summary>Writes a value.</summary>
    /// <param name="value">The value.</param>
    public void Write(int value) => _ = value;

    /// <summary>Writes a value.</summary>
    /// <param name="value">The value.</param>
    public void Write(string value) => _ = value;

    /// <summary>Joins values.</summary>
    /// <param name="separator">What goes between them.</param>
    /// <param name="values">The values.</param>
    public string Join(string separator, params int[] values) => string.Join(separator, values);

    /// <summary>Takes a value and a name.</summary>
    /// <param name="value">The value.</param>
    /// <param name="name">The name.</param>
    public void Named(int value, string name) => _ = (value, name);

    /// <summary>Picks a value.</summary>
    /// <typeparam name="T">The type of the values.</typeparam>
    /// <param name="values">The values.</param>
    /// <param name="index">Which one.</param>
    public T Pick<T>(T[] values, int index) => values[index];

    /// <summary>An identifier that is a keyword.</summary>
    public int @class => 1;

    /// <summary>Another identifier that is a keyword.</summary>
    public int @event => 1;

    /// <summary>An ordinary name that an escaped word can complete.</summary>
    public int Everything => 42;
}

/// <summary>A second kind of script's globals, in the same assembly.</summary>
public sealed class OtherGlobals
{
    /// <summary>Only the second kind of script has it.</summary>
    public int OtherValue => 1;
}

/// <summary>A type of the script API, for imports.</summary>
public sealed class ApiType
{
    /// <summary>A member of the type.</summary>
    public static int Value => 1;
}
