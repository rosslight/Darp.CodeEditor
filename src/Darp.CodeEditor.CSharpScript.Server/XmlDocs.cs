using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// A symbol's XML docs as Markdown: its summary, and the description of one of its parameters or type parameters. A
/// reference reads as the name it refers to, and it, a keyword, a parameter's name and <c>&lt;c&gt;</c> are code
/// spans; paragraphs and list items stay on lines of their own. With
/// <c>&lt;inheritdoc/&gt;</c>, what the docs leave out comes from the member they inherit from, as in hover, its
/// parameters and type parameters by their place, as an override or implementation may rename them.
/// </summary>
internal sealed class XmlDocs
{
    /// <summary>How a <c>cref</c> reads: by its name and its containing types, with its parameters' types.</summary>
    private static readonly SymbolDisplayFormat s_reference = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
    );

    /// <summary>How many <c>&lt;inheritdoc/&gt;</c> are followed one after another, as a cycle of them would never end.</summary>
    private const int MaxInherited = 8;

    /// <summary>
    /// Where code starts and ends in the text as it is written, so that code next to code becomes one code span
    /// (<see cref="Spans"/>): two spans side by side would read as stray backticks. The docs' own text leaves them out.
    /// </summary>
    private const char CodeStart = '\uE000';

    private const char CodeEnd = '\uE001';

    private readonly ISymbol _symbol;
    private readonly XElement? _docs;
    private readonly XmlDocs? _inherited;
    private readonly SemanticModel _model;
    private readonly int _position;

    private XmlDocs(ISymbol symbol, XElement? docs, XmlDocs? inherited, SemanticModel model, int position)
    {
        _symbol = symbol;
        _docs = docs;
        _inherited = inherited;
        _model = model;
        _position = position;
    }

    /// <summary>The docs of <paramref name="symbol"/>, with references named as the code at <paramref name="position"/> names them.</summary>
    public static XmlDocs Of(ISymbol symbol, SemanticModel model, int position, CancellationToken cancellationToken) =>
        Of(symbol, model, position, MaxInherited, cancellationToken);

    private static XmlDocs Of(
        ISymbol symbol,
        SemanticModel model,
        int position,
        int inheriting,
        CancellationToken cancellationToken
    )
    {
        string xml =
            symbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: cancellationToken) ?? "";
        XElement? docs;
        try
        {
            docs = xml.Length == 0 ? null : XElement.Parse(xml);
        }
        // A comment that is not well-formed says nothing.
        catch (XmlException)
        {
            docs = null;
        }
        XmlDocs? inherited =
            inheriting > 0
            && docs?.Element("inheritdoc") is { } inheritdoc
            && InheritedFrom(symbol, inheritdoc, model.Compilation) is { } from
                ? Of(from, model, position, inheriting - 1, cancellationToken)
                : null;
        return new XmlDocs(symbol, docs, inherited, model, position);
    }

    /// <summary>The member that <c>&lt;inheritdoc/&gt;</c> names, or else the one it overrides or implements.</summary>
    private static ISymbol? InheritedFrom(ISymbol symbol, XElement inheritdoc, Compilation compilation)
    {
        if ((string?)inheritdoc.Attribute("cref") is { } cref)
            return DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation);
        ISymbol? overridden = symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            INamedTypeSymbol type => type.BaseType,
            _ => null,
        };
        return overridden
            ?? symbol
                .ContainingType?.AllInterfaces.SelectMany(type => type.GetMembers(symbol.Name))
                .FirstOrDefault(member =>
                    SymbolEqualityComparer.Default.Equals(
                        symbol.ContainingType.FindImplementationForInterfaceMember(member),
                        symbol
                    )
                );
    }

    public string? Summary => Text(_docs?.Element("summary")) ?? _inherited?.Summary;

    public string? Parameter(string name) =>
        Text(Named("param", name))
        ?? (_inherited is { } inherited ? inherited.Parameter(InheritedName(name, inherited, ParametersOf)) : null);

    public string? TypeParameter(string name) =>
        Text(Named("typeparam", name))
        ?? (
            _inherited is { } inherited
                ? inherited.TypeParameter(InheritedName(name, inherited, TypeParametersOf))
                : null
        );

    /// <summary>The name that <paramref name="inherited"/> gives the parameter in the place of <paramref name="name"/>.</summary>
    private string InheritedName(string name, XmlDocs inherited, Func<ISymbol, IReadOnlyList<ISymbol>> parametersOf)
    {
        IReadOnlyList<ISymbol> own = parametersOf(_symbol);
        IReadOnlyList<ISymbol> theirs = parametersOf(inherited._symbol);
        for (int place = 0; place < Math.Min(own.Count, theirs.Count); place++)
        {
            if (own[place].Name == name)
                return theirs[place].Name;
        }
        return name;
    }

    private static IReadOnlyList<ISymbol> ParametersOf(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol property => property.Parameters,
            _ => [],
        };

    private static IReadOnlyList<ISymbol> TypeParametersOf(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol method => method.TypeParameters,
            INamedTypeSymbol type => type.TypeParameters,
            _ => [],
        };

    private XElement? Named(string tag, string name) =>
        _docs?.Elements(tag).FirstOrDefault(element => (string?)element.Attribute("name") == name);

    /// <summary>The text of <paramref name="element"/>; null for none.</summary>
    private string? Text(XElement? element)
    {
        if (element is null)
            return null;
        var text = new StringBuilder();
        Append(text, element);
        // Lines without the spaces at their ends, and paragraphs one blank line apart.
        var lines = new List<string>();
        foreach (string line in Spans(text.ToString()).Split('\n').Select(line => line.Trim()))
        {
            if (line.Length > 0 || (lines.Count > 0 && lines[^1].Length > 0))
                lines.Add(line);
        }
        string joined = string.Join('\n', lines).TrimEnd();
        return joined.Length == 0 ? null : joined;
    }

    private void Append(StringBuilder text, XElement element)
    {
        foreach (XNode node in element.Nodes())
        {
            switch (node)
            {
                case XText plain:
                    foreach (char c in plain.Value)
                    {
                        // Whitespace, line breaks included, reads as one space.
                        if (c is CodeStart or CodeEnd)
                            continue;
                        if (!char.IsWhiteSpace(c))
                            Markdown.AppendEscaped(text, c);
                        else if (text.Length > 0 && text[^1] is not (' ' or '\n'))
                            text.Append(' ');
                    }
                    break;
                case XElement { Name.LocalName: "c" } code when OneLine(code.Value) is { Length: > 0 } inline:
                    text.Append(Code(inline));
                    break;
                case XElement { Name.LocalName: "para" or "code" or "list" } block:
                    text.Append("\n\n");
                    Append(text, block);
                    text.Append("\n\n");
                    break;
                case XElement { Name.LocalName: "item" } item:
                    text.Append("\n• ");
                    Append(text, item);
                    break;
                case XElement { Name.LocalName: "br" }:
                    text.Append('\n');
                    break;
                case XElement { Name.LocalName: "paramref" or "typeparamref" } reference
                    when (string?)reference.Attribute("name") is { Length: > 0 } name:
                    text.Append(Code(name));
                    break;
                case XElement { Name.LocalName: "see" or "seealso" } reference when !reference.Nodes().Any():
                    text.Append(Reference(reference));
                    break;
                case XElement other:
                    Append(text, other);
                    break;
            }
        }
    }

    /// <summary>What a <c>see</c> without text of its own reads as: its keyword or its symbol as code, or its link.</summary>
    private string? Reference(XElement see)
    {
        if ((string?)see.Attribute("langword") is { Length: > 0 } keyword)
            return Code(keyword);
        if ((string?)see.Attribute("cref") is not { Length: > 0 } cref)
            return (string?)see.Attribute("href") is { } link ? Markdown.Escape(link) : null;
        return Code(
            DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, _model.Compilation) is { } symbol
                ? symbol.ToMinimalDisplayString(_model, _position, s_reference)
                // As written, without the kind before the colon.
                : cref[(cref.IndexOf(':') + 1)..]
        );
    }

    /// <summary><paramref name="code"/> marked as code in the text as it is written.</summary>
    private static string Code(string code) => $"{CodeStart}{code}{CodeEnd}";

    /// <summary><paramref name="text"/> with its marked code as Markdown code spans, code next to code as one.</summary>
    private static string Spans(string text)
    {
        text = text.Replace($"{CodeEnd}{CodeStart}", "");
        var markdown = new StringBuilder(text.Length);
        int at = 0;
        while (text.IndexOf(CodeStart, at) is var start and >= 0)
        {
            int end = text.IndexOf(CodeEnd, start);
            markdown.Append(text, at, start - at).Append(Markdown.Code(text[(start + 1)..end]));
            at = end + 1;
        }
        return markdown.Append(text, at, text.Length - at).ToString();
    }

    /// <summary><paramref name="text"/> with each run of whitespace, line breaks included, as one space.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
