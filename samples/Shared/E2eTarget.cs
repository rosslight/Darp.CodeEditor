using System.Globalization;

namespace Darp.CodeEditor.Samples;

/// <summary>
/// Where the input latency probe (tools/Darp.CodeEditor.Probe) types: the end of a line, in screen pixels, and the
/// window that must have the keyboard focus while it types, with its process. Every demo writes the same file for
/// --e2e, so the probe measures each editor the same way.
/// </summary>
internal static class E2eTarget
{
    /// <param name="path">The file to write.</param>
    /// <param name="window">The native window handle: an HWND on Windows, an X window id on X11.</param>
    /// <param name="x">The end of the line, in screen pixels.</param>
    /// <param name="y">The top of the line, in screen pixels.</param>
    /// <param name="lineHeight">In screen pixels.</param>
    /// <param name="charWidth">In screen pixels.</param>
    public static void Write(string path, long window, double x, double y, double lineHeight, double charWidth)
    {
        string json = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"pid":{{Environment.ProcessId}},"window":{{window}},"x":{{x:F1}},"y":{{y:F1}},"lineHeight":{{lineHeight:F1}},"charWidth":{{charWidth:F2}}}"""
        );
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, json);
    }
}
