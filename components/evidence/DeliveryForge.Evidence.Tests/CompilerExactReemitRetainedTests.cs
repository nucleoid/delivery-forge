using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace DeliveryForge.Evidence.Tests;

public sealed class CompilerExactReemitRetainedTests
{
    [Fact]
    [Trait("Category", "InstalledToolAcceptance")]
    public void Retained_real_Execution_inputs_reemit_the_complete_PE_and_PDB_without_executing_a_producer()
    {
        var specPath = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST");
        var specHash = Environment.GetEnvironmentVariable("DELIVERY_FORGE_QUALITY_ACTUAL_MANIFEST_SHA256");
        if (string.IsNullOrEmpty(specPath) || string.IsNullOrEmpty(specHash))
            Assert.Skip("Pinned retained real Execution compiler inputs are not selected; no producer is executed.");
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        try
        {
            var specBytes = files.Read(specPath!, 1024 * 1024);
            Assert.Equal(specHash, InstalledToolFiles.Identity(specBytes));
            using var spec = ProducerJson.Read(specBytes);
            if (!spec.RootElement.TryGetProperty("retainedCompilerReemitComparison", out var selected))
                Assert.Skip("The selected specification has no retained exact compiler comparison DATA.");
            var root = selected.GetProperty("bundleRoot").GetString()!;
            var recipePath = selected.GetProperty("recipePath").GetString()!;
            Assert.True(Path.IsPathFullyQualified(root)); Assert.True(Path.IsPathFullyQualified(recipePath));
            Assert.True(InstalledToolGateProvider.Inside(recipePath, root));
            var recipeBytes = files.Read(recipePath, ProducerJson.MaximumBytes);
            Assert.Equal(selected.GetProperty("recipeSha256").GetString(), InstalledToolFiles.Identity(recipeBytes));
            using var recipe = ProducerJson.Read(recipeBytes);
            var value = recipe.RootElement;
            byte[] Read(JsonElement row)
            {
                files.Check();
                var locator = row.GetProperty("locator").GetString()!;
                InstalledToolProfile.Relative(locator);
                var path = Path.GetFullPath(Path.Combine(root, locator));
                Assert.True(InstalledToolGateProvider.Inside(path, root));
                var bytes = files.Read(path, 128 * 1024 * 1024);
                Assert.Equal(row.GetProperty("length").GetInt64(), bytes.LongLength);
                Assert.Equal(row.GetProperty("sha256").GetString(), InstalledToolFiles.Identity(bytes));
                files.Check(); return bytes;
            }
            // Check the closed selected membership and complete declared extent
            // before reading ANY retained input/output/compiler content.
            var sourceRows = value.GetProperty("sources").EnumerateArray().ToArray();
            var referenceRows = value.GetProperty("references").EnumerateArray().ToArray();
            var embeddedRows = value.GetProperty("embeddedSourcePaths").EnumerateArray().ToArray();
            var observedClosure = value.GetProperty("observedClosureArtifacts").EnumerateArray().ToArray();
            Assert.Equal(27, sourceRows.Length); Assert.Equal(173, referenceRows.Length);
            Assert.Equal(3, embeddedRows.Length); Assert.Equal(12, observedClosure.Length);
            const long maximumCapture = 128L * 1024 * 1024;
            long declared = 0;
            foreach (var row in sourceRows.Concat(referenceRows).Concat(observedClosure).Concat(new[]
                { value.GetProperty("compilerCommon"), value.GetProperty("compilerCSharp"), value.GetProperty("assembly"), value.GetProperty("pdb") }))
            {
                files.Check();
                var length = row.GetProperty("length").GetInt64();
                Assert.InRange(length, 0L, maximumCapture);
                declared = checked(declared + length); Assert.InRange(declared, 0L, maximumCapture);
            }
            var sources = sourceRows.Select(row =>
                new CompilerExactReemit.Input(row.GetProperty("path").GetString()!, Read(row))).ToArray();
            var references = referenceRows.Select(row =>
                new CompilerExactReemit.Reference(row.GetProperty("path").GetString()!, Read(row),
                    row.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()!).ToArray(),
                    row.GetProperty("embedInteropTypes").GetBoolean())).ToArray();
            Assert.Equal(27, sources.Length); Assert.Equal(173, references.Length);
            var embedded = embeddedRows.Select(path => path.GetString()!).ToArray();
            Assert.Equal(3, embedded.Length);
            // These exact retained analyzer/config bytes participate in the
            // selected closure, but are NEVER loaded or executed by the helper.
            foreach (var row in observedClosure) _ = Read(row);
            var request = new CompilerExactReemit.Request(value.GetProperty("baseDirectory").GetString()!,
                value.GetProperty("scalarArguments").EnumerateArray().Select(argument => argument.GetString()!).ToArray(),
                sources, references, embedded);
            var common = Read(value.GetProperty("compilerCommon"));
            var csharp = Read(value.GetProperty("compilerCSharp"));
            var actualAssembly = Read(value.GetProperty("assembly"));
            var actualPdb = Read(value.GetProperty("pdb"));
            var result = CompilerExactReemit.Compare(request, common, csharp, actualAssembly, actualPdb, files);
            if (!result.Equal)
            {
                files.Check();
                TestContext.Current.AddAttachment("reemitted-DeliveryForge.Execution.dll", result.EmittedPe, "application/octet-stream");
                TestContext.Current.AddAttachment("reemitted-DeliveryForge.Execution.pdb", result.EmittedPdb, "application/octet-stream");
                files.Check();
            }
            TestContext.Current.AddAttachment("DeliveryForge.Execution-semantic-runtime-regions.json", result.OriginInventory, "application/json");
            using var origins = ProducerJson.Read(result.OriginInventory);
            Assert.Equal("inventory-only-no-origin-proof", origins.RootElement.GetProperty("disposition").GetString());
            Assert.Equal(27, origins.RootElement.GetProperty("sourceCount").GetInt32());
            Assert.Equal(679, origins.RootElement.GetProperty("noVisibleIlBodyCount").GetInt32());
            Assert.NotEmpty(origins.RootElement.GetProperty("regions").EnumerateArray().ToArray());
            Assert.True(result.Equal, result.Reason);
            Assert.Contains("body-origin remains unproven", result.Reason, StringComparison.Ordinal);
            Assert.True(result.HasProvenSourceOrigin);
            // The fixed retained build used the repository-root /_/ PathMap.
            // Derive roles lexically from its exact project base and captured
            // source bytes; no current source/PDB document path is opened here.
            var repositoryRoot = Path.GetFullPath(Path.Combine(request.BaseDirectory, "..", "..", ".."));
            Assert.Equal("components/execution/DeliveryForge.Execution",
                Path.GetRelativePath(repositoryRoot, request.BaseDirectory).Replace('\\', '/'));
            var utf8 = new UTF8Encoding(false, true);
            var documents = request.Sources.Select(source =>
            {
                files.Check();
                var repositoryPath = Path.GetRelativePath(repositoryRoot, source.Path).Replace('\\', '/');
                InstalledToolProfile.Relative(repositoryPath);
                Assert.True(InstalledToolGateProvider.Inside(source.Path, repositoryRoot));
                var text = utf8.GetString(source.Bytes);
                var document = new CompilerLineCensus.Document(repositoryPath, repositoryPath, source.Path,
                    InstalledToolFiles.Identity(source.Bytes)[7..], text, Generated: false);
                files.Check(); return document;
            }).ToArray();
            Assert.Equal(27, documents.Length);
            // Keep every captured compiler source eligible, including the three
            // embedded/generated trees: no generated-source exclusion is used.
            using var peReader = new PEReader(new MemoryStream(actualAssembly, writable: false));
            var metadata = peReader.GetMetadataReader();
            var module = metadata.GetModuleDefinition();
            Assert.Equal("DeliveryForge.Execution.dll", metadata.GetString(module.Name));
            var entry = Assert.Single(peReader.ReadDebugDirectory(), row => row.Type == DebugDirectoryEntryType.CodeView);
            var codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(actualPdb, writable: false));
            var pdbReader = provider.GetMetadataReader();
            var header = pdbReader.DebugMetadataHeader ?? throw new IOException("Retained Execution PDB has no content identity.");
            Assert.Equal(20, header.Id.Length);
            var content = new BlobContentId(header.Id);
            var build = new CompilerLineCensus.Build("DeliveryForge.Execution", metadata.GetGuid(module.Mvid).ToString("D"),
                CompilerLineCensus.DebugIdentity(codeView.Guid, codeView.Age, entry.Stamp, content.Stamp));
            files.Check();
            var census = CompilerLineCensus.Inspect(actualAssembly, actualPdb, build, documents, files, result);
            Assert.True(census.Complete, census.Reason);
            Assert.Equal(3785, census.Points.Count);
            Assert.NotEmpty(census.ProviderLines);
            Assert.NotEmpty(census.PolicyLines);
            files.Check();
            // Actual retained compiler reconstruction, source-origin closure and
            // complete PDB/H/E census only. No coverage hits are supplied or
            // fabricated; this is no fresh producer/quality receipt, issuer,
            // installed gate, freshness, or gate/baseline PASS.
        }
        finally { files.ReleaseKnown(); }
    }
}
