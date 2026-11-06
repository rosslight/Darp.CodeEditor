using Avalonia.Platform.Storage;
using Darp.CodeEditor;

namespace Sample.Documents;

/// <summary>A kind of document that the app creates and opens: its name, its language and its file extensions.</summary>
public sealed record FileType(string Name, CodeLanguage Language, IReadOnlyList<string> Extensions)
{
    /// <summary>Python: a language that the app defines itself, from the files of VS Code's Python extension.</summary>
    /// <remarks>
    /// The app ships the files as Avalonia resources, so a NativeAOT app needs no loose files. The language starts
    /// loading them in the background when it is created, which is at startup: the examples include a Python script.
    /// </remarks>
    public static FileType Python { get; } =
        new(
            "Python",
            new CodeLanguage(
                "python",
                grammar: new Uri("avares://Darp.CodeEditor.Sample/Languages/MagicPython.tmLanguage.json"),
                configuration: new Uri("avares://Darp.CodeEditor.Sample/Languages/python.language-configuration.json")
            ),
            [".py"]
        );

    public static FileType Text { get; } = new("Text", CodeLanguage.PlainText, [".txt"]);

    public static FileType Json { get; } = new("JSON", CodeLanguage.Json, [".json"]);

    public static FileType Xml { get; } = new("XML", CodeLanguage.Xml, [".xml"]);

    public static FileType Yaml { get; } = new("YAML", CodeLanguage.Yaml, [".yaml", ".yml"]);

    /// <summary>Every file type, for the file pickers.</summary>
    public static IReadOnlyList<FileType> All { get; } = [Text, Json, Xml, Yaml, Python];

    /// <summary>The file type of a file name by its extension; <see cref="Text"/> for others.</summary>
    public static FileType ForFile(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return All.FirstOrDefault(type => type.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            ?? Text;
    }

    /// <summary>The filter of a file picker for this type.</summary>
    public FilePickerFileType ToPickerType() => new(Name) { Patterns = [.. Extensions.Select(e => "*" + e)] };
}
