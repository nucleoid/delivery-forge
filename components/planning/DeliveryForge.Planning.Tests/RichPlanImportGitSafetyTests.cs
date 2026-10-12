using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;

namespace DeliveryForge.Planning.Tests;

[Collection(RichPlanImportFixtureCollection.Name)]
public sealed class RichPlanImportGitSafetyTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("null")]
    [InlineData("blank")]
    [InlineData("whitespace")]
    [InlineData("relative")]
    [InlineData("missing")]
    public async Task Mandatory_selected_import_git_never_falls_back_to_ambient_path(string scenario)
    {
        await using var repository = new ImportRepositoryFixture(TestContext.Current.CancellationToken);
        try
        {
            var setup = await Prepare(repository, race: false);
            var configured = scenario switch
            {
                "null" => null!, "blank" => "", "whitespace" => " \t",
                "relative" => "never-start-relative-git",
                "missing" => Path.Combine(setup.HelperRoot, "never-start-missing-git"),
                _ => throw new InvalidOperationException("Unknown mandatory executable specification.")
            };
            var actual = PlanImporter.ImportAndReconcileAsync(setup.Plan.CanonicalBytes,
                setup.Source with { TrustedGitExecutable = configured }, setup.Expectations, repository.WorkRemaining(),
                cancellationToken: repository.WorkToken);
            var refusal = await Assert.ThrowsAsync<PlanningException>(() => repository.Wait(actual));
            Assert.Contains("Git", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(setup.HelperRoot, "invocation-*-entered.json"));
            Assert.False(File.Exists(Path.Combine(setup.HelperRoot, "filter-entered.json")));
            // No supplied facts or PATH mutation. With current blank/null behavior,
            // a complete Ready result from real ambient Git fails the exception assertion.
        }
        catch (Exception failure) { repository.Primary = failure; throw; }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_config_at_real_status_boundary_cannot_execute_a_local_clean_filter(bool race)
    {
        await using var repository = new ImportRepositoryFixture(TestContext.Current.CancellationToken);
        Task<PlanImportResult>? actual = null; Setup? setup = null;
        byte[]? before = null; string? configPath = null;
        Exception? primary = null; var cleanup = new List<Exception>();
        try
        {
            setup = await Prepare(repository, race);
            configPath = Path.Combine(repository.Root, ".git", "config");
            before = await repository.Wait(File.ReadAllBytesAsync(configPath, repository.WorkToken));
            actual = PlanImporter.ImportAndReconcileAsync(setup.Plan.CanonicalBytes, setup.Source, setup.Expectations,
                repository.WorkRemaining(), cancellationToken: repository.WorkToken);
            if (race)
            {
                var arrivalPath = Path.Combine(setup.HelperRoot, "race-entered.json");
                await WaitUntil(repository, () => File.Exists(arrivalPath));
                using var arrival = JsonDocument.Parse(await repository.Wait(File.ReadAllBytesAsync(arrivalPath, repository.WorkToken)));
                Assert.Equal(setup.Nonce, arrival.RootElement.GetProperty("Nonce").GetString());
                Assert.Contains(arrival.RootElement.GetProperty("arguments").EnumerateArray(), item => item.GetString() == "status");
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(before)), arrival.RootElement.GetProperty("configSha256").GetString());
                Assert.Equal(before, await repository.Wait(File.ReadAllBytesAsync(configPath, repository.WorkToken)));
                await Release(setup, repository);
            }
            PlanImportResult? result = null; Exception? importFailure = null;
            try { result = await repository.Wait(actual); }
            catch (Exception failure) { importFailure = failure; }
            // Even a later owner refusal cannot erase the external side effect.
            Assert.False(File.Exists(Path.Combine(setup.HelperRoot, "filter-entered.json")));
            if (importFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(importFailure).Throw();
            Assert.NotNull(result);
            Assert.True(result.DownstreamReady); Assert.NotNull(result.Plan);
            Assert.Equal(setup.Plan.Identity, result.Plan.Identity);
            Assert.Equal(setup.Plan.CanonicalBytes, result.Plan.CanonicalBytes);
            Assert.Equal(before, await repository.Wait(File.ReadAllBytesAsync(configPath, repository.WorkToken)));
            var entries = Directory.GetFiles(setup.HelperRoot, "invocation-*-entered.json");
            var receipts = Directory.GetFiles(setup.HelperRoot, "invocation-*-result.json");
            Assert.NotEmpty(entries); Assert.Equal(entries.Length, receipts.Length);
            foreach (var path in receipts)
            {
                using var receipt = JsonDocument.Parse(await repository.Wait(File.ReadAllBytesAsync(path, repository.WorkToken)));
                Assert.Equal(setup.Nonce, receipt.RootElement.GetProperty("Nonce").GetString());
                Assert.True(receipt.RootElement.GetProperty("originalExit").GetBoolean());
                Assert.True(receipt.RootElement.GetProperty("stdoutCompleted").GetBoolean());
                Assert.True(receipt.RootElement.GetProperty("stderrCompleted").GetBoolean());
                Assert.True(receipt.RootElement.GetProperty("cleanupTasksSettled").GetBoolean());
                var exitCode = receipt.RootElement.GetProperty("childExit").GetInt32();
                if (exitCode == 1)
                {
                    // This ONE actual allowFailure query uses Git's no-match=1
                    // contract. No other nonzero command is a healthy observation.
                    var arguments = receipt.RootElement.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray();
                    Assert.True(arguments.Length >= 4);
                    Assert.Equal(new[] { "config", "--includes", "--get-regexp", "^(remote\\..*\\.promisor|extensions\\.partialclone)$" }, arguments[^4..]);
                    Assert.Equal(0L, receipt.RootElement.GetProperty("stdoutLength").GetInt64());
                    Assert.Equal(0L, receipt.RootElement.GetProperty("stderrLength").GetInt64());
                    var emptyHash = Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));
                    Assert.Equal(emptyHash, receipt.RootElement.GetProperty("stdoutSha256").GetString());
                    Assert.Equal(emptyHash, receipt.RootElement.GetProperty("stderrSha256").GetString());
                }
                else Assert.Equal(0, exitCode);
                Assert.True(receipt.RootElement.GetProperty("elapsedSeconds").GetDouble() < 30);
                Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("primary").ValueKind);
                Assert.Empty(receipt.RootElement.GetProperty("secondary").EnumerateArray());
            }
            // Receipts prove only this fixture's original real-Git root/streams.
            // Root's enclosing delegated physical scope separately guards the tree.
        }
        catch (Exception failure) { primary = failure; repository.Primary = failure; throw; }
        finally
        {
            // Release and original operation observation are independent stages,
            // clipped to the SAME original fixture work allowance, never a new tail.
            if (setup is not null && race)
                try { if (!File.Exists(Path.Combine(setup.HelperRoot, "race-release.json"))) await Release(setup, repository, cleanup: true); }
                catch (Exception failure) { cleanup.Add(failure); }
            if (actual is not null)
                try { _ = await repository.ObserveCleanup(() => actual!); }
                catch (Exception failure) { if (!ReferenceEquals(failure, primary)) cleanup.Add(failure); }
            if (before is not null && configPath is not null)
                try { _ = await repository.ObserveCleanup(async () =>
                    {
                        // Teardown deliberately ignores the canceled WORK token;
                        // ObserveCleanup clips/retains this actual task under the
                        // original120s endpoint and original10s cleanup reserve.
#pragma warning disable xUnit1051
                        await File.WriteAllBytesAsync(configPath, before, CancellationToken.None);
#pragma warning restore xUnit1051
                        return true;
                    }); }
                catch (Exception failure) { cleanup.Add(failure); }
            if (cleanup.Count > 0)
            {
                var combined = new AggregateException("Real import primary and independent release/operation observations", primary is null ? cleanup : new[] { primary }.Concat(cleanup));
                repository.Primary = combined; throw combined;
            }
        }
    }

    private static async Task Release(Setup setup, ImportRepositoryFixture repository, bool cleanup = false)
    {
        var path = Path.Combine(setup.HelperRoot, "race-release.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { nonce = setup.Nonce });
        Task<bool> Write()
        {
            return Task.Run(() =>
            {
                void Check() { _ = cleanup ? repository.CleanupRemaining() : repository.WorkRemaining(); }
                var staged = path + ".pending-" + Guid.NewGuid().ToString("N"); Check();
                using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes); output.Flush(true); }
                Check(); File.Move(staged, path, overwrite: false); Check(); return true;
            }, CancellationToken.None);
        }
        if (cleanup) _ = await repository.ObserveCleanup(Write);
        else { _ = repository.WorkRemaining(); _ = await repository.Wait(Write()); }
    }
    private static async Task WaitUntil(ImportRepositoryFixture repository, Func<bool> observation)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            _ = repository.WorkRemaining(); if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(10)) throw new TimeoutException("Original bounded real-Git barrier observation expired.");
            var found = await repository.Wait(Task.Run(observation, CancellationToken.None)); _ = repository.WorkRemaining();
            if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(10)) throw new TimeoutException("Late real-Git barrier observation refused.");
            if (found) return;
            await Task.Delay(20, repository.WorkToken);
        }
    }
    internal static async Task<Setup> Prepare(ImportRepositoryFixture repository, bool race)
    {
        await repository.InitializeAsync();
        await repository.Wait(File.WriteAllTextAsync(Path.Combine(repository.Root, ".gitattributes"), "tracked.txt filter=owned-test\n", repository.WorkToken).ContinueWith(done =>
        { done.GetAwaiter().GetResult(); return true; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        await repository.Command(repository.Root, "add", "--", ".gitattributes");
        await repository.Command(repository.Root, "commit", "-q", "-m", "actual fixture filter attribute without executable driver");
        var reader = new GitRepositoryContextReader(repository.Git);
        var context = await repository.Wait(reader.ReadAsync(repository.Root, "refs/heads/main", repository.WorkToken));
        var file = await repository.Wait(reader.ReadFileAsync(context, "tracked.txt", repository.WorkToken));
        var now = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, now, [], EvidenceRequirement.Required)];
        var request = new PlanningRequest("fixture/import-safety", "#4", "implement", "Import using the selected non-transforming Git view",
            ["tracked.txt"], ["production"], ["No workspace-config executable side effect"], "implement");
        var plan = PlanFreezer.Freeze(new(request, context, evidence, [new("tracked.txt", "content", "update")],
            [new("base", [], "current source")], [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", ["inspect diagnostics"]), [], IntakePlanner.Assess(request, evidence)), "git-safety", now);
        Assert.True(plan.DownstreamReady); Assert.Equal(plan.Identity, CanonicalJson.ComputeIdentity(plan.CanonicalBytes));
        var supplied = Environment.GetEnvironmentVariable("DELIVERY_FORGE_IMPORT_GIT_HELPER");
        if (string.IsNullOrWhiteSpace(supplied) || !Path.IsPathFullyQualified(supplied) || !File.Exists(supplied))
            throw new IOException("Root must build the actual private ImportGitHelper apphost before this native fixture; no synthetic fallback.");
        var helperRoot = repository.Root + "-helper";
        var sourceRoot = Path.GetDirectoryName(supplied)!;
        const string name = "DeliveryForge.Planning.ImportGitHelper";
        if (Path.GetFileName(supplied) != name + (OperatingSystem.IsWindows() ? ".exe" : "")) throw new IOException("Actual helper apphost name differs.");
        var files = new[] { Path.GetFileName(supplied), name + ".dll", name + ".deps.json", name + ".runtimeconfig.json" };
        await repository.Wait(Task.Run(() =>
        {
            _ = repository.WorkRemaining(); if (Path.Exists(helperRoot)) throw new IOException("Fresh helper root collision."); Directory.CreateDirectory(helperRoot);
            foreach (var companion in files)
            {
                _ = repository.WorkRemaining(); var original = Path.Combine(sourceRoot, companion);
                if (new FileInfo(original).Length > 8 * 1024 * 1024) throw new IOException("Actual helper companion exceeds8MiB.");
                var target = Path.Combine(helperRoot, companion); File.Copy(original, target, overwrite: false);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, File.GetUnixFileMode(original));
                if (!SHA256.HashData(File.ReadAllBytes(original)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(target)))) throw new IOException("Actual helper copy differs.");
            }
            _ = repository.WorkRemaining(); return true;
        }, CancellationToken.None));
        var nonce = Guid.NewGuid().ToString("N");
        var configuration = JsonSerializer.SerializeToUtf8Bytes(new { Git = repository.Git, Repository = repository.Root, Nonce = nonce, Race = race });
        await repository.Wait(Task.Run(() =>
        {
            _ = repository.WorkRemaining(); using var output = new FileStream(Path.Combine(helperRoot, "git-helper.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.Write(configuration); output.Flush(true); _ = repository.WorkRemaining(); return true;
        }, CancellationToken.None));
        var source = new PlanImportSource(repository.Root, "refs/heads/main", Path.Combine(helperRoot, Path.GetFileName(supplied)));
        return new(plan, source, new(plan.Identity, plan.ContractIdentity, request.Repository, request.WorkItem, repository.Root, source.MutableReference, request.RequestedCeiling), helperRoot, nonce);
    }
    internal sealed record Setup(FrozenPlan Plan, PlanImportSource Source, PlanImportExpectations Expectations, string HelperRoot, string Nonce);
}
