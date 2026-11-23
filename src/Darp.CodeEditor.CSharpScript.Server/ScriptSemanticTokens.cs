using System.Collections.Frozen;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>Semantic colors from Roslyn's classifier, for the lines an editor shows.</summary>
internal static class ScriptSemanticTokens
{
    private const int ReadonlyModifier = 1;

    /// <summary>The token types of the server's legend, the protocol's standard names, which color themes know.</summary>
    public static readonly string[] TokenTypes =
    [
        "namespace",
        "class",
        "enum",
        "interface",
        "struct",
        "typeParameter",
        "parameter",
        "variable",
        "property",
        "enumMember",
        "event",
        "method",
    ];

    /// <summary>The token modifiers of the server's legend.</summary>
    public static readonly string[] TokenModifiers = ["readonly"];

    private static readonly FrozenDictionary<string, (int Type, int Modifiers)> s_tokens = new Dictionary<
        string,
        (string Type, int Modifiers)
    >
    {
        [ClassificationTypeNames.NamespaceName] = ("namespace", 0),
        [ClassificationTypeNames.ClassName] = ("class", 0),
        [ClassificationTypeNames.RecordClassName] = ("class", 0),
        [ClassificationTypeNames.DelegateName] = ("class", 0),
        [ClassificationTypeNames.EnumName] = ("enum", 0),
        [ClassificationTypeNames.InterfaceName] = ("interface", 0),
        [ClassificationTypeNames.StructName] = ("struct", 0),
        [ClassificationTypeNames.RecordStructName] = ("struct", 0),
        [ClassificationTypeNames.TypeParameterName] = ("typeParameter", 0),
        [ClassificationTypeNames.ParameterName] = ("parameter", 0),
        [ClassificationTypeNames.LocalName] = ("variable", 0),
        [ClassificationTypeNames.ConstantName] = ("variable", ReadonlyModifier),
        [ClassificationTypeNames.FieldName] = ("variable", 0),
        [ClassificationTypeNames.PropertyName] = ("property", 0),
        [ClassificationTypeNames.EnumMemberName] = ("enumMember", 0),
        [ClassificationTypeNames.EventName] = ("event", 0),
        [ClassificationTypeNames.MethodName] = ("method", 0),
        [ClassificationTypeNames.ExtensionMethodName] = ("method", 0),
    }.ToFrozenDictionary(pair => pair.Key, pair => (Array.IndexOf(TokenTypes, pair.Value.Type), pair.Value.Modifiers));

    /// <summary>The tokens in <paramref name="range"/>, in the protocol's relative format.</summary>
    public static async Task<LspSemanticTokens?> GetAsync(
        ScriptSnapshot script,
        LspRange range,
        CancellationToken cancellationToken
    )
    {
        var span = TextSpan.FromBounds(script.Lines.ToOffset(range.Start), script.Lines.ToOffset(range.End));
        IEnumerable<ClassifiedSpan> spans = await Classifier
            .GetClassifiedSpansAsync(script.Document, span, cancellationToken)
            .ConfigureAwait(false);
        var data = new List<int>();
        (int line, int column) = (0, 0);
        foreach (ClassifiedSpan classified in spans.OrderBy(s => s.TextSpan.Start))
        {
            if (!s_tokens.TryGetValue(classified.ClassificationType, out (int Type, int Modifiers) token))
                continue;
            LspPosition start = script.Lines.ToPosition(classified.TextSpan.Start);
            data.AddRange(
                start.Line - line,
                start.Line == line ? start.Character - column : start.Character,
                classified.TextSpan.Length,
                token.Type,
                token.Modifiers
            );
            (line, column) = (start.Line, start.Character);
        }
        return new LspSemanticTokens(data);
    }
}
