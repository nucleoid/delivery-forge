using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DeliveryForge.Evidence;

// Conditional source-origin attribution in the trusted target compiler island.
// The caller must still require entire reconstructed PE AND PDB equality. This
// neither proves execution/coverage nor discharges a constructor/base-call body.
internal sealed class CompilerInitializerOrigins(CSharpCompilation compilation,
    MetadataReader metadata, MetadataReader pdb, IReadOnlyDictionary<DocumentHandle, string> documentPaths,
    CompilerNamedMethodBindings.Table bindings, Action step, Action<long, string?[]> admit, CancellationToken token)
{
    internal sealed record Witness(int Offset, int StartLine, int StartColumn, int EndLine, int EndColumn,
        string ChecksumAlgorithm, string Checksum);
    internal sealed record Route(int? ConstructorToken, string Disposition, int? DelegatesToToken,
        int? TerminalToken, Witness? Point);
    internal sealed record Attribution(int? FieldToken, string Disposition, IReadOnlyList<Route> Constructors)
    {
        // Same-computation seam for the source-exhaustion binder. Not serialized
        // by the inventory and never read from a caller-supplied certificate.
        internal bool Complete { get; init; }
    }
    internal sealed record ConstructorArguments(int? CallerToken, int? TargetToken, string Disposition, Witness? Point)
    {
        internal bool Complete => CallerToken is not null && TargetToken is not null && Point is not null;
    }
    private readonly Dictionary<SyntaxTree, bool> plainSources = [];
    private readonly Dictionary<(DocumentHandle, SyntaxTree), bool> checkedDocuments = [];

    internal ConstructorArguments CaptureArguments(SyntaxNode span, string path)
    {
        step();
        IMethodSymbol? caller = null;
        IMethodSymbol? target = null;
        SyntaxNode? legal = null;
        SyntaxNode required = span;
        if (span is ConstructorInitializerSyntax { Parent: ConstructorDeclarationSyntax declaration } initializer)
        {
            var model = compilation.GetSemanticModel(span.SyntaxTree);
            caller = model.GetDeclaredSymbol(declaration, token);
            var info = model.GetSymbolInfo(initializer, token);
            if (info.CandidateReason == CandidateReason.None) target = info.Symbol as IMethodSymbol;
            legal = declaration;
            required = initializer.ArgumentList;
        }
        else if (span is ArgumentListSyntax { Parent: PrimaryConstructorBaseTypeSyntax primary } &&
            primary.Parent?.Parent is TypeDeclarationSyntax { ParameterList: not null } typeDeclaration)
        {
            var model = compilation.GetSemanticModel(span.SyntaxTree);
            var type = model.GetDeclaredSymbol(typeDeclaration, token);
            if (type is not null)
                foreach (var constructor in type.InstanceConstructors)
                {
                    step();
                    foreach (var reference in constructor.DeclaringSyntaxReferences)
                    {
                        step();
                        if (reference.SyntaxTree != span.SyntaxTree) continue;
                        var syntax = reference.GetSyntax(token);
                        var declaredType = syntax as TypeDeclarationSyntax ??
                            (syntax as ParameterListSyntax)?.Parent as TypeDeclarationSyntax;
                        if (declaredType?.Span != typeDeclaration.Span) continue;
                        if (caller is not null) return ArgumentResult(null, null, "ambiguous-primary-constructor-owner");
                        caller = constructor;
                    }
                }
            var info = model.GetSymbolInfo(primary, token);
            if (info.CandidateReason == CandidateReason.None) target = info.Symbol as IMethodSymbol;
            legal = typeDeclaration;
        }
        if (caller is null || target is null || legal is null || !Plain(span.SyntaxTree) || Hidden(caller))
            return ArgumentResult(null, null, "constructor-argument-source-or-target-unresolved");
        for (INamedTypeSymbol? type = caller.ContainingType; type is not null; type = type.ContainingType)
        {
            step();
            if (Hidden(type)) return ArgumentResult(null, null, "constructor-argument-type-hiding-unsupported");
            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                step();
                if (!Plain(reference.SyntaxTree)) return ArgumentResult(null, null, "constructor-argument-mapping-unsupported");
            }
        }
        var callerBinding = bindings.For(caller);
        var targetBinding = bindings.ConstructorTarget(target);
        if (callerBinding.MethodToken is not { } callerToken || targetBinding.MethodToken is not { } targetToken)
            return ArgumentResult(callerBinding.MethodToken, targetBinding.MethodToken, "constructor-argument-signature-unresolved");
        var witness = FindPoint(callerToken, required, legal, path);
        return ArgumentResult(callerToken, targetToken, witness is null ? "constructor-argument-point-unresolved"
            : "constructor-argument-origin-conditional-on-exact-reemit", witness);
    }

    private ConstructorArguments ArgumentResult(int? caller, int? target, string disposition, Witness? point = null)
    {
        admit(256, [disposition]); return new(caller, target, disposition, point);
    }

    internal Attribution Capture(SyntaxNode value, ISymbol owner, string path)
    {
        step();
        IFieldSymbol? field = owner as IFieldSymbol;
        if (owner is IPropertySymbol property)
        {
            foreach (var member in property.ContainingType.GetMembers())
            {
                step();
                if (member is not IFieldSymbol candidate ||
                    !SymbolEqualityComparer.Default.Equals(candidate.AssociatedSymbol, property)) continue;
                if (field is not null) return Unsupported("ambiguous-associated-property-field");
                field = candidate;
            }
        }
        if (field is null || field.IsConst || field.ContainingType.TypeKind != TypeKind.Class)
            return Unsupported("initializer-field-or-class-receiver-unsupported");
        var binding = bindings.ForField(field);
        if (binding.FieldToken is not { } fieldToken) return Unsupported(binding.Disposition);
        SyntaxNode? declaration = value.Parent?.Parent switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1 } variables }
                when variables.Parent is FieldDeclarationSyntax => variables.Parent,
            PropertyDeclarationSyntax propertyDeclaration => propertyDeclaration,
            _ => null
        };
        if (declaration is null || !Plain(value.SyntaxTree) || Hidden(owner) || Hidden(field))
            return Unsupported("initializer-declaration-mapping-or-hiding-unsupported", fieldToken);
        var type = field.ContainingType;
        for (INamedTypeSymbol? enclosing = type; enclosing is not null; enclosing = enclosing.ContainingType)
        {
            step();
            if (Hidden(enclosing)) return Unsupported("initializer-containing-type-hiding-unsupported", fieldToken);
            foreach (var reference in enclosing.DeclaringSyntaxReferences)
            {
                step();
                if (!Plain(reference.SyntaxTree)) return Unsupported("initializer-containing-type-mapping-unsupported", fieldToken);
            }
        }
        var constructors = field.IsStatic ? type.StaticConstructors : type.InstanceConstructors;
        if (constructors.Length is 0 or > 10000) return Unsupported("initializer-constructor-membership-unsupported", fieldToken);
        admit(512L + 256L * constructors.Length, []);
        var members = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var visiting = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var routes = new Dictionary<IMethodSymbol, (Route Route, bool Resolved)>(SymbolEqualityComparer.Default);
        foreach (var constructor in constructors) { step(); members.Add(constructor.OriginalDefinition); }

        (Route Route, bool Resolved) Resolve(IMethodSymbol constructor)
        {
            step();
            constructor = constructor.OriginalDefinition;
            if (routes.TryGetValue(constructor, out var previous)) return previous;
            var methodToken = bindings.For(constructor).MethodToken;
            (Route, bool) Finish(string disposition, bool resolved, int? target = null, int? terminal = null, Witness? point = null)
            {
                admit(256, [disposition]);
                var result = (new Route(methodToken, disposition, target, terminal, point), resolved);
                routes.Add(constructor, result);
                visiting.Remove(constructor);
                return result;
            }
            if (!visiting.Add(constructor))
                return (new(methodToken, "cyclic-this-constructor-route", null, null, null), false);
            if (methodToken is null || constructor.IsStatic != field.IsStatic || Hidden(constructor))
                return Finish("unbound-or-hidden-constructor", false);
            // C# record CLASS copy-constructor semantics, not generated naming,
            // IsImplicitlyDeclared, or absence of a sequence point. Bodies remain
            // independent obligations even when this initializer is not executed.
            if (!constructor.IsStatic && type.IsRecord && type.TypeKind == TypeKind.Class &&
                constructor.MethodKind == MethodKind.Constructor && constructor.Parameters.Length == 1 &&
                constructor.Parameters[0].RefKind == RefKind.None &&
                SymbolEqualityComparer.Default.Equals(constructor.Parameters[0].Type, type))
                return Finish("semantic-record-class-copy-skips-initializers", true, terminal: methodToken);
            ConstructorInitializerSyntax? thisInitializer = null;
            foreach (var reference in constructor.DeclaringSyntaxReferences)
            {
                step();
                if (!Plain(reference.SyntaxTree)) return Finish("constructor-source-mapping-unsupported", false);
                if (reference.GetSyntax(token) is ConstructorDeclarationSyntax
                    { Initializer.ThisOrBaseKeyword.RawKind: (int)SyntaxKind.ThisKeyword } syntax)
                {
                    if (thisInitializer is not null) return Finish("ambiguous-this-constructor-source", false);
                    thisInitializer = syntax.Initializer;
                }
            }
            if (thisInitializer is not null)
            {
                step();
                var info = compilation.GetSemanticModel(thisInitializer.SyntaxTree).GetSymbolInfo(thisInitializer, token);
                if (info.CandidateReason != CandidateReason.None || info.Symbol is not IMethodSymbol target ||
                    target.MethodKind != MethodKind.Constructor || target.IsStatic ||
                    !SymbolEqualityComparer.Default.Equals(target.ContainingType.OriginalDefinition, type.OriginalDefinition) ||
                    !members.Contains(target.OriginalDefinition)) return Finish("unresolved-this-constructor-target", false);
                var targetBinding = bindings.For(target);
                if (targetBinding.MethodToken is null) return Finish("unbound-this-constructor-target", false);
                var terminal = Resolve(target);
                return Finish("semantic-this-delegation-to-bound-constructor", terminal.Resolved,
                    targetBinding.MethodToken, terminal.Route.TerminalToken);
            }
            var witness = FindPoint(methodToken.Value, value, declaration, path);
            return witness is null ? Finish("missing-or-ambiguous-exact-initializer-point", false)
                : Finish("positive-exact-initializer-point-trusted-compiler", true, terminal: methodToken, point: witness);
        }
        var complete = true;
        var positive = false;
        foreach (var constructor in constructors)
        {
            step();
            var route = Resolve(constructor);
            complete &= route.Resolved;
            positive |= route.Route.Point is not null;
        }
        admit(64L + 32L * constructors.Length, []);
        var result = new Route[constructors.Length];
        for (var i = 0; i < result.Length; i++) { step(); result[i] = routes[constructors[i].OriginalDefinition].Route; }
        const string attributed = "all-applicable-initializer-origins-conditional-on-exact-reemit";
        const string unresolved = "initializer-constructor-origin-unresolved";
        admit(256, [complete && positive ? attributed : unresolved]);
        return new(fieldToken, complete && positive ? attributed : unresolved, result) { Complete = complete && positive };
    }

    private Attribution Unsupported(string disposition, int? field = null)
    {
        admit(256, [disposition]);
        return new(field, disposition, []);
    }

    private bool Plain(SyntaxTree tree)
    {
        step();
        if (plainSources.TryGetValue(tree, out var result)) return result;
        result = true;
        foreach (var trivia in tree.GetRoot(token).DescendantTrivia(descendIntoTrivia: true))
        {
            step();
            if (trivia.RawKind is (int)SyntaxKind.LineDirectiveTrivia or (int)SyntaxKind.LineSpanDirectiveTrivia or
                (int)SyntaxKind.PragmaChecksumDirectiveTrivia) result = false;
        }
        admit(128, []); plainSources.Add(tree, result); return result;
    }

    private bool Hidden(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            step();
            if (attribute.AttributeClass is
            {
                MetadataName: "DebuggerHiddenAttribute" or "DebuggerStepThroughAttribute" or "DebuggerNonUserCodeAttribute",
                ContainingNamespace: { Name: "Diagnostics", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } }
            }
                or
            {
                MetadataName: "ExcludeFromCodeCoverageAttribute",
                ContainingNamespace:
                {
                    Name: "CodeAnalysis", ContainingNamespace:
                    {
                        Name: "Diagnostics",
                        ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true }
                    }
                }
            }) return true;
        }
        return false;
    }

    private Witness? FindPoint(int methodToken, SyntaxNode value, SyntaxNode declaration, string path)
    {
        step();
        var handle = MetadataTokens.MethodDefinitionHandle(methodToken & 0x00ffffff);
        if ((uint)methodToken >> 24 != 6 || metadata.GetMethodDefinition(handle).RelativeVirtualAddress == 0) return null;
        var debug = pdb.GetMethodDebugInformation(handle);
        var text = value.SyntaxTree.GetText(token);
        var interior = text.Lines.GetLinePositionSpan(value.Span);
        var legal = text.Lines.GetLinePositionSpan(declaration.Span);
        Witness? found = null;
        foreach (var point in debug.GetSequencePoints())
        {
            step();
            if (point.IsHidden) continue;
            var document = point.Document.IsNil ? debug.Document : point.Document;
            if (document.IsNil || !documentPaths.TryGetValue(document, out var actualPath) ||
                !StringComparer.Ordinal.Equals(actualPath, path) || !DocumentMatches(document, value.SyntaxTree)) continue;
            var start = new LinePosition(point.StartLine - 1, point.StartColumn - 1);
            var end = new LinePosition(point.EndLine - 1, point.EndColumn - 1);
            if (start.CompareTo(legal.Start) < 0 || end.CompareTo(legal.End) > 0 ||
                start.CompareTo(interior.Start) > 0 || end.CompareTo(interior.End) < 0 || start.CompareTo(end) >= 0) continue;
            if (found is not null) return null;
            admit(512, []);
            var hash = text.GetChecksum();
            found = new(point.Offset, point.StartLine, point.StartColumn, point.EndLine, point.EndColumn,
                text.ChecksumAlgorithm.ToString(), Convert.ToHexStringLower(hash.AsSpan()));
        }
        return found;
    }

    private bool DocumentMatches(DocumentHandle document, SyntaxTree tree)
    {
        step();
        if (checkedDocuments.TryGetValue((document, tree), out var previous)) return previous;
        var row = pdb.GetDocument(document);
        var algorithm = pdb.GetGuid(row.HashAlgorithm);
        var text = tree.GetText(token);
        var expectedAlgorithm = text.ChecksumAlgorithm switch
        {
            SourceHashAlgorithm.Sha1 => new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460"),
            SourceHashAlgorithm.Sha256 => new Guid("8829d00f-11b8-4213-878b-770e8597ac16"),
            _ => Guid.Empty
        };
        var blob = pdb.GetBlobReader(row.Hash);
        var length = text.ChecksumAlgorithm == SourceHashAlgorithm.Sha1 ? 20 : 32;
        admit(256 + 2L * length, []);
        var matches = expectedAlgorithm != Guid.Empty && algorithm == expectedAlgorithm && blob.Length == length &&
            blob.ReadBytes(length).AsSpan().SequenceEqual(text.GetChecksum().AsSpan());
        checkedDocuments.Add((document, tree), matches);
        return matches;
    }
}
