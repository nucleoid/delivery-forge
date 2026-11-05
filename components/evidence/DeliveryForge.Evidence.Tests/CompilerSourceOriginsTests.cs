using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace DeliveryForge.Evidence.Tests;

// In-process compiler controls exercise source attribution, never producer or
// coverage authority. The separate retained real test uses the pinned 5.9 island
// and additionally requires complete actual PE/PDB byte equality.
public sealed class CompilerSourceOriginsTests
{
    [Theory]
    [InlineData("public class C { public int F(int x) => x + 1; }")]
    [InlineData("public interface I { int F(int x); }")]
    [InlineData("public class C { public int P { get; set; } }")]
    [InlineData("public partial class C { public int F() => 1; }")]
    [InlineData("public class C { public int F(int x) { System.Func<int,int> f = y => y + 1; return f(x); } }")]
    public void Semantic_bodies_and_their_own_compiler_points_close_only_origin_association(string source)
    {
        using var inventory = Capture(source);
        var closure = inventory.RootElement.GetProperty("sourceExhaustion");
        Assert.True(closure.GetProperty("complete").GetBoolean(), closure.GetRawText());
        Assert.Equal("inventory-only-no-origin-proof", inventory.RootElement.GetProperty("disposition").GetString());
    }

    [Theory]
    [InlineData("using Hidden = System.Diagnostics.DebuggerHiddenAttribute; public class C { [Hidden] public int F() => 1; }")]
    [InlineData("[System.Diagnostics.DebuggerStepThrough] public class C { public int F() => 1; }")]
    [InlineData("public class C { [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] public int F() => 1; }")]
    [InlineData("public class C { [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] public event System.Action E { add { } remove { } } }")]
    [InlineData("public record C([property: System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] int X);")]
    [InlineData("public class C { [method: System.Diagnostics.DebuggerHidden] public event System.Action E; }")]
    [InlineData("#line 40 \"Other.cs\"\npublic class C { public int F() => 1; }")]
    [InlineData("public class C { public System.Collections.Generic.IEnumerable<int> F() { yield return 1; } }")]
    [InlineData("public class C { public System.Linq.Expressions.Expression<System.Func<int,int>> F() => x => x + 1; }")]
    [InlineData("using System.Linq; public class C { public System.Collections.Generic.IEnumerable<int> F(int[] xs) => from x in xs select x + 1; }")]
    [InlineData("public partial class C { partial void F(); partial void F() { } }")]
    public void Unclosed_source_forms_and_hiding_on_every_target_remain_unresolved(string source)
    {
        using var inventory = Capture(source);
        Assert.False(inventory.RootElement.GetProperty("sourceExhaustion").GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void Another_real_method_point_cannot_supply_an_unmatched_authored_method()
    {
        using var inventory = Capture("public class C { public int Authored() => 1; }",
            "public class C { public int Different() => 1; }");
        Assert.False(inventory.RootElement.GetProperty("sourceExhaustion").GetProperty("complete").GetBoolean());
    }

    private static readonly MetadataReference[] References = FrameworkReferences();
    private static MetadataReference[] FrameworkReferences()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return new[] { typeof(object).Assembly.Location, typeof(Enumerable).Assembly.Location,
                typeof(System.Linq.Expressions.Expression).Assembly.Location,
                Path.Combine(runtime, "System.Runtime.dll"), Path.Combine(runtime, "System.Collections.dll"),
                Path.Combine(runtime, "System.Threading.Tasks.dll") }
            .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    }

    private static JsonDocument Capture(string source, string? emittedSource = null)
    {
        var token = TestContext.Current.CancellationToken;
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(30), token);
        try
        {
            CSharpCompilation Compilation(string text) => CSharpCompilation.Create("SourceOriginFixture",
                [CSharpSyntaxTree.ParseText(SourceText.From(text, Encoding.UTF8),
                    new CSharpParseOptions(LanguageVersion.CSharp14), Path.GetFullPath("OriginFixture.cs"), token)],
                References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, deterministic: true));
            var compilation = Compilation(source);
            var emitted = emittedSource is null ? compilation : Compilation(emittedSource);
            using var pe = new MemoryStream(); using var pdb = new MemoryStream();
            var result = emitted.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
                cancellationToken: token);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return ProducerJson.Read(CompilerSourceOrigins.Capture(compilation, pe.ToArray(), pdb.ToArray(), files.Check, token));
        }
        finally { files.ReleaseKnown(); }
    }
}
