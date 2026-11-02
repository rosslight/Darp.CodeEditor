using System.Globalization;

namespace Darp.CodeEditor.Samples;

/// <summary>
/// Where the input latency probe (tools/Darp.CodeEditor.Probe) types: the end of a line, in screen pixels, and the
/// process that must own the foreground window while it types. Every demo writes the same file for --e2e, so the
/// probe measures each editor the same way.
/// </summary>
internal static class E2eTarget
{
    public static void Write(string path, double x, double y, double lineHeight, double charWidth)
    {
        string json = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"pid":{{Environment.ProcessId}},"x":{{x:F1}},"y":{{y:F1}},"lineHeight":{{lineHeight:F1}},"charWidth":{{charWidth:F2}}}"""
        );
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, json);
    }
}
