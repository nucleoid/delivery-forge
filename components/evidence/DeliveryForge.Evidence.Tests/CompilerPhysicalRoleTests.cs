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

public sealed class CompilerPhysicalRoleTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("compiler-source")]
    public void Genuine_absolute_generated_document_uses_exact_context_mapping_without_opening_generated_file(string role)
    {
        using var fixture = new Fixture();
        Assert.False(File.Exists(fixture.GeneratedPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.GeneratedPath)));
        var result = fixture.Read(role: role);
        Assert.True(result.CompilerCensusComplete, result.Reason); Assert.Equal(GateOutcome.Pass, result.Outcome);
        Assert.Equal(1, result.ChangedExecutableLines); Assert.Equal(1, result.CoveredChangedLines); Assert.Equal(100m, result.Percent);
        Assert.False(File.Exists(fixture.GeneratedPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.GeneratedPath)));
    }

    [Fact]
    public void Missing_mapping_does_not_adopt_a_unique_matching_generated_checksum()
    {
        using var fixture = new Fixture();
        var result = fixture.Read(fault: "missing");
        Assert.False(result.CompilerCensusComplete); Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("roles are not completely", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("foreign", "outside the selected repository")]
    [InlineData("duplicate", "Duplicate")]
    [InlineData("logical-case", "exact same-context")]
    [InlineData("physical-case", "exact repository-relative")]
    [InlineData("alias", "exact canonical")]
    [InlineData("malformed", "Malformed")]
    public void Foreign_duplicate_alias_or_malformed_mapping_refuses_before_identity_can_be_borrowed(string fault, string reason)
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<EvidenceWriteException>(() => fixture.Read(fault: fault));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.GeneratedPath));
    }

    [Fact]
    public void Exact_mapping_never_replaces_generated_document_checksum_verification()
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<EvidenceWriteException>(() => fixture.Read(fault: "checksum"));
        Assert.Contains("document checksum differs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_report_DATA_keeps_its_prior_authored_mapping_semantics()
    {
        using var fixture = new Fixture();
        var result = fixture.Read(fault: "foreign", raw: true);
        Assert.False(result.CompilerCensusComplete); Assert.Equal(GateOutcome.Pass, result.Outcome);
        Assert.Equal(1, result.ChangedExecutableLines);
    }

    [Fact]
    [Trait("Category", "InstalledToolAcceptance")]
    public void Retained_genuine_compiler_line_capture_reduces_without_reexecuting_the_producer()
    {
        var specPath = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST");
        var specIdentity = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST_SHA256");
        if (string.IsNullOrEmpty(specPath) || string.IsNullOrEmpty(specIdentity))
            Assert.Skip("Retained genuine collector DATA requires an independently selected pinned capture specification; no process is executed.");
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        try
        {
            var specBytes = files.Read(specPath!, 1024 * 1024); Assert.Equal(specIdentity, InstalledToolFiles.Identity(specBytes));
            using var spec = ProducerJson.Read(specBytes);
            if (!spec.RootElement.TryGetProperty("retainedCompilerLineCapture", out var capture))
                Assert.Skip("This selected fixture does not contain retained compiler-line capture DATA.");
            var manifestPath = capture.GetProperty("manifestPath").GetString()!;
            var bundleRoot = capture.GetProperty("bundleRoot").GetString()!;
            var repository = capture.GetProperty("repository").GetString()!;
            var diffPath = capture.GetProperty("diffPath").GetString()!;
            Assert.True(Path.IsPathFullyQualified(manifestPath)); Assert.True(Path.IsPathFullyQualified(bundleRoot));
            Assert.True(Path.IsPathFullyQualified(repository)); Assert.True(Path.IsPathFullyQualified(diffPath));
            Assert.True(InstalledToolGateProvider.Inside(manifestPath, bundleRoot));
            var manifestBytes = files.Read(manifestPath, ProducerJson.MaximumBytes);
            Assert.Equal(capture.GetProperty("manifestSha256").GetString(), InstalledToolFiles.Identity(manifestBytes));
            var diff = files.Read(diffPath, ProducerJson.MaximumBytes);
            Assert.Equal(capture.GetProperty("diffSha256").GetString(), InstalledToolFiles.Identity(diff));
            using var manifest = ProducerJson.Read(manifestBytes);
            byte[] ReadArtifact(string locator)
            {
                InstalledToolProfile.Relative(locator);
                var path = Path.GetFullPath(Path.Combine(bundleRoot, locator));
                Assert.True(InstalledToolGateProvider.Inside(path, bundleRoot));
                return files.Read(path, 128 * 1024 * 1024);
            }
            var result = ChangedLineCoverage.ReadCompilerBound(diff, manifest.RootElement, repository, ReadArtifact, files);
            var expected = capture.GetProperty("expectedLines").GetInt32(); Assert.Equal(4, expected);
            Assert.True(result.CompilerCensusComplete, result.Reason); Assert.Equal(GateOutcome.Pass, result.Outcome);
            Assert.Equal(expected, result.ChangedExecutableLines); Assert.Equal(expected, result.CoveredChangedLines);
            Assert.Equal(0, result.UnmeasuredChangedLines); Assert.Equal(100m, result.Percent);
            // Read-only reduction of pinned old actual outputs, not a new
            // producer execution, receipt, freshness grant or baseline approval.
        }
        finally { files.ReleaseKnown(); }
    }

    private sealed class Fixture : IDisposable
    {
        private const string AuthoredText = "public static class Subject { public static int Value(int n) => n + 1; }\n";
        private const string GeneratedText = "[assembly: System.Reflection.AssemblyMetadata(\"CompilerRoleFixture\", \"generated\")]\n";
        private const string GeneratedLogical = "obj/Generated.AssemblyInfo.cs";
        private readonly string root = Path.Combine(Path.GetTempPath(), "forge-generated-role-data-" + Guid.NewGuid().ToString("N"));
        private readonly InstalledToolFiles files = new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        private readonly byte[] authored = Encoding.UTF8.GetBytes(AuthoredText), generated = Encoding.UTF8.GetBytes(GeneratedText);
        private readonly byte[] pe, pdb;
        private readonly CompilerLineCensus.Build build;
        internal string GeneratedPath => Path.GetFullPath(Path.Combine(root, GeneratedLogical));
        private string AuthoredPath => Path.Combine(root, "Subject.cs");

        internal Fixture()
        {
            Directory.CreateDirectory(root); File.WriteAllBytes(AuthoredPath, authored);
            var authoredSource = SourceText.From(AuthoredText, new UTF8Encoding(false), SourceHashAlgorithm.Sha256);
            var generatedSource = SourceText.From(GeneratedText, new UTF8Encoding(false), SourceHashAlgorithm.Sha256);
            var options = new CSharpParseOptions(LanguageVersion.CSharp14);
            var trees = new[]
            {
                CSharpSyntaxTree.ParseText(authoredSource, options, AuthoredPath, TestContext.Current.CancellationToken),
                CSharpSyntaxTree.ParseText(generatedSource, options, GeneratedPath, TestContext.Current.CancellationToken)
            };
            var compilation = CSharpCompilation.Create("PhysicalRoleFixture", trees,
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true));
            using var assembly = new MemoryStream(); using var symbols = new MemoryStream();
            // Genuine compiler embedding retains an otherwise nonexecutable
            // generated document, as normal SDK generated-input PDBs can do.
            var emitted = compilation.Emit(assembly, symbols, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
                embeddedTexts: [EmbeddedText.FromSource(GeneratedPath, generatedSource)], cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            pe = assembly.ToArray(); pdb = symbols.ToArray();
            using var reader = new PEReader(new MemoryStream(pe)); var metadata = reader.GetMetadataReader();
            using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb)); var debug = provider.GetMetadataReader();
            Assert.Equal(2, debug.Documents.Count);
            Assert.Contains(debug.Documents, handle => debug.GetString(debug.GetDocument(handle).Name).Replace('\\', '/') == GeneratedPath.Replace('\\', '/'));
            var hashes = debug.Documents.Select(handle => Convert.ToHexStringLower(debug.GetBlobBytes(debug.GetDocument(handle).Hash))).ToArray();
            Assert.Equal(2, hashes.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(InstalledToolFiles.Identity(generated)[7..], hashes);
            var directory = reader.ReadDebugDirectory().Single(row => row.Type == DebugDirectoryEntryType.CodeView);
            var codeView = reader.ReadCodeViewDebugDirectoryData(directory); var content = new BlobContentId(debug.DebugMetadataHeader!.Id);
            build = new("PhysicalRoleFixture", metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D"),
                CompilerLineCensus.DebugIdentity(codeView.Guid, codeView.Age, directory.Stamp, content.Stamp));
        }

        internal ChangedLineCoverage.Result Read(string? fault = null, string role = "compiler-source", bool raw = false)
        {
            var capturedGenerated = generated.ToArray();
            if (fault == "checksum") capturedGenerated[^3] ^= 1;
            var data = new Dictionary<string, byte[]> { ["authored"] = authored, ["generated"] = capturedGenerated, ["pe"] = pe, ["pdb"] = pdb };
            var mappings = new JsonArray();
            if (fault != "missing")
            {
                var physical = fault switch
                {
                    "foreign" => Path.GetFullPath(Path.Combine(root, "..", "foreign", "Generated.AssemblyInfo.cs")),
                    "physical-case" => GeneratedPath.Replace("Generated.AssemblyInfo.cs", "generated.AssemblyInfo.cs", StringComparison.Ordinal),
                    "alias" => Path.Combine(root, "obj", "..", "obj", "Generated.AssemblyInfo.cs"),
                    "malformed" => "relative/Generated.AssemblyInfo.cs",
                    _ => GeneratedPath
                };
                mappings.Add(new JsonObject { ["reportRoot"] = physical, ["logicalRoot"] = fault == "logical-case" ? "obj/generated.AssemblyInfo.cs" : GeneratedLogical });
                if (fault == "duplicate") mappings.Add(mappings[0]!.DeepClone());
            }
            // Include the normal authored file record too; it never replaces
            // authored current-byte checks or XML physical path matching.
            mappings.Add(new JsonObject { ["reportRoot"] = AuthoredPath, ["logicalRoot"] = "Subject.cs" });
            var inputs = new JsonArray(
                JsonSerializer.SerializeToNode(new
                {
                    role = "source",
                    logicalPath = "Subject.cs",
                    repositoryPath = "Subject.cs",
                    locator = "authored",
                    length = authored.Length,
                    sha256 = InstalledToolFiles.Identity(authored)[7..],
                    generated = false,
                    encoding = "utf-8"
                }),
                JsonSerializer.SerializeToNode(new
                {
                    role,
                    logicalPath = GeneratedLogical,
                    repositoryPath = GeneratedLogical,
                    locator = "generated",
                    length = capturedGenerated.Length,
                    sha256 = InstalledToolFiles.Identity(capturedGenerated)[7..],
                    generated = true,
                    encoding = "utf-8"
                }));
            var contexts = new JsonArray(new JsonObject
            {
                ["id"] = "c",
                ["inputs"] = inputs,
                ["pathPolicy"] = new JsonObject { ["casePolicy"] = "insensitive", ["reportRootMappings"] = mappings }
            });
            var builds = new JsonArray(JsonSerializer.SerializeToNode(new
            {
                id = "b",
                contextId = "c",
                moduleIdentity = build.ModuleIdentity,
                mvid = build.Mvid,
                debugIdentity = build.DebugIdentity,
                assemblySha256 = InstalledToolFiles.Identity(pe)[7..],
                pdbSha256 = InstalledToolFiles.Identity(pdb)[7..]
            }));
            var executions = new JsonArray(JsonSerializer.SerializeToNode(new { id = "e", contextId = "c", buildId = "b", completed = true, exitCode = 0 }));
            var artifacts = new JsonArray();
            foreach (var kind in new[] { "assembly", "pdb" })
            {
                var locator = kind == "assembly" ? "pe" : "pdb";
                artifacts.Add(JsonSerializer.SerializeToNode(new
                {
                    id = kind,
                    kind,
                    locator,
                    contextId = "c",
                    buildId = "b",
                    length = data[locator].Length,
                    sha256 = InstalledToolFiles.Identity(data[locator])[7..]
                }));
            }
            var method = new XElement("Method", new XElement("FileRef", new XAttribute("uid", "1")),
                new XElement("SequencePoints", new XElement("SequencePoint", new XAttribute("fileid", "1"), new XAttribute("sl", "1"), new XAttribute("vc", "1"))));
            var module = new XElement("Module", new XElement("ModuleName", build.ModuleIdentity),
                new XElement("Files", new XElement("File", new XAttribute("uid", "1"), new XAttribute("fullPath", AuthoredPath))),
                new XElement("Classes", new XElement("Class", new XElement("Methods", method))));
            data["coverage"] = Encoding.UTF8.GetBytes(new XElement("CoverageSession", new XElement("Modules", module)).ToString(SaveOptions.DisableFormatting));
            artifacts.Add(JsonSerializer.SerializeToNode(new
            {
                id = "coverage",
                kind = "coverage",
                locator = "coverage",
                contextId = "c",
                buildId = "b",
                executionId = "e",
                format = "opencover",
                coordinateKind = "line",
                length = data["coverage"].Length,
                sha256 = InstalledToolFiles.Identity(data["coverage"])[7..]
            }));
            using var manifest = JsonDocument.Parse(new JsonObject
            {
                ["contexts"] = contexts,
                ["builds"] = builds,
                ["executions"] = executions,
                ["artifacts"] = artifacts
            }.ToJsonString());
            var diff = Encoding.UTF8.GetBytes("+++ b/Subject.cs\n@@ -0,0 +1,1 @@\n+" + AuthoredText);
            return raw ? ChangedLineCoverage.Read(diff, manifest.RootElement, root, locator => data[locator], files)
                : ChangedLineCoverage.ReadCompilerBound(diff, manifest.RootElement, root, locator => data[locator], files);
        }
        public void Dispose() { files.ReleaseKnown(); }
    }
}
