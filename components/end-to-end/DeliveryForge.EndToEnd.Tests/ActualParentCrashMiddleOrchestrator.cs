using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;

namespace DeliveryForge.EndToEnd.Tests;

internal sealed record MiddleCrashPrerequisites(string RuntimeRoot, string RuntimeBaseCommit,
    string FrozenPlanContractPath, string? ActualNativeResultPath, string? ActualGateReceiptPath,
    string? ActualTrxPath, string? ActualReviewReceiptPath, string PrerequisiteObservationPath);
internal sealed record ActualMiddleCrashSnapshot(string ReadyPath, string CapturedReadyPath,
    string ParentObservationPath, string RecoveryReportPath, string ReleaseControlReportPath, string BoundaryArtifactPath);

internal static class ActualParentCrashMiddleOrchestrator
{
    internal static async Task<ActualMiddleCrashSnapshot> RunMiddleAsync(OwnedScenarioRun artifacts,
        string stage, string nonce, string sentinelPath, MiddleCrashPrerequisites prerequisites,
        long originalStarted, RealDotNetFixtureClock? originalToolClock, CancellationToken token)
    {
        var tool = stage == "gate-receipt-written"; var total = tool ? 540 : 120;
        var lifetime = artifacts.MiddleLifetime;
        if (stage is not ("command-completed" or "gate-receipt-written" or "review-registration" or "review-receipt") ||
            tool != (originalToolClock is not null) || tool && originalToolClock!.Started != originalStarted ||
            lifetime.Started != originalStarted || lifetime.Total != total)
            throw new InvalidOperationException("Middle stage must use its original approved case clock.");
        var host = Existing("DELIVERY_FORGE_DOTNET_HOST"); var driver = Existing("DELIVERY_FORGE_ACTUAL_CRASH_DRIVER_DLL");
        var hostHash = Hash(await File.ReadAllBytesAsync(host, token)); var driverHash = Hash(await File.ReadAllBytesAsync(driver, token));
        var sentinel = await File.ReadAllBytesAsync(sentinelPath, token);
        var owners = new List<OwnedProcess>();
        var startTicks = new Dictionary<OwnedProcess, long>(ReferenceEqualityComparer.Instance);
        Exception? primary = null;
        try
        {
            _ = Budget(1);
            var controlNonce = Guid.NewGuid().ToString("N"); var controlRoot = Path.Combine(artifacts.Root, "middle-control-" + controlNonce);
            Directory.CreateDirectory(controlRoot);
            var control = Start("release-control", controlRoot, controlNonce);
            await Ready(control, controlRoot, controlNonce);
            await Release(controlRoot, controlNonce);
            await PositiveExit(control, Budget(15), token);
            if (control.Process.ExitCode != 0) throw new InvalidOperationException("Actual middle calibration driver failed.");
            using var calibration = JsonDocument.Parse(await Read(Path.Combine(controlRoot, "normal-release-result.json"), controlRoot, token));
            if (!calibration.RootElement.GetProperty("actualReadyReached").GetBoolean() || !calibration.RootElement.GetProperty("normalReleaseInsteadOfKill").GetBoolean())
                throw new InvalidOperationException("Actual selected boundary was not reached in calibration.");
            var controlReport = Path.Combine(artifacts.Root, "actual-middle-release-control.json");
            await Write(controlReport, JsonSerializer.SerializeToUtf8Bytes(new { fixture = true, stage,
                actualReadyReached = true, normalReleaseInsteadOfKill = true, driverExitCode = control.Process.ExitCode,
                originalControllerExitObserved = artifacts.Processes.Observe(control.Identity), rawDriverResult = Path.Combine(controlRoot, "normal-release-result.json") }), token);
            var originalRoot = Path.Combine(artifacts.Root, "middle-original-" + nonce); Directory.CreateDirectory(originalRoot);
            var original = Start("barrier", originalRoot, nonce);
            var readyPath = await Ready(original, originalRoot, nonce);
            var readyBytes = await Read(readyPath, originalRoot, token);
            using var ready = JsonDocument.Parse(readyBytes); var observed = ready.RootElement;
            if (observed.GetProperty("stage").GetString() != stage || observed.GetProperty("nonce").GetString() != nonce ||
                observed.GetProperty("frozenBaseCommit").GetString() != prerequisites.RuntimeBaseCommit ||
                observed.GetProperty("originalCaseStarted").GetInt64() != originalStarted)
                throw new IOException("Middle driver ready source/nonce/original clock differs.");
            foreach (var file in observed.GetProperty("durableFiles").EnumerateArray())
            {
                var path = file.GetProperty("path").GetString()!; Within(path, prerequisites.RuntimeRoot, originalRoot);
                if (Hash(await Read(path, Path.GetDirectoryName(path)!, token)) != file.GetProperty("sha256").GetString())
                    throw new IOException("Parent independently reopened durable bytes differ before kill.");
            }
            var captured = Path.Combine(artifacts.Root, "actual-middle-prekill-ready.json"); await Write(captured, readyBytes, token);
            var beforeKill = artifacts.Processes.Observe(original.Identity);
            if (beforeKill.Outcome != ProcessControlOutcome.LiveOwned) throw new InvalidOperationException("Original driver is not freshly live-owned before exact force kill.");
            original.Process.Kill(entireProcessTree: false);
            await PositiveExit(original, Budget(5), token);
            var afterKill = artifacts.Processes.Observe(original.Identity);
            if (afterKill.Outcome != ProcessControlOutcome.AlreadyExited) throw new InvalidOperationException("Original force termination was not positively observed.");
            var originalExitObservedAt = Stopwatch.GetTimestamp();
            var fresh = Start("recover", originalRoot, nonce);
            var freshStartedAt = Stopwatch.GetTimestamp();
            await PositiveExit(fresh, Budget(15), token);
            if (fresh.Process.ExitCode != 0) throw new InvalidOperationException("Fresh second middle recovery driver failed.");
            var recovery = Path.Combine(originalRoot, "recovery-result.json"); _ = await Read(recovery, originalRoot, token);
            var nowSentinel = await File.ReadAllBytesAsync(sentinelPath, token);
            if (!sentinel.SequenceEqual(nowSentinel)) throw new IOException("Outside middle sentinel changed.");
            if (Hash(await File.ReadAllBytesAsync(host, token)) != hostHash || Hash(await File.ReadAllBytesAsync(driver, token)) != driverHash)
                throw new IOException("Actual pinned host/driver changed during experiment.");
            var parent = Path.Combine(artifacts.Root, "actual-middle-parent-observation.json");
            await Write(parent, JsonSerializer.SerializeToUtf8Bytes(new
            {
                fixture = true, productionCertified = false, firstFullRealRunCertified = false, stage, nonce, actualOsParentCrash = true,
                originalBeforeKillOutcome = beforeKill.Outcome.ToString(), originalAfterKillOutcome = afterKill.Outcome.ToString(),
                terminationMode = "owned-original-handle-Kill(false)", readySha256BeforeKill = Hash(readyBytes),
                originalTotalSeconds = total, originalCaseStarted = originalStarted, cleanupReserveSeconds = 20,
                freshRecoveryProcessStartedAfterOriginalExit = afterKill.Outcome == ProcessControlOutcome.AlreadyExited && freshStartedAt > originalExitObservedAt,
                originalDriverStartUtcTicks = startTicks[original], recoveryDriverStartUtcTicks = startTicks[fresh],
                original.Identity, originalExitCode = original.Process.ExitCode, recoveryIdentity = fresh.Identity,
                recoveryExitCode = fresh.Process.ExitCode, driver, driverHash, host, hostHash,
                originalLaunch = Launch("barrier", originalRoot, nonce), recoveryLaunch = Launch("recover", originalRoot, nonce),
                artifacts.IntegratedSource, elapsedSeconds = Stopwatch.GetElapsedTime(originalStarted).TotalSeconds,
                limitation = "Actual owned original handle termination and SECOND process; no imported PID/root receipts as control authority, no source/runtime deletion."
            }), token);
            _ = Budget(1);
            return new(readyPath, captured, parent, recovery, controlReport, observed.GetProperty("boundaryArtifactPath").GetString()!);
        }
        catch (Exception failure) { primary = failure; lifetime.PrimaryFailure(failure); throw; }
        finally
        {
            var cleanupErrors = new List<Exception>();
            lifetime.BeginCleanup();
            foreach (var owner in owners)
            {
                try
                {
                    var stages = await lifetime.StagesAsync(("original-driver:" + owner.Identity.ProcessId, owner, async cleanupToken =>
                    {
                        var remaining = lifetime.Remaining(true);
                        var current = artifacts.Processes.Observe(owner.Identity);
                        if (current.Outcome == ProcessControlOutcome.LiveOwned) { owner.Process.Kill(entireProcessTree: false); await PositiveExit(owner, remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5), cleanupToken); }
                        else if (current.Outcome != ProcessControlOutcome.AlreadyExited) throw new InvalidOperationException("Original middle driver identity remains unknown; no imported PID cleanup.");
                    }));
                    cleanupErrors.AddRange(stages.Where(item => item.Failure is not null).Select(item => item.Failure!));
                }
                catch (Exception secondary) { cleanupErrors.Add(secondary); lifetime.Retain(owner); lifetime.Retain(secondary); artifacts.RetainArtifacts("Actual original driver cleanup remains unknown: " + secondary); }
            }
            if (cleanupErrors.Count != 0 && primary is null) throw new AggregateException("Middle original ownership cleanup failed.", cleanupErrors);
            if (cleanupErrors.Count != 0 && primary is not null) primary.Data["ActualMiddleCleanupFailures"] = cleanupErrors.ToArray();
        }

        ProcessLaunch Launch(string mode, string directory, string runNonce) => new(host,
            [driver, mode, stage, prerequisites.RuntimeRoot, directory, runNonce, prerequisites.RuntimeBaseCommit,
                OwnedScenarioRun.InitialIntegratedBase, "11019", originalStarted.ToString(CultureInfo.InvariantCulture), Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)]);
        OwnedProcess Start(string mode, string directory, string runNonce)
        {
            _ = Budget(1);
            if (Hash(File.ReadAllBytes(host)) != hostHash || Hash(File.ReadAllBytes(driver)) != driverHash) throw new IOException("Pinned launch bytes changed before creation.");
            var actual = artifacts.Processes.StartOwned(Launch(mode, directory, runNonce), "middle-driver:" + mode + ":" + runNonce);
            owners.Add(actual); lifetime.Retain(actual); startTicks.Add(actual, actual.Process.StartTime.ToUniversalTime().Ticks); return actual;
        }
        TimeSpan Budget(int maximum)
        {
            token.ThrowIfCancellationRequested(); originalToolClock?.WallToken.ThrowIfCancellationRequested();
            var left = lifetime.Remaining();
            if (left <= TimeSpan.Zero) throw new TimeoutException("Same original middle work clock expired; cleanup reserve retained.");
            return left < TimeSpan.FromSeconds(maximum) ? left : TimeSpan.FromSeconds(maximum);
        }
        async Task<string> Ready(OwnedProcess owner, string directory, string runNonce)
        {
            var setupLimit = tool ? Budget(540) : TimeSpan.FromSeconds(75) - Stopwatch.GetElapsedTime(originalStarted);
            if (setupLimit <= TimeSpan.Zero) throw new TimeoutException("Same original middle setup allowance expired.");
            await Published(Path.Combine(directory, "setup-complete-" + runNonce + ".json"), directory, owner, setupLimit, token);
            var path = Path.Combine(directory, "ready-" + stage + "-" + runNonce + ".json");
            await Published(path, directory, owner, Budget(5), token); return path;
        }
        async Task Release(string directory, string runNonce)
        {
            var staging = Path.Combine(directory, "release-staging-" + Guid.NewGuid().ToString("N"));
            await Write(staging, Encoding.UTF8.GetBytes(runNonce), token);
            var final = Path.Combine(directory, "release-" + runNonce); Within(final, directory); File.Move(staging, final, overwrite: false);
        }
        async Task PositiveExit(OwnedProcess owner, TimeSpan limit, CancellationToken current)
        {
            var begun = Stopwatch.GetTimestamp(); var actualExit = owner.Process.WaitForExitAsync(current);
            lifetime.Track(actualExit); await actualExit.WaitAsync(limit, current);
            while (true)
            {
                current.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(begun) >= limit) throw new TimeoutException("Original driver exit observation expired.");
                var sample = artifacts.Processes.Observe(owner.Identity);
                if (Stopwatch.GetElapsedTime(begun) >= limit) throw new TimeoutException("Original driver exit observation arrived late.");
                if (sample.Outcome == ProcessControlOutcome.AlreadyExited) return;
                if (sample.Outcome is not (ProcessControlOutcome.LiveOwned or ProcessControlOutcome.IdentityUnknown)) throw new InvalidOperationException("Original driver exit remains unknown.");
                var left = limit - Stopwatch.GetElapsedTime(begun); await Task.Delay(left < TimeSpan.FromMilliseconds(20) ? left : TimeSpan.FromMilliseconds(20), current);
            }
        }
    }
    private static string Existing(string name)
    { var path = Environment.GetEnvironmentVariable(name); if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new PlatformNotSupportedException("Actual middle input unavailable: " + name); Plain(path); return Path.GetFullPath(path); }
    private static async Task Published(string path, string root, OwnedProcess owner, TimeSpan limit, CancellationToken token)
    {
        var begun = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested(); if (Stopwatch.GetElapsedTime(begun) >= limit) throw new TimeoutException("Actual middle stage did not publish within original allowance.");
            if (owner.Process.HasExited) throw new InvalidOperationException("Actual middle driver exited before ready; code=" + owner.Process.ExitCode);
            if (File.Exists(path))
            {
                try { using var parsed = JsonDocument.Parse(await Read(path, root, token)); return; }
                catch (IOException) { }
            }
            await Task.Delay(10, token);
        }
    }
    private static async Task<byte[]> Read(string path, string root, CancellationToken token)
    { Within(path, root); if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Middle protocol file exceeds1MiB."); return await File.ReadAllBytesAsync(path, token); }
    private static async Task Write(string path, byte[] bytes, CancellationToken token)
    { Plain(path); await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true); }
    private static void Within(string path, params string[] roots)
    { var full = Path.GetFullPath(path); if (!roots.Any(root => full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) throw new IOException("Middle diagnostic escaped exact roots."); Plain(full); }
    private static void Plain(string path)
    { for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) { try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Middle diagnostic namespace redirected."); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } } }
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
