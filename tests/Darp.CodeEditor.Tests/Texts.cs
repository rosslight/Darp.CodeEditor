namespace Darp.CodeEditor.Tests;

/// <summary>Documents for the editor's tests.</summary>
internal static class Texts
{
    /// <summary>A JSON object with <paramref name="lines"/> lines, one property per line from the second.</summary>
    public static string Json(int lines) =>
        "{\n"
        + string.Concat(Enumerable.Range(2, lines - 3).Select(i => $"  \"key{i}\": \"value {i}\",\n"))
        + "  \"end\": 0\n}";

    /// <summary>A YAML mapping with <paramref name="lines"/> lines.</summary>
    public static string Yaml(int lines) =>
        string.Concat(
            Enumerable.Range(1, lines).Select(i => i % 10 == 0 ? $"# section {i}\n" : $"key{i}: value {i} # note\n")
        );
}
