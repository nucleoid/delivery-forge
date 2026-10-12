using System.Security.Cryptography;
using System.Text.Json;

namespace DeliveryForge.Planning.Tests;

[Collection(RichPlanImportFixtureCollection.Name)]
public sealed class RichPlanImportObjectProfileTests
{
    [Fact(Timeout = 120_000)]
    public async Task Genuine_repack_preserves_canonical_import_and_actual_pack_auxiliaries()
    {
        await using var repository = new ImportRepositoryFixture(TestContext.Current.CancellationToken);
        try
        {
            var setup = await RichPlanImportGitSafetyTests.Prepare(repository, false);
            await Ready(repository, setup);
            await Repack(repository);
            var objects = Path.Combine(repository.Root, ".git", "objects", "pack");
            var packs = Directory.GetFiles(objects, "pack-*.pack");
            Assert.NotEmpty(packs);
            foreach (var pack in packs) Assert.True(File.Exists(Path.ChangeExtension(pack, ".idx")));
            // Observe actual Git-produced auxiliary files; no synthetic bitmap/reverse-index bytes.
            var auxiliaries = Directory.GetFiles(objects).Where(path => Path.GetExtension(path) is ".rev" or ".bitmap").ToArray();
            Assert.Contains(auxiliaries, path => Path.GetExtension(path) == ".bitmap");
            foreach (var path in auxiliaries) Assert.True(File.Exists(Path.ChangeExtension(path, ".pack")));
            var before = await Ledger(repository, setup);
            await Ready(repository, setup);
            var after = await Ledger(repository, setup);
            await Observation(repository, setup, new { scenario = "actual-packed-roundtrip", packs = packs.Select(Path.GetFileName).ToArray(),
                auxiliaries = auxiliaries.Select(Path.GetFileName).ToArray(), before, after });
            Assert.True(after.Count > before.Count); // Actual selected helper dispatch, not bare output claims.
        }
        catch (Exception failure) { repository.Primary = failure; throw; }
    }

    [Theory(Timeout = 120_000)]
    [InlineData("incomplete-pair", "matching pack/index pairs")]
    [InlineData("alternates", "routing/external object authority")]
    [InlineData("promisor", "packed object encoding/routing")]
    [InlineData("unknown", "object root shape")]
    [InlineData("file-count", "object file bound")]
    [InlineData("individual-bytes", "metadata byte bound")]
    [InlineData("aggregate-bytes", "object aggregate byte bound")]
    public async Task Actual_unsupported_or_over_limit_object_store_refuses_before_any_new_git_dispatch(string scenario, string reason)
    {
        await using var repository = new ImportRepositoryFixture(TestContext.Current.CancellationToken);
        try
        {
            var setup = await RichPlanImportGitSafetyTests.Prepare(repository, false);
            await Ready(repository, setup); // Actual public Reader/Freezer and healthy importer first.
            if (scenario is "incomplete-pair" or "promisor") await Repack(repository);
            var before = await Ledger(repository, setup);
            Assert.NotEmpty(before); // Actual nonce-bound original exit/drains receipts already established.
            _ = await repository.Wait(Task.Run(() =>
            {
                void Check() { _ = repository.WorkRemaining(); }
                var objects = Path.Combine(repository.Root, ".git", "objects"); Check();
                void Sparse(string path, long length)
                {
                    Check();
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    file.SetLength(length); file.Flush(true); Check();
                }
                if (scenario == "incomplete-pair")
                {
                    var index = Directory.GetFiles(Path.Combine(objects, "pack"), "pack-*.idx").Single();
                    var bytes = File.ReadAllBytes(index); Check();
                    var retained = Path.Combine(setup.HelperRoot, "actual-original-pack-index.idx");
                    File.Move(index, retained, overwrite: false); Check();
                    Assert.Equal(Hash(bytes), Hash(File.ReadAllBytes(retained))); // Preserve actual original, never fake a pair.
                }
                else if (scenario == "promisor")
                {
                    var pack = Directory.GetFiles(Path.Combine(objects, "pack"), "pack-*.pack").Single();
                    Sparse(Path.ChangeExtension(pack, ".promisor"), 0);
                }
                else if (scenario == "alternates")
                {
                    var info = Path.Combine(objects, "info"); Directory.CreateDirectory(info);
                    Sparse(Path.Combine(info, "alternates"), 0);
                }
                else if (scenario == "unknown") Sparse(Path.Combine(objects, "owned-unknown-object.json"), 0);
                else
                {
                    var fanout = Path.Combine(objects, "ff"); Directory.CreateDirectory(fanout); Check();
                    var count = scenario == "file-count" ? 4097 : scenario == "aggregate-bytes" ? 9 : 1;
                    var length = scenario == "individual-bytes" ? 64L * 1024 * 1024 + 1 : scenario == "aggregate-bytes" ? 32L * 1024 * 1024 : 0;
                    for (var index = 1; index <= count; index++)
                        Sparse(Path.Combine(fanout, index.ToString("x").PadLeft(38, '0')), length);
                    // These are real ordinary files with real lengths, not valid synthesized Git objects
                    // or mocked counters. They must be rejected BEFORE any Git reads their bodies.
                }
                Check(); return true;
            }, CancellationToken.None));
            var result = await repository.Wait(PlanImporter.ImportAndReconcileAsync(setup.Plan.CanonicalBytes,
                setup.Source, setup.Expectations, repository.WorkRemaining(), cancellationToken: repository.WorkToken));
            var after = await Ledger(repository, setup);
            await Observation(repository, setup, new { scenario, result.DownstreamReady, result.Diagnostics, before, after });
            Assert.False(result.DownstreamReady); Assert.Null(result.Plan);
            Assert.Contains(result.Diagnostics, item => item.Contains(reason, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(before.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
                after.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        }
        catch (Exception failure) { repository.Primary = failure; throw; }
    }

    private static async Task Repack(ImportRepositoryFixture repository)
    {
        await repository.Command(repository.Root, "-c", "pack.writeReverseIndex=true", "repack", "-ad", "--write-bitmap-index");
    }
    private static async Task Ready(ImportRepositoryFixture repository, RichPlanImportGitSafetyTests.Setup setup)
    {
        var result = await repository.Wait(PlanImporter.ImportAndReconcileAsync(setup.Plan.CanonicalBytes,
            setup.Source, setup.Expectations, repository.WorkRemaining(), cancellationToken: repository.WorkToken));
        Assert.True(result.DownstreamReady, string.Join("; ", result.Diagnostics)); Assert.NotNull(result.Plan);
        Assert.Equal(setup.Plan.CanonicalBytes, result.Plan.CanonicalBytes);
        _ = await Ledger(repository, setup);
    }
    private static async Task<Dictionary<string, string>> Ledger(ImportRepositoryFixture repository,
        RichPlanImportGitSafetyTests.Setup setup)
    {
        return await repository.Wait(Task.Run(() =>
        {
            _ = repository.WorkRemaining();
            var entered = Directory.GetFiles(setup.HelperRoot, "invocation-*-entered.json");
            var results = Directory.GetFiles(setup.HelperRoot, "invocation-*-result.json");
            Assert.NotEmpty(entered); Assert.Equal(entered.Length, results.Length);
            if (entered.Length > 512) throw new IOException("Actual fixture invocation ledger exceeds512 entries.");
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in entered.Concat(results))
            {
                _ = repository.WorkRemaining();
                if (new FileInfo(path).Length > 64 * 1024) throw new IOException("Actual helper receipt exceeds64KiB.");
                var bytes = File.ReadAllBytes(path); _ = repository.WorkRemaining();
                using var document = JsonDocument.Parse(bytes); var data = document.RootElement;
                Assert.Equal(setup.Nonce, data.GetProperty("Nonce").GetString());
                if (path.EndsWith("-result.json", StringComparison.Ordinal))
                {
                    Assert.True(data.GetProperty("originalExit").GetBoolean());
                    Assert.True(data.GetProperty("stdoutCompleted").GetBoolean());
                    Assert.True(data.GetProperty("stderrCompleted").GetBoolean());
                    Assert.True(data.GetProperty("cleanupTasksSettled").GetBoolean());
                    Assert.Equal(JsonValueKind.Null, data.GetProperty("primary").ValueKind);
                    Assert.Empty(data.GetProperty("secondary").EnumerateArray());
                    var exit = data.GetProperty("childExit").GetInt32();
                    if (exit == 1)
                    {
                        var args = data.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
                        Assert.Equal(new[] { "config", "--includes", "--get-regexp", "^(remote\\..*\\.promisor|extensions\\.partialclone)$" }, args[^4..]);
                        Assert.Equal(0L, data.GetProperty("stdoutLength").GetInt64());
                        Assert.Equal(0L, data.GetProperty("stderrLength").GetInt64());
                        Assert.Equal(Hash([]), data.GetProperty("stdoutSha256").GetString());
                        Assert.Equal(Hash([]), data.GetProperty("stderrSha256").GetString());
                    }
                    else Assert.Equal(0, exit);
                }
                hashes.Add(Path.GetFileName(path), Hash(bytes));
            }
            _ = repository.WorkRemaining(); return hashes;
        }, CancellationToken.None));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task Observation(ImportRepositoryFixture repository, RichPlanImportGitSafetyTests.Setup setup, object data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { fixture = true, setup.Nonce, data });
        if (bytes.Length > 256 * 1024) throw new IOException("Actual fixture observation exceeds256KiB.");
        _ = await repository.Wait(Task.Run(() =>
        {
            _ = repository.WorkRemaining();
            using var stream = new FileStream(Path.Combine(setup.HelperRoot, "actual-object-profile-" + Guid.NewGuid().ToString("N") + ".json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes); stream.Flush(true); _ = repository.WorkRemaining(); return true;
        }, CancellationToken.None));
    }
}
