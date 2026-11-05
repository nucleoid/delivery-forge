using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DeliveryForge.Evidence;

// A closed body-origin proof, not an exemption based on a generated name/attribute.
// Unknown syntax or IL stays opaque. No compiler/reference assembly is executed.
internal sealed class CompilerImplicitConstructor(IReadOnlyList<CompilerLineCensus.Document> documents, InstalledToolFiles files)
{
    private static readonly byte[] ParameterlessInstanceVoid = [0x20, 0x00, 0x01];
    private Dictionary<string, List<BaseTypeDeclarationSyntax>>? declarations;
    private bool parsed;

    internal bool Proves(MethodDefinitionHandle handle, MetadataReader reader, PEReader pe)
    {
        files.Check();
        var method = reader.GetMethodDefinition(handle);
        var constructorFlags = MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.HideBySig;
        if (reader.GetString(method.Name) != ".ctor" || method.RelativeVirtualAddress == 0 ||
            (method.Attributes & ~MethodAttributes.MemberAccessMask) != constructorFlags ||
            method.ImplAttributes != MethodImplAttributes.IL ||
            method.GetGenericParameters().Count != 0 || method.GetCustomAttributes().Count != 0 ||
            !reader.GetBlobBytes(method.Signature).AsSpan().SequenceEqual(ParameterlessInstanceVoid)) return false;
        var type = reader.GetTypeDefinition(method.GetDeclaringType());
        if (!type.GetDeclaringType().IsNil || type.GetGenericParameters().Count != 0 ||
            (type.Attributes & TypeAttributes.Interface) != 0 || type.BaseType.Kind != HandleKind.TypeReference) return false;
        var baseType = reader.GetTypeReference((TypeReferenceHandle)type.BaseType);
        if (reader.GetString(baseType.Namespace) != "System" || reader.GetString(baseType.Name) != "Object" ||
            baseType.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
        var assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)baseType.ResolutionScope);
        var key = Convert.ToHexStringLower(reader.GetBlobBytes(assembly.PublicKeyOrToken));
        if ((assembly.Flags & AssemblyFlags.PublicKey) != 0 || assembly.Version.Major <= 0 ||
            (reader.GetString(assembly.Name), key) is not
                (("System.Runtime", "b03f5f7f11d50a3a") or ("System.Private.CoreLib", "7cec85d7bea7798e"))) return false;
        if (!ProvesSource(reader.GetString(type.Namespace), reader.GetString(type.Name))) return false;
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        if (!body.LocalSignature.IsNil || body.ExceptionRegions.Length != 0) return false;
        var il = body.GetILBytes();
        if (il is null || il.Length > 64) return false;
        var offset = 0;
        void Nops() { while (offset < il.Length && il[offset] == 0x00) { files.Check(); offset++; } }
        Nops();
        if (offset >= il.Length || il[offset++] != 0x02) return false; // ldarg.0
        Nops();
        if (offset >= il.Length || il[offset++] != 0x28 || il.Length - offset < 4) return false; // call
        var token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4)); offset += 4;
        var row = token & 0x00ffffff;
        if ((uint)token >> 24 != 0x0a || row == 0 || row > reader.MemberReferences.Count) return false;
        var member = reader.GetMemberReference(MetadataTokens.MemberReferenceHandle(row));
        if (member.Parent != type.BaseType || reader.GetString(member.Name) != ".ctor" ||
            !reader.GetBlobBytes(member.Signature).AsSpan().SequenceEqual(ParameterlessInstanceVoid)) return false;
        Nops();
        if (offset >= il.Length || il[offset++] != 0x2a) return false; // ret
        Nops(); files.Check();
        return offset == il.Length;
    }

    private bool ProvesSource(string space, string name)
    {
        if (!parsed) Parse();
        if (declarations is null || !declarations.TryGetValue(space + ":" + name, out var matches) || matches.Count != 1 ||
            matches[0] is not ClassDeclarationSyntax type || type.TypeParameterList is not null ||
            type.ParameterList is not null || type.BaseList is not null ||
            type.Ancestors().Any(node => node is BaseTypeDeclarationSyntax) ||
            type.Modifiers.Any(token => token.IsKind(SyntaxKind.PartialKeyword) || token.IsKind(SyntaxKind.StaticKeyword) || token.IsKind(SyntaxKind.FileKeyword))) return false;
        foreach (var member in type.Members)
        {
            files.Check();
            if (member is ConstructorDeclarationSyntax ||
                member is BaseFieldDeclarationSyntax field && field.Declaration.Variables.Any(variable => variable.Initializer is not null) ||
                member is PropertyDeclarationSyntax { Initializer: not null }) return false;
        }
        return true;
    }

    private void Parse()
    {
        parsed = true;
        // This optional proof has a narrower finite parser profile; exceeding it
        // leaves a body unsupported instead of increasing existing resource limits.
        if (documents.Sum(document => (long)document.Text.Length) > 8 * 1024 * 1024) return;
        var result = new Dictionary<string, List<BaseTypeDeclarationSyntax>>(StringComparer.Ordinal);
        var count = 0;
        foreach (var document in documents)
        {
            files.Check();
            var tree = CSharpSyntaxTree.ParseText(document.Text,
                new CSharpParseOptions(LanguageVersion.CSharp14, DocumentationMode.None),
                cancellationToken: files.Token);
            files.Check();
            if (tree.GetDiagnostics(files.Token).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) return;
            files.Check();
            var root = tree.GetRoot(files.Token);
            foreach (var node in root.DescendantNodes(descendIntoTrivia: true))
            {
                files.Check();
                if (++count > 1_000_000) return;
                // Conditional/remapping directives need exact effective parse input.
                // Nullable/warning/region directives do not choose type membership.
                if (node is DirectiveTriviaSyntax directive && directive is not
                    (NullableDirectiveTriviaSyntax or PragmaWarningDirectiveTriviaSyntax or RegionDirectiveTriviaSyntax or EndRegionDirectiveTriviaSyntax)) return;
                if (node is not BaseTypeDeclarationSyntax type) continue;
                var names = new List<string>();
                foreach (var ancestor in type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse())
                {
                    files.Check();
                    var name = Namespace(ancestor.Name);
                    if (name is null) return;
                    names.Add(name);
                }
                var identity = string.Join('.', names) + ":" + type.Identifier.ValueText;
                if (!result.TryGetValue(identity, out var matches)) result.Add(identity, matches = []);
                matches.Add(type);
            }
        }
        files.Check(); declarations = result;
    }

    private static string? Namespace(NameSyntax name)
    {
        var parts = new Stack<string>();
        while (name is QualifiedNameSyntax qualified)
        {
            if (parts.Count >= 1024 || qualified.Right is not IdentifierNameSyntax identifier) return null;
            parts.Push(identifier.Identifier.ValueText); name = qualified.Left;
        }
        if (name is not IdentifierNameSyntax first) return null;
        parts.Push(first.Identifier.ValueText);
        return string.Join('.', parts);
    }
}
