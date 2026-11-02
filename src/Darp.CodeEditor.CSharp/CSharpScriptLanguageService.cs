using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Darp.CodeEditor.LanguageServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using CompletionItem = Darp.CodeEditor.LanguageServices.CompletionItem;
using CompletionList = Darp.CodeEditor.LanguageServices.CompletionList;
using CompletionTrigger = Darp.CodeEditor.LanguageServices.CompletionTrigger;
using DiagnosticSeverity = Darp.CodeEditor.LanguageServices.DiagnosticSeverity;
using ParameterInfo = Darp.CodeEditor.LanguageServices.ParameterInfo;
using QuickInfo = Darp.CodeEditor.LanguageServices.QuickInfo;
using RoslynCompletion = Microsoft.CodeAnalysis.Completion;
using SignatureHelp = Darp.CodeEditor.LanguageServices.SignatureHelp;

namespace Darp.CodeEditor.CSharp;

/// <summary>
/// C# script language features from Roslyn's Workspaces and Features layers. Every document is its own script
/// submission with the configured globals type, references and imports.
/// </summary>
public sealed class CSharpScriptLanguageService : ILanguageService, IDisposable
{
    private const string WarmUpDocumentId = "<warm-up>";

    private static readonly ConcurrentDictionary<string, MetadataReference> s_references = new(StringComparer.Ordinal);

    private static readonly CSharpParseOptions s_parseOptions = new(
        LanguageVersion.Latest,
        DocumentationMode.Parse,
        SourceCodeKind.Script
    );

    private static readonly SymbolDisplayFormat s_signatureFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat.WithParameterOptions(
            SymbolDisplayParameterOptions.IncludeType
                | SymbolDisplayParameterOptions.IncludeName
                | SymbolDisplayParameterOptions.IncludeDefaultValue
                | SymbolDisplayParameterOptions.IncludeParamsRefOut
        );

    private readonly AdhocWorkspace _workspace;
    private readonly CSharpCompilationOptions _compilationOptions;
    private readonly ImmutableArray<MetadataReference> _references;
    private readonly Type? _globalsType;
    private readonly ConcurrentDictionary<string, ScriptDocument> _documents = new(StringComparer.Ordinal);
    private long _nextResultId;

    private CSharpScriptLanguageService(CSharpScriptOptions options)
    {
        _workspace = new AdhocWorkspace(MefHostServices.DefaultHost);
        _compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            usings: options.Imports,
            nullableContextOptions: NullableContextOptions.Enable
        );
        _references = ResolveReferences(options);
        _globalsType = options.GlobalsType;
    }

    public LanguageServiceCapabilities Capabilities { get; } =
        new(['.'], ['.', '(', ')', '[', ']', ';', ',', '{', '}', '<', '>', '=', '+', '-', '*', '/'], ['(', ',']);

    /// <summary>
    /// Creates the service on a thread-pool thread: composes Roslyn's services, loads the references and warms up
    /// completion, classification and diagnostics. The returned task completes when the service is ready.
    /// </summary>
    public static Task<CSharpScriptLanguageService> CreateAsync(
        CSharpScriptOptions options,
        CancellationToken cancellationToken = default
    ) =>
        Task.Run(
            async () =>
            {
                var service = new CSharpScriptLanguageService(options);
                await service.WarmUpAsync(cancellationToken).ConfigureAwait(false);
                return service;
            },
            cancellationToken
        );

    public async Task<CompletionList?> GetCompletionsAsync(
        DocumentSnapshot snapshot,
        int position,
        CompletionTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        ScriptDocument script = GetScript(snapshot.DocumentId);
        Document document = script.Get(snapshot);
        RoslynCompletion.CompletionService service = GetRequiredService(
            RoslynCompletion.CompletionService.GetService(document)
        );
        RoslynCompletion.CompletionTrigger roslynTrigger = trigger.Kind switch
        {
            CompletionTriggerKind.Invoke => RoslynCompletion.CompletionTrigger.Invoke,
            _ => RoslynCompletion.CompletionTrigger.CreateInsertionTrigger(trigger.Character),
        };
        if (trigger.Kind != CompletionTriggerKind.Invoke)
        {
            SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (!service.ShouldTriggerCompletion(text, position, roslynTrigger))
                return null;
        }

        RoslynCompletion.CompletionList list = await service
            .GetCompletionsAsync(document, position, roslynTrigger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (list.ItemsList.Count == 0)
            return null;

        RoslynCompletion.CompletionItem[] items =
        [
            .. list
                .ItemsList.OrderBy(i => i.SortText, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.DisplayText, StringComparer.Ordinal),
        ];
        long resultId = Interlocked.Increment(ref _nextResultId);
        script.LastCompletion = new CompletionResult(resultId, document, items);
        return new CompletionList(
            resultId,
            new TextRange(list.Span.Start, list.Span.Length),
            Array.ConvertAll(items, ToCompletionItem)
        );
    }

    public async Task<string?> GetCompletionDescriptionAsync(
        CompletionItemReference item,
        CancellationToken cancellationToken
    )
    {
        if (
            !_documents.TryGetValue(item.DocumentId, out ScriptDocument? script)
            || script.LastCompletion is not { } result
            || result.ResultId != item.ResultId
            || (uint)item.Index >= (uint)result.Items.Length
        )
        {
            return null;
        }
        RoslynCompletion.CompletionService service = GetRequiredService(
            RoslynCompletion.CompletionService.GetService(result.Document)
        );
        RoslynCompletion.CompletionDescription? description = await service
            .GetDescriptionAsync(result.Document, result.Items[item.Index], cancellationToken)
            .ConfigureAwait(false);
        return description?.Text;
    }

    public async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
        DocumentSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        Document document = GetScript(snapshot.DocumentId).Get(snapshot);
        SemanticModel model = GetRequiredService(
            await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
        );
        return
        [
            .. model
                .GetDiagnostics(cancellationToken: cancellationToken)
                .Where(d => d.Severity != Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden && d.Location.IsInSource)
                .Select(d => new DiagnosticInfo(
                    new TextRange(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length),
                    d.Severity switch
                    {
                        Microsoft.CodeAnalysis.DiagnosticSeverity.Error => DiagnosticSeverity.Error,
                        Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                        _ => DiagnosticSeverity.Info,
                    },
                    d.Id,
                    d.GetMessage()
                )),
        ];
    }

    public async Task<IReadOnlyList<ClassifiedRange>> GetClassificationsAsync(
        DocumentSnapshot snapshot,
        TextRange range,
        CancellationToken cancellationToken
    )
    {
        Document document = GetScript(snapshot.DocumentId).Get(snapshot);
        IEnumerable<ClassifiedSpan> spans = await Classifier
            .GetClassifiedSpansAsync(document, TextSpan.FromBounds(range.Start, range.End), cancellationToken)
            .ConfigureAwait(false);
        var ranges = new List<ClassifiedRange>();
        foreach (ClassifiedSpan span in spans)
        {
            if (GetClassificationKind(span.ClassificationType) is { } kind)
                ranges.Add(new ClassifiedRange(new TextRange(span.TextSpan.Start, span.TextSpan.Length), kind));
        }
        return ranges;
    }

    public async Task<QuickInfo?> GetQuickInfoAsync(
        DocumentSnapshot snapshot,
        int position,
        CancellationToken cancellationToken
    )
    {
        Document document = GetScript(snapshot.DocumentId).Get(snapshot);
        if (QuickInfoService.GetService(document) is not { } service)
            return null;
        QuickInfoItem? item = await service
            .GetQuickInfoAsync(document, position, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
            return null;
        string text = string.Join(Environment.NewLine, item.Sections.Select(s => s.Text).Where(t => t.Length > 0));
        return text.Length == 0 ? null : new QuickInfo(new TextRange(item.Span.Start, item.Span.Length), text);
    }

    /// <remarks>
    /// Roslyn's signature help service is internal, so this covers invocations and object creations with the
    /// semantic model: every overload of the called member, with the parameter the caret is in.
    /// </remarks>
    public async Task<SignatureHelp?> GetSignatureHelpAsync(
        DocumentSnapshot snapshot,
        int position,
        CancellationToken cancellationToken
    )
    {
        Document document = GetScript(snapshot.DocumentId).Get(snapshot);
        SyntaxNode root = GetRequiredService(
            await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
        );
        // The token before the caret: at the end of the document, the token at the caret is the end of file.
        ArgumentListSyntax? arguments = root.FindToken(Math.Max(0, position - 1))
            .Parent?.AncestorsAndSelf()
            .OfType<ArgumentListSyntax>()
            .FirstOrDefault(a =>
                a.OpenParenToken.Span.End <= position
                && (a.CloseParenToken.IsMissing || position <= a.CloseParenToken.SpanStart)
            );
        if (arguments?.Parent is not { } call)
            return null;

        SemanticModel model = GetRequiredService(
            await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
        );
        IMethodSymbol[] methods = call switch
        {
            InvocationExpressionSyntax invocation =>
            [
                .. model.GetMemberGroup(invocation.Expression, cancellationToken).OfType<IMethodSymbol>(),
            ],
            BaseObjectCreationExpressionSyntax creation
                when model.GetTypeInfo(creation, cancellationToken).Type is INamedTypeSymbol type =>
            [
                .. type.InstanceConstructors,
            ],
            _ => [],
        };
        if (methods.Length == 0)
            return null;

        int activeParameter = arguments.Arguments.GetSeparators().Count(s => s.SpanStart < position);
        ISymbol? called = model.GetSymbolInfo(call, cancellationToken).Symbol;
        int activeSignature = Array.FindIndex(methods, m => SymbolEqualityComparer.Default.Equals(m, called));
        if (activeSignature < 0)
            activeSignature = Math.Max(0, Array.FindIndex(methods, m => m.Parameters.Length > activeParameter));
        return new SignatureHelp(
            [
                .. methods.Select(m => new SignatureInfo(
                    m.ToDisplayString(s_signatureFormat),
                    null,
                    [.. m.Parameters.Select(p => new ParameterInfo(p.ToDisplayString(s_signatureFormat), null))]
                )),
            ],
            activeSignature,
            activeParameter,
            TextRange.FromBounds(arguments.OpenParenToken.Span.End, arguments.CloseParenToken.SpanStart)
        );
    }

    public void Dispose() => _workspace.Dispose();

    private ScriptDocument GetScript(string documentId) => _documents.GetOrAdd(documentId, CreateScript);

    private ScriptDocument CreateScript(string documentId)
    {
        var projectId = ProjectId.CreateNewId(documentId);
        var roslynDocumentId = DocumentId.CreateNewId(projectId, documentId);
        ProjectInfo project = ProjectInfo
            .Create(
                projectId,
                VersionStamp.Default,
                documentId,
                documentId,
                LanguageNames.CSharp,
                compilationOptions: _compilationOptions,
                parseOptions: s_parseOptions,
                metadataReferences: _references,
                isSubmission: true,
                hostObjectType: _globalsType
            )
            .WithDocuments([
                DocumentInfo.Create(
                    roslynDocumentId,
                    $"{documentId}.csx",
                    sourceCodeKind: SourceCodeKind.Script,
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(""), VersionStamp.Default))
                ),
            ]);
        // Each script gets its own solution; scripts never see each other.
        Document document = GetRequiredService(
            _workspace.CurrentSolution.AddProject(project).GetDocument(roslynDocumentId)
        );
        return new ScriptDocument(document);
    }

    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        const string text = """
            var numbers = new System.Collections.Generic.List<int> { 1, 2 };
            numbers.Add(3);
            System.Console.WriteLine(numbers.Count);
            numbers.
            """;
        var snapshot = new DocumentSnapshot(WarmUpDocumentId, 0, text);
        await GetCompletionsAsync(
                snapshot,
                text.Length,
                new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, '.'),
                cancellationToken
            )
            .ConfigureAwait(false);
        await GetCompletionsAsync(
                snapshot,
                text.IndexOf("numbers.Add", StringComparison.Ordinal) + 1,
                new CompletionTrigger(CompletionTriggerKind.Insertion, 'n'),
                cancellationToken
            )
            .ConfigureAwait(false);
        await GetClassificationsAsync(snapshot, new TextRange(0, text.Length), cancellationToken).ConfigureAwait(false);
        await GetDiagnosticsAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await GetQuickInfoAsync(snapshot, text.IndexOf("Add", StringComparison.Ordinal), cancellationToken)
            .ConfigureAwait(false);
        await GetSignatureHelpAsync(snapshot, text.IndexOf("Add(", StringComparison.Ordinal) + 4, cancellationToken)
            .ConfigureAwait(false);
        _documents.TryRemove(WarmUpDocumentId, out _);
    }

    private static ImmutableArray<MetadataReference> ResolveReferences(CSharpScriptOptions options)
    {
        var platformAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        foreach (string path in trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            platformAssemblies.TryAdd(Path.GetFileNameWithoutExtension(path.AsSpan()).ToString(), path);

        IEnumerable<Assembly> assemblies = options.GlobalsType is null
            ? options.References
            : options.References.Append(options.GlobalsType.Assembly);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (Assembly assembly in assemblies.Append(typeof(object).Assembly))
        {
            if (assembly.Location.Length > 0)
                paths.Add(assembly.Location);
            foreach (AssemblyName dependency in assembly.GetReferencedAssemblies())
            {
                if (dependency.Name is { } name && platformAssemblies.TryGetValue(name, out string? path))
                    paths.Add(path);
            }
        }
        // MetadataReference instances are shared by all services, so Roslyn reads each assembly once.
        return [.. paths.Order(StringComparer.Ordinal).Select(p => s_references.GetOrAdd(p, CreateReference))];
    }

    private static MetadataReference CreateReference(string path)
    {
        string documentation = Path.ChangeExtension(path, ".xml");
        return MetadataReference.CreateFromFile(
            path,
            documentation: File.Exists(documentation) ? XmlDocumentationProvider.CreateFromFile(documentation) : null
        );
    }

    private static CompletionItem ToCompletionItem(RoslynCompletion.CompletionItem item) =>
        new(
            item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix,
            item.FilterText,
            // Symbol items carry the text to insert, such as "List" for "List<>"; the rest insert their display text.
            item.Properties.TryGetValue("InsertionText", out string? insertion)
                ? insertion
                : item.DisplayText,
            GetCompletionKind(item.Tags),
            item.InlineDescription.Length > 0 ? item.InlineDescription : null
        );

    private static CompletionItemKind GetCompletionKind(ImmutableArray<string> tags)
    {
        foreach (string tag in tags)
        {
            CompletionItemKind? kind = tag switch
            {
                WellKnownTags.Keyword => CompletionItemKind.Keyword,
                WellKnownTags.Snippet => CompletionItemKind.Snippet,
                WellKnownTags.Namespace => CompletionItemKind.Namespace,
                WellKnownTags.Class => CompletionItemKind.Class,
                WellKnownTags.Structure => CompletionItemKind.Struct,
                WellKnownTags.Interface => CompletionItemKind.Interface,
                WellKnownTags.Enum => CompletionItemKind.Enum,
                WellKnownTags.EnumMember => CompletionItemKind.EnumMember,
                WellKnownTags.Delegate => CompletionItemKind.Delegate,
                WellKnownTags.TypeParameter => CompletionItemKind.TypeParameter,
                WellKnownTags.Method => CompletionItemKind.Method,
                WellKnownTags.ExtensionMethod => CompletionItemKind.ExtensionMethod,
                WellKnownTags.Property => CompletionItemKind.Property,
                WellKnownTags.Field => CompletionItemKind.Field,
                WellKnownTags.Event => CompletionItemKind.Event,
                WellKnownTags.Constant => CompletionItemKind.Constant,
                WellKnownTags.Local => CompletionItemKind.Local,
                WellKnownTags.Parameter => CompletionItemKind.Parameter,
                _ => null,
            };
            if (kind is { } k)
                return k;
        }
        return CompletionItemKind.Text;
    }

    private static ClassificationKind? GetClassificationKind(string classificationType) =>
        classificationType switch
        {
            ClassificationTypeNames.NamespaceName => ClassificationKind.Namespace,
            ClassificationTypeNames.ClassName
            or ClassificationTypeNames.RecordClassName
            or ClassificationTypeNames.ModuleName => ClassificationKind.Class,
            ClassificationTypeNames.StructName or ClassificationTypeNames.RecordStructName => ClassificationKind.Struct,
            ClassificationTypeNames.InterfaceName => ClassificationKind.Interface,
            ClassificationTypeNames.EnumName => ClassificationKind.Enum,
            ClassificationTypeNames.DelegateName => ClassificationKind.Delegate,
            ClassificationTypeNames.TypeParameterName => ClassificationKind.TypeParameter,
            ClassificationTypeNames.MethodName => ClassificationKind.Method,
            ClassificationTypeNames.ExtensionMethodName => ClassificationKind.ExtensionMethod,
            ClassificationTypeNames.PropertyName => ClassificationKind.Property,
            ClassificationTypeNames.EventName => ClassificationKind.Event,
            ClassificationTypeNames.FieldName => ClassificationKind.Field,
            ClassificationTypeNames.ConstantName => ClassificationKind.Constant,
            ClassificationTypeNames.EnumMemberName => ClassificationKind.EnumMember,
            ClassificationTypeNames.LocalName => ClassificationKind.Local,
            ClassificationTypeNames.ParameterName => ClassificationKind.Parameter,
            ClassificationTypeNames.LabelName => ClassificationKind.Label,
            _ => null,
        };

    private static T GetRequiredService<T>(T? value)
        where T : class => value ?? throw new InvalidOperationException($"Roslyn did not provide {typeof(T).Name}.");

    private sealed record CompletionResult(long ResultId, Document Document, RoslynCompletion.CompletionItem[] Items);

    private sealed record VersionedDocument(int Version, string Text, Document Document);

    /// <summary>
    /// One script and its newest version. Requests for the same version share one Roslyn document, and therefore one
    /// compilation. New versions are applied as a single text change against the previous text, so Roslyn can reparse
    /// incrementally.
    /// </summary>
    private sealed class ScriptDocument(Document empty)
    {
        private readonly Lock _gate = new();
        private VersionedDocument _latest = new(-1, "", empty);

        public CompletionResult? LastCompletion { get; set; }

        public Document Get(DocumentSnapshot snapshot)
        {
            lock (_gate)
            {
                if (_latest.Version == snapshot.Version)
                    return _latest.Document;
                VersionedDocument latest = _latest;
                Document document = latest.Document.WithText(ApplyDifference(latest, snapshot.Text));
                // A request for an older version, arriving late, must not replace the newest document.
                if (snapshot.Version > latest.Version)
                    _latest = new VersionedDocument(snapshot.Version, snapshot.Text, document);
                return document;
            }
        }

        private static SourceText ApplyDifference(VersionedDocument latest, string newText)
        {
            SourceText oldSource = latest.Document.TryGetText(out SourceText? text)
                ? text
                : SourceText.From(latest.Text);
            ReadOnlySpan<char> oldSpan = latest.Text;
            ReadOnlySpan<char> newSpan = newText;
            int prefix = oldSpan.CommonPrefixLength(newSpan);
            int suffix = CommonSuffixLength(oldSpan[prefix..], newSpan[prefix..]);
            var change = new TextChange(
                TextSpan.FromBounds(prefix, oldSpan.Length - suffix),
                newText[prefix..(newSpan.Length - suffix)]
            );
            return oldSource.WithChanges(change);
        }

        private static int CommonSuffixLength(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        {
            int length = 0;
            while (length < a.Length && length < b.Length && a[^(length + 1)] == b[^(length + 1)])
                length++;
            return length;
        }
    }
}
