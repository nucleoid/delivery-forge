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
