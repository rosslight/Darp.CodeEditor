using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// .NET's XML docs, from a folder of the reference pack's files, for all of .NET's assemblies at once: the runtime's
/// assemblies don't match the reference assemblies one to one. <c>string</c> is in System.Private.CoreLib at runtime,
/// and its docs are in System.Runtime.xml. A file is named after the runtime assembly that defines or forwards the types
/// it documents, so a member's file is found by its type. The types are read with the first request, and a file when one
/// of its members is first asked for.
/// </summary>
internal sealed class FrameworkDocs : DocumentationProvider
{
    /// <summary>The files that may document a type, by the type's name in documentation IDs.</summary>
    private readonly Lazy<ILookup<string, DocsFile>> _filesOfTypes;

    /// <param name="folder">The folder of the docs; without it, or without a file, members have none.</param>
    /// <param name="assemblies">The paths of .NET's runtime assemblies.</param>
    public FrameworkDocs(string folder, IReadOnlyList<string> assemblies) =>
        _filesOfTypes = new(() => FilesOfTypes(folder, assemblies));

    protected override string GetDocumentationForSymbol(
        string documentationMemberID,
        CultureInfo preferredCulture,
        CancellationToken cancellationToken = default
    )
    {
        // "M:System.String.Concat(System.String,System.String)" is in the file of the longest name before a dot that is
        // a type's: a nested type's members are in its outermost type's file.
        string name = documentationMemberID[(documentationMemberID.IndexOf(':') + 1)..];
        int parameters = name.IndexOf('(');
        if (parameters >= 0)
            name = name[..parameters];
        while (true)
        {
            // A type that two assemblies forward is documented in one of their files.
            foreach (DocsFile file in _filesOfTypes.Value[name])
            {
                if (file.Find(documentationMemberID) is { } docs)
                    return docs;
            }
            int dot = name.LastIndexOf('.');
            if (dot < 0)
                return "";
            name = name[..dot];
        }
    }

    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    /// <summary>The public types that each of <paramref name="assemblies"/> with a file in <paramref name="folder"/> defines or forwards.</summary>
    private static ILookup<string, DocsFile> FilesOfTypes(string folder, IReadOnlyList<string> assemblies)
    {
        var types = new List<(string Name, DocsFile File)>();
        foreach (string assembly in assemblies)
        {
            string path = Path.Combine(folder, Path.GetFileNameWithoutExtension(assembly) + ".xml");
            if (!File.Exists(path))
                continue;
            var file = new DocsFile(path);
            using var reader = new PEReader(File.OpenRead(assembly));
            MetadataReader metadata = reader.GetMetadataReader();
            foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
            {
                TypeDefinition type = metadata.GetTypeDefinition(handle);
                if ((type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public)
                    types.Add((Name(metadata, type.Namespace, type.Name), file));
            }
            foreach (ExportedTypeHandle handle in metadata.ExportedTypes)
            {
                ExportedType type = metadata.GetExportedType(handle);
                if (type.Implementation.Kind == HandleKind.AssemblyReference)
                    types.Add((Name(metadata, type.Namespace, type.Name), file));
            }
        }
        return types.ToLookup(type => type.Name, type => type.File);
    }

    private static string Name(MetadataReader metadata, StringHandle space, StringHandle name) =>
        space.IsNil ? metadata.GetString(name) : $"{metadata.GetString(space)}.{metadata.GetString(name)}";

    /// <summary>A file of XML docs, read and indexed by member when one of its members is first asked for.</summary>
    private sealed class DocsFile
    {
        private readonly Lazy<(byte[] Xml, Dictionary<string, Range> Members)> _members;

        public DocsFile(string path) => _members = new(() => Read(path));

        /// <summary>The <c>member</c> element of <paramref name="id"/>, as XML; null when the file has none.</summary>
        public string? Find(string id)
        {
            (byte[] xml, Dictionary<string, Range> members) = _members.Value;
            return members.TryGetValue(id, out Range member) ? Encoding.UTF8.GetString(xml.AsSpan(member)) : null;
        }

        private static (byte[] Xml, Dictionary<string, Range> Members) Read(string path)
        {
            byte[] xml;
            try
            {
                xml = File.ReadAllBytes(path);
            }
            // A file that went away with its SDK leaves its members without docs.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ([], []);
            }
            return (xml, Index(xml));
        }

        /// <summary>
        /// Where each member is, by its start tag as the docs are written: <c>&lt;member name="ID"&gt;</c> up to
        /// <c>&lt;/member&gt;</c>, or <c>&lt;member name="ID" /&gt;</c> for none. Reading the 7.6 MB of
        /// System.Runtime.xml as XML takes over 100 ms, and this a few.
        /// </summary>
        private static Dictionary<string, Range> Index(ReadOnlySpan<byte> xml)
        {
            ReadOnlySpan<byte> startTag = "<member name=\""u8;
            ReadOnlySpan<byte> endTag = "</member>"u8;
            var members = new Dictionary<string, Range>();
            int position = 0;
            while (Find(xml, position, startTag) is var start and >= 0)
            {
                int name = start + startTag.Length;
                int quote = Find(xml, name, "\""u8);
                int tagEnd = quote < 0 ? -1 : Find(xml, quote, ">"u8);
                int end =
                    tagEnd < 0 ? -1
                    : xml[tagEnd - 1] == '/' ? tagEnd + 1
                    : Find(xml, tagEnd, endTag) is var close and >= 0 ? close + endTag.Length
                    : -1;
                if (end < 0)
                    break;
                members[Encoding.UTF8.GetString(xml[name..quote])] = start..end;
                position = end;
            }
            return members;
        }

        /// <summary>Where <paramref name="value"/> is in <paramref name="xml"/>, from <paramref name="from"/> on; -1 for nowhere.</summary>
        private static int Find(ReadOnlySpan<byte> xml, int from, ReadOnlySpan<byte> value) =>
            xml[from..].IndexOf(value) is var found and >= 0 ? from + found : -1;
    }
}
