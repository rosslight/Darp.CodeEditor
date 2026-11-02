namespace Darp.CodeEditor.Demo;

/// <summary>What device scripts can use without qualification.</summary>
public sealed class ScriptGlobals
{
    /// <summary>The device this script runs on.</summary>
    public Device Device { get; } = new();

    /// <summary>Limits configured for the device.</summary>
    public Limits Limits { get; } = new();

    /// <summary>Writes a message to the script log.</summary>
    /// <param name="message">The text to write.</param>
    public void Log(string message) => Console.WriteLine(message);
}

/// <summary>A sensor device.</summary>
public sealed class Device
{
    /// <summary>The display name of the device.</summary>
    public string Name => "Sensor 7";

    /// <summary>Reads the current temperature in degrees Celsius.</summary>
    public double ReadTemperature() => 21.5;

    /// <summary>Switches the status LED.</summary>
    /// <param name="on">True to switch the LED on.</param>
    public void SetLed(bool on) { }

    /// <summary>Reads raw bytes from a device register.</summary>
    /// <param name="register">The register address.</param>
    /// <param name="length">How many bytes to read.</param>
    public Task<byte[]> ReadAsync(int register, int length) => Task.FromResult(new byte[length]);

    /// <summary>Raised when the temperature changes by more than 0.5 °C.</summary>
    public event EventHandler<double>? TemperatureChanged
    {
        add { }
        remove { }
    }
}

/// <summary>Limits of the device.</summary>
public sealed class Limits
{
    /// <summary>Above this temperature the script should act.</summary>
    public double MaxTemperature => 40;
}
