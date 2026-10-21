using System.Xml.Linq;
using System.Security.Cryptography;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Globalization;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeliveryForge.Evidence.Tests;

public sealed class CompilerOfficialRecipeTests
{
    [Fact]
    [Trait("Category", "InstalledToolAcceptance")]
    public void Retained_official_target401_recipe_reemits_the_complete_real_PE_and_PDB()
    {
        var specPath = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST");
        var specHash = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST_SHA256");
        if (string.IsNullOrEmpty(specPath) || string.IsNullOrEmpty(specHash))
            Assert.Skip("Pinned official target401 recipe capture is not selected; no producer is executed.");
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        try
        {
            var specBytes = files.Read(specPath!, 1024 * 1024); Assert.Equal(specHash, InstalledToolFiles.Identity(specBytes));
            using var spec = ProducerJson.Read(specBytes);
            if (!spec.RootElement.TryGetProperty("retainedOfficialCompilerReemitComparison", out var selected))
                Assert.Skip("The selected DATA has no official target401 recipe comparison.");
            var root = selected.GetProperty("bundleRoot").GetString()!;
            var manifestPath = selected.GetProperty("manifestPath").GetString()!;
            Assert.True(Path.IsPathFullyQualified(root)); Assert.True(Path.IsPathFullyQualified(manifestPath));
            Assert.True(InstalledToolGateProvider.Inside(manifestPath, root));
            var manifestBytes = files.Read(manifestPath, ProducerJson.MaximumBytes);
            Assert.Equal(selected.GetProperty("manifestSha256").GetString(), InstalledToolFiles.Identity(manifestBytes));
            using var manifest = ProducerJson.Read(manifestBytes);
            byte[] Read(string locator)
            {
                InstalledToolProfile.Relative(locator);
                var path = Path.GetFullPath(Path.Combine(root, locator));
                Assert.True(InstalledToolGateProvider.Inside(path, root));
                return files.Read(path, 128 * 1024 * 1024);
            }
            var value = CompilerOfficialRecipe.Decode(manifest.RootElement, selected.GetProperty("contextId").GetString()!,
                selected.GetProperty("buildId").GetString()!, Read, files);
            // This separately selected natural tiny consumer is not the frozen
            // Execution198 fixture. None of that fixture's assertions changes.
            Assert.Equal(4, value.Request.Sources.Length); Assert.Equal(167, value.Request.References.Length);
            Assert.Equal(3, value.Request.EmbeddedSourcePaths.Length);
            Assert.True(CompilerExactReemit.Supports(value.Request, value.Common, value.CSharp, value.Assembly, value.Pdb, files));
            var result = CompilerExactReemit.Compare(value.Request, value.Common, value.CSharp, value.Assembly, value.Pdb, files);
            if (!result.Equal)
            {
                files.Check();
                TestContext.Current.AddAttachment("official-reemitted-Example.dll", result.EmittedPe, "application/octet-stream");
                TestContext.Current.AddAttachment("official-reemitted-Example.pdb", result.EmittedPdb, "application/octet-stream");
                files.Check();
            }
            TestContext.Current.AddAttachment("official-Example-semantic-regions.json", result.OriginInventory, "application/json");
            using var origins = ProducerJson.Read(result.OriginInventory);
            Assert.Equal("inventory-only-no-origin-proof", origins.RootElement.GetProperty("disposition").GetString());
            Assert.Equal(4, origins.RootElement.GetProperty("sourceCount").GetInt32());
            Assert.True(result.Equal, result.Reason);
            Assert.Contains("body-origin remains unproven", result.Reason, StringComparison.Ordinal);
            Assert.True(result.HasProvenSourceOrigin);
            var build = Assert.Single(manifest.RootElement.GetProperty("builds").EnumerateArray(),
                row => row.GetProperty("id").GetString() == selected.GetProperty("buildId").GetString());
            var expected = new CompilerLineCensus.Build(build.GetProperty("moduleIdentity").GetString()!,
                build.GetProperty("mvid").GetString()!, build.GetProperty("debugIdentity").GetString()!);
            var census = CompilerLineCensus.Inspect(value.Assembly, value.Pdb, expected, value.Documents, files, result);
            Assert.True(census.Complete, census.Reason);
            Assert.NotEmpty(census.PolicyLines);
            // Retained compiler/output/source bytes; DATA diff and measurement
            // mutations below grant no new producer, execution or quality authority.
            var authored = Assert.Single(value.Documents, document => !document.Generated);
            Assert.Equal("Flag.cs", authored.LogicalPath);
            Assert.Equal("src/Example/Flag.cs", authored.RepositoryPath);
            var physical = authored.PhysicalPath!;
            var relative = authored.RepositoryPath!.Replace('/', Path.DirectorySeparatorChar);
            Assert.EndsWith(relative, physical, StringComparison.Ordinal);
            var repository = physical[..^relative.Length].TrimEnd(Path.DirectorySeparatorChar);
            Assert.Equal(physical, Path.GetFullPath(Path.Combine(repository, authored.RepositoryPath)));
            var changedLines = new HashSet<int>(Enumerable.Range(7, 6));
            Assert.Equal(new[] { 7, 8, 9, 10, 11, 12 }, census.ProviderLines
                .Where(line => line.LogicalPath == authored.LogicalPath && changedLines.Contains(line.Number))
                .Select(line => line.Number).Distinct().Order().ToArray());
            Assert.Equal(new[] { 7, 8, 11, 12 }, census.PolicyLines
                .Where(line => line.LogicalPath == authored.LogicalPath && changedLines.Contains(line.Number))
                .Select(line => line.Number).Distinct().Order().ToArray());
            var sourceLines = authored.Text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            var diff = Encoding.UTF8.GetBytes("+++ b/" + authored.RepositoryPath + "\n@@ -0,0 +7,6 @@\n" +
                string.Join("\n", sourceLines.Skip(6).Take(6).Select(line => "+" + line)) + "\n");
            var selectedContext = selected.GetProperty("contextId").GetString()!;
            var selectedBuild = selected.GetProperty("buildId").GetString()!;
            var coverage = Assert.Single(manifest.RootElement.GetProperty("artifacts").EnumerateArray(), row =>
                row.GetProperty("kind").GetString() == "coverage" &&
                row.GetProperty("contextId").GetString() == selectedContext &&
                row.GetProperty("buildId").GetString() == selectedBuild);
            var coverageLocator = coverage.GetProperty("locator").GetString()!;
            var originalCoverage = Read(coverageLocator);
            var originalCoverageHash = InstalledToolFiles.Identity(originalCoverage);
            ChangedLineCoverage.Result Reduce(string? mutation = null)
            {
                if (mutation is null)
                    return ChangedLineCoverage.ReadCompilerBound(diff, manifest.RootElement, repository, Read, files);
                var xml = System.Xml.Linq.XDocument.Parse(new UTF8Encoding(false, true).GetString(originalCoverage));
                var point = Assert.Single(xml.Descendants("SequencePoint"), row =>
                    row.Attribute("sl")?.Value == (mutation == "provider-zero" ? "10" : "7"));
                if (mutation == "required-omitted") point.Remove();
                else point.SetAttributeValue("vc", "0");
                var alteredCoverage = Encoding.UTF8.GetBytes(xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
                var dataManifest = JsonNode.Parse(manifestBytes)!;
                var artifact = Assert.Single(dataManifest["artifacts"]!.AsArray(), row =>
                    row!["id"]!.GetValue<string>() == coverage.GetProperty("id").GetString())!;
                artifact["length"] = alteredCoverage.Length;
                artifact["sha256"] = InstalledToolFiles.Identity(alteredCoverage)[7..];
                using var parsedData = ProducerJson.Read(JsonSerializer.SerializeToUtf8Bytes(dataManifest));
                return ChangedLineCoverage.ReadCompilerBound(diff, parsedData.RootElement, repository,
                    locator => locator == coverageLocator ? alteredCoverage : Read(locator), files);
            }
            void Counts(ChangedLineCoverage.Result reduction, GateOutcome outcome, int hits, int missing)
            {
                Assert.True(reduction.CompilerCensusComplete, reduction.Reason);
                Assert.Equal(4, reduction.ChangedExecutableLines);
                Assert.Equal(hits, reduction.CoveredChangedLines);
                Assert.Equal(hits == 4 ? 100m : 75m, reduction.Percent);
                Assert.Equal(missing, reduction.UnmeasuredChangedLines);
                Assert.Equal(outcome, reduction.Outcome);
            }
            Counts(Reduce(), GateOutcome.Pass, 4, 0);
            Counts(Reduce("provider-zero"), GateOutcome.Pass, 4, 0);
            Counts(Reduce("required-zero"), GateOutcome.Fail, 3, 0);
            Counts(Reduce("required-omitted"), GateOutcome.Incomplete, 3, 1);
            Assert.Equal(originalCoverageHash, InstalledToolFiles.Identity(originalCoverage));
            // The private result cannot transfer to an extra no-point source,
            // changed source text, another physical role or altered module bytes.
            void Refuses(IReadOnlyList<CompilerLineCensus.Document> documents, byte[]? assembly = null) =>
                Assert.Throws<EvidenceWriteException>(() => CompilerLineCensus.Inspect(assembly ?? value.Assembly,
                    value.Pdb, expected, documents, files, result));
            Refuses(value.Documents.Concat([value.Documents[0] with { PhysicalPath = value.Documents[0].PhysicalPath + ".extra" }]).ToArray());
            var changed = value.Documents.ToArray(); changed[0] = changed[0] with { Text = changed[0].Text + " " }; Refuses(changed);
            changed = value.Documents.ToArray(); changed[0] = changed[0] with { PhysicalPath = changed[0].PhysicalPath + ".other" }; Refuses(changed);
            var altered = value.Assembly.ToArray(); altered[^1] ^= 1; Refuses(value.Documents, altered);
            // Actual retained compiler/census behavior only. No fresh producer,
            // loaded-image, installed gate, quality receipt or baseline authority.
        }
        finally { files.ReleaseKnown(); }
    }

    // Add to CompilerOfficialRecipeTests. Additional usings required:
    // System.Buffers.Binary; System.Globalization; System.Reflection.Metadata;
    // System.Reflection.PortableExecutable; System.Security.Cryptography; System.Xml.Linq.
    [Fact]
    [Trait("Category", "InstalledToolAcceptance")]
    public void Compiled_exact401_record_DATA_uses_the_production_recipe_fallback_without_granting_producer_authority()
    {
        var specPath = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST");
        var specHash = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST_SHA256");
        if (string.IsNullOrEmpty(specPath) || string.IsNullOrEmpty(specHash))
            Assert.Skip("Pinned official target401 compiler DATA is not selected; no producer is executed.");
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var fixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "forge-official-branch-" + Guid.NewGuid().ToString("N")));
        try
        {
            var specBytes = files.Read(specPath!, 1024 * 1024);
            Assert.Equal(specHash, InstalledToolFiles.Identity(specBytes));
            using var spec = ProducerJson.Read(specBytes);
            if (!spec.RootElement.TryGetProperty("retainedOfficialCompilerReemitComparison", out var selected))
                Assert.Skip("The selected DATA has no retained official target401 compiler pair.");
            var bundle = selected.GetProperty("bundleRoot").GetString()!;
            var manifestPath = selected.GetProperty("manifestPath").GetString()!;
            Assert.True(Path.IsPathFullyQualified(bundle)); Assert.True(Path.IsPathFullyQualified(manifestPath));
            Assert.True(InstalledToolGateProvider.Inside(manifestPath, bundle));
            var manifestBytes = files.Read(manifestPath, ProducerJson.MaximumBytes);
            Assert.Equal(selected.GetProperty("manifestSha256").GetString(), InstalledToolFiles.Identity(manifestBytes));
            using var originalManifest = ProducerJson.Read(manifestBytes);
            byte[] ReadRetained(string locator)
            {
                InstalledToolProfile.Relative(locator);
                var path = Path.GetFullPath(Path.Combine(bundle, locator));
                Assert.True(InstalledToolGateProvider.Inside(path, bundle));
                return files.Read(path, 128 * 1024 * 1024);
            }
            var contextId = selected.GetProperty("contextId").GetString()!;
            var buildId = selected.GetProperty("buildId").GetString()!;
            var retained = CompilerOfficialRecipe.Decode(originalManifest.RootElement, contextId, buildId, ReadRetained, files);
            Assert.Equal(4, retained.Request.Sources.Length); Assert.Equal(167, retained.Request.References.Length);
            var authoredIndex = Assert.Single(Enumerable.Range(0, retained.Documents.Count), index => !retained.Documents[index].Generated);
            Assert.Equal("Flag.cs", retained.Documents[authoredIndex].LogicalPath);
            Assert.Equal("src/Example/Flag.cs", retained.Documents[authoredIndex].RepositoryPath);
            var project = Path.Combine(fixtureRoot, "src", "Example");
            Directory.CreateDirectory(project);
            string Rebase(string path)
            {
                Assert.True(InstalledToolGateProvider.Inside(path, retained.Request.BaseDirectory));
                var rebased = Path.GetFullPath(Path.Combine(project, Path.GetRelativePath(retained.Request.BaseDirectory, path)));
                Assert.True(InstalledToolGateProvider.Inside(rebased, fixtureRoot));
                return rebased;
            }
            // Fixture source and synthetic visit rows below are DATA. No method is
            // invoked, no producer is rerun, and no installed receipt is minted.
            const string source = "namespace Example;\npublic sealed record Flag\n{\n    public int Value { get; } = 7;\n    public int Read()\n    {\n        return global::System.Math.Max(\n            Value,\n\n            // Provider-only line.\n            1\n        );\n    }\n}\n";
            var fixtureSources = retained.Request.Sources.Select((input, index) => new CompilerExactReemit.Input(Rebase(input.Path),
                index == authoredIndex ? Encoding.UTF8.GetBytes(source) : input.Bytes.ToArray())).ToArray();
            foreach (var input in fixtureSources)
            {
                files.Check(); Assert.True(InstalledToolGateProvider.Inside(input.Path, fixtureRoot));
                Directory.CreateDirectory(Path.GetDirectoryName(input.Path)!); File.WriteAllBytes(input.Path, input.Bytes); files.Check();
            }
            // This specific retained recipe has relative output scalars and no
            // pathmap. Keep that precondition explicit instead of guessing a rewrite.
            Assert.DoesNotContain(retained.Request.ScalarArguments, argument => argument.StartsWith("/pathmap:", StringComparison.Ordinal));
            foreach (var argument in retained.Request.ScalarArguments.Where(argument =>
                argument.StartsWith("/out:", StringComparison.Ordinal) || argument.StartsWith("/refout:", StringComparison.Ordinal) ||
                argument.StartsWith("/pdb:", StringComparison.Ordinal)))
                Assert.False(Path.IsPathFullyQualified(argument[(argument.IndexOf(':') + 1)..]));
            var request = new CompilerExactReemit.Request(project, retained.Request.ScalarArguments,
                fixtureSources, retained.Request.References, retained.Request.EmbeddedSourcePaths.Select(Rebase).ToArray());
            var emitted = CompilerExactReemit.Compare(request, retained.Common, retained.CSharp, [1], [1], files);
            var diagnostic = Path.Combine(fixtureRoot, ".evidence");
            Directory.CreateDirectory(diagnostic);
            File.WriteAllBytes(Path.Combine(diagnostic, "fixture.dll"), emitted.EmittedPe);
            File.WriteAllBytes(Path.Combine(diagnostic, "fixture.pdb"), emitted.EmittedPdb);
            Assert.False(emitted.Equal); Assert.False(emitted.HasProvenSourceOrigin);
            TestContext.Current.AddAttachment("compiled-record-fixture-semantic-regions.json", emitted.OriginInventory, "application/json");
            using (var origins = ProducerJson.Read(emitted.OriginInventory))
                Assert.True(origins.RootElement.GetProperty("sourceExhaustion").GetProperty("complete").GetBoolean(),
                    origins.RootElement.GetProperty("sourceExhaustion").GetRawText());
            var expected = BranchFixtureBuild(emitted.EmittedPe);
            var documents = retained.Documents.Select((document, index) => document with
            {
                PhysicalPath = fixtureSources[index].Path,
                Sha256 = InstalledToolFiles.Identity(fixtureSources[index].Bytes)[7..],
                Text = new UTF8Encoding(false, true).GetString(fixtureSources[index].Bytes)
            }).ToArray();
            var legacy = CompilerLineCensus.Inspect(emitted.EmittedPe, emitted.EmittedPdb, expected, documents, files);
            Assert.False(legacy.Complete); // Essential: the fixture must use fallback.
            // The real record copy constructor also has a visible point over
            // the declaration's Flag token. Keep it in both inventories and
            // require its measurement alongside the accessor and method body.
            var recordPoint = Assert.Single(legacy.Points, point => point.LogicalPath == "Flag.cs" && point.StartLine == 2);
            Assert.Equal((22, 2, 26), (recordPoint.StartColumn, recordPoint.EndLine, recordPoint.EndColumn));
            Assert.Equal(new[] { 2, 4, 7, 8, 9, 10, 11, 12 }, legacy.ProviderLines.Where(line => line.LogicalPath == "Flag.cs")
                .Select(line => line.Number).Distinct().Order().ToArray());
            Assert.Equal(new[] { 2, 4, 7, 8, 11, 12 }, legacy.PolicyLines.Where(line => line.LogicalPath == "Flag.cs")
                .Select(line => line.Number).Distinct().Order().ToArray());

            // Clone retained DATA solely as a compact closed-shape fixture template.
            // Context/build labels below are test labels, never canonical receipt claims.
            var manifest = JsonNode.Parse(manifestBytes)!;
            JsonObject SelectNode(string collection, string id) => Assert.Single(manifest[collection]!.AsArray(),
                row => row!["id"]!.GetValue<string>() == id)!.AsObject();
            var context = SelectNode("contexts", contextId); var build = SelectNode("builds", buildId);
            var recipeArtifact = Assert.Single(manifest["artifacts"]!.AsArray(), row => row!["kind"]!.GetValue<string>() == "compiler-invocation-recipe" &&
                row["contextId"]!.GetValue<string>() == contextId && row["buildId"]!.GetValue<string>() == buildId)!.AsObject();
            var recipe = JsonNode.Parse(ReadRetained(recipeArtifact["locator"]!.GetValue<string>()))!.AsObject();
            // Retained analyzer/config bytes stay inert and are never loaded.
            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            void Descriptor(JsonObject row, byte[] bytes, string locator, bool rawHash)
            {
                data[locator] = bytes; row["locator"] = locator; row["length"] = bytes.Length;
                row["sha256"] = rawHash ? InstalledToolFiles.Identity(bytes)[7..] : InstalledToolFiles.Identity(bytes);
            }
            recipe["baseDirectory"] = project;
            for (var index = 0; index < fixtureSources.Length; index++)
            {
                var input = fixtureSources[index]; var logical = retained.Documents[index].LogicalPath;
                var sourceRow = recipe["sources"]![index]!.AsObject();
                Assert.Equal(logical, sourceRow["logicalPath"]!.GetValue<string>());
                sourceRow["path"] = input.Path;
                var locator = "fixture/source-" + index.ToString(CultureInfo.InvariantCulture) + ".bin";
                Descriptor(sourceRow, input.Bytes, locator, false);
                var captured = Assert.Single(context["inputs"]!.AsArray(), row => row!["logicalPath"]!.GetValue<string>() == logical &&
                    row["role"]!.GetValue<string>() is "source" or "compiler-source")!.AsObject();
                Descriptor(captured, input.Bytes, locator, true);
                var mapping = Assert.Single(context["pathPolicy"]!["reportRootMappings"]!.AsArray(), row =>
                    row!["logicalRoot"]!.GetValue<string>() == logical)!;
                mapping["reportRoot"] = input.Path;
            }
            recipe["embeddedSourcePaths"] = new JsonArray(request.EmbeddedSourcePaths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
            void Output(string recipeName, string buildHash, byte[] bytes)
            {
                var row = recipe[recipeName]!.AsObject(); var artifact = SelectNode("artifacts", row["artifactId"]!.GetValue<string>());
                row["compilerPath"] = Rebase(row["compilerPath"]!.GetValue<string>());
                var locator = "fixture/" + recipeName + ".bin";
                Descriptor(row, bytes, locator, false); Descriptor(artifact, bytes, locator, true);
                build[buildHash] = InstalledToolFiles.Identity(bytes)[7..];
            }
            Output("assembly", "assemblySha256", emitted.EmittedPe); Output("pdb", "pdbSha256", emitted.EmittedPdb);
            recipe["referenceAssembly"] = null; // No retained reference-output authority.
            build["moduleIdentity"] = expected.ModuleIdentity; build["mvid"] = expected.Mvid; build["debugIdentity"] = expected.DebugIdentity;
            var arguments = new JsonArray(); var roles = new JsonArray();
            void Argument(string role, int ordinal, string? name, string argument)
            {
                var raw = Encoding.UTF8.GetBytes(argument); var position = arguments.Count;
                arguments.Add(new JsonObject
                {
                    ["ordinal"] = position,
                    ["utf8Length"] = raw.Length,
                    ["sha256"] = InstalledToolFiles.Identity(raw)[7..]
                });
                roles.Add(new JsonObject { ["ordinal"] = position, ["role"] = role, ["inputOrdinal"] = ordinal, ["name"] = name });
            }
            for (var index = 0; index < request.ScalarArguments.Length; index++)
            {
                var argument = request.ScalarArguments[index]; Argument("scalar", index, argument[1..].Split(':', 2)[0], argument);
            }
            for (var index = 0; index < request.References.Length; index++)
            {
                var reference = request.References[index]; var name = reference.EmbedInteropTypes ? "link" : "reference";
                var aliases = reference.Aliases.Length == 0 ? "" : string.Join(',', reference.Aliases) + "=";
                Argument("reference", index, name, "/" + name + ":" + aliases + reference.Path);
            }
            for (var index = 0; index < fixtureSources.Length; index++) Argument("source", index, null, fixtureSources[index].Path);
            for (var index = 0; index < request.EmbeddedSourcePaths.Length; index++)
                Argument("embedded-source", index, "embed", "/embed:" + request.EmbeddedSourcePaths[index]);
            foreach (var (capturedRole, argumentRole) in new[] { ("analyzer", "analyzer"), ("analyzerConfig", "analyzerconfig"), ("additionalFile", "additionalfile") })
            {
                var ordinal = 0;
                foreach (var row in recipe["observedClosureArtifacts"]!.AsArray())
                    if (row!["role"]!.GetValue<string>() == capturedRole)
                        Argument(argumentRole, ordinal++, argumentRole, "/" + argumentRole + ":" + row["compilerPath"]!.GetValue<string>());
            }
            recipe["arguments"] = arguments; recipe["argumentRoles"] = roles;
            recipe["taskAssociationSha256"] = InstalledToolFiles.Identity(JsonSerializer.SerializeToUtf8Bytes(arguments))[7..];
            Descriptor(recipeArtifact, JsonSerializer.SerializeToUtf8Bytes(recipe), "fixture/recipe.json", true);
            var coverageArtifact = Assert.Single(manifest["artifacts"]!.AsArray(), row => row!["kind"]!.GetValue<string>() == "coverage" &&
                row["contextId"]!.GetValue<string>() == contextId && row["buildId"]!.GetValue<string>() == buildId)!.AsObject();
            var points = new[] { 2, 4, 7, 8, 9, 10, 11, 12 }.Select(line => new XElement("SequencePoint",
                new XAttribute("sl", line), new XAttribute("vc", 1), new XAttribute("fileid", "1")));
            var coverageMethod = new XElement("Method", new XElement("FileRef", new XAttribute("uid", "1")),
                new XElement("SequencePoints", points));
            var coverageModule = new XElement("Module", new XElement("ModuleName", expected.ModuleIdentity),
                new XElement("Files", new XElement("File", new XAttribute("uid", "1"),
                    new XAttribute("fullPath", fixtureSources[authoredIndex].Path))),
                new XElement("Classes", new XElement("Class", new XElement("Methods", coverageMethod))));
            var coverage = new XElement("CoverageSession", new XElement("Modules", coverageModule));
            Descriptor(coverageArtifact, Encoding.UTF8.GetBytes(coverage.ToString(SaveOptions.DisableFormatting)), "fixture/coverage.xml", true);
            var sourceLines = source.TrimEnd('\n').Split('\n');
            var diff = Encoding.UTF8.GetBytes("+++ b/src/Example/Flag.cs\n@@ -0,0 +1," + sourceLines.Length.ToString(CultureInfo.InvariantCulture) + " @@\n" +
                string.Join("\n", sourceLines.Select(line => "+" + line)) + "\n");
            var compilerLocators = new HashSet<string>(new[] { recipe["compilerCommon"]!["locator"]!.GetValue<string>(),
                recipe["compilerCSharp"]!["locator"]!.GetValue<string>() }, StringComparer.Ordinal);
            var compilerReads = new HashSet<string>(StringComparer.Ordinal);
            byte[] ReadFixture(string locator)
            {
                if (compilerLocators.Contains(locator)) compilerReads.Add(locator);
                return data.TryGetValue(locator, out var bytes) ? bytes : ReadRetained(locator);
            }

            File.WriteAllBytes(Path.Combine(diagnostic, "manifest-DATA.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
            File.WriteAllBytes(Path.Combine(diagnostic, "diff-DATA.patch"), diff);
            using (var parsed = ProducerJson.Read(JsonSerializer.SerializeToUtf8Bytes(manifest)))
            {
                var reduction = ChangedLineCoverage.ReadCompilerBound(diff, parsed.RootElement, fixtureRoot, ReadFixture, files);
                Assert.True(reduction.CompilerCensusComplete, reduction.Reason);
                Assert.Equal(GateOutcome.Pass, reduction.Outcome); Assert.Equal(6, reduction.ChangedExecutableLines);
                Assert.Equal(6, reduction.CoveredChangedLines); Assert.Equal(100m, reduction.Percent);
                Assert.Equal(0, reduction.UnmeasuredChangedLines);
                Assert.True(compilerReads.SetEquals(compilerLocators));
            }
            // Same exact source/module/visit DATA, now without the optional recipe.
            // A complete result here would mean the positive did not need fallback.
            compilerReads.Clear(); manifest["artifacts"]!.AsArray().Remove(recipeArtifact);
            File.WriteAllBytes(Path.Combine(diagnostic, "manifest-no-recipe-DATA.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
            using (var parsed = ProducerJson.Read(JsonSerializer.SerializeToUtf8Bytes(manifest)))
            {
                var reduction = ChangedLineCoverage.ReadCompilerBound(diff, parsed.RootElement, fixtureRoot, ReadFixture, files);
                Assert.False(reduction.CompilerCensusComplete); Assert.Equal(GateOutcome.Incomplete, reduction.Outcome);
                Assert.Empty(compilerReads);
            }
        }
        finally
        {
            files.ReleaseKnown();
            // Retain test fixture source on either outcome; it contains no secrets
            // and lets failures be inspected without deleting original evidence.
            Console.WriteLine("Compiled recipe branch DATA fixture retained: " + fixtureRoot);
        }
    }

    private static CompilerLineCensus.Build BranchFixtureBuild(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false); using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader(); var module = metadata.GetModuleDefinition();
        var entry = Assert.Single(pe.ReadDebugDirectory(), row => row.Type == DebugDirectoryEntryType.CodeView);
        var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Field(string value)
        {
            var raw = Encoding.UTF8.GetBytes(value); Span<byte> length = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(length, raw.Length); digest.AppendData(length); digest.AppendData(raw);
        }
        Field("sha256-canonical-v1"); Field("portable-pdb-debug-identity-v1");
        foreach (var field in new[] { codeView.Guid.ToString("D"), codeView.Age.ToString(CultureInfo.InvariantCulture),
            entry.Stamp.ToString("x8", CultureInfo.InvariantCulture), entry.Stamp.ToString("x8", CultureInfo.InvariantCulture) })
        { digest.AppendData([1]); Field(field); }
        return new(Path.GetFileNameWithoutExtension(metadata.GetString(module.Name)), metadata.GetGuid(module.Mvid).ToString("D"),
            Convert.ToHexStringLower(digest.GetHashAndReset()));
    }

    [Fact]
    public void Closed_DATA_decoder_preserves_order_and_never_opens_compiler_paths()
    {
        using var fixture = new Fixture();
        var value = fixture.Decode();
        Assert.Single(value.Request.Sources); Assert.Single(value.Request.References);
        Assert.Equal(fixture.SourceBytes, value.Request.Sources[0].Bytes);
        Assert.Equal(fixture.ReferenceBytes, value.Request.References[0].Bytes);
        Assert.Equal(fixture.SourcePath, value.Request.Sources[0].Path);
        Assert.All(fixture.Reads, locator => Assert.StartsWith("inputs/", locator, StringComparison.Ordinal));
        // These deliberately noncompiler bytes are DATA decoder controls, not
        // target compiler acceptance; Compare would reject their hard pins.
        Assert.False(CompilerExactReemit.Supports(value.Request, value.Common, value.CSharp, value.Assembly, value.Pdb, fixture.Files));
        Assert.Throws<IOException>(() => CompilerExactReemit.Compare(value.Request, value.Common, value.CSharp,
            value.Assembly, value.Pdb, fixture.Files));
    }

    [Theory]
    [InlineData("context", "selected context/build")]
    [InlineData("build", "foreign build context")]
    [InlineData("role", "typed argument role")]
    [InlineData("artifact", "artifact kind/context/build")]
    [InlineData("scalar", "scalar argument digest/name")]
    [InlineData("extent", "aggregate declared extent")]
    [InlineData("aliases", "reference aliases")]
    [InlineData("coherent-aliases", "canonical reference property identity")]
    [InlineData("source-path", "exact captured source path role")]
    [InlineData("unknown", "closed object shape")]
    [InlineData("duplicate-map", "duplicate captured source path role")]
    [InlineData("duplicate-output", "unique actual build output role")]
    public void Coherently_rehashed_recipe_DATA_substitution_refuses_before_compiler_image_reads(string fault, string reason)
    {
        using var fixture = new Fixture(); _ = fixture.Decode(); // Same-binding positive first.
        fixture.Reads.Clear(); fixture.Change(fault);
        var error = Assert.Throws<EvidenceWriteException>(() => fixture.Decode());
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.CommonLocator, fixture.Reads);
        Assert.DoesNotContain(fixture.CSharpLocator, fixture.Reads);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly InstalledToolFiles Files = new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        internal readonly byte[] SourceBytes = Encoding.UTF8.GetBytes("public static class Subject {}\n"), ReferenceBytes = [1, 2, 3];
        private readonly Dictionary<string, byte[]> bytes = new(StringComparer.Ordinal);
        internal readonly List<string> Reads = [];
        internal readonly string SourcePath;
        internal string CommonLocator => common["locator"]!.GetValue<string>();
        internal string CSharpLocator => csharp["locator"]!.GetValue<string>();
        private readonly JsonObject manifest, recipe, common, csharp;
        private string selectedContext = "context";
        internal Fixture()
        {
            var root = Path.Combine(Path.GetTempPath(), "official-recipe-data-" + Guid.NewGuid().ToString("N"));
            SourcePath = Path.Combine(root, "Subject.cs");
            var source = Input("source", "Subject.cs", SourceBytes);
            var reference = Input("reference", "references/" + InstalledToolFiles.Identity(ReferenceBytes)[7..] + ".dll", ReferenceBytes);
            common = FileRow("common", "compiler-common", [4], Path.Combine(root, "never-open-common.dll"));
            csharp = FileRow("csharp", "compiler-csharp", [5], Path.Combine(root, "never-open-csharp.dll"));
            var pe = FileRow("pe", null, [6], Path.Combine(root, "Subject.dll"));
            var pdb = FileRow("pdb", null, [7], Path.Combine(root, "Subject.pdb"));
            var artifacts = new JsonArray(Artifact(common, "compiler-image"), Artifact(csharp, "compiler-image"), Artifact(pe, "assembly"), Artifact(pdb, "pdb"));
            manifest = new JsonObject
            {
                ["manifestSchemaVersion"] = "1.0",
                ["contexts"] = new JsonArray(new JsonObject
                {
                    ["id"] = "context",
                    ["inputs"] = new JsonArray(source, reference),
                    ["pathPolicy"] = new JsonObject { ["reportRootMappings"] = new JsonArray(new JsonObject { ["logicalRoot"] = "Subject.cs", ["reportRoot"] = SourcePath }) }
                }),
                ["builds"] = new JsonArray(new JsonObject { ["id"] = "build", ["contextId"] = "context", ["assemblySha256"] = pe["sha256"]!.GetValue<string>()[7..], ["pdbSha256"] = pdb["sha256"]!.GetValue<string>()[7..] }),
                ["artifacts"] = artifacts
            };
            var scalar = new[] { "/noconfig", "/nostdlib+" };
            var arguments = scalar.Concat([SourcePath, "/reference:" + Path.Combine(root, "ref.dll")]).ToArray();
            var argumentRows = new JsonArray();
            for (var i = 0; i < arguments.Length; i++) argumentRows.Add(new JsonObject
            { ["ordinal"] = i, ["utf8Length"] = Encoding.UTF8.GetByteCount(arguments[i]), ["sha256"] = InstalledToolFiles.Identity(Encoding.UTF8.GetBytes(arguments[i]))[7..] });
            JsonObject Role(int ordinal, string role, int input, string? name) => new()
            { ["ordinal"] = ordinal, ["role"] = role, ["inputOrdinal"] = input, ["name"] = name };
            var images = new JsonArray();
            foreach (var role in new[] { "dotnet-host", "csc", "compiler-common", "compiler-csharp", "compiler-deps", "compiler-runtimeconfig", "sdk-csc-target", "sdk-csc-task" })
                images.Add(new JsonObject { ["role"] = role, ["length"] = 1, ["sha256"] = role == "compiler-common" ? common["sha256"]!.GetValue<string>()[7..] : csharp["sha256"]!.GetValue<string>()[7..] });
            recipe = new JsonObject
            {
                ["provider"] = "completed-csc-typed-input-recipe-v1",
                ["disposition"] = "typed-input-data-only",
                ["contextId"] = "context",
                ["buildId"] = "build",
                ["baseDirectory"] = root,
                ["scalarArguments"] = new JsonArray(scalar.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["sources"] = new JsonArray(new JsonObject
                {
                    ["path"] = SourcePath,
                    ["locator"] = source["locator"]!.DeepClone(),
                    ["length"] = SourceBytes.Length,
                    ["sha256"] = InstalledToolFiles.Identity(SourceBytes),
                    ["ordinal"] = 0,
                    ["role"] = "source",
                    ["logicalPath"] = "Subject.cs",
                    ["encoding"] = "utf-8",
                    ["generated"] = false
                }),
                ["references"] = new JsonArray(new JsonObject
                {
                    ["path"] = Path.Combine(root, "ref.dll"),
                    ["locator"] = reference["locator"]!.DeepClone(),
                    ["length"] = ReferenceBytes.Length,
                    ["sha256"] = InstalledToolFiles.Identity(ReferenceBytes),
                    ["aliases"] = new JsonArray(),
                    ["embedInteropTypes"] = false,
                    ["ordinal"] = 0,
                    ["logicalPath"] = reference["logicalPath"]!.DeepClone()
                }),
                ["embeddedSourcePaths"] = new JsonArray(),
                ["compilerCommon"] = common.DeepClone(),
                ["compilerCSharp"] = csharp.DeepClone(),
                ["assembly"] = pe,
                ["pdb"] = pdb,
                ["referenceAssembly"] = null,
                ["observedClosureArtifacts"] = new JsonArray(),
                ["taskAssociationSha256"] = new string('a', 64),
                ["taskBinding"] = "selected-sdk-core-target",
                ["commandBinding"] = "selected-host-exec-csc-arguments-match",
                ["images"] = images,
                ["arguments"] = argumentRows,
                ["argumentRoles"] = new JsonArray(Role(0, "scalar", 0, "noconfig"), Role(1, "scalar", 1, "nostdlib+"), Role(2, "source", 0, null), Role(3, "reference", 0, "reference")),
                ["taskOptions"] = new JsonArray(),
                ["unsupportedAttestations"] = new JsonArray("recipe.loadedCompilerAndTaskImageNotAttested", "recipe.runtimeLoadedImageNotAttested")
            };
            RebindRecipe();
        }
        private JsonObject Input(string role, string logical, byte[] content)
        {
            var hash = InstalledToolFiles.Identity(content)[7..]; var locator = "inputs/" + hash + ".bin"; bytes[locator] = content;
            return new() { ["role"] = role, ["logicalPath"] = logical, ["locator"] = locator, ["length"] = content.Length, ["sha256"] = hash, ["encoding"] = role == "source" ? "utf-8" : null, ["generated"] = false };
        }
        private JsonObject FileRow(string id, string? role, byte[] content, string compilerPath)
        {
            var identity = InstalledToolFiles.Identity(content); var locator = "inputs/" + identity[7..] + ".bin"; bytes[locator] = content;
            var value = new JsonObject { ["locator"] = locator, ["length"] = content.Length, ["sha256"] = identity, ["artifactId"] = id, ["compilerPath"] = compilerPath };
            if (role is not null) value["role"] = role; return value;
        }
        private static JsonObject Artifact(JsonObject row, string kind) => new()
        {
            ["id"] = row["artifactId"]!.DeepClone(),
            ["kind"] = kind,
            ["contextId"] = "context",
            ["buildId"] = "build",
            ["locator"] = row["locator"]!.DeepClone(),
            ["length"] = row["length"]!.DeepClone(),
            ["sha256"] = row["sha256"]!.GetValue<string>()[7..]
        };
        private void RebindRecipe()
        {
            var raw = JsonSerializer.SerializeToUtf8Bytes(recipe); var identity = InstalledToolFiles.Identity(raw); var locator = "inputs/" + identity[7..] + ".bin"; bytes[locator] = raw;
            var artifacts = manifest["artifacts"]!.AsArray(); var old = artifacts.SingleOrDefault(n => n!["kind"]!.GetValue<string>() == "compiler-invocation-recipe");
            if (old is not null) artifacts.Remove(old);
            artifacts.Add(new JsonObject { ["id"] = "recipe", ["kind"] = "compiler-invocation-recipe", ["contextId"] = "context", ["buildId"] = "build", ["locator"] = locator, ["length"] = raw.Length, ["sha256"] = identity[7..] });
        }
        internal void Change(string fault)
        {
            if (fault == "context") selectedContext = "foreign";
            if (fault == "build") manifest["builds"]![0]!["contextId"] = "foreign";
            if (fault == "role") recipe["argumentRoles"]![2]!["inputOrdinal"] = 1;
            if (fault == "artifact") manifest["artifacts"]![0]!["kind"] = "foreign";
            if (fault == "scalar") recipe["scalarArguments"]![0] = "/noconfig-different";
            if (fault == "extent")
            {
                recipe["sources"]![0]!["length"] = 128L * 1024 * 1024 + 1;
                manifest["contexts"]![0]!["inputs"]![0]!["length"] = 128L * 1024 * 1024 + 1;
            }
            if (fault == "aliases") recipe["references"]![0]!["aliases"] = new JsonArray("Probe");
            if (fault == "coherent-aliases")
            {
                recipe["references"]![0]!["aliases"] = new JsonArray("Probe");
                manifest["contexts"]![0]!["inputs"]![1]!["referenceAliases"] = "Probe";
            }
            if (fault == "source-path") recipe["sources"]![0]!["path"] = Path.Combine(Path.GetDirectoryName(SourcePath)!, "Foreign.cs");
            if (fault == "unknown") recipe["extra"] = true;
            if (fault == "duplicate-map")
            {
                var maps = manifest["contexts"]![0]!["pathPolicy"]!["reportRootMappings"]!.AsArray();
                maps.Add(maps[0]!.DeepClone());
            }
            if (fault == "duplicate-output")
            {
                var outputs = manifest["artifacts"]!.AsArray();
                var extra = outputs.Single(row => row!["kind"]!.GetValue<string>() == "assembly")!.DeepClone();
                extra["id"] = "another-output"; outputs.Add(extra);
            }
            RebindRecipe();
        }
        internal CompilerOfficialRecipe.Decoded Decode()
        {
            using var document = ProducerJson.Read(JsonSerializer.SerializeToUtf8Bytes(manifest));
            return CompilerOfficialRecipe.Decode(document.RootElement, selectedContext, "build", locator =>
            { Reads.Add(locator); return bytes[locator]; }, Files);
        }
        public void Dispose() => Files.ReleaseKnown();
    }
}
