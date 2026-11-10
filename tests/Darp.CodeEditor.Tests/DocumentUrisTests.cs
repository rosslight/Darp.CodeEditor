using Darp.CodeEditor.LanguageClient;

namespace Darp.CodeEditor.Tests;

/// <summary>Which URIs name one document: servers write file URIs in forms of their own.</summary>
public sealed class DocumentUrisTests
{
    [Theory]
    // VS Code's URI library writes a drive letter in lower case and its colon encoded, as its own tests show.
    [InlineData("file:///C:/work/a.txt", "file:///c%3A/work/a.txt")]
    [InlineData("file:///C:/work/a%20b.txt", "file:///c%3a/work/a%20b.txt")]
    [InlineData("file:///home/work/a+b%20(1).txt", "file:///home/work/a%2Bb%20%281%29.txt")]
    [InlineData("file:///home/work/a.txt", "FILE:///home/work/a.txt")]
    [InlineData("file://Server/share/a.txt", "file://server/share/a.txt")]
    public void Forms_of_one_file_URI_name_one_document(string ours, string servers) =>
        DocumentUris.Identity(servers).ShouldBe(DocumentUris.Identity(ours));

    [Theory]
    // Paths differ by case on Linux; only a drive letter's case doesn't count.
    [InlineData("file:///home/work/A.txt", "file:///home/work/a.txt")]
    [InlineData("file:///C:/work/A.txt", "file:///C:/work/a.txt")]
    [InlineData("untitled:Untitled-1", "untitled:untitled-1")]
    public void URIs_of_different_documents_stay_apart(string one, string other) =>
        DocumentUris.Identity(other).ShouldNotBe(DocumentUris.Identity(one));
}
