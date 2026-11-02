namespace Darp.CodeEditor.CSharp.TestGlobals;

/// <summary>Globals of device scripts. Sealed, like most host globals.</summary>
public sealed class DeviceGlobals
{
    /// <summary>The device the script runs on.</summary>
    public TestDevice Device { get; } = new();

    /// <summary>How often the script ran.</summary>
    public int Counter { get; set; }

    /// <summary>Writes a message to the script log.</summary>
    /// <param name="message">The text to write.</param>
    public void Log(string message) { }

    /// <summary>Writes a formatted message to the script log.</summary>
    /// <param name="format">A composite format string.</param>
    /// <param name="args">The values to format.</param>
    public void Log(string format, params object[] args) { }

    /// <summary>Writes a number to the script log.</summary>
    /// <param name="value">The number to write.</param>
    public void Log(int value) { }

    /// <summary>Reads a setting.</summary>
    /// <typeparam name="T">The type of the setting.</typeparam>
    /// <param name="key">The name of the setting.</param>
    public T? Get<T>(string key) => default;

    /// <summary>Keeps a reference to an object.</summary>
    public void Keep<T>(T value)
        where T : class { }

    /// <summary>Waits for the next sample.</summary>
    public Task<int> NextSampleAsync() => Task.FromResult(1);
}

/// <summary>Members every globals type of the app has.</summary>
public class BaseGlobals
{
    /// <summary>The name of the script.</summary>
    public string Name => "base";

    /// <summary>The limit of the script.</summary>
    public virtual int Limit => 1;

    /// <summary>Visible to derived globals types only.</summary>
    protected int Secret => 2;

    /// <summary>Writes a message to the app log.</summary>
    public void Trace(string message) { }
}

/// <summary>Globals that inherit members and hide one of them.</summary>
public sealed class DerivedGlobals : BaseGlobals
{
    /// <summary>The name, shadowing <see cref="BaseGlobals.Name"/> with another type.</summary>
    public new int Name => 1;

    /// <summary>The limit of the script.</summary>
    public override int Limit => 2;

    /// <summary>The device the script runs on.</summary>
    public TestDevice Device { get; } = new();
}

/// <summary>A sensor device.</summary>
public sealed class TestDevice
{
    /// <summary>Reads the current temperature in degrees Celsius.</summary>
    public double ReadTemperature() => 21.5;

    /// <summary>Reads raw bytes from a device register.</summary>
    /// <param name="register">The register address.</param>
    /// <param name="length">How many bytes to read.</param>
    public Task<byte[]> ReadAsync(int register, int length) => Task.FromResult(new byte[length]);
}
