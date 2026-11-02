using System.Buffers;
using System.Text;
using System.Text.Json;
using Darp.CodeEditor.Web;

namespace Darp.CodeEditor.WebDemo;

/// <summary>Awaits page events and runs measurement commands in the page (see web/src/measure.ts).</summary>
internal static class PageCommands
{
    private static int s_nextId;

    /// <summary>Completes with the next event of that type. Subscribe before the page can send it.</summary>
    public static Task<WebEditorEvent> NextEventAsync(
        WebCodeEditor editor,
        string type,
        Func<WebEditorEvent, bool>? match = null
    )
    {
        var completion = new TaskCompletionSource<WebEditorEvent>();
        editor.PageEvent += OnEvent;
        return completion.Task;

        void OnEvent(object? sender, WebEditorEvent e)
        {
            if (e.Type != type || (match is not null && !match(e)))
                return;
            editor.PageEvent -= OnEvent;
            completion.TrySetResult(e);
        }
    }

    /// <summary>Completes with the event's wall-clock time ("at"), in ms after process start.</summary>
    public static async Task<double> MilestoneAsync(WebCodeEditor editor, string type) =>
        WebProcessClock.FromUnixMs((await NextEventAsync(editor, type)).Data.GetProperty("at").GetDouble());

    /// <summary>Runs a command and returns the result lines; throws when the page reports a failure.</summary>
    public static async Task<JsonElement> RunAsync(
        WebCodeEditor editor,
        string type,
        Action<Utf8JsonWriter>? writeArguments = null,
        TimeSpan? timeout = null
    )
    {
        int id = Interlocked.Increment(ref s_nextId);
        Task<WebEditorEvent> result = NextEventAsync(
            editor,
            "result",
            e => e.Data.TryGetProperty("id", out JsonElement value) && value.GetInt32() == id
        );
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteNumber("id", id);
            writeArguments?.Invoke(writer);
            writer.WriteEndObject();
        }
        editor.PostCommand(Encoding.UTF8.GetString(buffer.WrittenSpan));
        JsonElement data = (await result.WaitAsync(timeout ?? TimeSpan.FromMinutes(3))).Data;
        if (data.TryGetProperty("failed", out JsonElement failed))
            throw new InvalidOperationException($"Page command {type} failed: {failed.GetString()}");
        return data;
    }

    public static IEnumerable<string> Lines(JsonElement result) =>
        result.GetProperty("lines").EnumerateArray().Select(l => l.GetString() ?? "");
}

/// <summary>Converts page timestamps (wall-clock ms since 1970) to ms after this process started.</summary>
internal static class WebProcessClock
{
    private static readonly double s_processStartUnixMs = new DateTimeOffset(
        System.Diagnostics.Process.GetCurrentProcess().StartTime
    ).ToUnixTimeMilliseconds();

    public static double FromUnixMs(double unixMs) => unixMs - s_processStartUnixMs;
}
