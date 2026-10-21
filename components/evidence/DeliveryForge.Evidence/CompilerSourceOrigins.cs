using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DeliveryForge.Evidence;

// Target-compiler DATA inventory, not a proof or an exemption. Every unresolved
// signature, constructor group and carrier remains an explicit obligation.
internal static class CompilerSourceOrigins
{
    private const int MaximumRows = 10000;
    private const int MaximumSteps = 1_000_000;
    private sealed record Point(int Method, string Path, int StartLine, int StartColumn, int EndLine, int EndColumn);
    private sealed record Candidate(int Method, int ContainedPoints, int AllMethodPoints, bool CompletePointSetContained);
    private sealed record ConstructorRole(string? DeclarationId, string Display, bool Implicit, bool DelegatesToThis);
    private sealed record Region(string Kind, string Path, int StartLine, int StartColumn, int EndLine, int EndColumn,
        string? DeclarationId, string SemanticDisplay, string ContainingType, bool? Static, bool IsAsync,
        bool EmptyBlock, string Obligation, IReadOnlyList<ConstructorRole> ConstructorGroup, IReadOnlyList<Candidate> Candidates);

    internal static byte[] Capture(CSharpCompilation compilation, byte[] assembly, byte[] pdb,
        Action check, CancellationToken token)
    {
        var steps = 0;
        void Step()
        {
            check(); token.ThrowIfCancellationRequested();
            if (++steps > MaximumSteps) throw new IOException("Semantic origin inventory exceeds the existing finite work profile.");
        }
        string Text(string value)
        {
            Step();
            if (value.Length > 16384) throw new IOException("Semantic origin identity exceeds the existing path/identity profile.");
            return value;
        }
        using var pe = new PEReader(new MemoryStream(assembly, writable: false));
        var metadata = pe.GetMetadataReader();
        using var symbols = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb, writable: false));
        var reader = symbols.GetMetadataReader();
        if (metadata.MethodDefinitions.Count > MaximumRows || reader.Documents.Count > MaximumRows)
            throw new IOException("Semantic origin inventory exceeds the bounded method/document profile.");
        var points = new List<Point>();
        var noVisibleIlBodies = new List<int>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            Step();
            var methodToken = MetadataTokens.GetToken(handle);
            var debug = reader.GetMethodDebugInformation(handle);
            var found = false;
            foreach (var point in debug.GetSequencePoints())
            {
                Step(); if (point.IsHidden) continue;
                var document = point.Document.IsNil ? debug.Document : point.Document;
                if (document.IsNil) throw new IOException("Visible semantic carrier has no PDB document.");
                var path = Text(reader.GetString(reader.GetDocument(document).Name).Replace('\\', '/'));
                points.Add(new(methodToken, path, point.StartLine, point.StartColumn, point.EndLine, point.EndColumn));
                found = true;
            }
            if (!found && metadata.GetMethodDefinition(handle).RelativeVirtualAddress != 0)
                noVisibleIlBodies.Add(methodToken);
        }
        var byPath = points.GroupBy(point => point.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var perMethod = points.GroupBy(point => point.Method).ToDictionary(group => group.Key, group => group.Count());
        var regions = new List<Region>();
        var hiding = new List<object>();
        var sourceRows = new List<object>();
        var expressionType = compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1");
        foreach (var tree in compilation.SyntaxTrees)
        {
            Step();
            if (sourceRows.Count >= MaximumRows) throw new IOException("Semantic source inventory exceeds the bounded source profile.");
            var root = tree.GetRoot(token);
            var text = tree.GetText(token);
            var model = compilation.GetSemanticModel(tree);
            var path = Text((compilation.Options.SourceReferenceResolver?.NormalizePath(tree.FilePath, baseFilePath: null)
                ?? tree.FilePath).Replace('\\', '/'));
            var before = regions.Count;
            var mappingDirectives = 0;
            foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
            {
                Step();
                if (trivia.RawKind is (int)SyntaxKind.LineDirectiveTrivia or (int)SyntaxKind.LineSpanDirectiveTrivia
                    or (int)SyntaxKind.PragmaChecksumDirectiveTrivia) mappingDirectives++;
            }
            void Add(string kind, SyntaxNode span, ISymbol? owner, string obligation, bool constructorGroup = false)
            {
                Step(); if (regions.Count >= MaximumRows) throw new IOException("Semantic runtime-region membership exceeds the bounded profile.");
                var position = text.Lines.GetLinePositionSpan(span.Span);
                var sl = position.Start.Line + 1; var sc = position.Start.Character + 1;
                var el = position.End.Line + 1; var ec = position.End.Character + 1;
                var counts = new Dictionary<int, int>();
                if (byPath.TryGetValue(path, out var rows))
                    foreach (var point in rows)
                    {
                        Step();
                        if ((point.StartLine > sl || point.StartLine == sl && point.StartColumn >= sc) &&
                            (point.EndLine < el || point.EndLine == el && point.EndColumn <= ec))
                            counts[point.Method] = counts.GetValueOrDefault(point.Method) + 1;
                    }
                var candidates = counts.OrderBy(pair => pair.Key)
                    .Select(pair => new Candidate(pair.Key, pair.Value, perMethod[pair.Key], pair.Value == perMethod[pair.Key])).ToArray();
                var constructors = new List<ConstructorRole>();
                if (constructorGroup && (owner?.ContainingType ?? owner as INamedTypeSymbol) is { } ownerType)
                    foreach (var constructor in owner?.IsStatic == true ? ownerType.StaticConstructors : ownerType.InstanceConstructors)
                    {
                        Step();
                        if (constructors.Count >= MaximumRows) throw new IOException("Constructor group exceeds the bounded method profile.");
                        var delegates = false;
                        foreach (var reference in constructor.DeclaringSyntaxReferences)
                        {
                            Step();
                            if (reference.GetSyntax(token) is ConstructorDeclarationSyntax
                                { Initializer.ThisOrBaseKeyword.RawKind: (int)SyntaxKind.ThisKeyword }) delegates = true;
                        }
                        constructors.Add(new(constructor.GetDocumentationCommentId() is { } constructorId ? Text(constructorId) : null,
                            Text(constructor.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)), constructor.IsImplicitlyDeclared, delegates));
                    }
                regions.Add(new(kind, path, sl, sc, el, ec,
                    owner?.GetDocumentationCommentId() is { } id ? Text(id) : null,
                    Text(owner?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty),
                    Text((owner?.ContainingType ?? owner as INamedTypeSymbol)?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty),
                    owner?.IsStatic, (owner as IMethodSymbol)?.IsAsync ?? false,
                    span is BlockSyntax { Statements.Count: 0 }, obligation, constructors, candidates));
            }
            void Attributes(ISymbol? symbol)
            {
                if (symbol is null) return;
                foreach (var attribute in symbol.GetAttributes())
                {
                    Step();
                    if (attribute.ApplicationSyntaxReference is not { } syntax || syntax.SyntaxTree != tree) continue;
                    var name = attribute.AttributeClass?.ToDisplayString();
                    if (name is not ("System.Diagnostics.DebuggerHiddenAttribute" or "System.Diagnostics.DebuggerStepThroughAttribute"
                        or "System.Diagnostics.DebuggerNonUserCodeAttribute" or "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute")) continue;
                    if (hiding.Count >= MaximumRows) throw new IOException("Authored hiding inventory exceeds the bounded profile.");
                    hiding.Add(new { path, name, start = syntax.Span.Start, length = syntax.Span.Length });
                }
            }
            Attributes(compilation.Assembly);
            foreach (var node in root.DescendantNodesAndSelf())
            {
                Step();
                switch (node)
                {
                    case BaseMethodDeclarationSyntax declaration:
                        var method = model.GetDeclaredSymbol(declaration, token) as IMethodSymbol;
                        Attributes(method);
                        var body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody?.Expression;
                        Add("named-method", body ?? declaration, method, body is null
                            ? "DeclarationOnly_NoAuthoredBody_ImplicitEffectsNotDischarged" : "NamedMetadataSignatureAndRuntimeCarrierUnresolved");
                        break;
                    case AccessorDeclarationSyntax accessor:
                        var accessMethod = model.GetDeclaredSymbol(accessor, token) as IMethodSymbol;
                        Attributes(accessMethod);
                        var accessBody = (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody?.Expression;
                        Add("accessor", accessBody ?? accessor, accessMethod, accessBody is null
                            ? "DeclarationOnly_AutoAccessorOriginUnresolved" : "NamedMetadataSignatureAndRuntimeCarrierUnresolved");
                        break;
                    case LocalFunctionStatementSyntax local:
                        var localMethod = model.GetDeclaredSymbol(local, token);
                        Attributes(localMethod);
                        Add("local-function", (SyntaxNode?)local.Body ?? (SyntaxNode?)local.ExpressionBody?.Expression ?? local,
                            localMethod, "NestedSemanticCarrierAndEnclosingOriginUnresolved");
                        break;
                    case AnonymousFunctionExpressionSyntax anonymous:
                        var anonymousMethod = (model.GetOperation(anonymous, token) as IAnonymousFunctionOperation)?.Symbol;
                        var converted = model.GetTypeInfo(anonymous, token).ConvertedType as INamedTypeSymbol;
                        var isExpressionTree = expressionType is not null && converted is not null &&
                            SymbolEqualityComparer.Default.Equals(converted.OriginalDefinition, expressionType);
                        SyntaxNode anonymousBody = anonymous switch
                        {
                            SimpleLambdaExpressionSyntax simple => simple.Body,
                            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.Body,
                            AnonymousMethodExpressionSyntax anonymousMethodSyntax => anonymousMethodSyntax.Block,
                            _ => anonymous
                        };
                        Attributes(anonymousMethod);
                        Add(isExpressionTree ? "expression-tree-lambda" : "delegate-or-unresolved-lambda", anonymousBody,
                            anonymousMethod, isExpressionTree ? "AuthoredDataRequiresOuterExecutionOrigin" : "NestedSemanticConversionAndCompleteCarrierUnresolved");
                        break;
                    case PropertyDeclarationSyntax property:
                        var propertySymbol = model.GetDeclaredSymbol(property, token);
                        Attributes(propertySymbol);
                        if (property.ExpressionBody is { } propertyBody)
                            Add("expression-property", propertyBody.Expression, propertySymbol?.GetMethod, "NamedMetadataSignatureAndRuntimeCarrierUnresolved");
                        if (property.Initializer is { } propertyInitializer)
                            Add("property-initializer", propertyInitializer.Value, propertySymbol, "AllApplicableConstructorGroupsUnresolved", constructorGroup: true);
                        break;
                    case IndexerDeclarationSyntax indexer:
                        var indexerSymbol = model.GetDeclaredSymbol(indexer, token);
                        Attributes(indexerSymbol);
                        if (indexer.ExpressionBody is { } indexerBody)
                            Add("expression-indexer", indexerBody.Expression, indexerSymbol?.GetMethod, "NamedMetadataSignatureAndRuntimeCarrierUnresolved");
                        break;
                    case VariableDeclaratorSyntax variable when variable.Parent?.Parent is BaseFieldDeclarationSyntax:
                        var field = model.GetDeclaredSymbol(variable, token);
                        Attributes(field);
                        if (variable.Initializer is { } initializer && field is not IFieldSymbol { IsConst: true })
                            Add(field?.IsStatic == true ? "static-field-initializer" : "instance-field-initializer",
                                initializer.Value, field, "AllApplicableConstructorGroupsUnresolved", constructorGroup: true);
                        break;
                    case ConstructorInitializerSyntax ctorInitializer:
                        Add("explicit-constructor-initializer", ctorInitializer,
                            ctorInitializer.Parent is ConstructorDeclarationSyntax ctor ? model.GetDeclaredSymbol(ctor, token) : null,
                            "ContainingConstructorAndBaseArgumentEffectsUnresolved");
                        break;
                    case PrimaryConstructorBaseTypeSyntax primary:
                        Add("primary-constructor-base", primary.ArgumentList,
                            primary.Parent?.Parent is TypeDeclarationSyntax primaryType ? model.GetDeclaredSymbol(primaryType, token) : null,
                            "PrimaryConstructorAndBaseArgumentEffectsUnresolved", constructorGroup: true);
                        break;
                    case BaseTypeDeclarationSyntax type:
                        Attributes(model.GetDeclaredSymbol(type, token));
                        break;
                    case GlobalStatementSyntax global:
                        Add("global-statement", global.Statement, compilation.GetEntryPoint(token), "TopLevelRuntimeOriginUnresolved");
                        break;
                }
            }
            sourceRows.Add(new { path, regions = regions.Count - before, mappingDirectives });
        }
        Step();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            provider = "target59-semantic-runtime-regions-v1", disposition = "inventory-only-no-origin-proof",
            sourceCount = sourceRows.Count, methodCount = metadata.MethodDefinitions.Count,
            visiblePointCount = points.Count, noVisibleIlBodyCount = noVisibleIlBodies.Count,
            noVisibleIlBodies, sources = sourceRows, authoredHiding = hiding, regions,
            unresolved = "Metadata signatures, full semantic ownership and constructor groups are not joined. Candidate spans never authorize an exemption."
        });
        if (bytes.Length > 128 * 1024 * 1024) throw new IOException("Semantic runtime inventory exceeds the existing artifact bound.");
        Step(); return bytes;
    }
}
