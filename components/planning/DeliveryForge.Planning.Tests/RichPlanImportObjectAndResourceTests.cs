using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeliveryForge.Planning.Tests;

[Collection(RichPlanImportFixtureCollection.Name)]
public sealed class RichPlanImportObjectAndResourceTests
{
    private static readonly ConcurrentDictionary<CancellationTokenSource, byte> RetainedImportCancellation = new();
    private static readonly ConcurrentDictionary<PhaseIoOwner, byte> RetainedPhaseIo = new();
    private sealed class PhaseIoOwner(ImportRepositoryFixture repository, CancellationTokenSource cancellation)
    {
        internal readonly ImportRepositoryFixture Repository = repository;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal Task? Actual, Observation;
        internal readonly List<Exception> Failures = [];
    }
    [Theory(Timeout = 120_000)]
    [InlineData(8)]
    [InlineData(600)]
    public async Task Actual_reference_discovery_and_pending_frontier_respect_512_directory_profile(int directories)
    {
        await using var repository = new ImportRepositoryFixture(TestContext.Current.CancellationToken);
        try
        {
            var setup = await RichPlanImportGitSafetyTests.Prepare(repository, false);
            _ = await repository.Wait(Task.Run(() =>
            {
                for (var i = 0; i < directories; i++)
                {
                    _ = repository.WorkRemaining();
                    Directory.CreateDirectory(Path.Combine(repository.Root, ".git", "refs", "owned-empty-" + i.ToString("D4")));
                    _ = repository.WorkRemaining();
                }
                return true;
            }, CancellationToken.None));
            var result = await repository.Wait(PlanImporter.ImportAndReconcileAsync(setup.Plan.CanonicalBytes,
                setup.Source, setup.Expectations, repository.WorkRemaining(), cancellationToken: repository.WorkToken));
            await Diagnostic(repository, setup, result);
            if (directories == 600)
            {
                Assert.False(result.DownstreamReady); Assert.Null(result.Plan);
                Assert.Contains(result.Diagnostics, item => item.Contains("reference directory bound", StringComparison.OrdinalIgnoreCase));
                // Include the ONE encountered candidate proving exhaustion, without admitting it.
                // Stop immediately: visiting every600 would contradict bounded discovery refusal.
                Assert.Equal(513, result.Resources.MaximumDiscoveredReferenceDirectories);
                // At most1024 allowed ref files plus512 yielded directory candidates;
                // the traversal root is a directory observation, not an enumerated entry.
                Assert.InRange(result.Resources.MaximumEnumeratedReferenceEntries, 1, 1536);
            }
            else
            {
                Assert.True(result.DownstreamReady, string.Join("; ", result.Diagnostics)); Assert.NotNull(result.Plan);
                Assert.Equal(setup.Plan.CanonicalBytes, result.Plan.CanonicalBytes);
                Assert.True(result.Resources.MaximumDiscoveredReferenceDirectories >= directories);
                Assert.InRange(result.Resources.MaximumDiscoveredReferenceDirectories, 1, 512);
                Assert.True(result.Resources.MaximumEnumeratedReferenceEntries >= directories);
            }
            // Owner records ACTUAL discoveries/frontier before push, including on typed refusal.
            // The old eventual pop bound is insufficient and still runs unchanged in this RED.
            Assert.InRange(result.Resources.MaximumQueuedReferenceDirectories, 1, 512);
        }
        catch (Exception failure) { repository.Primary = failure; throw; }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_blob_read_cannot_consume_transient_live_external_object_routing(bool alternate)
    {
        await using var repository = new ImportRepositoryFixture(TestContext.Current.CancellationToken);
        var importCancellation = CancellationTokenSource.CreateLinkedTokenSource(repository.WorkToken);
        RetainedImportCancellation.TryAdd(importCancellation, 0);
        RichPlanImportGitSafetyTests.Setup? setup = null;
        Task<PlanImportResult>? operation = null;
        AlternateFixturePhase? phase = null;
        long dispatchStarted = 0;
        Exception? primary = null; var secondary = new List<Exception>();
        try
        {
            setup = await RichPlanImportGitSafetyTests.Prepare(repository, false);
            var blob = await repository.Command(repository.Root, "rev-parse", "--verify", "HEAD:tracked.txt");
            Assert.Equal(40, blob.Length);
            var originalBlob = Path.Combine(repository.Root, ".git", "objects", blob[..2], blob[2..]);
            var stored = await repository.Wait(File.ReadAllBytesAsync(originalBlob, repository.WorkToken));
            var storedHash = Hash(stored);
            var expectedOutput = await repository.Wait(File.ReadAllBytesAsync(Path.Combine(repository.Root, "tracked.txt"), repository.WorkToken));
            var externalRoot = repository.Root + "-owned-external-objects";
            _ = await repository.Wait(Task.Run(() =>
            {
                _ = repository.WorkRemaining();
                if (Path.Exists(externalRoot)) throw new IOException("Fresh owned object namespace collision.");
                Directory.CreateDirectory(externalRoot); _ = repository.WorkRemaining(); return true;
            }, CancellationToken.None));
            var configuration = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Git = repository.Git, Repository = repository.Root, Nonce = setup.Nonce, Race = false,
                Alternate = alternate, BlobId = blob, ObjectDirectory = externalRoot, StoredSha256 = storedHash
            });
            _ = await repository.Wait(File.WriteAllBytesAsync(Path.Combine(setup.HelperRoot, "git-helper.json"), configuration,
                repository.WorkToken).ContinueWith(done => { done.GetAwaiter().GetResult(); return true; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            dispatchStarted = Stopwatch.GetTimestamp();
            operation = PlanImporter.ImportAndReconcileAsync(setup.Plan.CanonicalBytes, setup.Source, setup.Expectations,
                repository.WorkRemaining(), cancellationToken: importCancellation.Token);
            if (alternate)
            {
                var arrivalPath = Path.Combine(setup.HelperRoot, "alternate-entered.json");
                await Arrival(repository, setup, arrivalPath, operation);
                var marker = await ReadPhaseMarker(repository, arrivalPath);
                using var arrival = JsonDocument.Parse(marker);
                Assert.Equal("alternate-cat-file-v1", arrival.RootElement.GetProperty("phaseProtocol").GetString());
                Assert.True(Guid.TryParseExact(arrival.RootElement.GetProperty("invocationId").GetString(), "N", out _));
                Assert.Equal(setup.Nonce, arrival.RootElement.GetProperty("Nonce").GetString());
                Assert.Equal(storedHash, arrival.RootElement.GetProperty("originalBlobSha").GetString());
                Assert.Equal(new[] { "cat-file", "blob", "--end-of-options", blob },
                    arrival.RootElement.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()).ToArray()[^4..]);
                var observedPhase = new AlternateFixturePhase(arrival.RootElement.GetProperty("phaseStartedTicks").GetInt64(),
                    arrival.RootElement.GetProperty("phaseDeadlineTicks").GetInt64(), arrival.RootElement.GetProperty("frequency").GetInt64());
                _ = observedPhase.Remaining(dispatchStarted, Stopwatch.GetTimestamp(), Stopwatch.Frequency, repository.WorkRemaining());
                phase = observedPhase; // Only exact literal current phase/nonce/argv/hash, never core/native authority.
                Assert.Equal(storedHash, Hash(await ObservePhase(repository, observedPhase, dispatchStarted,
                    cancellation => File.ReadAllBytesAsync(originalBlob, cancellation))));
                Assert.False(Path.Exists(Path.Combine(repository.Root, ".git", "objects", "info", "alternates")));
                _ = observedPhase.Remaining(dispatchStarted, Stopwatch.GetTimestamp(), Stopwatch.Frequency, repository.WorkRemaining());
                await Release(repository, setup, false, observedPhase, dispatchStarted);
            }
            PlanImportResult? result = null; Exception? importFailure = null;
            try { result = await repository.Wait(operation); }
            catch (Exception failure) { importFailure = failure; }
            if (result is not null) await Diagnostic(repository, setup, result);
            if (alternate)
            {
                using var observation = JsonDocument.Parse(await repository.Wait(File.ReadAllBytesAsync(
                    Path.Combine(setup.HelperRoot, "alternate-observed.json"), repository.WorkToken)));
                var data = observation.RootElement;
                Assert.Equal(setup.Nonce, data.GetProperty("Nonce").GetString());
                Assert.Equal(0, data.GetProperty("childExit").GetInt32());
                Assert.Equal(expectedOutput.LongLength, data.GetProperty("stdoutLength").GetInt64());
                Assert.Equal(Hash(expectedOutput), data.GetProperty("stdoutSha256").GetString());
                Assert.Equal(storedHash, data.GetProperty("externalBlobSha").GetString());
                Assert.Equal(storedHash, Hash(await repository.Wait(File.ReadAllBytesAsync(originalBlob, repository.WorkToken))));
                Assert.False(Path.Exists(Path.Combine(repository.Root, ".git", "objects", "info", "alternates")));
                Assert.False(Path.Exists(Path.Combine(externalRoot, blob[..2], blob[2..])));
                // Actual source blob absent + exact external blob present while real Git returned
                // original bytes. This observation is DATA, not imported object/source authority.
                Assert.False(data.GetProperty("externalRoutingConsumed").GetBoolean());
            }
            if (importFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(importFailure).Throw();
            Assert.NotNull(result); Assert.True(result.DownstreamReady, string.Join("; ", result.Diagnostics)); Assert.NotNull(result.Plan);
            Assert.Equal(setup.Plan.CanonicalBytes, result.Plan.CanonicalBytes);
            Assert.Equal(storedHash, Hash(await repository.Wait(File.ReadAllBytesAsync(originalBlob, repository.WorkToken))));
            Assert.False(Path.Exists(Path.Combine(repository.Root, ".git", "objects", "info", "alternates")));
            Assert.False(Path.Exists(Path.Combine(externalRoot, blob[..2], blob[2..])));
        }
        catch (Exception failure) { primary = failure; repository.Primary = failure; throw; }
        finally
        {
            try
            {
                _ = await repository.ObserveCleanup(() => Task.Run(() => { importCancellation.Cancel(); return true; }, CancellationToken.None));
            }
            catch (Exception failure) { secondary.Add(failure); }
            // Release and observe independently before repository disposal. Actual tasks/handles
            // remain rooted by the fixture on unknown; no unconditional file restoration/deletion.
            if (setup is not null && alternate && phase is { } observedPhase)
                try
                {
                    if (!File.Exists(Path.Combine(setup.HelperRoot, "alternate-release.json")))
                        await Release(repository, setup, true, observedPhase, dispatchStarted);
                }
                catch (Exception failure) { secondary.Add(failure); }
            if (operation is not null)
                try { _ = await repository.ObserveCleanup(() => operation); }
                catch (Exception failure) { if (!ReferenceEquals(failure, primary)) secondary.Add(failure); }
            if ((operation is null || operation.IsCompleted) && secondary.Count == 0)
            {
                importCancellation.Dispose(); RetainedImportCancellation.TryRemove(importCancellation, out _);
            }
            if (secondary.Count > 0)
            {
                var combined = new AggregateException("Actual import and independent original release/operation observations",
                    primary is null ? secondary : new[] { primary }.Concat(secondary));
                repository.Primary = combined; throw combined;
            }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static async Task Arrival(ImportRepositoryFixture repository, RichPlanImportGitSafetyTests.Setup setup,
        string path, Task<PlanImportResult> operation)
    {
        while (true)
        {
            _ = repository.WorkRemaining();
            var exists = await repository.Wait(Task.Run(() => File.Exists(path), CancellationToken.None));
            _ = repository.WorkRemaining();
            if (exists) return;
            if (operation.IsCompleted)
            {
                var result = await repository.Wait(operation);
                await Diagnostic(repository, setup, result);
                throw new IOException("Import returned before genuine alternate barrier: " + string.Join("; ", result.Diagnostics));
            }
            await Task.Delay(20, repository.WorkToken);
        }
    }
    private static async Task<byte[]> ReadPhaseMarker(ImportRepositoryFixture repository, string path)
    {
        // Closed owned fixture metadata only, at most4KiB, spending original work.
        _ = repository.WorkRemaining();
        var actual = Read();
        return await repository.Wait(actual);
        async Task<byte[]> Read()
        {
            var bytes = new byte[4097]; var length = 0;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            while (length < bytes.Length)
            {
                _ = repository.WorkRemaining();
                var count = await stream.ReadAsync(bytes.AsMemory(length), repository.WorkToken);
                _ = repository.WorkRemaining(); if (count == 0) break; length += count;
            }
            if (length > 4096) throw new IOException("Original creator marker exceeds4KiB.");
            return bytes[..length];
        }
    }
    private static async Task Diagnostic(ImportRepositoryFixture repository, RichPlanImportGitSafetyTests.Setup setup,
        PlanImportResult result)
    {
        // Bounded fixture DATA only; actual public result, never a readiness capability.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        { fixture = true, setup.Nonce, result.DownstreamReady, result.Diagnostics, result.Resources });
        if (bytes.Length > 64 * 1024) throw new IOException("Actual import diagnostic exceeds64KiB.");
        _ = await repository.Wait(Task.Run(() =>
        {
            _ = repository.WorkRemaining();
            var path = Path.Combine(setup.HelperRoot, "public-import-result-" + Guid.NewGuid().ToString("N") + ".json");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes); stream.Flush(true); _ = repository.WorkRemaining(); return true;
        }, CancellationToken.None));
    }
    private static async Task Release(ImportRepositoryFixture repository, RichPlanImportGitSafetyTests.Setup setup,
        bool cleanup, AlternateFixturePhase phase, long dispatchStarted)
    {
        Task<bool> Write(CancellationToken cancellation) => Task.Run(() =>
        {
            void Check()
            {
                cancellation.ThrowIfCancellationRequested();
                var left = cleanup ? repository.CleanupRemaining() : repository.WorkRemaining();
                _ = phase.Remaining(dispatchStarted, Stopwatch.GetTimestamp(), Stopwatch.Frequency, left);
            }
            Check(); var target = Path.Combine(setup.HelperRoot, "alternate-release.json");
            var staged = target + ".pending-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { nonce = setup.Nonce })); stream.Flush(true); }
            Check(); File.Move(staged, target, overwrite: false); Check(); return true;
        }, CancellationToken.None);
        _ = await ObservePhase(repository, phase, dispatchStarted, Write, cleanup);
    }
    private static async Task<T> ObservePhase<T>(ImportRepositoryFixture repository, AlternateFixturePhase phase,
        long dispatchStarted, Func<CancellationToken, Task<T>> operation, bool cleanup = false)
    {
        TimeSpan Remaining() => phase.Remaining(dispatchStarted, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
            cleanup ? repository.CleanupRemaining() : repository.WorkRemaining());
        var cap = Remaining(); // Same creator endpoint and original work/cleanup, before any IO.
        var cancellation = cleanup ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(repository.WorkToken);
        cancellation.CancelAfter(cap);
        var owner = new PhaseIoOwner(repository, cancellation);
        RetainedPhaseIo.TryAdd(owner, 0); // Actual IO/token/fixture references before invocation or clipped wait.
        try
        {
            _ = Remaining(); cancellation.Token.ThrowIfCancellationRequested();
            var actual = operation(cancellation.Token); owner.Actual = actual; ObserveFault(actual);
            // Repository also roots the ORIGINAL task before its own whole-case observation.
            var observed = cleanup ? repository.ObserveCleanup(() => actual) : repository.Wait(actual);
            owner.Observation = observed; ObserveFault(observed);
            var value = await observed.WaitAsync(Remaining(), cancellation.Token);
            _ = Remaining(); cancellation.Token.ThrowIfCancellationRequested();
            cancellation.Dispose(); RetainedPhaseIo.TryRemove(owner, out _);
            return value;
        }
        catch (Exception primary)
        {
            owner.Failures.Add(primary);
            try { cancellation.Cancel(); }
            catch (Exception secondary)
            {
                owner.Failures.Add(secondary);
                throw new AggregateException("Original phase IO and independent cancellation retained.", primary, secondary);
            }
            // No timed-out wrapper/late task status is closure: exact IO/token/fixture graph stays rooted.
            throw;
        }
    }
    private static void ObserveFault(Task actual)
    {
        _ = actual.ContinueWith(done => _ = done.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
