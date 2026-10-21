using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace DeliveryForge.Evidence.Tests;

public sealed class CompilerImplicitConstructorTests
{
    [Theory]
    [InlineData("public class C { public int Value() => 7; }", "C")]
    [InlineData("namespace A.B; public class C { public int Value() => 7; }", "C")]
    [InlineData("namespace A { namespace B { public abstract class C { public int Value() => 7; } } }", "C")]
    [InlineData("#nullable enable\npublic class C { public int Value() { int local = 7; return local; } }", "C")]
    public void Genuine_compiler_body_and_unique_source_prove_only_plain_implicit_constructor(string source, string type)
    {
        using var fixture = new Fixture(source, type);
        Assert.True(fixture.Proves());
        // Actual ordinary compiler output retains the authored method's points;
        // accepting this constructor never means exempting the type's methods.
        using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(fixture.Pdb));
        var pdb = provider.GetMetadataReader();
        Assert.Empty(pdb.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(
            MetadataTokens.GetRowNumber(fixture.Constructor))).GetSequencePoints());
        Assert.Contains(pdb.MethodDebugInformation.Select(pdb.GetMethodDebugInformation),
            method => method.GetSequencePoints().Any(point => !point.IsHidden));
    }

    [Theory]
    [InlineData("public class C { public C() {} }")]
    [InlineData("public class C { public int Field = 7; }")]
    [InlineData("public class C { public int Property { get; } = 7; }")]
    [InlineData("public class C : object { }")]
    [InlineData("public partial class C { }")]
    [InlineData("public class C<T> { }")]
    [InlineData("public class C() { }")]
    [InlineData("public class Outer { public class C { } }")]
    [InlineData("public record C;")]
    [InlineData("#if true\npublic class C { }\n#endif")]
    public void Genuine_ambiguous_or_authored_constructor_forms_are_not_exempted(string source)
    {
        using var fixture = new Fixture(source, "C");
        Assert.False(fixture.Proves());
    }

    [Fact]
    public void Another_captured_compiler_source_cannot_hide_explicit_constructor_membership()
    {
        using var fixture = new Fixture("public class C { }", "C");
        Assert.True(fixture.Proves());
        var extra = fixture.Document("Compiler.cs", "public class C { public C() {} }") with { Generated = true };
        Assert.False(fixture.Proves(documents: [fixture.Source, extra]));
    }

    [Theory]
    [InlineData("opcode")]
    [InlineData("foreign-token")]
    [InlineData("invalid-token-kind")]
    public void Altered_real_constructor_il_is_not_presumed_generated(string change)
    {
        using var fixture = new Fixture("public class C { }", "C");
        var bytes = fixture.Pe.ToArray();
        using var reader = new PEReader(new MemoryStream(bytes, writable: false));
        var method = reader.GetMetadataReader().GetMethodDefinition(fixture.Constructor);
        var section = reader.PEHeaders.SectionHeaders.Single(section => method.RelativeVirtualAddress >= section.VirtualAddress &&
            method.RelativeVirtualAddress - section.VirtualAddress < section.SizeOfRawData);
        var header = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
        Assert.Equal(2, bytes[header] & 3); // Genuine seven-byte tiny constructor.
        Assert.Equal(0x02, bytes[header + 1]); Assert.Equal(0x28, bytes[header + 2]);
        if (change == "opcode") bytes[header + 1] = 0x14; // ldnull cannot prove the exact implicit body.
        else System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(header + 3, 4),
            change == "foreign-token" ? 0x0affffff : unchecked((int)0x7fffffff));
        Assert.False(fixture.Proves(bytes));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly InstalledToolFiles files = new(TimeSpan.FromSeconds(30));
        internal readonly byte[] Pe, Pdb;
        internal readonly CompilerLineCensus.Document Source;
        internal readonly MethodDefinitionHandle Constructor;

        internal Fixture(string source, string name)
        {
            Source = Document("Subject.cs", source);
            var syntax = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8),
                new CSharpParseOptions(LanguageVersion.CSharp14), path: "Subject.cs");
            var compilation = CSharpCompilation.Create("ConstructorFixture", [syntax],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, deterministic: true));
            using var pe = new MemoryStream(); using var pdb = new MemoryStream();
            var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Pe = pe.ToArray(); Pdb = pdb.ToArray();
            using var reader = new PEReader(new MemoryStream(Pe, writable: false));
            var metadata = reader.GetMetadataReader();
            var type = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
                .Single(type => metadata.GetString(type.Name).Split('`')[0] == name);
            Constructor = type.GetMethods().Single(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == ".ctor" &&
                metadata.GetBlobBytes(metadata.GetMethodDefinition(handle).Signature).AsSpan().SequenceEqual([0x20, 0x00, 0x01]));
        }
        internal CompilerLineCensus.Document Document(string path, string text)
            => new(path, path, null, InstalledToolFiles.Identity(Encoding.UTF8.GetBytes(text))[7..], text, false);

        internal bool Proves(byte[]? pe = null, IReadOnlyList<CompilerLineCensus.Document>? documents = null)
        {
            using var reader = new PEReader(new MemoryStream(pe ?? Pe, writable: false));
            return new CompilerImplicitConstructor(documents ?? [Source], files).Proves(Constructor, reader.GetMetadataReader(), reader);
        }
        public void Dispose() => files.ReleaseKnown();
    }
}
