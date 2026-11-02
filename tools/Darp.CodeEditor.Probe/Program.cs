using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Darp.CodeEditor.Probe;

// Measures editors from outside their process, the same way for every editor:
//   latency --target e2e.json [--count 60] [--out file]
//     Injects keystrokes as the OS would (XTest on X11, SendInput on Windows) and polls the screen until the typed
//     character's pixels change. The target file comes from a demo started with --e2e e2e.json.
//   startup [--title "Code editor demo"] [--out file] -- <app> [arguments]
//     Starts the app and polls its window until the first editor lines show text, then highlighted text (at least two
//     hues). Times are from starting the process, as seen on screen.
//   memory --pid <pid> [--out file]
//     Sums memory over the process and all its descendants (web content, network and GPU processes).

string? Value(string flag)
{
    int index = Array.IndexOf(args, flag);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var output = new List<string>();
void Report(string line)
{
    output.Add(line);
    Console.WriteLine(line);
}

switch (args.FirstOrDefault())
{
    case "latency":
    {
        JsonElement target = JsonDocument.Parse(File.ReadAllText(Value("--target") ?? "e2e.json")).RootElement;
        var cell = new TargetCell(
            target.GetProperty("x").GetDouble(),
            target.GetProperty("y").GetDouble(),
            target.GetProperty("lineHeight").GetDouble(),
            target.GetProperty("charWidth").GetDouble()
        );
        int count = int.Parse(Value("--count") ?? "60", CultureInfo.InvariantCulture);
        using IScreenInput screen = OperatingSystem.IsWindows() ? new Win32ScreenInput() : new X11ScreenInput();
        InputLatency.Run(screen, cell, count, Report);
        break;
    }
    case "startup":
    {
        int separator = Array.IndexOf(args, "--");
        if (separator < 0 || separator + 1 >= args.Length)
            throw new ArgumentException("startup needs -- <app> [arguments]");
        using IScreenInput screen = OperatingSystem.IsWindows() ? new Win32ScreenInput() : new X11ScreenInput();
        StartupOnScreen.Run(
            screen,
            Value("--title") ?? "Code editor demo",
            args[separator + 1],
            args[(separator + 2)..],
            Report
        );
        break;
    }
    case "memory":
    {
        int pid = int.Parse(
            Value("--pid") ?? throw new ArgumentException("--pid is required"),
            CultureInfo.InvariantCulture
        );
        ProcessTreeMemory.Report(pid, Report);
        break;
    }
    default:
        Console.Error.WriteLine(
            "usage: latency --target e2e.json [--count 60] [--out file] | memory --pid <pid> [--out file]"
        );
        return 2;
}

if (Value("--out") is { } file)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".");
    File.WriteAllLines(file, output);
}
return 0;

namespace Darp.CodeEditor.Probe
{
    /// <summary>The end of a text line on screen, in physical pixels: where the next character appears.</summary>
    internal readonly record struct TargetCell(double X, double Y, double LineHeight, double CharWidth)
    {
        /// <summary>The middle of the next character cell: the typed glyph changes it, the caret does not.</summary>
        public (int X, int Y, int Width, int Height) GlyphRegion =>
            (
                (int)Math.Round(X + CharWidth * 0.25),
                (int)Math.Round(Y + LineHeight * 0.2),
                Math.Max(2, (int)Math.Round(CharWidth * 0.5)),
                Math.Max(2, (int)Math.Round(LineHeight * 0.6))
            );
    }

    /// <summary>Synthetic input and screen reads through the window system.</summary>
    internal interface IScreenInput : IDisposable
    {
        void Click(int x, int y);

        /// <summary>Presses and releases the key that types 'm' (<paramref name="backspace"/> false) or BackSpace.</summary>
        void Key(bool backspace);

        /// <summary>The pixels of a screen region, as currently shown, 4 bytes per pixel (blue, green, red, unused).</summary>
        byte[] Capture(int x, int y, int width, int height);

        /// <summary>The screen position of the client area of the first visible window whose title starts so.</summary>
        (int X, int Y)? FindWindow(string titlePrefix);
    }

    internal static class StartupOnScreen
    {
        // In client coordinates of the demo windows: the first editor lines, right of the line numbers.
        private const int RegionX = 70;
        private const int RegionY = 70;
        private const int RegionWidth = 600;
        private const int RegionHeight = 130;

        public static void Run(IScreenInput screen, string title, string app, string[] arguments, Action<string> report)
        {
            var start = Stopwatch.StartNew();
            using Process process =
                Process.Start(
                    new ProcessStartInfo(app, arguments) { RedirectStandardOutput = true, RedirectStandardError = true }
                ) ?? throw new InvalidOperationException($"Cannot start {app}.");
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            double? window = null,
                text = null,
                highlighted = null;
            (int X, int Y)? origin = null;
            while (start.Elapsed < TimeSpan.FromSeconds(30) && highlighted is null && !process.HasExited)
            {
                if (origin is null)
                {
                    origin = screen.FindWindow(title);
                    if (origin is not null)
                        window = start.Elapsed.TotalMilliseconds;
                    else
                        Thread.Sleep(2);
                    continue;
                }
                (int textPixels, int darkPixels, int hues) = Analyze(
                    screen.Capture(origin.Value.X + RegionX, origin.Value.Y + RegionY, RegionWidth, RegionHeight)
                );
                double now = start.Elapsed.TotalMilliseconds;
                // Text on the dark editor background: a web view that has not painted yet is light gray all over.
                bool editorBackground = darkPixels > RegionWidth * RegionHeight * 6 / 10;
                if (text is null && editorBackground && textPixels >= 150)
                    text = now;
                if (text is not null && editorBackground && hues >= 2)
                    highlighted = now;
            }
            report(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"on screen after process start: window {window:F0} ms, editor text {text:F0} ms, highlighted text {highlighted:F0} ms ({Path.GetFileName(app)} {string.Join(' ', arguments)})"
                )
            );
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        /// <summary>Light and dark pixels, and how many hue sectors have clearly colored pixels.</summary>
        private static (int TextPixels, int DarkPixels, int Hues) Analyze(byte[] pixels)
        {
            int text = 0;
            int dark = 0;
            Span<int> sectors = stackalloc int[12];
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                int b = pixels[i],
                    g = pixels[i + 1],
                    r = pixels[i + 2];
                int max = Math.Max(r, Math.Max(g, b)),
                    min = Math.Min(r, Math.Min(g, b));
                if (max > 110)
                    text++;
                else if (max < 60)
                    dark++;
                if (max - min < 50 || max < 90)
                    continue;
                double hue =
                    max == r ? (g - b) / (double)(max - min)
                    : max == g ? 2 + (b - r) / (double)(max - min)
                    : 4 + (r - g) / (double)(max - min);
                sectors[((int)Math.Floor((hue < 0 ? hue + 6 : hue) * 2) % 12 + 12) % 12]++;
            }
            int hues = 0;
            foreach (int count in sectors)
                if (count >= 20)
                    hues++;
            return (text, dark, hues);
        }
    }

    internal static class InputLatency
    {
        private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Types 'm' and deletes it again, <paramref name="count"/> times each, always in the same cell. Each sample is
        /// the time from injecting the key until the cell's pixels first differ from before.
        /// </summary>
        public static void Run(IScreenInput screen, TargetCell cell, int count, Action<string> report)
        {
            // Clicking right of the line end focuses the editor and puts the caret at the end of the line.
            screen.Click((int)(cell.X + cell.CharWidth * 6), (int)(cell.Y + cell.LineHeight / 2));
            Thread.Sleep(500);
            (int x, int y, int width, int height) = cell.GlyphRegion;
            byte[] empty = screen.Capture(x, y, width, height);
            var typed = new List<double>();
            var deleted = new List<double>();
            int timeouts = 0;
            int notRestored = 0;
            var random = new Random(1);
            for (int i = 0; i < count * 2; i++)
            {
                bool backspace = i % 2 == 1;
                byte[] before = screen.Capture(x, y, width, height);
                long start = Stopwatch.GetTimestamp();
                screen.Key(backspace);
                double? ms = null;
                while (Stopwatch.GetElapsedTime(start) < s_timeout)
                {
                    if (!screen.Capture(x, y, width, height).AsSpan().SequenceEqual(before))
                    {
                        ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        break;
                    }
                }
                if (ms is null)
                    timeouts++;
                else
                    (backspace ? deleted : typed).Add(ms.Value);
                // Settle, then check that deleting restored the empty cell; vary the pause so samples do not lock
                // onto the display refresh.
                Thread.Sleep(80 + random.Next(80));
                if (backspace && !screen.Capture(x, y, width, height).AsSpan().SequenceEqual(empty))
                    notRestored++;
            }
            report(
                $"input latency, key injected -> changed pixels on screen, glyph region {width}x{height} at {x},{y}"
            );
            report(Summary("type 'm'", typed));
            report(Summary("backspace", deleted));
            report(
                $"timeouts (no change within {s_timeout.TotalMilliseconds:F0} ms): {timeouts}, cell not empty after backspace: {notRestored}"
            );
        }

        private static string Summary(string name, List<double> samples)
        {
            if (samples.Count == 0)
                return $"  {name}: no samples";
            double[] sorted = [.. samples.Order()];
            double At(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
            return string.Create(
                CultureInfo.InvariantCulture,
                $"  {name, -10}: n {sorted.Length}, p50 {At(0.5):F1} ms, p95 {At(0.95):F1} ms, p99 {At(0.99):F1} ms, max {sorted[^1]:F1} ms, mean {sorted.Average():F1} ms"
            );
        }
    }
}
