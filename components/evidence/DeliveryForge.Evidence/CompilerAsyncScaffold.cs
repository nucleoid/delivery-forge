using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DeliveryForge.Evidence;

// Closed Release/net10 one-await proof over the captured source and exact PE/PDB.
// Names/attributes never exempt a body: all three bodies, metadata relationships,
// framework signatures and authored points must match before anything is admitted.
internal sealed class CompilerAsyncScaffold
{
    private readonly HashSet<MethodDefinitionHandle> scaffolds = [];
    private readonly HashSet<CustomAttributeHandle> hiding = [];
    private readonly InstalledToolFiles files;
    private readonly MetadataReader metadata;
    private readonly MetadataReader pdb;
    private readonly PEReader pe;
    private readonly IReadOnlyDictionary<DocumentHandle, CompilerLineCensus.Document> mapped;
    private readonly Dictionary<string, List<(CompilerLineCensus.Document Document, ClassDeclarationSyntax Type)>> sources = new(StringComparer.Ordinal);
    private bool sourceComplete;

    internal CompilerAsyncScaffold(IReadOnlyList<CompilerLineCensus.Document> documents,
        IReadOnlyDictionary<DocumentHandle, CompilerLineCensus.Document> mapped,
        MetadataReader metadata, MetadataReader pdb, PEReader pe, InstalledToolFiles files)
    {
        this.files = files; this.metadata = metadata; this.pdb = pdb; this.pe = pe; this.mapped = mapped;
        Parse(documents);
        if (!sourceComplete) return;
        foreach (var handle in metadata.MethodDefinitions)
        {
            files.Check();
            if (Prove(handle) is not { } proof) continue;
            scaffolds.Add(handle); scaffolds.Add(proof.Set); hiding.Add(proof.Hiding);
        }
    }

    internal bool Proves(MethodDefinitionHandle handle) => scaffolds.Contains(handle);
    internal bool ProvesHiding(CustomAttributeHandle handle) => hiding.Contains(handle);

    private sealed record Proof(MethodDefinitionHandle Set, CustomAttributeHandle Hiding);

    private Proof? Prove(MethodDefinitionHandle kickoff)
    {
        var method = metadata.GetMethodDefinition(kickoff);
        var ownerHandle = method.GetDeclaringType(); var owner = metadata.GetTypeDefinition(ownerHandle);
        if (method.Attributes != (MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig) ||
            method.ImplAttributes != MethodImplAttributes.IL || method.RelativeVirtualAddress == 0 ||
            method.GetGenericParameters().Count != 0 ||
            owner.Attributes != (TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit) ||
            !owner.GetDeclaringType().IsNil || owner.GetGenericParameters().Count != 0 ||
            owner.GetCustomAttributes().Count != 0 || owner.GetFields().Count != 0 || owner.GetMethods().Count != 1 ||
            owner.GetInterfaceImplementations().Count != 0 || owner.GetProperties().Count != 0 || owner.GetEvents().Count != 0 || owner.GetNestedTypes().Length != 1) return null;
        var identity = metadata.GetString(owner.Namespace) + ":" + metadata.GetString(owner.Name);
        if (!sources.TryGetValue(identity, out var matches) || matches.Count != 1) return null;
        var (document, syntax) = matches[0];
        if (!Source(syntax, metadata.GetString(method.Name), out var authored, out var awaited)) return null;
        var parameters = method.GetParameters().ToArray();
        if (parameters.Length != 1) return null;
        var parameter = metadata.GetParameter(parameters[0]);
        if (parameter.SequenceNumber != 1 || parameter.Attributes != ParameterAttributes.None || parameter.GetCustomAttributes().Count != 0 ||
            metadata.GetString(parameter.Name) != authored.ParameterList.Parameters[0].Identifier.ValueText) return null;

        var task = Framework("System.Threading.Tasks", "Task");
        var builder = Framework("System.Runtime.CompilerServices", "AsyncTaskMethodBuilder");
        var machine = Framework("System.Runtime.CompilerServices", "IAsyncStateMachine");
        var awaiter = Framework("System.Runtime.CompilerServices", "TaskAwaiter");
        var exception = Framework("System", "Exception");
        if (task.IsNil || builder.IsNil || machine.IsNil || awaiter.IsNil || exception.IsNil ||
            owner.BaseType != Framework("System", "Object") ||
            !Blob(method.Signature, Join(B(0, 1), Type(0x12, task), Type(0x12, task)))) return null;

        var asyncType = Framework("System.Runtime.CompilerServices", "AsyncStateMachineAttribute");
        var asyncConstructor = Member(asyncType, ".ctor", Join(B(0x20, 1, 1), Type(0x12, Framework("System", "Type"))));
        if (asyncConstructor.IsNil) return null;
        TypeDefinitionHandle frameHandle = default;
        var nullableContext = 0;
        foreach (var handle in method.GetCustomAttributes())
        {
            files.Check(); var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor == asyncConstructor)
            {
                if (!frameHandle.IsNil) return null;
                var blob = metadata.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1) return null;
                var target = blob.ReadSerializedString();
                if (blob.RemainingBytes != 2 || blob.ReadUInt16() != 0) return null;
                foreach (var nested in owner.GetNestedTypes())
                {
                    files.Check(); var type = metadata.GetTypeDefinition(nested);
                    if (target != metadata.GetString(owner.Namespace) + "." + metadata.GetString(owner.Name) + "+" + metadata.GetString(type.Name)) continue;
                    if (!frameHandle.IsNil) return null;
                    frameHandle = nested;
                }
            }
            else if (++nullableContext > 1 || !EmptyOrNullable(handle, "NullableContextAttribute", B(1, 0, 1, 0, 0))) return null;
        }
        if (frameHandle.IsNil) return null;
        var frame = metadata.GetTypeDefinition(frameHandle);
        if (frame.GetDeclaringType() != ownerHandle || !frame.Namespace.IsNil || frame.Attributes != (TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit) ||
            frame.BaseType != Framework("System", "ValueType") || frame.GetGenericParameters().Count != 0 ||
            frame.GetNestedTypes().Length != 0 || frame.GetProperties().Count != 0 || frame.GetEvents().Count != 0 ||
            frame.GetMethods().Count != 2 || frame.GetFields().Count != 4 || frame.GetCustomAttributes().Count != 1 ||
            !EmptyOrNullable(frame.GetCustomAttributes().Single(), "CompilerGeneratedAttribute", B(1, 0, 0, 0))) return null;
        var interfaces = frame.GetInterfaceImplementations().ToArray();
        if (interfaces.Length != 1 || metadata.GetInterfaceImplementation(interfaces[0]).Interface != machine ||
            metadata.GetInterfaceImplementation(interfaces[0]).GetCustomAttributes().Count != 0) return null;
        var stateField = Field(frame, B(6, 8), FieldAttributes.Public);
        var builderField = Field(frame, Join(B(6), Type(0x11, builder)), FieldAttributes.Public);
        var taskField = Field(frame, Join(B(6), Type(0x12, task)), FieldAttributes.Public);
        var awaiterField = Field(frame, Join(B(6), Type(0x11, awaiter)), FieldAttributes.Private);
        if (stateField.IsNil || builderField.IsNil || taskField.IsNil || awaiterField.IsNil ||
            metadata.GetString(metadata.GetFieldDefinition(taskField).Name) != metadata.GetString(parameter.Name)) return null;
        var move = Method(frame, "MoveNext", B(0x20, 0, 1));
        var set = Method(frame, "SetStateMachine", Join(B(0x20, 1, 1), Type(0x12, machine)));
        if (move.IsNil || set.IsNil) return null;
        var moveDefinition = metadata.GetMethodDefinition(move); var setDefinition = metadata.GetMethodDefinition(set);
        var setParameters = setDefinition.GetParameters().ToArray();
        if (moveDefinition.GetCustomAttributes().Count != 0 || moveDefinition.GetParameters().Count != 0 ||
            setDefinition.GetCustomAttributes().Count != 1 || setParameters.Length != 1) return null;
        var setParameter = metadata.GetParameter(setParameters[0]);
        if (setParameter.SequenceNumber != 1 || setParameter.Attributes != ParameterAttributes.None || setParameter.GetCustomAttributes().Count != 1 ||
            !EmptyOrNullable(setParameter.GetCustomAttributes().Single(), "NullableAttribute", B(1, 0, 1, 0, 0))) return null;
        var hidden = setDefinition.GetCustomAttributes().Single();
        if (!Attribute(hidden, Framework("System.Diagnostics", "DebuggerHiddenAttribute"), B(0x20, 0, 1), B(1, 0, 0, 0))) return null;
        var interfaceMove = Member(machine, "MoveNext", B(0x20, 0, 1));
        var interfaceSet = Member(machine, "SetStateMachine", Join(B(0x20, 1, 1), Type(0x12, machine)));
        var implementations = frame.GetMethodImplementations().ToArray();
        if (implementations.Length != 2 || !implements(move, interfaceMove) || !implements(set, interfaceSet)) return null;
        bool implements(MethodDefinitionHandle body, MemberReferenceHandle declaration) => !declaration.IsNil && implementations.Count(handle =>
        {
            var row = metadata.GetMethodImplementation(handle);
            return row.MethodBody == body && row.MethodDeclaration == declaration;
        }) == 1;

        var create = Member(builder, "Create", Join(B(0, 0), Type(0x11, builder)));
        var startMember = Member(builder, "Start", B(0x30, 1, 1, 1, 0x10, 0x1e, 0));
        var getTask = Member(builder, "get_Task", Join(B(0x20, 0), Type(0x12, task)));
        var getAwaiter = Member(task, "GetAwaiter", Join(B(0x20, 0), Type(0x11, awaiter)));
        var completed = Member(awaiter, "get_IsCompleted", B(0x20, 0, 2));
        var awaitMember = Member(builder, "AwaitUnsafeOnCompleted", B(0x30, 2, 2, 1, 0x10, 0x1e, 0, 0x10, 0x1e, 1));
        var result = Member(awaiter, "GetResult", B(0x20, 0, 1));
        var setException = Member(builder, "SetException", Join(B(0x20, 1, 1), Type(0x12, exception)));
        var setResult = Member(builder, "SetResult", B(0x20, 0, 1));
        var setMachine = Member(builder, "SetStateMachine", Join(B(0x20, 1, 1), Type(0x12, machine)));
        if (create.IsNil || getTask.IsNil || getAwaiter.IsNil || completed.IsNil || result.IsNil || setException.IsNil || setResult.IsNil || setMachine.IsNil) return null;
        var start = Specification(startMember, Join(B(0x0a, 1), Type(0x11, frameHandle)));
        var awaitSpec = Specification(awaitMember, Join(B(0x0a, 2), Type(0x11, awaiter), Type(0x11, frameHandle)));
        if (start.IsNil || awaitSpec.IsNil) return null;
        var kickoffIl = Join(B(0x12, 0, 0x28), Token(create), B(0x7d), Token(builderField), B(0x12, 0, 2, 0x7d), Token(taskField),
            B(0x12, 0, 0x15, 0x7d), Token(stateField), B(0x12, 0, 0x7c), Token(builderField), B(0x12, 0, 0x28), Token(start),
            B(0x12, 0, 0x7c), Token(builderField), B(0x28), Token(getTask), B(0x2a));
        var setIl = Join(B(2, 0x7c), Token(builderField), B(3, 0x28), Token(setMachine), B(0x2a));
        var moveIl = Join(B(2, 0x7b), Token(stateField), B(0x0a, 6, 0x2c, 0x35, 2, 0x7b), Token(taskField), B(0x6f), Token(getAwaiter),
            B(0x0b, 0x12, 1, 0x28), Token(completed), B(0x2d, 0x3c, 2, 0x16, 0x25, 0x0a, 0x7d), Token(stateField),
            B(2, 7, 0x7d), Token(awaiterField), B(2, 0x7c), Token(builderField), B(0x12, 1, 2, 0x28), Token(awaitSpec),
            B(0xde, 0x4f, 2, 0x7b), Token(awaiterField), B(0x0b, 2, 0x7c), Token(awaiterField), B(0xfe, 0x15), Token(awaiter),
            B(2, 0x15, 0x25, 0x0a, 0x7d), Token(stateField), B(0x12, 1, 0x28), Token(result), B(0xde, 0x17, 0x0c, 2, 0x1f, 0xfe, 0x7d),
            Token(stateField), B(2, 0x7c), Token(builderField), B(8, 0x28), Token(setException), B(0xde, 0x13, 2, 0x1f, 0xfe, 0x7d),
            Token(stateField), B(2, 0x7c), Token(builderField), B(0x28), Token(setResult), B(0x2a));
        if (!Body(kickoff, kickoffIl, Join(B(7, 1), Type(0x11, frameHandle)), 2, true, default) ||
            !Body(set, setIl, null, 8, false, default) ||
            !Body(move, moveIl, Join(B(7, 3, 8), Type(0x11, awaiter), Type(0x12, exception)), 3, true, exception)) return null;
        if (!Points(kickoff, move, set, document, authored, awaited)) return null;
        return new(set, hidden);
    }

    private bool Body(MethodDefinitionHandle handle, byte[] expected, byte[]? locals, int stack, bool initialized, TypeReferenceHandle catchType)
    {
        files.Check(); var body = pe.GetMethodBody(metadata.GetMethodDefinition(handle).RelativeVirtualAddress);
        if (body.MaxStack != stack || body.LocalVariablesInitialized != initialized || body.GetILBytes() is not { } il || !il.AsSpan().SequenceEqual(expected)) return false;
        if (locals is null ? !body.LocalSignature.IsNil : body.LocalSignature.IsNil || !Blob(metadata.GetStandaloneSignature(body.LocalSignature).Signature, locals)) return false;
        if (catchType.IsNil) return body.ExceptionRegions.Length == 0;
        if (body.ExceptionRegions.Length != 1) return false;
        var region = body.ExceptionRegions[0];
        return region.Kind == ExceptionRegionKind.Catch && region.TryOffset == 7 && region.TryLength == 93 &&
            region.HandlerOffset == 100 && region.HandlerLength == 23 && region.CatchType == catchType;
    }

    private bool Points(MethodDefinitionHandle kickoff, MethodDefinitionHandle move, MethodDefinitionHandle set,
        CompilerLineCensus.Document document, MethodDeclarationSyntax method, AwaitExpressionSyntax awaited)
    {
        MethodDebugInformation Debug(MethodDefinitionHandle handle) => pdb.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(MetadataTokens.GetRowNumber(handle)));
        if (Debug(kickoff).GetSequencePoints().Any() || Debug(set).GetSequencePoints().Any()) return false;
        var associated = 0;
        foreach (var handle in metadata.MethodDefinitions)
        {
            files.Check(); var relation = Debug(handle).GetStateMachineKickoffMethod();
            if (relation != kickoff) continue;
            if (handle != move || ++associated != 1) return false;
        }
        if (associated != 1 || !Debug(kickoff).GetStateMachineKickoffMethod().IsNil || !Debug(set).GetStateMachineKickoffMethod().IsNil) return false;
        var debug = Debug(move); var points = debug.GetSequencePoints().ToArray();
        if (points.Length != 7) return false;
        int[] offsets = [0, 7, 10, 22, 100, 123, 131];
        var awaitSpan = awaited.Parent!.GetLocation().GetLineSpan(); var closeSpan = method.Body!.CloseBraceToken.GetLocation().GetLineSpan();
        for (var i = 0; i < points.Length; i++)
        {
            files.Check(); var point = points[i]; var handle = point.Document.IsNil ? debug.Document : point.Document;
            if (point.Offset != offsets[i] || !mapped.TryGetValue(handle, out var source) || !ReferenceEquals(source, document)) return false;
            if (i is not (2 or 5)) { if (!point.IsHidden) return false; continue; }
            var span = i == 2 ? awaitSpan : closeSpan;
            if (point.IsHidden || point.StartLine != span.StartLinePosition.Line + 1 || point.StartColumn != span.StartLinePosition.Character + 1 ||
                point.EndLine != span.EndLinePosition.Line + 1 || point.EndColumn != span.EndLinePosition.Character + 1) return false;
        }
        var rows = pdb.GetCustomDebugInformation(move).ToArray();
        if (rows.Length != 1) return false;
        var row = pdb.GetCustomDebugInformation(rows[0]);
        return pdb.GetGuid(row.Kind) == new Guid("54fd2ac5-e925-401a-9c2a-f94f171072f8") &&
            pdb.GetBlobBytes(row.Value).AsSpan().SequenceEqual(Join(B(0, 0, 0, 0, 40, 0, 0, 0, 63, 0, 0, 0), Compressed(MetadataTokens.GetRowNumber(move))));
    }

    private TypeReferenceHandle Framework(string space, string name)
    {
        TypeReferenceHandle found = default;
        foreach (var handle in metadata.TypeReferences)
        {
            files.Check(); var type = metadata.GetTypeReference(handle);
            if (metadata.GetString(type.Namespace) != space || metadata.GetString(type.Name) != name || type.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
            var assembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            if (metadata.GetString(assembly.Name) != "System.Runtime" || assembly.Version != new Version(10, 0, 0, 0) ||
                assembly.Flags != 0 || !assembly.Culture.IsNil ||
                !metadata.GetBlobBytes(assembly.PublicKeyOrToken).AsSpan().SequenceEqual(B(0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a))) continue;
            if (!found.IsNil) return default;
            found = handle;
        }
        return found;
    }

    private MemberReferenceHandle Member(TypeReferenceHandle parent, string name, byte[] signature)
    {
        if (parent.IsNil) return default;
        MemberReferenceHandle found = default;
        foreach (var handle in metadata.MemberReferences)
        {
            files.Check(); var member = metadata.GetMemberReference(handle);
            if (member.Parent != parent || metadata.GetString(member.Name) != name || !Blob(member.Signature, signature)) continue;
            if (!found.IsNil) return default;
            found = handle;
        }
        return found;
    }

    private MethodSpecificationHandle Specification(MemberReferenceHandle method, byte[] signature)
    {
        if (method.IsNil) return default;
        MethodSpecificationHandle found = default;
        for (var i = 1; i <= metadata.GetTableRowCount(TableIndex.MethodSpec); i++)
        {
            files.Check(); var handle = MetadataTokens.MethodSpecificationHandle(i); var row = metadata.GetMethodSpecification(handle);
            if (row.Method != method || !Blob(row.Signature, signature)) continue;
            if (!found.IsNil) return default;
            found = handle;
        }
        return found;
    }

    private FieldDefinitionHandle Field(TypeDefinition type, byte[] signature, FieldAttributes flags)
    {
        FieldDefinitionHandle found = default;
        foreach (var handle in type.GetFields())
        {
            files.Check(); var field = metadata.GetFieldDefinition(handle);
            if (field.Attributes != flags || field.GetCustomAttributes().Count != 0 || !Blob(field.Signature, signature)) continue;
            if (!found.IsNil) return default;
            found = handle;
        }
        return found;
    }

    private MethodDefinitionHandle Method(TypeDefinition type, string name, byte[] signature)
    {
        MethodDefinitionHandle found = default;
        foreach (var handle in type.GetMethods())
        {
            files.Check(); var method = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(method.Name) != name || !Blob(method.Signature, signature) || method.RelativeVirtualAddress == 0 ||
                method.Attributes != (MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot) ||
                method.ImplAttributes != MethodImplAttributes.IL || method.GetGenericParameters().Count != 0) continue;
            if (!found.IsNil) return default;
            found = handle;
        }
        return found;
    }

    private bool EmptyOrNullable(CustomAttributeHandle handle, string name, byte[] value) =>
        Attribute(handle, Framework("System.Runtime.CompilerServices", name), name is "NullableAttribute" or "NullableContextAttribute" ? B(0x20, 1, 1, 5) : B(0x20, 0, 1), value);

    private bool Attribute(CustomAttributeHandle handle, TypeReferenceHandle type, byte[] signature, byte[] value)
    {
        var attribute = metadata.GetCustomAttribute(handle); var constructor = Member(type, ".ctor", signature);
        return !constructor.IsNil && attribute.Constructor == constructor && Blob(attribute.Value, value);
    }

    private bool Blob(BlobHandle handle, byte[] value) => metadata.GetBlobBytes(handle).AsSpan().SequenceEqual(value);
    private static byte[] B(params byte[] values) => values;
    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();
    private static byte[] Compressed(int value) { var builder = new BlobBuilder(); builder.WriteCompressedInteger(value); return builder.ToArray(); }
    private static byte[] Type(byte kind, EntityHandle handle) => Join(B(kind), Compressed(checked(MetadataTokens.GetRowNumber(handle) * 4 + (handle.Kind == HandleKind.TypeReference ? 1 : 0))));
    private static byte[] Token(EntityHandle handle) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, MetadataTokens.GetToken(handle)); return bytes; }

    private static bool Source(ClassDeclarationSyntax type, string name, out MethodDeclarationSyntax method, out AwaitExpressionSyntax awaited)
    {
        method = null!; awaited = null!;
        if (type.AttributeLists.Count != 0 || type.TypeParameterList is not null || type.ParameterList is not null || type.BaseList is not null ||
            type.Ancestors().Any(node => node is BaseTypeDeclarationSyntax) || type.Members.Count != 1 ||
            !Modifiers(type.Modifiers, SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword) || type.Members[0] is not MethodDeclarationSyntax candidate ||
            candidate.Identifier.ValueText != name || candidate.AttributeLists.Count != 0 || candidate.TypeParameterList is not null ||
            candidate.ConstraintClauses.Count != 0 || candidate.ExplicitInterfaceSpecifier is not null || candidate.ExpressionBody is not null ||
            !Modifiers(candidate.Modifiers, SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword, SyntaxKind.AsyncKeyword) || !Task(candidate.ReturnType) ||
            candidate.ParameterList.Parameters.Count != 1 || candidate.Body is not { Statements.Count: 1 } body ||
            body.Statements[0] is not ExpressionStatementSyntax { Expression: AwaitExpressionSyntax { Expression: IdentifierNameSyntax argument } expression }) return false;
        var parameter = candidate.ParameterList.Parameters[0];
        if (parameter.Type is null || !Task(parameter.Type) || parameter.AttributeLists.Count != 0 || parameter.Modifiers.Count != 0 || parameter.Default is not null ||
            parameter.Identifier.ValueText != argument.Identifier.ValueText) return false;
        var span = body.GetLocation().GetLineSpan();
        if (span.StartLinePosition.Line != span.EndLinePosition.Line) return false;
        method = candidate; awaited = expression; return true;
    }

    private static bool Task(TypeSyntax type) => type is QualifiedNameSyntax
    {
        Left: QualifiedNameSyntax
        {
            Left: QualifiedNameSyntax
            {
                Left: AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global", Name.Identifier.ValueText: "System" },
                Right.Identifier.ValueText: "Threading"
            },
            Right.Identifier.ValueText: "Tasks"
        },
        Right.Identifier.ValueText: "Task"
    };

    private static bool Modifiers(SyntaxTokenList tokens, params SyntaxKind[] kinds) => tokens.Count == kinds.Length && kinds.All(kind => tokens.Count(token => token.IsKind(kind)) == 1);

    private void Parse(IReadOnlyList<CompilerLineCensus.Document> documents)
    {
        if (documents.Sum(document => (long)document.Text.Length) > 8 * 1024 * 1024) return;
        var count = 0;
        foreach (var document in documents)
        {
            files.Check();
            var tree = CSharpSyntaxTree.ParseText(document.Text, new CSharpParseOptions(LanguageVersion.CSharp14, DocumentationMode.None), cancellationToken: files.Token);
            if (tree.GetDiagnostics(files.Token).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) return;
            foreach (var node in tree.GetRoot(files.Token).DescendantNodes(descendIntoTrivia: true))
            {
                files.Check(); if (++count > 1_000_000) return;
                if (node is DirectiveTriviaSyntax directive && directive is not (NullableDirectiveTriviaSyntax or PragmaWarningDirectiveTriviaSyntax or RegionDirectiveTriviaSyntax or EndRegionDirectiveTriviaSyntax)) return;
                if (node is not BaseTypeDeclarationSyntax declaration) continue;
                var names = new List<string>();
                foreach (var space in declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse())
                {
                    if (space.Name.DescendantNodesAndSelf().Any(part => part is not (IdentifierNameSyntax or QualifiedNameSyntax))) return;
                    names.Add(space.Name.WithoutTrivia().ToString());
                }
                var identity = string.Join('.', names) + ":" + declaration.Identifier.ValueText;
                if (!sources.TryGetValue(identity, out var matches)) sources.Add(identity, matches = []);
                // A generated or non-class declaration refuses this entire
                // source-vector profile; no unsupported input disappears from it.
                if (declaration is not ClassDeclarationSyntax type || document.Generated) { sources.Clear(); return; }
                matches.Add((document, type));
            }
        }
        files.Check(); sourceComplete = true;
    }
}
