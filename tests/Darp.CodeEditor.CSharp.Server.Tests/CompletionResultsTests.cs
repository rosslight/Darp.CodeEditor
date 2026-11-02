using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using Shouldly;

namespace Darp.CodeEditor.CSharp.Server.Tests;

public sealed class CompletionResultsTests
{
    [Fact]
    public void A_list_that_completes_after_its_document_closed_is_dropped()
    {
        var results = new CompletionResults();
        var document = new OpenDocument("file:///a.csx", "file:///a.csx", 1, SourceText.From("D"));
        using var workspace = new AdhocWorkspace();
        Document roslynDocument = workspace.AddDocument(
            workspace.AddProject("a", LanguageNames.CSharp).Id,
            "a.csx",
            SourceText.From("D")
        );

        bool beforeClose = results.Set(document, Result(results.NextId(), roslynDocument));
        document.Close();
        results.Remove(document.Key);
        bool afterClose = results.Set(document, Result(results.NextId(), roslynDocument));

        beforeClose.ShouldBeTrue();
        afterClose.ShouldBeFalse();
        results.TryGetLatest(document.Key, out _).ShouldBeFalse();
    }

    private static CompletionResult Result(long id, Document document) =>
        new(
            id,
            document,
            LineMap.Create(SourceText.From("D")),
            1,
            CompletionTrigger.Invoke,
            new TextSpan(0, 1),
            ImmutableArray<Microsoft.CodeAnalysis.Completion.CompletionItem>.Empty
        );
}
