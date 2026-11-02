using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Lsp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary><c>textDocument/semanticTokens/range</c> from Roslyn's classifier.</summary>
/// <remarks>
/// Token types use LSP's standard names where one exists and C# names otherwise. Operators and punctuation are left
/// to syntax highlighting. Tokens that span lines are split per line.
/// </remarks>
internal static class SemanticTokensHandler
{
    private static readonly List<string> s_tokenTypes =
    [
        "namespace",
        "class",
        "struct",
        "interface",
        "enum",
        "delegate",
        "typeParameter",
        "method",
        "extensionMethod",
        "property",
        "event",
        "field",
        "constant",
        "enumMember",
        "variable",
        "parameter",
        "label",
        "keyword",
        "string",
        "number",
        "comment",
    ];

    private static readonly List<string> s_tokenModifiers = ["static"];

    public static SemanticTokensOptions Options { get; } =
        new(new SemanticTokensLegend(s_tokenTypes, s_tokenModifiers), Range: true, Full: false);

    public static void Register(JsonRpcConnection connection, ScriptSession session) =>
        connection.OnRequest(LspMethods.SemanticTokensRange, (request, token) => HandleAsync(session, request, token));

    private static async Task<SemanticTokens?> HandleAsync(
        ScriptSession session,
        SemanticTokensRangeParams request,
        CancellationToken token
    )
    {
        if (!session.TryGetSnapshot(request.TextDocument.Uri, out DocumentSnapshot? snapshot))
            return null;
        TextSpan span = snapshot.Lines.GetSpan(request.Range);
        // The snapshot is taken; the rest runs on the thread pool while later messages are dispatched.
        await Task.Yield();
        return await StaleWork
            .RunAsync(
                token,
                snapshot.Changed,
                async cancellationToken =>
                    await GetTokensAsync(
                            snapshot.Owner.GetDocument(
                                await session.Workspace.WaitAsync(cancellationToken).ConfigureAwait(false),
                                snapshot
                            ),
                            snapshot.Lines,
                            span,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
            )
            .ConfigureAwait(false);
    }

    public static async Task<SemanticTokens> GetTokensAsync(
        Document document,
        LineMap lines,
        TextSpan span,
        CancellationToken cancellationToken
    )
    {
        IEnumerable<ClassifiedSpan> classified = await Classifier
            .GetClassifiedSpansAsync(document, span, cancellationToken)
            .ConfigureAwait(false);

        var tokens = new List<(TextSpan Span, int Type)>();
        var staticSpans = new HashSet<TextSpan>();
        foreach (ClassifiedSpan classifiedSpan in classified)
        {
            if (classifiedSpan.ClassificationType == ClassificationTypeNames.StaticSymbol)
                staticSpans.Add(classifiedSpan.TextSpan);
            else if (GetTokenType(classifiedSpan.ClassificationType) is { } type)
                tokens.Add((classifiedSpan.TextSpan, type));
        }
        tokens.Sort(
            (a, b) => a.Span.Start != b.Span.Start ? a.Span.Start - b.Span.Start : b.Span.Length - a.Span.Length
        );

        var data = new List<int>(tokens.Count * 5);
        int previousLine = 0;
        int previousStart = 0;
        int end = 0;
        foreach ((TextSpan tokenSpan, int type) in tokens)
        {
            // LSP tokens must not overlap. Spans inside others, such as escapes inside a string, are dropped.
            if (tokenSpan.Start < end || tokenSpan.IsEmpty)
                continue;
            end = tokenSpan.End;
            int modifiers = staticSpans.Contains(tokenSpan) ? 1 : 0;
            for (int line = lines.GetLine(tokenSpan.Start); line < lines.LineCount; line++)
            {
                int start = Math.Max(tokenSpan.Start, lines.GetLineStart(line));
                int length = Math.Min(tokenSpan.End, lines.GetLineEnd(line)) - start;
                if (start >= tokenSpan.End)
                    break;
                if (length <= 0)
                    continue;
                int character = start - lines.GetLineStart(line);
                data.Add(line - previousLine);
                data.Add(line == previousLine ? character - previousStart : character);
                data.Add(length);
                data.Add(type);
                data.Add(modifiers);
                previousLine = line;
                previousStart = character;
            }
        }
        return new SemanticTokens([.. data]);
    }

    private static int? GetTokenType(string classificationType)
    {
        string? name = classificationType switch
        {
            ClassificationTypeNames.NamespaceName => "namespace",
            ClassificationTypeNames.ClassName
            or ClassificationTypeNames.RecordClassName
            or ClassificationTypeNames.ModuleName => "class",
            ClassificationTypeNames.StructName or ClassificationTypeNames.RecordStructName => "struct",
            ClassificationTypeNames.InterfaceName => "interface",
            ClassificationTypeNames.EnumName => "enum",
            ClassificationTypeNames.DelegateName => "delegate",
            ClassificationTypeNames.TypeParameterName => "typeParameter",
            ClassificationTypeNames.MethodName => "method",
            ClassificationTypeNames.ExtensionMethodName => "extensionMethod",
            ClassificationTypeNames.PropertyName => "property",
            ClassificationTypeNames.EventName => "event",
            ClassificationTypeNames.FieldName => "field",
            ClassificationTypeNames.ConstantName => "constant",
            ClassificationTypeNames.EnumMemberName => "enumMember",
            ClassificationTypeNames.LocalName => "variable",
            ClassificationTypeNames.ParameterName => "parameter",
            ClassificationTypeNames.LabelName => "label",
            ClassificationTypeNames.Keyword
            or ClassificationTypeNames.ControlKeyword
            or ClassificationTypeNames.PreprocessorKeyword => "keyword",
            ClassificationTypeNames.StringLiteral or ClassificationTypeNames.VerbatimStringLiteral => "string",
            ClassificationTypeNames.NumericLiteral => "number",
            _ when classificationType == ClassificationTypeNames.Comment
                    || classificationType.StartsWith("xml doc comment", StringComparison.Ordinal) => "comment",
            _ => null,
        };
        return name is null ? null : s_tokenTypes.IndexOf(name);
    }
}
