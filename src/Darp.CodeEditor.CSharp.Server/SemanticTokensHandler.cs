using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Client.ClientCapabilities;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Common;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Server;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Server.Options;
using EmmyLua.LanguageServer.Framework.Protocol.Message.SemanticToken;
using EmmyLua.LanguageServer.Framework.Server.Handler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary><c>textDocument/semanticTokens/range</c> from Roslyn's classifier.</summary>
/// <remarks>
/// Token types use LSP's standard names where one exists and C# names otherwise. Operators and punctuation are left
/// to syntax highlighting. Tokens that span lines are split per line.
/// </remarks>
internal sealed class SemanticTokensHandler : SemanticTokensHandlerBase
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

    private readonly ScriptSession _session;

    public SemanticTokensHandler(ScriptSession session) => _session = session;

    public override void RegisterCapability(
        ServerCapabilities serverCapabilities,
        ClientCapabilities clientCapabilities
    ) =>
        serverCapabilities.SemanticTokensProvider = new SemanticTokensOptions
        {
            Legend = new SemanticTokensLegend { TokenTypes = s_tokenTypes, TokenModifiers = s_tokenModifiers },
            Range = true,
            Full = false,
        };

    protected override Task<SemanticTokens?> Handle(SemanticTokensRangeParams request, CancellationToken token)
    {
        if (!_session.TryGetSnapshot(request.TextDocument.Uri, out DocumentSnapshot? snapshot))
            return Task.FromResult<SemanticTokens?>(null);
        TextSpan span = snapshot.Lines.GetSpan(request.Range);
        ScriptSession session = _session;
        return Task.Run(
            async () =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, snapshot.Changed);
                ScriptWorkspace workspace = await session.Workspace.WaitAsync(cancellation.Token).ConfigureAwait(false);
                return (SemanticTokens?)
                    await GetTokensAsync(
                            snapshot.Owner.GetDocument(workspace, snapshot),
                            snapshot.Lines,
                            span,
                            cancellation.Token
                        )
                        .ConfigureAwait(false);
            },
            token
        );
    }

    protected override Task<SemanticTokens?> Handle(SemanticTokensParams request, CancellationToken token) =>
        Task.FromResult<SemanticTokens?>(null);

    protected override Task<SemanticTokensDeltaResponse?> Handle(
        SemanticTokensDeltaParams request,
        CancellationToken token
    ) => Task.FromResult<SemanticTokensDeltaResponse?>(null);

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

        var data = new List<uint>(tokens.Count * 5);
        int previousLine = 0;
        int previousStart = 0;
        int end = 0;
        foreach ((TextSpan tokenSpan, int type) in tokens)
        {
            // LSP tokens must not overlap. Spans inside others, such as escapes inside a string, are dropped.
            if (tokenSpan.Start < end || tokenSpan.IsEmpty)
                continue;
            end = tokenSpan.End;
            uint modifiers = staticSpans.Contains(tokenSpan) ? 1u : 0u;
            for (int line = lines.GetLine(tokenSpan.Start); line < lines.LineCount; line++)
            {
                int start = Math.Max(tokenSpan.Start, lines.GetLineStart(line));
                int length = Math.Min(tokenSpan.End, lines.GetLineEnd(line)) - start;
                if (start >= tokenSpan.End)
                    break;
                if (length <= 0)
                    continue;
                int character = start - lines.GetLineStart(line);
                data.Add((uint)(line - previousLine));
                data.Add((uint)(line == previousLine ? character - previousStart : character));
                data.Add((uint)length);
                data.Add((uint)type);
                data.Add(modifiers);
                previousLine = line;
                previousStart = character;
            }
        }
        return new SemanticTokens { Data = data };
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
