using System.Runtime.CompilerServices;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The line break of documents whose text has none, which their new lines take: the system's, or the one that the
/// environment variable <c>CODE_EDITOR_TESTS_EOL</c> names, <c>lf</c> or <c>crlf</c>, to run the tests as on another
/// system. Tests write their texts with <c>\n</c>.
/// </summary>
internal static class NewLines
{
    /// <summary>The line break of a document whose text had none.</summary>
    public static string OfNewDocuments => LineBreaks.System;

    /// <summary>
    /// <paramref name="expected"/>, written with <c>\n</c>, in the line breaks of a document that started as
    /// <paramref name="start"/>: as written when <paramref name="start"/> has line breaks, else in
    /// <see cref="OfNewDocuments"/>.
    /// </summary>
    public static string In(string expected, string start) =>
        start.Contains('\n') ? expected : expected.Replace("\n", OfNewDocuments, StringComparison.Ordinal);

    /// <summary>Takes the environment's line break before any test creates a document.</summary>
    [ModuleInitializer]
    internal static void Initialize() =>
        LineBreaks.System = Environment.GetEnvironmentVariable("CODE_EDITOR_TESTS_EOL")?.ToLowerInvariant() switch
        {
            null or "" => Environment.NewLine,
            "lf" => "\n",
            "crlf" => "\r\n",
            var other => throw new InvalidOperationException(
                $"CODE_EDITOR_TESTS_EOL is \"{other}\": use \"lf\" or \"crlf\"."
            ),
        };
}
