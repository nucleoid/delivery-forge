using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Execution;

namespace DeliveryForge.EndToEnd.Tests;

public sealed record ActualParentCrashSnapshot(string ReadyPath, string CapturedReadyPath, string RecoveryReportPath,
    string ReleaseControlReportPath, string PlanContractPath, string ParentObservationPath);

internal static class ActualParentCrashOrchestrator
{
    internal static async Task<ActualParentCrashSnapshot> RunEarlyAsync(OwnedScenarioRun artifacts, OwnedEphemeralRuntime runtime,
        string stage, string nonce, string sentinelPath, string frozenBaseCommit, long originalStarted, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stage is not ("plan-frozen" or "worker-prepared" or "host-accepted" or "owned-command-started"))
            throw new ArgumentException("This compiled RED slice covers only the four early boundaries.", nameof(stage));
        var host = ExistingAbsolute("DELIVERY_FORGE_DOTNET_HOST");
        var driver = ExistingAbsolute("DELIVERY_FORGE_ACTUAL_CRASH_DRIVER_DLL");
        var sentinelBytes = await File.ReadAllBytesAsync(sentinelPath, token);
        var controlNonce = Guid.NewGuid().ToString("N");
        var controlRoot = Path.Combine(artifacts.Root, "crash-control-" + controlNonce); Directory.CreateDirectory(controlRoot);
        var control = artifacts.Processes.StartOwned(Launch("release-control", controlRoot, controlNonce), "crash-driver-control:" + controlNonce);
        await SetupAndReady(control, controlRoot, controlNonce);
        await PublishRelease(controlRoot, controlNonce);
        await PositiveExit(control, WorkBudget(15), token);
        if (control.Process.ExitCode != 0) throw new InvalidOperationException("Actual normal-release driver failed; crash calibration is invalid.");
        using var controlResult = JsonDocument.Parse(await ReadPlain(Path.Combine(controlRoot, "normal-release-result.json"), controlRoot, token));
        if (!controlResult.RootElement.GetProperty("actualReadyReached").GetBoolean() || !controlResult.RootElement.GetProperty("normalReleaseInsteadOfKill").GetBoolean())
            throw new InvalidOperationException("Normal calibration did not reach and release the selected real owner boundary.");
        var controlReport = Path.Combine(artifacts.Root, "actual-crash-release-control.json");
        await WriteNew(controlReport, JsonSerializer.SerializeToUtf8Bytes(new { fixture = true, stage, actualReadyReached = true,
            normalReleaseInsteadOfKill = true, driverExitCode = control.Process.ExitCode,
            positiveOriginalControllerExitObserved = artifacts.Processes.Observe(control.Identity).Outcome == ProcessControlOutcome.AlreadyExited,
            control.Identity, rawDriverResultPath = Path.Combine(controlRoot, "normal-release-result.json") }), token);
        var driverRoot = Path.Combine(artifacts.Root, "crash-original-" + nonce); Directory.CreateDirectory(driverRoot);
        var original = artifacts.Processes.StartOwned(Launch("barrier", driverRoot, nonce), "crash-driver-original:" + nonce);
        var originalUtcStart = original.Process.StartTime.ToUniversalTime().Ticks;
        var readyPath = await SetupAndReady(original, driverRoot, nonce);
        var readyBytes = await ReadPlain(readyPath, driverRoot, token);
        var capturedReady = Path.Combine(artifacts.Root, "actual-prekill-ready.json"); await WriteNew(capturedReady, readyBytes, token);
        using var ready = JsonDocument.Parse(readyBytes);
        var root = ready.RootElement;
        if (root.GetProperty("stage").GetString() != stage || root.GetProperty("nonce").GetString() != nonce ||
            root.GetProperty("frozenBaseCommit").GetString() != frozenBaseCommit ||
            root.GetProperty("integratedBaseCommit").GetString() != OwnedScenarioRun.InitialIntegratedBase)
            throw new IOException("Actual ready diagnostic does not match this exact stage/source/nonce.");
        foreach (var file in root.GetProperty("durableFiles").EnumerateArray())
        {
            var path = file.GetProperty("path").GetString()!;
            RequireWithin(path, runtime.Root, driverRoot);
            if (Hash(await File.ReadAllBytesAsync(path, token)) != file.GetProperty("sha256").GetString())
                throw new IOException("Independent parent reopened stage bytes differ before force termination.");
        }
        var beforeKill = artifacts.Processes.Observe(original.Identity);
        if (beforeKill.Outcome != ProcessControlOutcome.LiveOwned) throw new InvalidOperationException("Original driver is not freshly LiveOwned before force kill: " + beforeKill.Reason);
        original.Process.Kill(entireProcessTree: false);
        await PositiveExit(original, WorkBudget(5), token);
        var afterKill = artifacts.Processes.Observe(original.Identity);
        if (afterKill.Outcome != ProcessControlOutcome.AlreadyExited) throw new InvalidOperationException("Original driver force termination is not positively observed.");
        var recovery = artifacts.Processes.StartOwned(Launch("recover", driverRoot, nonce), "crash-driver-recovery:" + nonce);
        var recoveryUtcStart = recovery.Process.StartTime.ToUniversalTime().Ticks;
        await PositiveExit(recovery, WorkBudget(15), token);
        if (recovery.Process.ExitCode != 0) throw new InvalidOperationException("Fresh actual recovery driver failed; no safe-state result established.");
        var recoveryReport = Path.Combine(driverRoot, "recovery-result.json");
        _ = await ReadPlain(recoveryReport, driverRoot, token);
        var currentSentinelBytes = await File.ReadAllBytesAsync(sentinelPath, token);
        if (!sentinelBytes.SequenceEqual(currentSentinelBytes)) throw new IOException("Outside crash sentinel changed.");
        var observation = Path.Combine(artifacts.Root, "actual-parent-termination-observation.json");
        await WriteNew(observation, JsonSerializer.SerializeToUtf8Bytes(new
        {
            fixture = true, productionCertified = false, actualOsParentCrash = true, firstFullRealRunCertified = false, stage, nonce,
            originalBeforeKillOutcome = beforeKill.Outcome.ToString(), originalAfterKillOutcome = afterKill.Outcome.ToString(),
            terminationMode = "owned-original-handle-Kill(false)", readySha256BeforeKill = Hash(readyBytes),
            originalDriverIdentity = original.Identity, recoveryDriverIdentity = recovery.Identity,
            originalDriverStartUtcTicks = originalUtcStart, recoveryDriverStartUtcTicks = recoveryUtcStart,
            freshRecoveryProcessStartedAfterOriginalExit = afterKill.Outcome == ProcessControlOutcome.AlreadyExited && recoveryUtcStart > originalUtcStart,
            originalExitCode = original.Process.ExitCode, recoveryExitCode = recovery.Process.ExitCode,
            actualDriverDll = driver, driverSha256 = Hash(await File.ReadAllBytesAsync(driver, token)),
            sdkExecutable = host, sdkExecutableSha256 = Hash(await File.ReadAllBytesAsync(host, token)),
            originalLaunch = Launch("barrier", driverRoot, nonce), freshRecoveryLaunch = Launch("recover", driverRoot, nonce),
            integratedBaseCommit = artifacts.IntegratedSource.Commit, implementationHead = artifacts.IntegratedSource.HeadCommit,
            elapsedSeconds = Stopwatch.GetElapsedTime(originalStarted).TotalSeconds, retention = "No old native child adoption or destructive cleanup; all experiment files retained."
        }), token);
        return new(readyPath, capturedReady, recoveryReport, controlReport, root.GetProperty("planContractPath").GetString()!, observation);

        ProcessLaunch Launch(string mode, string rootPath, string runNonce) => new(host,
            [driver, mode, stage, runtime.Root, rootPath, runNonce, frozenBaseCommit, OwnedScenarioRun.InitialIntegratedBase, "11019"]);
        TimeSpan WorkBudget(int seconds)
        {
            token.ThrowIfCancellationRequested();
            var remaining = TimeSpan.FromSeconds(100) - Stopwatch.GetElapsedTime(originalStarted);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Original crash-case work budget exhausted; retention20s remains reserved.");
            return remaining < TimeSpan.FromSeconds(seconds) ? remaining : TimeSpan.FromSeconds(seconds);
        }
        async Task<string> SetupAndReady(OwnedProcess owned, string directory, string runNonce)
        {
            var setupRemaining = TimeSpan.FromSeconds(75) - Stopwatch.GetElapsedTime(originalStarted);
            if (setupRemaining <= TimeSpan.Zero) throw new TimeoutException("Original crash setup75 exhausted.");
            await WaitForPublished(Path.Combine(directory, "setup-complete-" + runNonce + ".json"), directory, owned,
                setupRemaining < WorkBudget(75) ? setupRemaining : WorkBudget(75), token);
            var path = Path.Combine(directory, "ready-" + stage + "-" + runNonce + ".json");
            await WaitForPublished(path, directory, owned, WorkBudget(5), token);
            return path;
        }
        async Task PublishRelease(string directory, string runNonce)
        {
            RequireWithin(Path.Combine(directory, "release-" + runNonce), directory);
            var staging = Path.Combine(directory, "release-staging-" + Guid.NewGuid().ToString("N"));
            await WriteNew(staging, System.Text.Encoding.UTF8.GetBytes(runNonce), token);
            File.Move(staging, Path.Combine(directory, "release-" + runNonce), overwrite: false);
        }
        async Task PositiveExit(OwnedProcess owned, TimeSpan limit, CancellationToken current)
        {
            await owned.Process.WaitForExitAsync(current).WaitAsync(limit, current);
            var observed = artifacts.Processes.Observe(owned.Identity);
            if (observed.Outcome != ProcessControlOutcome.AlreadyExited) throw new InvalidOperationException("Exact original driver exit remains unknown: " + observed.Reason);
        }
    }

    private static string ExistingAbsolute(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new PlatformNotSupportedException("Actual parent-crash fixture input unavailable: " + variable);
        return Path.GetFullPath(path);
    }
    private static async Task WaitForPublished(string path, string root, OwnedProcess owned, TimeSpan limit, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= limit) throw new TimeoutException("Actual crash driver stage/publication deadline exceeded.");
            if (owned.Process.HasExited) throw new InvalidOperationException("Driver exited before actual ready publication; exit=" + owned.Process.ExitCode);
            if (File.Exists(path))
            {
                try
                {
                    using var document = JsonDocument.Parse(await ReadPlain(path, root, token));
                    if (Stopwatch.GetElapsedTime(started) >= limit) throw new TimeoutException("Ready bytes arrived after original bound.");
                    return;
                }
                catch (IOException) { /* CreateNew publisher still owns its exclusive writer; wait finitely. */ }
            }
            await Task.Delay(10, token);
        }
    }
    private static async Task<byte[]> ReadPlain(string path, string root, CancellationToken token)
    {
        RequireWithin(path, root);
        if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Crash protocol file exceeds its one-MiB bound.");
        return await File.ReadAllBytesAsync(path, token);
    }
    private static void RequireWithin(string path, params string[] roots)
    {
        path = Path.GetFullPath(path);
        if (!roots.Any(root => path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new IOException("Crash diagnostic escaped its exact experiment leaf.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Crash path/ancestor is redirected."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static async Task WriteNew(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true);
    }
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
