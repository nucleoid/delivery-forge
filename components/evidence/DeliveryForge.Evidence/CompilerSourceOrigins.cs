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
    private const long MaximumBytes = 128 * 1024 * 1024;
    private sealed record Point(int Method, string Path, int StartLine, int StartColumn, int EndLine, int EndColumn);
    private sealed record Candidate(int Method, int ContainedPoints, int AllMethodPoints, bool CompletePointSetContained);
    private sealed record ConstructorRole(string? DeclarationId, string Display, bool Implicit, bool DelegatesToThis,
        CompilerNamedMethodBindings.Binding NamedMethodBinding);
    private sealed record Region(string Kind, string Path, int StartLine, int StartColumn, int EndLine, int EndColumn,
        string? DeclarationId, string SemanticDisplay, string ContainingType, bool? Static, bool IsAsync,
        bool EmptyBlock, string Obligation, CompilerNamedMethodBindings.Binding NamedMethodBinding,
        IReadOnlyList<ConstructorRole> ConstructorGroup, IReadOnlyList<Candidate> Candidates,
        int? ParentRegion, bool OwnYield, NestedCarrier? NestedCarrier,
        string SourceForm, RegionAssociation? OriginAssociation)
    {
        public CompilerInitializerOrigins.Attribution? InitializerOrigin { get; init; }
        public CompilerInitializerOrigins.ConstructorArguments? ConstructorArgumentOrigin { get; init; }
    }
    private sealed record RegionAssociation(bool Associated, string Disposition, int? CarrierMethodToken);
    private sealed record DeclarationAssociation(string Kind, string Path, int Start, bool Associated, string Disposition);

    internal static byte[] Capture(CSharpCompilation compilation, byte[] assembly, byte[] pdb,
        Action check, CancellationToken token)
    {
        var steps = 0;
        long admittedBytes = 2048;
        void Step()
        {
            check(); token.ThrowIfCancellationRequested();
            if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
                throw new CompilerRecipeUnsupportedException("Semantic origin recursion exceeds the available execution stack.");
            if (++steps > MaximumSteps) throw new CompilerRecipeUnsupportedException("Semantic origin inventory exceeds the existing finite work profile.");
        }
        // Charge before retaining rows or allocating their arrays. Eight bytes per
        // UTF-16 unit covers worst-case JSON escaping, with conservative row space.
        // Repeated serialized identities are charged even when strings are shared.
        void Admit(long rowBytes, params string?[] identities)
        {
            Step();
            foreach (var identity in identities)
                rowBytes = checked(rowBytes + 32L + 8L * (identity?.Length ?? 0));
            admittedBytes = checked(admittedBytes + rowBytes);
            if (admittedBytes > MaximumBytes)
                throw new CompilerRecipeUnsupportedException("Semantic runtime inventory exceeds the existing artifact bound.");
        }
        string Text(string value)
        {
            Step();
            if (value.Length > 16384) throw new CompilerRecipeUnsupportedException("Semantic origin identity exceeds the existing path/identity profile.");
            Admit(32, value);
            return value;
        }
        using var pe = new PEReader(new MemoryStream(assembly, writable: false));
        var metadata = pe.GetMetadataReader();
        using var symbols = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb, writable: false));
        var reader = symbols.GetMetadataReader();
        if (metadata.MethodDefinitions.Count > MaximumRows || reader.Documents.Count > MaximumRows)
            throw new CompilerRecipeUnsupportedException("Semantic origin inventory exceeds the bounded method/document profile.");
        var namedBindings = CompilerNamedMethodBindings.Capture(compilation, metadata, reader, Step, Admit);
        var points = new List<Point>();
        var documentPaths = new Dictionary<DocumentHandle, string>();
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
                if (!documentPaths.TryGetValue(document, out var path))
                {
                    path = Text(reader.GetString(reader.GetDocument(document).Name).Replace('\\', '/'));
                    Admit(128);
                    documentPaths.Add(document, path);
                }
                Admit(256);
                points.Add(new(methodToken, path, point.StartLine, point.StartColumn, point.EndLine, point.EndColumn));
                found = true;
            }
            if (!found && metadata.GetMethodDefinition(handle).RelativeVirtualAddress != 0)
            {
                Admit(32);
                noVisibleIlBodies.Add(methodToken);
            }
        }
        var initializerOrigins = new CompilerInitializerOrigins(compilation, metadata, reader, documentPaths,
            namedBindings, Step, Admit, token);
        var byPath = points.GroupBy(point => point.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var perMethod = points.GroupBy(point => point.Method).ToDictionary(group => group.Key, group => group.Count());
        var regions = new List<Region>();
        var hiding = new List<object>();
        var sourceRows = new List<object>();
        var declarations = new List<DeclarationAssociation>();
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        void Unsupported(string code)
        {
            Step();
            if (unresolved.Contains(code)) return;
            Admit(128, code); unresolved.Add(code);
        }
        var expressionType = compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1");
        foreach (var tree in compilation.SyntaxTrees)
        {
            Step();
            if (sourceRows.Count >= MaximumRows) throw new CompilerRecipeUnsupportedException("Semantic source inventory exceeds the bounded source profile.");
            var root = tree.GetRoot(token);
            var text = tree.GetText(token);
            var model = compilation.GetSemanticModel(tree);
            var path = Text((compilation.Options.SourceReferenceResolver?.NormalizePath(tree.FilePath, baseFilePath: null)
                ?? tree.FilePath).Replace('\\', '/'));
            var before = regions.Count;
            var regionSpans = new Dictionary<SyntaxNode, int>();
            var mappingDirectives = 0;
            foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
            {
                Step();
                if (trivia.RawKind is (int)SyntaxKind.LineDirectiveTrivia or (int)SyntaxKind.LineSpanDirectiveTrivia
                    or (int)SyntaxKind.PragmaChecksumDirectiveTrivia)
                { mappingDirectives++; Unsupported("source.line-remapping-unsupported"); }
            }
            void Add(string kind, SyntaxNode span, ISymbol? owner, string obligation, bool constructorGroup = false)
            {
                Step(); if (regions.Count >= MaximumRows) throw new CompilerRecipeUnsupportedException("Semantic runtime-region membership exceeds the bounded profile.");
                Admit(1024, kind, path, obligation);
                int? parentRegion = null;
                for (var ancestor = span.Parent; ancestor is not null; ancestor = ancestor.Parent)
                {
                    Step();
                    if (regionSpans.TryGetValue(ancestor, out var parentIndex)) { parentRegion = parentIndex; break; }
                }
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
                        {
                            if (!counts.ContainsKey(point.Method)) Admit(128);
                            counts[point.Method] = counts.GetValueOrDefault(point.Method) + 1;
                        }
                    }
                Admit(256L * counts.Count);
                var candidates = counts.OrderBy(pair => pair.Key)
                    .Select(pair => new Candidate(pair.Key, pair.Value, perMethod[pair.Key], pair.Value == perMethod[pair.Key])).ToArray();
                var constructors = new List<ConstructorRole>();
                var ownerType = owner as INamedTypeSymbol ?? owner?.ContainingType;
                if (constructorGroup && ownerType is not null)
                    foreach (var constructor in owner?.IsStatic == true ? ownerType.StaticConstructors : ownerType.InstanceConstructors)
                    {
                        Step();
                        if (constructors.Count >= MaximumRows) throw new CompilerRecipeUnsupportedException("Constructor group exceeds the bounded method profile.");
                        Admit(128);
                        var delegates = false;
                        foreach (var reference in constructor.DeclaringSyntaxReferences)
                        {
                            Step();
                            if (reference.GetSyntax(token) is ConstructorDeclarationSyntax
                                { Initializer.ThisOrBaseKeyword.RawKind: (int)SyntaxKind.ThisKeyword }) delegates = true;
                        }
                        constructors.Add(new(constructor.GetDocumentationCommentId() is { } constructorId ? Text(constructorId) : null,
                            Text(constructor.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)), constructor.IsImplicitlyDeclared, delegates,
                            NamedBinding(constructor)));
                    }
                CompilerInitializerOrigins.Attribution? initializer = null;
                if (constructorGroup && (owner is IFieldSymbol or IPropertySymbol))
                    initializer = initializerOrigins.Capture(span, owner, path);
                var arguments = kind is "explicit-constructor-initializer" or "primary-constructor-base"
                    ? initializerOrigins.CaptureArguments(span, path) : null;
                regions.Add(new(kind, path, sl, sc, el, ec,
                    owner?.GetDocumentationCommentId() is { } id ? Text(id) : null,
                    Text(owner?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty),
                    Text(ownerType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty),
                    owner?.IsStatic, (owner as IMethodSymbol)?.IsAsync ?? false,
                    span is BlockSyntax { Statements.Count: 0 }, obligation, NamedBinding(owner as IMethodSymbol), constructors, candidates,
                    parentRegion, HasOwnYield(span, Step), null, SourceForm(kind, span, owner), null)
                { InitializerOrigin = initializer, ConstructorArgumentOrigin = arguments });
                Admit(128);
                if (!regionSpans.TryAdd(span, regions.Count - 1))
                    throw new IOException("Semantic source ownership has a duplicate runtime span.");
            }
            CompilerNamedMethodBindings.Binding NamedBinding(IMethodSymbol? method)
            {
                var binding = namedBindings.For(method);
                Admit(256L + 32L * binding.PdbStateMachineCarriers.Count, binding.Disposition);
                return binding;
            }
            void Attribute(AttributeSyntax syntax)
            {
                Step();
                // Bind the actual constructor on EVERY source AttributeSyntax.
                // This includes aliases, all attribute targets, record parameters
                // and captured generated trees; compiler-inserted metadata has no
                // source syntax and cannot take this source-authored route.
                if (model.GetSymbolInfo(syntax, token).Symbol is not IMethodSymbol constructor) return;
                var name = constructor.ContainingType.ToDisplayString();
                if (name is not ("System.Diagnostics.DebuggerHiddenAttribute" or "System.Diagnostics.DebuggerStepThroughAttribute"
                    or "System.Diagnostics.DebuggerNonUserCodeAttribute" or "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute")) return;
                if (hiding.Count >= MaximumRows) throw new CompilerRecipeUnsupportedException("Authored hiding inventory exceeds the bounded profile.");
                Admit(512, path, name);
                hiding.Add(new { path, name, start = syntax.Span.Start, length = syntax.Span.Length });
                Unsupported("source.authored-hiding-unsupported");
            }
            void Declaration(string kind, SyntaxNode syntax, bool associated, string disposition)
            {
                Step();
                if (declarations.Count >= MaximumRows) throw new CompilerRecipeUnsupportedException("Source declaration semantics exceed the bounded profile.");
                Admit(512, kind, path, disposition);
                declarations.Add(new(kind, path, syntax.Span.Start, associated, disposition));
                if (!associated) Unsupported(disposition);
            }
            foreach (var node in root.DescendantNodesAndSelf())
            {
                Step();
                // Partial type declarations remain supported. Partial executable
                // member families need a separate positive implementation join.
                var partialMember = node switch
                {
                    BaseMethodDeclarationSyntax member => member.Modifiers.Any(SyntaxKind.PartialKeyword),
                    BasePropertyDeclarationSyntax member => member.Modifiers.Any(SyntaxKind.PartialKeyword),
                    EventFieldDeclarationSyntax member => member.Modifiers.Any(SyntaxKind.PartialKeyword),
                    _ => false
                };
                if (partialMember) Unsupported("source.partial-member-unsupported");
                switch (node)
                {
                    case AttributeSyntax attribute: Attribute(attribute); break;
                    case QueryExpressionSyntax: Unsupported("source.query-unsupported"); break;
                    case YieldStatementSyntax: Unsupported("source.iterator-unsupported"); break;
                    case BaseMethodDeclarationSyntax declaration:
                        var method = model.GetDeclaredSymbol(declaration, token) as IMethodSymbol;
                        var body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody?.Expression;
                        Add("named-method", body ?? declaration, method, body is null
                            ? "DeclarationOnly_NoAuthoredBody_ImplicitEffectsNotDischarged" : "NamedMetadataSignatureAndRuntimeCarrierUnresolved");
                        break;
                    case AccessorDeclarationSyntax accessor:
                        var accessMethod = model.GetDeclaredSymbol(accessor, token) as IMethodSymbol;
                        var accessBody = (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody?.Expression;
                        Add("accessor", accessBody ?? accessor, accessMethod, accessBody is null
                            ? "DeclarationOnly_AutoAccessorOriginUnresolved" : "NamedMetadataSignatureAndRuntimeCarrierUnresolved");
                        break;
                    case LocalFunctionStatementSyntax local:
                        var localMethod = model.GetDeclaredSymbol(local, token);
                        Add(localMethod is null ? "unresolved-local-function" : "local-function", (SyntaxNode?)local.Body ?? (SyntaxNode?)local.ExpressionBody?.Expression ?? local,
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
                        var isDelegate = converted?.TypeKind == TypeKind.Delegate && anonymousMethod is not null;
                        if (isExpressionTree) Unsupported("source.expression-tree-unsupported");
                        else if (!isDelegate) Unsupported("source.lambda-conversion-unresolved");
                        Add(isExpressionTree ? "expression-tree-lambda" : isDelegate ? "delegate-lambda" : "unresolved-lambda-conversion", anonymousBody,
                            anonymousMethod, isExpressionTree ? "AuthoredDataRequiresOuterExecutionOrigin" : "NestedSemanticConversionAndCompleteCarrierUnresolved");
                        break;
                    case PropertyDeclarationSyntax property:
                        var propertySymbol = model.GetDeclaredSymbol(property, token);
                        if (property.ExpressionBody is { } propertyBody)
                            Add("expression-property", propertyBody.Expression, propertySymbol?.GetMethod, "NamedMetadataSignatureAndRuntimeCarrierUnresolved");
                        if (property.Initializer is { } propertyInitializer)
                            Add("property-initializer", propertyInitializer.Value, propertySymbol, "AllApplicableConstructorGroupsUnresolved", constructorGroup: true);
                        break;
                    case IndexerDeclarationSyntax indexer:
                        var indexerSymbol = model.GetDeclaredSymbol(indexer, token);
                        if (indexer.ExpressionBody is { } indexerBody)
                            Add("expression-indexer", indexerBody.Expression, indexerSymbol?.GetMethod, "NamedMetadataSignatureAndRuntimeCarrierUnresolved");
                        break;
                    case VariableDeclaratorSyntax variable when variable.Parent?.Parent is BaseFieldDeclarationSyntax:
                        var field = model.GetDeclaredSymbol(variable, token);
                        if (variable.Parent?.Parent is EventFieldDeclarationSyntax)
                        {
                            var eventSymbol = field as IEventSymbol;
                            var add = eventSymbol?.AddMethod is { } addMethod ? namedBindings.For(addMethod).MethodToken : null;
                            var remove = eventSymbol?.RemoveMethod is { } removeMethod ? namedBindings.For(removeMethod).MethodToken : null;
                            Declaration("event-field-compiler-template", variable, eventSymbol is not null && add is not null && remove is not null,
                                eventSymbol is not null && add is not null && remove is not null
                                    ? "event-field-exact-accessor-symbols-initializer-separate" : "source.event-field-binding-unproven");
                        }
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
                        if (model.GetDeclaredSymbol(type, token) is not INamedTypeSymbol)
                            Unsupported("source.type-declaration-binding-unproven");
                        break;
                    case ParameterSyntax parameter when parameter.Parent?.Parent is TypeDeclarationSyntax primaryDeclaration &&
                        ReferenceEquals(primaryDeclaration.ParameterList, parameter.Parent):
                        var primaryParameter = model.GetDeclaredSymbol(parameter, token) as IParameterSymbol;
                        var primaryConstructor = primaryParameter?.ContainingSymbol as IMethodSymbol;
                        var primaryBinding = primaryConstructor is not null ? namedBindings.For(primaryConstructor).MethodToken : null;
                        var primaryAssociated = primaryParameter is not null && primaryConstructor is { MethodKind: MethodKind.Constructor } &&
                            primaryBinding is not null &&
                            metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(primaryBinding.Value & 0x00ffffff)).RelativeVirtualAddress != 0 &&
                            SymbolEqualityComparer.Default.Equals(primaryConstructor.ContainingType,
                                model.GetDeclaredSymbol(primaryDeclaration, token));
                        Declaration("primary-parameter-compiler-template", parameter, primaryAssociated,
                            primaryAssociated ? "primary-parameter-exact-constructor-symbol-initializers-base-separate" : "source.primary-parameter-binding-unproven");
                        break;
                    case GlobalStatementSyntax global:
                        Add("global-statement", global.Statement, compilation.GetEntryPoint(token), "TopLevelRuntimeOriginUnresolved");
                        break;
                }
            }
            Admit(256, path);
            sourceRows.Add(new { path, regions = regions.Count - before, mappingDirectives });
        }
        BindNestedCarriers(regions, namedBindings, metadata, reader, Step, Admit);
        var pendingInitializerCount = CloseAuthoredRegions(regions, metadata, Unsupported, Step, Admit);
        Step();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            provider = "target59-semantic-runtime-regions-v1",
            disposition = "inventory-only-no-origin-proof",
            sourceCount = sourceRows.Count,
            methodCount = metadata.MethodDefinitions.Count,
            visiblePointCount = points.Count,
            noVisibleIlBodyCount = noVisibleIlBodies.Count,
            noVisibleIlBodies,
            sources = sourceRows,
            authoredHiding = hiding,
            regions,
            declarationAssociations = declarations,
            sourceExhaustion = new { complete = unresolved.Count == 0, unresolved = unresolved.Order(StringComparer.Ordinal), pendingInitializerCount },
            unresolved = "Every supported region has an explicit positive or unresolved association. Source exhaustion is conditional on trusted exact PE/PDB equality; pending initializer and source-form guards remain blocking. No coverage point or hit is fabricated."
        });
        if (bytes.Length > MaximumBytes) throw new CompilerRecipeUnsupportedException("Semantic runtime inventory exceeds the existing artifact bound.");
        Step(); return bytes;
    }

    private static string SourceForm(string kind, SyntaxNode span, ISymbol? owner)
    {
        if (kind is "static-field-initializer" or "instance-field-initializer" or "property-initializer" or
            "explicit-constructor-initializer" or "primary-constructor-base") return "initializer-pending";
        if (kind is "delegate-lambda" or "local-function") return "nested-runtime-body";
        if (kind is not ("named-method" or "accessor" or "expression-property" or "expression-indexer") ||
            owner is not IMethodSymbol method) return "unsupported-source-form";
        if (method.IsPartialDefinition || method.PartialDefinitionPart is not null || method.PartialImplementationPart is not null)
            return "unsupported-partial-member";
        if (span is BlockSyntax or ExpressionSyntax) return "authored-runtime-body";
        if (method.IsAbstract || method.IsExtern) return "semantic-nonbody-declaration";
        if (span is AccessorDeclarationSyntax accessor && !accessor.SemicolonToken.IsMissing &&
            accessor.Parent?.Parent is PropertyDeclarationSyntax property && property.AccessorList is { } accessors &&
            method.AssociatedSymbol is IPropertySymbol &&
            accessors.Accessors.All(item => item.Body is null && item.ExpressionBody is null && !item.SemicolonToken.IsMissing))
            return "auto-property-accessor-template";
        return "unsupported-source-form";
    }

    private static int CloseAuthoredRegions(List<Region> regions, MetadataReader metadata, Action<string> unsupported,
        Action step, Action<long, string?[]> admit)
    {
        var pendingInitializers = 0;
        for (var index = 0; index < regions.Count; index++)
        {
            step();
            var region = regions[index];
            var associated = false;
            int? carrier = null;
            var disposition = "source.region-form-unproven";
            if (region.OwnYield) disposition = "source.iterator-unsupported";
            else switch (region.SourceForm)
                {
                    case "authored-runtime-body":
                    case "auto-property-accessor-template":
                        var named = region.NamedMethodBinding;
                        if (named.MethodToken is { } token)
                        {
                            if (region.IsAsync && named.PdbStateMachineCarriers.Count == 1)
                                carrier = named.PdbStateMachineCarriers[0];
                            else if (!region.IsAsync && named.PdbStateMachineCarriers.Count == 0) carrier = token;
                        }
                        // An arbitrary point is insufficient: the semantic named
                        // declaration, exact CLI token, real PDB async relation and
                        // a point wholly in THIS owned body must all agree. Child
                        // functions/initializer effects remain separate obligations.
                        if (carrier is { } actual &&
                            metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(actual & 0x00ffffff)).RelativeVirtualAddress != 0)
                            foreach (var candidate in region.Candidates)
                            {
                                step();
                                if (candidate.Method == actual && candidate.ContainedPoints > 0) associated = true;
                            }
                        disposition = associated
                            ? region.SourceForm == "authored-runtime-body" ? "semantic-named-body-own-carrier-point" : "semantic-auto-accessor-own-carrier-point"
                            : region.SourceForm == "authored-runtime-body" ? "source.named-body-point-unproven" : "source.auto-accessor-point-unproven";
                        break;
                    case "semantic-nonbody-declaration":
                        associated = region.NamedMethodBinding.MethodToken is { } declaration &&
                            metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(declaration & 0x00ffffff)).RelativeVirtualAddress == 0;
                        disposition = associated ? "semantic-abstract-or-extern-exact-rva0-declaration" : "source.declaration-nonbody-unproven";
                        break;
                    case "nested-runtime-body":
                        associated = region.NestedCarrier is not null;
                        carrier = region.NestedCarrier?.MethodToken;
                        disposition = associated ? "semantic-nested-exclusive-complete-carrier" : "source.nested-carrier-unproven";
                        break;
                    case "initializer-pending":
                        // These are the same Capture's private computed witnesses,
                        // never a serialized status or caller-supplied proof boolean.
                        associated = region.Kind is "explicit-constructor-initializer" or "primary-constructor-base"
                            ? region.ConstructorArgumentOrigin?.Complete == true : region.InitializerOrigin?.Complete == true;
                        if (!associated) pendingInitializers++;
                        disposition = associated ? "semantic-applicable-constructor-source-point" : "source.initializer-pending";
                        break;
                    case "unsupported-partial-member":
                        disposition = "source.partial-member-unsupported";
                        break;
                }
            admit(256, [disposition]);
            regions[index] = region with { OriginAssociation = new(associated, disposition, carrier) };
            if (!associated) unsupported(disposition);
        }
        return pendingInitializers;
    }

    private sealed record NestedCarrier(int MethodToken, int? KickoffMethodToken, string Disposition);

    // Called from Add with its exact owned runtime span and shared Step. Nested
    // function syntax is a separate boundary even when it is the span's root.
    private static bool HasOwnYield(SyntaxNode span, Action step)
    {
        foreach (var node in span.DescendantNodesAndSelf(descendIntoChildren: node =>
            node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            step();
            if (node is YieldStatementSyntax) return true;
        }
        return false;
    }

    private static void BindNestedCarriers(List<Region> regions,
        CompilerNamedMethodBindings.Table namedBindings, MetadataReader metadata, MetadataReader pdb,
        Action step, Action<long, string?[]> admit)
    {
        static bool CanBind(Region region) => region.Kind is "delegate-lambda" or "local-function";
        static bool IsNestedBoundary(Region region) => region.Kind is "delegate-lambda" or "local-function" or
            "expression-tree-lambda" or "unresolved-lambda-conversion" or "delegate-or-unresolved-lambda" or
            "unresolved-local-function";
        var descendantCandidates = new Dictionary<int, HashSet<int>>();
        var selected = new Dictionary<int, (int Carrier, int? Kickoff)>();
        var assignments = new Dictionary<int, int>();

        // ParentRegion MUST come from exact SyntaxNode ancestry recorded by Add,
        // not a line-span containment guess. A descendant's full candidates cannot
        // be borrowed by its outer region even when that descendant stays unresolved.
        for (var index = 0; index < regions.Count; index++)
        {
            step();
            var region = regions[index];
            // Unsupported children still reserve their complete candidate carriers.
            // Eligibility to bind is deliberately narrower than source ownership.
            if (!IsNestedBoundary(region)) continue;
            foreach (var candidate in region.Candidates)
            {
                step();
                if (!candidate.CompletePointSetContained || candidate.AllMethodPoints <= 0 ||
                    candidate.ContainedPoints != candidate.AllMethodPoints) continue;
                var child = index;
                for (var parent = region.ParentRegion; parent is { } ancestor; parent = regions[ancestor].ParentRegion)
                {
                    step();
                    if (ancestor < 0 || ancestor >= child)
                        throw new IOException("Semantic source ownership has an invalid parent relation.");
                    child = ancestor;
                    if (!IsNestedBoundary(regions[ancestor])) continue;
                    if (!descendantCandidates.TryGetValue(ancestor, out var tokens))
                    {
                        admit(128, []);
                        descendantCandidates.Add(ancestor, tokens = []);
                    }
                    if (!tokens.Contains(candidate.Method))
                    {
                        admit(64, []);
                        tokens.Add(candidate.Method);
                    }
                }
            }
        }

        for (var index = 0; index < regions.Count; index++)
        {
            step();
            var region = regions[index];
            if (!CanBind(region) || region.OwnYield || region.ParentRegion is null) continue;
            var supportedParent = true;
            var previous = index;
            for (var parent = region.ParentRegion; parent is { } ancestor; parent = regions[ancestor].ParentRegion)
            {
                step();
                if (ancestor < 0 || ancestor >= previous)
                    throw new IOException("Semantic source ownership has an invalid parent relation.");
                previous = ancestor;
                var outer = regions[ancestor];
                if (outer.OwnYield || (outer.Kind is "expression-tree-lambda" or "unresolved-lambda-conversion" or
                    "delegate-or-unresolved-lambda" or "unresolved-local-function") ||
                    (outer.Kind is "named-method" or "accessor" or "expression-property" or "expression-indexer") &&
                    outer.NamedMethodBinding.MethodToken is null)
                {
                    supportedParent = false;
                    break;
                }
            }
            if (!supportedParent) continue;
            int? only = null;
            var count = 0;
            foreach (var candidate in region.Candidates)
            {
                step();
                if (!candidate.CompletePointSetContained || candidate.AllMethodPoints <= 0 ||
                    candidate.ContainedPoints != candidate.AllMethodPoints ||
                    namedBindings.IsNamedCarrier(candidate.Method) ||
                    descendantCandidates.TryGetValue(index, out var descendants) && descendants.Contains(candidate.Method)) continue;
                only = candidate.Method;
                count++;
            }
            // No greedy first match. Missing/overlapping/ambiguous stays unsupported.
            if (count != 1 || only is not { } carrier) continue;
            if ((carrier & unchecked((int)0xff000000)) != 0x06000000)
                throw new BadImageFormatException("Nested carrier is not an exact MethodDef token.");
            var handle = MetadataTokens.MethodDefinitionHandle(carrier & 0x00ffffff);
            if (metadata.GetMethodDefinition(handle).RelativeVirtualAddress == 0) continue;
            var kickoff = pdb.GetMethodDebugInformation(handle).GetStateMachineKickoffMethod();
            // This slice supports ordinary synchronous functions and async state
            // machines. Iterator semantics remain a separate unsupported obligation.
            if (region.IsAsync == kickoff.IsNil) continue;
            int? kickoffToken = kickoff.IsNil ? null : MetadataTokens.GetToken(kickoff);
            if (kickoffToken is { } actualKickoff && namedBindings.IsNamedCarrier(actualKickoff)) continue;
            admit(256, []);
            selected.Add(index, (carrier, kickoffToken));
            if (!assignments.ContainsKey(carrier)) admit(128, []);
            assignments[carrier] = assignments.GetValueOrDefault(carrier) + 1;
        }
        foreach (var pair in selected)
        {
            step();
            // One carrier cannot be the positive witness for two source functions.
            if (assignments[pair.Value.Carrier] != 1) continue;
            const string disposition = "unique-owned-complete-point-set-target59";
            admit(256, [disposition]);
            regions[pair.Key] = regions[pair.Key] with
            {
                NestedCarrier = new(pair.Value.Carrier, pair.Value.Kickoff, disposition)
            };
        }
    }
}
