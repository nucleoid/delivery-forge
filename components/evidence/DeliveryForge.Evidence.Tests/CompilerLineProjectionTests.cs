using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace DeliveryForge.Evidence.Tests;

// Genuine compiler PE/PDB/source, with explicit DATA-only line reports below.
// These controls do not certify an actual collector, producer receipt or owner.
public sealed class CompilerLineProjectionTests
{
    private const string Ordinary = "public static class Subject\n{\n    public static int Value(int value)\n    {\n        return\n            value // VALUE_TOKEN\n            // COMMENT_ONLY\n\n            +\n            1;\n    }\n}\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Genuine_multiline_compiler_spans_keep_code_tokens_separate_from_provider_comments_and_blank_lines(bool debug)
    {
        using var fixture = new Fixture(Ordinary, debug);
        var inventory = fixture.Inventory();
        Assert.True(inventory.Complete, inventory.Reason);
        Assert.Contains(inventory.Points, point => point.EndLine > point.StartLine + 1);
        var h = inventory.ProviderLines.Select(row => row.Number).ToHashSet();
        var e = inventory.PolicyLines.Select(row => row.Number).ToHashSet();
        Assert.Contains(7, h); Assert.Contains(8, h);
        Assert.DoesNotContain(7, e); Assert.DoesNotContain(8, e);
        foreach (var line in new[] { 5, 6, 9, 10 }) Assert.Contains(line, e);
        Assert.True(e.IsSubsetOf(h));
        var result = fixture.Read();
        Assert.True(result.CompilerCensusComplete, result.Reason); Assert.Equal(GateOutcome.Pass, result.Outcome);
        Assert.Equal(e.Count, result.ChangedExecutableLines); Assert.Equal(e.Count, result.CoveredChangedLines);
        Assert.Equal(100m, result.Percent); Assert.Equal(0, result.UnmeasuredChangedLines);
    }

    [Fact]
    public void Provider_only_comment_measurement_neither_inflates_denominator_nor_counts_as_a_miss()
    {
        using var fixture = new Fixture(Ordinary);
        var result = fixture.Read(zeroLine: 7);
        Assert.Equal(GateOutcome.Pass, result.Outcome); Assert.Equal(100m, result.Percent);
        Assert.Equal(fixture.Inventory().PolicyLines.Count, result.ChangedExecutableLines);
    }

    [Fact]
    public void Provider_only_change_has_no_policy_line_and_no_automatic_exemption()
    {
        using var fixture = new Fixture(Ordinary);
        var result = fixture.Read(onlyLine: 7);
        Assert.True(result.CompilerCensusComplete); Assert.Equal(0, result.ChangedExecutableLines);
        Assert.Equal(0, result.UnmeasuredChangedLines); Assert.Null(result.Percent);
        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Required_token_line_needs_a_measurement_and_hit_in_every_actual_context(bool secondContext)
    {
        using var fixture = new Fixture(Ordinary);
        Assert.Contains(fixture.Inventory().PolicyLines, row => row.Number == 6);
        var missing = fixture.Read(onlyLine: 6, omitLine: 6, secondContext: secondContext);
        Assert.Equal(GateOutcome.Incomplete, missing.Outcome); Assert.Equal(1, missing.UnmeasuredChangedLines);
        Assert.Equal(1, missing.ChangedExecutableLines);
        var zero = fixture.Read(onlyLine: 6, zeroLine: 6, secondContext: secondContext);
        Assert.Equal(GateOutcome.Fail, zero.Outcome); Assert.Equal(0, zero.UnmeasuredChangedLines);
        Assert.Equal(0, zero.CoveredChangedLines); Assert.Equal(0m, zero.Percent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Complete_context_refuses_outside_provider_rows_even_when_another_context_is_incomplete(bool incompleteSecond)
    {
        using var fixture = new Fixture(Ordinary);
        Assert.DoesNotContain(fixture.Inventory().ProviderLines, row => row.Number == 1);
        var failure = Assert.Throws<EvidenceWriteException>(() => fixture.Read(
            outsideLine: 1, secondContext: incompleteSecond, incompleteSecond: incompleteSecond));
        Assert.Contains("outside the independently captured compiler provider", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Incomplete_context_cannot_turn_partial_known_rows_into_complete_measurements()
    {
        using var fixture = new Fixture(Ordinary);
        var result = fixture.Read(secondContext: true, incompleteSecond: true);
        Assert.False(result.CompilerCensusComplete); Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("directives", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Genuine_multiline_literal_content_is_token_content_even_when_it_looks_like_a_comment()
    {
        const string source = "public static class Subject\n{\n    public static string Text()\n    {\n        return @\"first\n// LITERAL_CONTENT\n    \nlast\";\n    }\n}\n";
        using var fixture = new Fixture(source);
        var inventory = fixture.Inventory(); Assert.True(inventory.Complete, inventory.Reason);
        Assert.Contains(inventory.PolicyLines, row => row.Number == 6);
        Assert.Contains(inventory.PolicyLines, row => row.Number == 7);
    }

    [Theory]
    [InlineData("#define CHOICE\n", "")]
    [InlineData("#if true\n", "#endif\n")]
    public void Genuine_compiler_input_with_unbound_choosing_directives_stays_unsupported(string prefix, string suffix)
    {
        using var fixture = new Fixture(prefix + Ordinary + suffix);
        var inventory = fixture.Inventory(); Assert.False(inventory.Complete);
        Assert.Contains("directives", inventory.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void Coordinate_DATA_half_open_end_excludes_next_line_token_while_provider_envelope_keeps_it(string newline)
    {
        // Coordinate DATA only; no manufactured PDB/collector acceptance.
        using var files = new TestFiles();
        var source = "class Subject" + newline + "{" + newline + "}" + newline;
        var projection = new CompilerLineProjection([Document(source)], files.Owner);
        Assert.True(projection.Add(new(0x06000001, "Subject.cs", 1, 1, 3, 1)), projection.Reason);
        Assert.Equal(new[] { 1, 2, 3 }, projection.ProviderLines.Select(row => row.Number).Order().ToArray());
        Assert.Equal(new[] { 1, 2 }, projection.PolicyLines.Select(row => row.Number).Order().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Coordinate_DATA_BOM_SUB_and_actual_trailing_newline_do_not_create_phantom_physical_membership(bool bom)
    {
        using var files = new TestFiles();
        var source = (bom ? "\ufeff" : "") + "class Subject {}\r\n\u001a";
        var projection = new CompilerLineProjection([Document(source)], files.Owner);
        Assert.True(projection.Add(new(0x06000001, "Subject.cs", 1, 1, 2, 1)), projection.Reason);
        Assert.Single(projection.ProviderLines); Assert.Single(projection.PolicyLines);
        var unterminated = new CompilerLineProjection([Document("class Subject {}")], files.Owner);
        Assert.False(unterminated.Add(new(0x06000001, "Subject.cs", 1, 1, 2, 1)));
    }

    [Theory]
    [InlineData(0, 1, 1, 2)]
    [InlineData(1, 0, 1, 2)]
    [InlineData(1, 4, 1, 2)]
    [InlineData(1, 1, 2, 100)]
    public void Coordinate_DATA_out_of_extent_or_reversed_span_is_not_complete(int line, int column, int end, int endColumn)
    {
        using var files = new TestFiles();
        var projection = new CompilerLineProjection([Document("class Subject {}\n")], files.Owner);
        Assert.False(projection.Add(new(0x06000001, "Subject.cs", line, column, end, endColumn)));
        Assert.NotNull(projection.Reason); Assert.Empty(projection.ProviderLines);
    }

    [Fact]
    public void Coordinate_DATA_nonhidden_span_without_token_characters_is_unsupported_not_an_exemption()
    {
        using var files = new TestFiles();
        var projection = new CompilerLineProjection([Document("// comment\nclass Subject {}\n")], files.Owner);
        Assert.False(projection.Add(new(0x06000001, "Subject.cs", 1, 1, 1, 11)));
        Assert.Contains("no usable source-token character", projection.Reason!, StringComparison.Ordinal);
        Assert.Empty(projection.PolicyLines);
    }

    [Fact]
    public void Actual_original_cancellation_is_not_recast_as_a_projection_limitation()
    {
        using var cancellation = new CancellationTokenSource();
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(30), cancellation.Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new CompilerLineProjection([Document(Ordinary)], files));
        files.ReleaseKnown();
    }

    [Fact]
    public void Repeated_overlapping_span_DATA_cannot_expand_work_through_deduplicated_membership()
    {
        using var files = new TestFiles();
        const string source = "class Subject {}\n";
        var projection = new CompilerLineProjection([Document(source)], files.Owner);
        var point = new CompilerLineCensus.Point(0x06000001, "Subject.cs", 1, 1, 1, source.Length);
        var accepted = 0;
        while (projection.Add(point)) accepted++;
        Assert.InRange(accepted, 1, 1_000_000);
        Assert.Contains("finite membership", projection.Reason!, StringComparison.Ordinal);
        Assert.Single(projection.ProviderLines); Assert.Single(projection.PolicyLines);
    }

    private static CompilerLineCensus.Document Document(string text) => new("Subject.cs", "Subject.cs", null,
        InstalledToolFiles.Identity(Encoding.UTF8.GetBytes(text))[7..], text, false);
    private sealed class TestFiles : IDisposable
    {
        internal readonly InstalledToolFiles Owner = new(TimeSpan.FromSeconds(30));
        public void Dispose() => Owner.ReleaseKnown();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly InstalledToolFiles Files = new(TimeSpan.FromSeconds(30));
        private readonly string root = Path.Combine(Path.GetTempPath(), "forge-multiline-data-" + Guid.NewGuid().ToString("N"));
        private readonly string text;
        private readonly byte[] source, pe, pdb;
        private readonly CompilerLineCensus.Build build;
        internal Fixture(string text, bool debug = false)
        {
            this.text = text; source = new UTF8Encoding(false, true).GetBytes(text);
            var syntax = CSharpSyntaxTree.ParseText(SourceText.From(text, new UTF8Encoding(false), SourceHashAlgorithm.Sha256),
                new CSharpParseOptions(LanguageVersion.CSharp14), path: "Subject.cs", cancellationToken: TestContext.Current.CancellationToken);
            var compilation = CSharpCompilation.Create("ProjectionFixture", [syntax],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: debug ? OptimizationLevel.Debug : OptimizationLevel.Release, deterministic: true));
            using var assembly = new MemoryStream(); using var symbols = new MemoryStream();
            var emitted = compilation.Emit(assembly, symbols, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            pe = assembly.ToArray(); pdb = symbols.ToArray();
            using var reader = new PEReader(new MemoryStream(pe));
            var metadata = reader.GetMetadataReader(); var module = metadata.GetModuleDefinition();
            var directory = reader.ReadDebugDirectory().Single(row => row.Type == DebugDirectoryEntryType.CodeView);
            var codeView = reader.ReadCodeViewDebugDirectoryData(directory);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb));
            var content = new BlobContentId(provider.GetMetadataReader().DebugMetadataHeader!.Id);
            build = new("ProjectionFixture", metadata.GetGuid(module.Mvid).ToString("D"),
                CompilerLineCensus.DebugIdentity(codeView.Guid, codeView.Age, directory.Stamp, content.Stamp));
            Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "Subject.cs"), source);
        }
        internal CompilerLineCensus.Inventory Inventory() => CompilerLineCensus.Inspect(pe, pdb, build,
            [Document(text) with { PhysicalPath = Path.Combine(root, "Subject.cs") }], Files);

        internal ChangedLineCoverage.Result Read(int? onlyLine = null, int? omitLine = null, int? zeroLine = null,
            int? outsideLine = null, bool secondContext = false, bool incompleteSecond = false)
        {
            var inventory = Inventory(); Assert.True(inventory.Complete, inventory.Reason);
            var rows = inventory.ProviderLines.Select(row => row.Number).Distinct().Order().ToArray();
            var data = new Dictionary<string, byte[]> { ["source"] = source, ["pe"] = pe, ["pdb"] = pdb };
            var contexts = new JsonArray(); var builds = new JsonArray(); var executions = new JsonArray(); var artifacts = new JsonArray();
            foreach (var id in secondContext ? new[] { "c", "c2" } : new[] { "c" })
            {
                var inputs = new JsonArray(JsonSerializer.SerializeToNode(new { role = "source", logicalPath = "Subject.cs", repositoryPath = "Subject.cs",
                    locator = "source", length = source.Length, sha256 = InstalledToolFiles.Identity(source)[7..], generated = false, encoding = "utf-8" }));
                if (incompleteSecond && id == "c2")
                {
                    data["extra"] = "#if true\npublic interface Extra {}\n#endif\n"u8.ToArray();
                    inputs.Add(JsonSerializer.SerializeToNode(new { role = "compiler-source", logicalPath = "Extra.cs", locator = "extra",
                        length = data["extra"].Length, sha256 = InstalledToolFiles.Identity(data["extra"])[7..], generated = true, encoding = "utf-8" }));
                }
                contexts.Add(new JsonObject { ["id"] = id, ["inputs"] = inputs });
                var bid = id + "b"; var eid = id + "e";
                builds.Add(JsonSerializer.SerializeToNode(new { id = bid, contextId = id, moduleIdentity = build.ModuleIdentity, mvid = build.Mvid,
                    debugIdentity = build.DebugIdentity, assemblySha256 = InstalledToolFiles.Identity(pe)[7..], pdbSha256 = InstalledToolFiles.Identity(pdb)[7..] }));
                executions.Add(JsonSerializer.SerializeToNode(new { id = eid, contextId = id, buildId = bid, completed = true, exitCode = 0 }));
                foreach (var role in new[] { "assembly", "pdb" })
                {
                    var locator = role == "assembly" ? "pe" : "pdb";
                    artifacts.Add(JsonSerializer.SerializeToNode(new { id = id + role, kind = role, locator, contextId = id, buildId = bid,
                        length = data[locator].Length, sha256 = InstalledToolFiles.Identity(data[locator])[7..] }));
                }
                var selected = !secondContext || id == "c2";
                var measured = rows.Where(line => !selected || line != omitLine).ToList();
                if (outsideLine is { } outside && id == "c") measured.Add(outside);
                var methods = measured.Select(line => new XElement("Method", new XElement("FileRef", new XAttribute("uid", "1")),
                    new XElement("SequencePoints", new XElement("SequencePoint", new XAttribute("fileid", "1"),
                        new XAttribute("sl", line), new XAttribute("vc", selected && line == zeroLine ? 0 : 1)))));
                var module = new XElement("Module",
                    new XElement("ModuleName", build.ModuleIdentity),
                    new XElement("Files", new XElement("File", new XAttribute("uid", "1"), new XAttribute("fullPath", Path.Combine(root, "Subject.cs")))),
                    new XElement("Classes", new XElement("Class", new XElement("Methods", methods))));
                var xml = new XElement("CoverageSession", new XElement("Modules", module));
                data[id + "coverage"] = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
                artifacts.Add(JsonSerializer.SerializeToNode(new { id = id + "coverage", kind = "coverage", locator = id + "coverage", contextId = id,
                    buildId = bid, executionId = eid, format = "opencover", coordinateKind = "line", length = data[id + "coverage"].Length,
                    sha256 = InstalledToolFiles.Identity(data[id + "coverage"])[7..] }));
            }
            using var manifest = JsonDocument.Parse(new JsonObject { ["contexts"] = contexts, ["builds"] = builds,
                ["executions"] = executions, ["artifacts"] = artifacts }.ToJsonString());
            var lines = text.Split('\n').SkipLast(1).ToArray();
            var diff = onlyLine is { } line
                ? "+++ b/Subject.cs\n@@ -0,0 +" + line + ",1 @@\n+" + lines[line - 1] + "\n"
                : "+++ b/Subject.cs\n@@ -0,0 +1," + lines.Length + " @@\n" + string.Join("\n", lines.Select(value => "+" + value)) + "\n";
            return ChangedLineCoverage.ReadCompilerBound(Encoding.UTF8.GetBytes(diff), manifest.RootElement, root, locator => data[locator], Files);
        }
        public void Dispose() { Files.ReleaseKnown(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
