using System.Text;

namespace Darp.CodeEditor.Samples;

internal static class SampleDocuments
{
    public const string CSharpScript = """
        // Device script: runs on every sample. `Device`, `Log` and `Limits` come from the host.
        using System.Text;

        var reading = Device.ReadTemperature();
        Log($"{Device.Name}: {reading:F1} °C");

        if (reading > Limits.MaxTemperature)
        {
            Device.SetLed(true);
            Log("Too hot, LED on");
        }

        var history = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            history.Add(Math.Round(Device.ReadTemperature(), 2));
        }

        var average = history.Average();
        var report = new StringBuilder()
            .AppendLine($"Average: {average:F2}")
            .AppendLine($"Peak: {history.Max():F2}")
            .ToString();
        Log(report);

        byte[] block = await Device.ReadAsync(register: 0x10, length: 4);
        int count = "three"; // error: shown as a squiggle

        static double ToFahrenheit(double celsius) => celsius * 9 / 5 + 32;
        Log($"In Fahrenheit: {ToFahrenheit(average):F1}");

        """;

    /// <summary>A script for the Run action: it returns a handler the app calls for each packet.</summary>
    public const string PacketScript = """
        // Packet script: press Run (F5). It runs in a separate process and returns a handler that the app
        // calls for every packet; App calls back into the app.
        class Decoder : IPacketHandler
        {
            private readonly IDeviceApp _app;

            public Decoder(IDeviceApp app) => _app = app;

            public async Task<Reading> HandleAsync(Packet packet, CancellationToken cancellationToken)
            {
                double offset = await _app.ReadTemperatureAsync(cancellationToken) - 21.5;
                return Decode(packet, offset);
            }

            // One call, and one call back into the app, for a whole batch of packets.
            public async Task<Reading[]> HandleBatchAsync(Packet[] packets, CancellationToken cancellationToken)
            {
                double offset = await _app.ReadTemperatureAsync(cancellationToken) - 21.5;
                return packets.Select(packet => Decode(packet, offset)).ToArray();
            }

            public void Dispose() { }

            private static Reading Decode(Packet packet, double offset) =>
                new(packet.Channel, BitConverter.ToInt16(packet.Payload) / 100.0 + offset, "°C");
        }

        await App.LogAsync("Decoder ready");
        new Decoder(App)

        """;

    /// <summary>A Luau script on the demo app's Luau API (<c>Luau/demo.d.luau</c>).</summary>
    public const string LuauScript = """
        --!strict
        -- Luau script with type annotations; device, limits and log come from the app
        type Reading = { name: string, value: number }

        local readings: { Reading } = {}

        local function addReading(name: string, value: number): Reading
            local reading = { name = name, value = value }
            table.insert(readings, reading)
            return reading
        end

        for i = 1, 10 do
            addReading(`{device.name} #{i}`, device:readTemperature())
        end

        local hottest: Reading? = nil
        for _, reading in readings do
            if hottest == nil or reading.value > hottest.value then
                hottest = reading
            end
        end

        if hottest and hottest.value > limits.maxTemperature then
            device:setLed(true)
            log(string.format("Hottest: %s at %.1f", hottest.name, hottest.value))
        end

        """;

    public const string Json = """
        {
          "device": {
            "name": "Sensor 7",
            "sampleRate": 50,
            "enabled": true,
            "channels": [ "temperature", "humidity", "pressure" ],
            "limits": { "maxTemperature": 42.5, "minTemperature": -10 }
          },
          "logging": {
            "level": "Information",
            "file": null
          }
        }

        """;

    /// <summary>Text typed in the typing bursts: words, member access and calls, so completion triggers often.</summary>
    public const string TypingBurst = """
        var current = Device.ReadTemperature();
        Log($"Current: {current}");
        var values = Enumerable.Range(0, 10).Select(i => Math.Sqrt(i)).ToList();
        if (current > Limits.MaxTemperature) { Device.SetLed(true); }
        var text = string.Join(", ", values.Where(v => v > 1));

        """;

    /// <summary>A C# file of about <paramref name="lines"/> lines.</summary>
    public static string LargeCSharpFile(int lines)
    {
        var builder = new StringBuilder();
        int blocks = lines / (CSharpScript.Count('\n') + 1) + 1;
        for (int i = 0; i < blocks; i++)
        {
            builder.AppendLine($"// Block {i}");
            builder.Append(CSharpScript.Replace("ToFahrenheit", $"ToFahrenheit{i}"));
        }
        return builder.ToString();
    }

    /// <summary>A C# document whose second line is a single line of <paramref name="length"/> characters.</summary>
    public static string LongLineFile(int length)
    {
        var builder = new StringBuilder("// One very long line follows\nvar data = new[] { ");
        for (int i = 0; builder.Length < length; i++)
            builder.Append($"\"item{i}\", {i}.5, ");
        builder.AppendLine("};").AppendLine("Log(data.Length.ToString());");
        return builder.ToString();
    }
}
