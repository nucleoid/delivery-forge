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
            var result = CompilerExactReemit.Compare(request, Read(value.GetProperty("compilerCommon")),
                Read(value.GetProperty("compilerCSharp")), Read(value.GetProperty("assembly")), Read(value.GetProperty("pdb")), files);
            if (!result.Equal)
            {
                files.Check();
                TestContext.Current.AddAttachment("reemitted-DeliveryForge.Execution.dll", result.EmittedPe, "application/octet-stream");
                TestContext.Current.AddAttachment("reemitted-DeliveryForge.Execution.pdb", result.EmittedPdb, "application/octet-stream");
                files.Check();
            }
            Assert.True(result.Equal, result.Reason);
            Assert.Contains("body-origin remains unproven", result.Reason, StringComparison.Ordinal);
            // This is a read-only local compilation of retained bytes and exact
            // output comparison. It is NOT a new actual producer run, freshness
            // receipt, semantic body-origin proof, census or gate/baseline PASS.
        }
        finally { files.ReleaseKnown(); }
    }
}
