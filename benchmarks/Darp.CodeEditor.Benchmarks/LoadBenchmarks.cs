using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// Loading each built-in language and theme into a new instance: reading and parsing its files. Each launch is a new
/// process, and its first load is measured, so the numbers include the first use of the parsers.
/// </summary>
[ColdStart]
public class LoadBenchmarks
{
    private static readonly string[] s_yamlIncludes = ["yaml-1.0", "yaml-1.1", "yaml-1.2", "yaml-1.3", "yaml-embedded"];

    [Benchmark]
    public object Json() => Load("json");

    [Benchmark]
    public object Xml() => Load("xml");

    [Benchmark]
    public object Yaml() => Load("yaml");

    /// <summary>The grammars that YAML's grammar includes, which the editor loads with it.</summary>
    [Benchmark]
    public int YamlIncludedGrammars() =>
        s_yamlIncludes
            .Select(name =>
                TextMateGrammars.Read(new Uri($"avares://Darp.CodeEditor/Languages/{name}.tmLanguage.json"))
            )
            .Count();

    [Benchmark]
    public object DarkPlus() => LoadTheme("dark_plus");

    [Benchmark]
    public object LightPlus() => LoadTheme("light_plus");

    private static LoadedLanguage Load(string id)
    {
        var language = new CodeLanguage(
            id,
            new Uri($"avares://Darp.CodeEditor/Languages/{id}.tmLanguage.json"),
            new Uri($"avares://Darp.CodeEditor/Languages/{id}.language-configuration.json")
        );
        LoadedLanguage loaded = language.Loaded.GetAwaiter().GetResult();
        return loaded.Grammar is null ? throw new InvalidOperationException($"{id} did not load.") : loaded;
    }

    private static ColorThemeFile LoadTheme(string name) =>
        new CodeEditorColorTheme { Source = new Uri($"avares://Darp.CodeEditor/Themes/{name}.json") }
            .Loaded.GetAwaiter()
            .GetResult()
        ?? throw new InvalidOperationException($"{name} did not load.");
}
