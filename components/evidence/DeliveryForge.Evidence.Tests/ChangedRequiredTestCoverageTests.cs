using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace DeliveryForge.Evidence.Tests;

// Genuine normal-build CensusFixture PE/PDB and exact source bytes. Inventory,
// visits and successful-test metadata are DATA controls, never producer authority.
public sealed class ChangedRequiredTestCoverageTests
{
    [Fact]
    public void Changed_production_and_exact_approved_required_test_keep_only_the_production_denominator()
    {
        using var fixture = new Fixture();
        var result = fixture.Read();
        Assert.True(result.CompilerCensusComplete, result.Reason);
        Assert.Equal(GateOutcome.Pass, result.Outcome);
        Assert.Equal(3, result.ChangedExecutableLines); Assert.Equal(3, result.CoveredChangedLines);
        Assert.Equal(100m, result.Percent);
    }

    [Theory]
    [InlineData("absent-approval", "Changed authored file is not in the complete captured compiler source set.")]
    [InlineData("unknown-source", "Changed authored file is not in the complete captured compiler source set.")]
    [InlineData("wrong-project", "Required-test inventory differs from the approved exact projects.")]
    [InlineData("foreign-pdb", "Compiler PE/PDB GUID, age, stamp or recorded debug identity differs.")]
    [InlineData("current-bytes", "Current required-test source differs from its captured compiler bytes.")]
    [InlineData("generated", "Changed authored file is not in the complete captured compiler source set.")]
    [InlineData("no-points", "Changed authored file is not in the complete captured compiler source set.")]
    public void Unapproved_unbound_or_unproved_test_source_cannot_disappear_from_changed_membership(string fault, string expectedReason)
    {
        using var fixture = new Fixture();
        var error = Assert.Throws<EvidenceWriteException>(() => fixture.Read(fault));
        Assert.Contains(expectedReason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_also_listed_by_an_approved_test_project_retains_its_production_lines_and_debt()
    {
        using var fixture = new Fixture();
        var result = fixture.Read(sharedProduction: true, uncoverLast: true);
        Assert.True(result.CompilerCensusComplete, result.Reason);
        Assert.Equal(3, result.ChangedExecutableLines); Assert.Equal(2, result.CoveredChangedLines);
        Assert.Equal(GateOutcome.Fail, result.Outcome); Assert.True(result.Percent < 80m);
    }

    [Fact]
    [Trait("Category", "InstalledToolAcceptance")]
    public void Pinned_actual_normal_report_bundle_and_two_file_diff_replay_as_DATA_only()
    {
        var specPath = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST");
        var specHash = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST_SHA256");
        if (string.IsNullOrEmpty(specPath) || string.IsNullOrEmpty(specHash))
            Assert.Skip("Pinned changed required-test DATA replay is not selected; no producer is executed.");
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        try
        {
            var specBytes = files.Read(specPath!, 1024 * 1024);
            Assert.Equal(specHash, InstalledToolFiles.Identity(specBytes));
            using var spec = ProducerJson.Read(specBytes);
            if (!spec.RootElement.TryGetProperty("retainedChangedRequiredTests", out var selected))
                Assert.Skip("Selected DATA has no changed required-test replay.");
            var root = selected.GetProperty("bundleRoot").GetString()!;
            var manifestPath = selected.GetProperty("manifestPath").GetString()!;
            var repository = selected.GetProperty("repository").GetString()!;
            Assert.True(Path.IsPathFullyQualified(root)); Assert.True(Path.IsPathFullyQualified(repository));
            Assert.True(InstalledToolGateProvider.Inside(manifestPath, root));
            var manifestBytes = files.Read(manifestPath, ProducerJson.MaximumBytes);
            Assert.Equal(selected.GetProperty("manifestSha256").GetString(), InstalledToolFiles.Identity(manifestBytes));
            var diff = files.Read(selected.GetProperty("diffPath").GetString()!, ProducerJson.MaximumBytes);
            Assert.Equal(selected.GetProperty("diffSha256").GetString(), InstalledToolFiles.Identity(diff));
            using var manifest = ProducerJson.Read(manifestBytes);
            byte[] Read(string locator)
            {
                InstalledToolProfile.Relative(locator);
                var path = Path.GetFullPath(Path.Combine(root, locator));
                Assert.True(InstalledToolGateProvider.Inside(path, root));
                return files.Read(path, 128 * 1024 * 1024);
            }
            var projects = selected.GetProperty("approvedTestProjects").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var value = ChangedLineCoverage.ReadCompilerBound(diff, manifest.RootElement, repository, Read, files, projects);
            Assert.True(value.CompilerCensusComplete, value.Reason); Assert.Equal(GateOutcome.Pass, value.Outcome);
            Assert.Equal(1, value.ChangedExecutableLines); Assert.Equal(1, value.CoveredChangedLines); Assert.Equal(100m, value.Percent);
        }
        finally { files.ReleaseKnown(); }
    }

    private static CompilerLineCensus.Build Expected(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader(); var module = reader.GetModuleDefinition();
        var entry = pe.ReadDebugDirectory().Single(row => row.Type == DebugDirectoryEntryType.CodeView);
        var cv = pe.ReadCodeViewDebugDirectoryData(entry);
        // Independently reproduce the existing producer's canonical tuple wire rule;
        // the actual MVID/name are read from the genuine fixture PE, not asserted DTOs.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Field(string value)
        {
            var raw = Encoding.UTF8.GetBytes(value); var count = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(count, raw.Length); hash.AppendData(count); hash.AppendData(raw);
        }
        Field("sha256-canonical-v1"); Field("portable-pdb-debug-identity-v1");
        foreach (var value in new[] { cv.Guid.ToString("D"), cv.Age.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     entry.Stamp.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), entry.Stamp.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) })
        { hash.AppendData([1]); Field(value); }
        return new(Path.GetFileNameWithoutExtension(reader.GetString(module.Name)), reader.GetGuid(module.Mvid).ToString("D"),
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "forge-required-test-lines-" + Guid.NewGuid().ToString("N"));
        private readonly InstalledToolFiles files = new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        private readonly byte[] source, declarations, pe, pdb, foreignPdb;
        private readonly CompilerLineCensus.Build build;
        private const string Context = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string ProductionGraph = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string TestGraph = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        internal Fixture()
        {
            var output = Path.GetDirectoryName(typeof(ChangedRequiredTestCoverageTests).Assembly.Location)!;
            var evidence = Path.GetFullPath(Path.Combine(output, "../../../.."));
            var configuration = Directory.GetParent(output)!.Name; var framework = Path.GetFileName(output);
            var project = Path.Combine(evidence, "DeliveryForge.Evidence.CensusFixture");
            source = files.Read(Path.Combine(project, "Subject.cs"), 128 * 1024 * 1024);
            declarations = files.Read(Path.Combine(project, "Declarations.cs"), 128 * 1024 * 1024);
            var binary = Path.Combine(project, "bin", configuration, framework, "DeliveryForge.Evidence.CensusFixture.dll");
            pe = files.Read(binary, 128 * 1024 * 1024); pdb = files.Read(Path.ChangeExtension(binary, ".pdb"), 128 * 1024 * 1024);
            var asyncOutput = typeof(DeliveryForge.Evidence.AsyncFixture.AsyncSubject).Assembly.Location;
            foreignPdb = files.Read(Path.ChangeExtension(asyncOutput, ".pdb"), 128 * 1024 * 1024);
            build = Expected(pe);
            foreach (var role in new[] { "Library", "Tests" })
            {
                Directory.CreateDirectory(Path.Combine(root, role, "census"));
                files.WriteNew(Path.Combine(root, role, "census", "Subject.cs"), source);
                files.WriteNew(Path.Combine(root, role, "census", "Declarations.cs"), declarations);
            }
        }
        private static string Hash(byte[] bytes) => InstalledToolFiles.Identity(bytes)[7..];
        private int Line(string marker) => Encoding.UTF8.GetString(source).Split('\n')
            .Select((line, index) => (line, index)).Single(x => x.line.Contains(marker, StringComparison.Ordinal)).index + 1;
        internal ChangedLineCoverage.Result Read(string? fault = null, bool sharedProduction = false, bool uncoverLast = false)
        {
            var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var artifacts = new JsonArray(); var prefix = "required-test-" + TestGraph;
            var buildId = "build-" + ProductionGraph; var executionId = "test-" + ProductionGraph + "-" + TestGraph;
            var project = sharedProduction ? "Library/Library.csproj" : "Tests/Tests.csproj";
            var testDirectory = sharedProduction ? "Library" : "Tests";
            JsonObject Artifact(string id, string kind, byte[] bytes, bool production = false)
            {
                values.Add(id, bytes);
                var node = new JsonObject { ["id"] = id, ["kind"] = kind, ["locator"] = id,
                    ["length"] = bytes.Length, ["sha256"] = Hash(bytes) };
                if (production) { node["contextId"] = Context; node["buildId"] = buildId; }
                artifacts.Add(node); return node;
            }
            JsonObject Input(string logical, string repository, string locator, byte[] bytes) => new()
            {
                ["role"] = "source", ["logicalPath"] = logical, ["repositoryPath"] = repository, ["locator"] = locator,
                ["length"] = bytes.Length, ["sha256"] = Hash(bytes), ["generated"] = false, ["encoding"] = "utf-8"
            };
            values.Add("source", source); values.Add("declarations", declarations);
            var context = new JsonObject { ["id"] = Context, ["project"] = "Library/Library.csproj",
                ["inputs"] = new JsonArray(Input("census/Subject.cs", "Library/census/Subject.cs", "source", source),
                    Input("census/Declarations.cs", "Library/census/Declarations.cs", "declarations", declarations)) };
            var compilerBuild = new JsonObject { ["id"] = buildId, ["contextId"] = Context,
                ["moduleIdentity"] = build.ModuleIdentity, ["mvid"] = build.Mvid, ["debugIdentity"] = build.DebugIdentity,
                ["assemblySha256"] = Hash(pe), ["pdbSha256"] = Hash(pdb) };
            Artifact("production-pe", "assembly", pe, true); Artifact("production-pdb", "pdb", pdb, true);
            var testPdb = fault == "foreign-pdb" ? foreignPdb : pdb;
            Artifact(prefix + "-pe", "required-test-assembly", pe);
            Artifact(prefix + "-pdb", "required-test-pdb", testPdb);
            var execution = new JsonObject { ["id"] = executionId, ["contextId"] = Context, ["buildId"] = buildId,
                ["completed"] = true, ["exitCode"] = 0, ["testProject"] = project,
                ["totalTests"] = 1, ["passedTests"] = 1, ["failedTests"] = 0, ["skippedTests"] = 0,
                ["testModuleIdentity"] = build.ModuleIdentity, ["testMvid"] = build.Mvid,
                ["testDebugIdentity"] = build.DebugIdentity, ["testAssemblySha256"] = Hash(pe), ["testPdbSha256"] = Hash(testPdb) };
            var methods = new[] { Line("CENSUS_FIRST"), Line("CENSUS_EXCLUDED"), Line("CENSUS_LAST") }.Select(line =>
                new XElement("Method", new XElement("FileRef", new XAttribute("uid", "1")), new XElement("SequencePoints",
                    new XElement("SequencePoint", new XAttribute("fileid", "1"), new XAttribute("sl", line),
                        new XAttribute("vc", uncoverLast && line == Line("CENSUS_LAST") ? 0 : 1)))));
            var coverage = Encoding.UTF8.GetBytes(new XElement("CoverageSession", new XElement("Modules", new XElement("Module",
                new XElement("ModuleName", build.ModuleIdentity), new XElement("Files", new XElement("File", new XAttribute("uid", "1"),
                    new XAttribute("fullPath", Path.Combine(root, "Library", "census", "Subject.cs")))),
                new XElement("Classes", new XElement("Class", new XElement("Methods", methods)))))).ToString(SaveOptions.DisableFormatting));
            var coverageArtifact = Artifact("production-coverage", "coverage", coverage, true);
            coverageArtifact["executionId"] = executionId; coverageArtifact["format"] = "opencover"; coverageArtifact["coordinateKind"] = "line";
            Artifact(prefix + "-coverage-" + ProductionGraph, "required-test-module-coverage", coverage);
            var closureInputs = new JsonArray();
            foreach (var (name, bytes, index) in new[] { ("Subject.cs", source, 0), ("Declarations.cs", declarations, 1) })
            {
                var id = "test-source-" + TestGraph + "-" + index;
                Artifact(id, "test-compiler-source", bytes);
                closureInputs.Add(new JsonObject { ["role"] = "compiler-source", ["logicalPath"] = "census/" + name,
                    ["locator"] = id, ["sha256"] = Hash(bytes), ["length"] = bytes.Length,
                    ["generated"] = fault == "generated" && index == 0 });
            }
            Artifact("test-closure-" + TestGraph, "test-compiler-input-closure", JsonSerializer.SerializeToUtf8Bytes(new
            { provider = "validated-csc-inputs-v1", project, inputs = closureInputs, reusableRecipe = false }));
            Artifact("required-test-inventory", "required-test-inventory", JsonSerializer.SerializeToUtf8Bytes(new
            {
                provider = "finite-sdk-required-test-inventory-v1", reusableRecipe = false,
                tests = new[] { new { graphId = TestGraph, project = fault == "wrong-project" ? "Wrong/Tests.csproj" : project,
                    trx = prefix + "-trx", assembly = prefix + "-pe", pdb = prefix + "-pdb", child = prefix + "-child",
                    moduleIdentity = build.ModuleIdentity, mvid = build.Mvid, debugIdentity = build.DebugIdentity,
                    passed = 1, rawExitCode = 0, modules = new[] { new { graphId = ProductionGraph, artifact = prefix + "-coverage-" + ProductionGraph } } } }
            }));
            var manifestNode = new JsonObject { ["contexts"] = new JsonArray(context), ["builds"] = new JsonArray(compilerBuild),
                ["executions"] = new JsonArray(execution), ["artifacts"] = artifacts };
            using var manifest = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(manifestNode));
            static string Hunk(string path, byte[] bytes)
            {
                var lines = Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n').Split('\n').Select(x => x.TrimEnd('\r')).ToArray();
                return "+++ b/" + path + "\n@@ -0,0 +1," + lines.Length + " @@\n" + string.Join("\n", lines.Select(x => "+" + x)) + "\n";
            }
            var diff = Hunk("Library/census/Subject.cs", source);
            if (!sharedProduction)
                diff += fault == "unknown-source" ? "+++ b/Unknown/Subject.cs\n@@ -0,0 +1 @@\n+unknown\n" :
                    fault == "no-points" ? Hunk(testDirectory + "/census/Declarations.cs", declarations) : Hunk(testDirectory + "/census/Subject.cs", source);
            if (fault == "current-bytes") File.AppendAllText(Path.Combine(root, testDirectory, "census", "Subject.cs"), "// substituted\n");
            return ChangedLineCoverage.ReadCompilerBound(Encoding.UTF8.GetBytes(diff), manifest.RootElement, root,
                locator => values[locator], files, fault == "absent-approval" ? null : [project]);
        }
        // Preserve this small fixture on failure; no recursive cleanup discards evidence.
        public void Dispose() => files.ReleaseKnown();
    }
}
