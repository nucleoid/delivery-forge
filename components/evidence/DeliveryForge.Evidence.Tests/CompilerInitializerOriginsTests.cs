using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace DeliveryForge.Evidence.Tests;

public sealed class CompilerInitializerOriginsTests
{
    private const string Attributed = "all-applicable-initializer-origins-conditional-on-exact-reemit";

    [Theory]
    [InlineData("public class C { public int Value = 7; public C() {} public C(int x) {} public C(string x) : this() {} }")]
    [InlineData("public class C { public int Value { get; } = 7; public C() {} public C(int x) {} public C(string x) : this() {} }")]
    public void Genuine_field_or_associated_property_has_every_entry_and_this_route(string source)
    {
        using var fixture = new Fixture(source);
        var result = fixture.Read();
        Assert.Equal(Attributed, result.Disposition);
        Assert.NotNull(result.FieldToken);
        Assert.Equal(3, result.Constructors.Count);
        Assert.Equal(2, result.Constructors.Count(route => route.Point is not null));
        var delegated = Assert.Single(result.Constructors, route => route.DelegatesToToken is not null);
        Assert.Contains(result.Constructors, route => route.ConstructorToken == delegated.DelegatesToToken &&
            route.Point is not null && route.ConstructorToken == delegated.TerminalToken);
    }

    [Theory]
    [InlineData("public record C { public int Value { get; } = 7; }")]
    [InlineData("public record C { public int Value { get; } = 7; public C() {} protected C(C other) {} }")]
    public void Genuine_record_class_copy_relation_skips_only_its_initializer_route(string source)
    {
        using var fixture = new Fixture(source);
        var result = fixture.Read();
        Assert.Equal(Attributed, result.Disposition);
        Assert.Single(result.Constructors, route => route.Point is not null);
        var copy = Assert.Single(result.Constructors, route =>
            route.Disposition == "semantic-record-class-copy-skips-initializers");
        Assert.Null(copy.Point);
        Assert.NotNull(copy.ConstructorToken);
        Assert.Equal(copy.ConstructorToken, copy.TerminalToken);
    }

    [Fact]
    public void Ordinary_class_self_parameter_is_not_a_record_copy_exclusion()
    {
        using var fixture = new Fixture("public class C { public int Value = 7; public C() {} public C(C other) {} }");
        var result = fixture.Read();
        Assert.Equal(Attributed, result.Disposition);
        Assert.Equal(2, result.Constructors.Count);
        Assert.All(result.Constructors, route => Assert.NotNull(route.Point));
    }

    [Fact]
    public void Static_initializer_has_its_exact_static_constructor_point()
    {
        using var fixture = new Fixture("public class C { public static int Value = 7; }");
        var result = fixture.Read();
        Assert.Equal(Attributed, result.Disposition);
        Assert.NotNull(Assert.Single(result.Constructors).Point);
    }

    [Theory]
    [InlineData("#line 20 \"Other.cs\"\npublic class C { public int Value = 7; }")]
    [InlineData("[System.Diagnostics.DebuggerStepThrough] public class C { public int Value = 7; }")]
    [InlineData("public class C { public int Value = 7; [System.Diagnostics.DebuggerHidden] public C() {} }")]
    [InlineData("public struct C { public int Value = 7; public C() {} }")]
    public void Genuine_mapping_hiding_and_struct_profiles_remain_unresolved(string source)
    {
        using var fixture = new Fixture(source);
        Assert.NotEqual(Attributed, fixture.Read().Disposition);
    }

    [Fact]
    public void Missing_exact_document_binding_cannot_borrow_another_path()
    {
        using var fixture = new Fixture("public class C { public int Value = 7; }");
        Assert.Equal(Attributed, fixture.Read().Disposition);
        Assert.NotEqual(Attributed, fixture.Read(wrongDocumentPath: true).Disposition);
    }

    [Fact]
    public void Changed_source_bytes_with_same_field_and_constructor_signatures_do_not_match_pdb_checksum()
    {
        using var fixture = new Fixture("public class C { public int Value = 7; }");
        Assert.Equal(Attributed, fixture.Read().Disposition);
        Assert.NotEqual(Attributed, fixture.Read(replacementSource: "public class C { public int Value = 8; }").Disposition);
    }

    [Fact]
    public void Compiler_semantic_capture_is_wired_but_does_not_claim_execution_or_exemptions()
    {
        using var fixture = new Fixture("public class C { public int Value = 7; }");
        using var json = System.Text.Json.JsonDocument.Parse(CompilerSourceOrigins.Capture(fixture.Compilation,
            fixture.Pe, fixture.Pdb, fixture.Check, TestContext.Current.CancellationToken));
        var region = Assert.Single(json.RootElement.GetProperty("regions").EnumerateArray(),
            row => row.GetProperty("Kind").GetString() == "instance-field-initializer");
        Assert.Equal(Attributed, region.GetProperty("InitializerOrigin").GetProperty("Disposition").GetString());
        Assert.Equal("inventory-only-no-origin-proof", json.RootElement.GetProperty("disposition").GetString());
    }

    [Theory]
    [InlineData("public class B { public B(int x) {} } public class C : B { public C(int x) : base(x + 1) {} }")]
    [InlineData("public class B { public B(int x) {} } public class C(int x) : B(x + 1) { }")]
    [InlineData("public class C(int x) : System.Exception(x.ToString()) { }")]
    [InlineData("public class C { public C() {} public C(int x) : this() {} }")]
    public void Genuine_explicit_this_or_base_and_primary_arguments_bind_the_caller_and_exact_target(string source)
    {
        using var fixture = new Fixture(source);
        var arguments = fixture.ReadArguments();
        Assert.Equal("constructor-argument-origin-conditional-on-exact-reemit", arguments.Disposition);
        Assert.NotNull(arguments.CallerToken);
        Assert.NotNull(arguments.TargetToken);
        Assert.NotNull(arguments.Point);
        Assert.Equal("C", fixture.ConstructorType(arguments.CallerToken!.Value));
        Assert.NotEqual(arguments.CallerToken, arguments.TargetToken);
    }

    [Fact]
    public void This_arguments_are_attributed_to_caller_not_the_delegated_destination()
    {
        using var fixture = new Fixture("public class C { public C() {} public C(int x) : this() {} }");
        var arguments = fixture.ReadArguments();
        Assert.NotEqual(arguments.CallerToken, arguments.TargetToken);
        Assert.Equal(1, fixture.ConstructorParameterRows(arguments.CallerToken!.Value));
        Assert.Equal(0, fixture.ConstructorParameterRows(arguments.TargetToken!.Value));
    }

    [Fact]
    public void Nested_primary_base_inventory_describes_the_inner_constructor_and_type()
    {
        using var fixture = new Fixture("public class B { public B(int x) {} } public class Outer { public Outer(string ignored) {} public class Inner(int value) : B(value + 1) {} }");
        using var json = System.Text.Json.JsonDocument.Parse(CompilerSourceOrigins.Capture(fixture.Compilation,
            fixture.Pe, fixture.Pdb, fixture.Check, TestContext.Current.CancellationToken));
        var region = Assert.Single(json.RootElement.GetProperty("regions").EnumerateArray(),
            row => row.GetProperty("Kind").GetString() == "primary-constructor-base");
        var inner = fixture.Compilation.GetTypeByMetadataName("Outer+Inner")!;
        var constructor = Assert.Single(inner.InstanceConstructors);
        Assert.Equal(inner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), region.GetProperty("ContainingType").GetString());
        var group = Assert.Single(region.GetProperty("ConstructorGroup").EnumerateArray());
        Assert.Equal(constructor.GetDocumentationCommentId(), group.GetProperty("DeclarationId").GetString());
        var arguments = region.GetProperty("ConstructorArgumentOrigin");
        Assert.Equal("constructor-argument-origin-conditional-on-exact-reemit", arguments.GetProperty("Disposition").GetString());
        var caller = arguments.GetProperty("CallerToken").GetInt32();
        Assert.Equal("Inner", fixture.ConstructorType(caller));
        Assert.Equal(caller, group.GetProperty("NamedMethodBinding").GetProperty("MethodToken").GetInt32());
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly CSharpCompilation Compilation;
        internal readonly byte[] Pe, Pdb;
        private readonly MemoryStream image, symbols;
        private readonly PEReader pe;
        private readonly MetadataReaderProvider provider;
        private long bytes;
        private int steps;

        internal Fixture(string source)
        {
            Compilation = Compile(source);
            using var output = new MemoryStream(); using var debug = new MemoryStream();
            var emitted = Compilation.Emit(output, debug,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            Pe = output.ToArray(); Pdb = debug.ToArray();
            image = new(Pe, writable: false); symbols = new(Pdb, writable: false);
            pe = new(image); provider = MetadataReaderProvider.FromPortablePdbStream(symbols);
        }

        private static CSharpCompilation Compile(string source) => CSharpCompilation.Create("InitializerFixture",
            [CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8, SourceHashAlgorithm.Sha256),
                new CSharpParseOptions(LanguageVersion.CSharp14), path: "Subject.cs", cancellationToken: TestContext.Current.CancellationToken)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, deterministic: true));

        internal void Check()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (++steps > 1_000_000) throw new IOException("Fixture exceeds the existing origin step bound.");
        }

        private void Admit(long row, string?[] text)
        {
            Check();
            foreach (var value in text) row = checked(row + 32L + 8L * (value?.Length ?? 0));
            bytes = checked(bytes + row);
            if (bytes > 128 * 1024 * 1024) throw new IOException("Fixture exceeds the existing origin byte bound.");
        }

        internal CompilerInitializerOrigins.Attribution Read(string? replacementSource = null, bool wrongDocumentPath = false)
        {
            var compilation = replacementSource is null ? Compilation : Compile(replacementSource);
            var metadata = pe.GetMetadataReader(); var pdb = provider.GetMetadataReader();
            var bindings = CompilerNamedMethodBindings.Capture(compilation, metadata, pdb, Check, Admit);
            var documents = new Dictionary<DocumentHandle, string>();
            foreach (var handle in pdb.Documents)
            {
                Check(); documents.Add(handle, wrongDocumentPath ? "Foreign.cs" : pdb.GetString(pdb.GetDocument(handle).Name));
            }
            var tree = compilation.SyntaxTrees.Single();
            var model = compilation.GetSemanticModel(tree);
            var initializer = Assert.Single(tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<EqualsValueClauseSyntax>());
            var owner = model.GetDeclaredSymbol(initializer.Parent!, TestContext.Current.CancellationToken)!;
            return new CompilerInitializerOrigins(compilation, metadata, pdb, documents, bindings, Check, Admit,
                TestContext.Current.CancellationToken).Capture(initializer.Value, owner, tree.FilePath);
        }

        internal CompilerInitializerOrigins.ConstructorArguments ReadArguments()
        {
            var metadata = pe.GetMetadataReader(); var pdb = provider.GetMetadataReader();
            var bindings = CompilerNamedMethodBindings.Capture(Compilation, metadata, pdb, Check, Admit);
            var documents = new Dictionary<DocumentHandle, string>();
            foreach (var handle in pdb.Documents) documents.Add(handle, pdb.GetString(pdb.GetDocument(handle).Name));
            var tree = Compilation.SyntaxTrees.Single(); var root = tree.GetRoot(TestContext.Current.CancellationToken);
            var spans = root.DescendantNodes().Select(node => node switch
            {
                ConstructorInitializerSyntax initializer => (SyntaxNode)initializer,
                PrimaryConstructorBaseTypeSyntax primary => primary.ArgumentList,
                _ => null
            }).Where(node => node is not null).ToArray();
            var span = Assert.Single(spans)!;
            return new CompilerInitializerOrigins(Compilation, metadata, pdb, documents, bindings, Check, Admit,
                TestContext.Current.CancellationToken).CaptureArguments(span, tree.FilePath);
        }

        internal string ConstructorType(int token)
        {
            var metadata = pe.GetMetadataReader();
            var method = metadata.GetMethodDefinition(System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle(token & 0x00ffffff));
            return metadata.GetString(metadata.GetTypeDefinition(method.GetDeclaringType()).Name);
        }

        internal int ConstructorParameterRows(int token)
        {
            var metadata = pe.GetMetadataReader();
            return metadata.GetMethodDefinition(System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle(token & 0x00ffffff))
                .GetParameters().Select(metadata.GetParameter).Count(parameter => parameter.SequenceNumber > 0);
        }

        public void Dispose() { pe.Dispose(); provider.Dispose(); image.Dispose(); symbols.Dispose(); }
    }
}
