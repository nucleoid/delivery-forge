using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Evidence;

namespace DeliveryForge.EndToEnd.Tests;

public enum QualityInterruptionPhase { ColdControl, SourceDrift, Timeout, LateArtifact, CallerCancellation }
public sealed record QualityInterruptionSnapshot(GateRunResult Gate, string EnteredPath, string Nonce,
    string InvocationTrxPath, string ProvenancePath, string CapturedReceiptPath);

// Trusted fixture-only subprofile; does not change ordinary120s or the separate real-dotnet540s profile.
internal sealed class QualityInterruptionClock : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource _wall = new(TimeSpan.FromSeconds(720));
    public const string ProfileId = "real-dotnet-interruption-fixture-v1";
    public const int TotalSeconds = 720;
    public const int CleanupSeconds = 60;
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);
    public TimeSpan Remaining => TimeSpan.FromSeconds(TotalSeconds) - Elapsed;
    public CancellationToken WallToken => _wall.Token;
    public TimeSpan WorkBudget(int stageSeconds)
    {
        _wall.Token.ThrowIfCancellationRequested();
        var remaining = TimeSpan.FromSeconds(TotalSeconds - CleanupSeconds) - Elapsed;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Interruption fixture work budget exhausted; cleanup reserve retained.");
        return remaining < TimeSpan.FromSeconds(stageSeconds) ? remaining : TimeSpan.FromSeconds(stageSeconds);
    }
    public void Dispose() => _wall.Dispose();
}

internal static class QualityInterruptionOrchestrator
{
    public static async Task<IReadOnlyDictionary<QualityInterruptionPhase, QualityInterruptionSnapshot>> RunFiveAsync(
        OwnedScenarioRun artifacts, string ownedSourceDirectory, QualityInterruptionClock clock, CancellationToken cancellationToken)
    {
        using var wall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, clock.WallToken);
        cancellationToken = wall.Token;
        var runtime = new OwnedQualityInterruptionRuntime(artifacts, clock, cancellationToken);
        var phases = new Dictionary<QualityInterruptionPhase, QualityInterruptionSnapshot>();
        try
        {
            runtime.BeginStage(TimeSpan.FromSeconds(30) - clock.Elapsed);
            await runtime.InitializeAsync(ownedSourceDirectory, cancellationToken);
            var repo = runtime.Repository;
            var calculator = Path.Combine(repo, "Subject", "Calculator.cs");
            var originalSource = await File.ReadAllBytesAsync(Path.Combine(repo, "Subject", "Calculator.green.cs.txt"), cancellationToken);
            await WriteNew(calculator, originalSource, cancellationToken);
            await WriteNew(Path.Combine(repo, ".gitignore"), await File.ReadAllBytesAsync(Path.Combine(repo, "runtime.gitignore.txt"), cancellationToken), cancellationToken);
            var policySource = await File.ReadAllBytesAsync(Path.Combine(repo, "evidence-policy-source.json"), cancellationToken);
            var policyNode = JsonNode.Parse(policySource)!.AsObject();
            policyNode["identity"] = CanonicalJson.ComputeIdentity(policySource);
            var policyBytes = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(policyNode.ToJsonString()));
            ContractValidator.ParseAndValidate(policyBytes);
            Directory.CreateDirectory(Path.Combine(repo, ".forge"));
            await WriteNew(Path.Combine(repo, ".forge", "evidence-policy.json"), policyBytes, cancellationToken);
            await runtime.GitAsync(cancellationToken, "init", "-q", "-b", "main");
            await runtime.GitAsync(cancellationToken, "add", "--all");
            await runtime.GitAsync(cancellationToken, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "actual compiled GREEN barrier fixture");
            var protectedBase = await runtime.GitAsync(cancellationToken, "rev-parse", "HEAD");
            runtime.BeginStage(clock.WorkBudget(60));
            var detection = await new ToolCapabilityDetector(runtime).DetectAsync(new("dotnet", ExecutableSource.TrustedExplicit,
                runtime.Dotnet, repo, null, ["--version"], ["--help"], "10.0.401", "trx-v1", true), cancellationToken);
            await artifacts.WriteNewJsonAsync("interruption-actual-probe.json", new { fixture = true, detection }, cancellationToken);
            if (!detection.Capability.Supported || detection.Capability.Version != "10.0.401" || detection.VersionResult?.StandardOutput.Trim() != "10.0.401")
                throw new InvalidOperationException("Actual pinned SDK capability is unavailable; interruption evidence incomplete.");
            OwnedQualityInterruptionRuntime.RequireSuccessful(detection.VersionResult!, "version probe");
            OwnedQualityInterruptionRuntime.RequireSuccessful(detection.HelpResult!, "help probe");
            var versionPath = await SaveText(artifacts.Root, "interruption-version.stdout.txt", detection.VersionResult!.StandardOutput, cancellationToken);
            var helpPath = await SaveText(artifacts.Root, "interruption-help.stdout.txt", detection.HelpResult!.StandardOutput, cancellationToken);
            runtime.CommandBudget(TimeSpan.FromSeconds(1));
            runtime.BeginStage(clock.WorkBudget(90));
            var project = Path.Combine(repo, "Subject.Tests", "Subject.Tests.csproj");
            var restore = await runtime.ExecuteAsync(new(runtime.Dotnet,
                ["restore", project, "--locked-mode", "--source", runtime.PackageCache, "--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false"], repo,
                runtime.DotnetEnvironment), TimeSpan.FromSeconds(90), cancellationToken);
            OwnedQualityInterruptionRuntime.RequireSuccessful(restore, "locked restore");
            var restoreOut = await SaveText(artifacts.Root, "interruption-restore.stdout.txt", restore.StandardOutput, cancellationToken);
            var restoreErr = await SaveText(artifacts.Root, "interruption-restore.stderr.txt", restore.StandardError, cancellationToken);
            var lockPath = Path.Combine(repo, "Subject.Tests", "packages.lock.json");
            var assets = Path.Combine(artifacts.Root, "interruption-initial-project.assets.json");
            await WriteNew(assets, await File.ReadAllBytesAsync(Path.Combine(repo, "Subject.Tests", "obj", "project.assets.json"), cancellationToken), cancellationToken);
            var barrierSourceHash = OwnedQualityInterruptionRuntime.HashFile(Path.Combine(repo, "Subject.Tests", "CalculatorTests.cs"));
            runtime.CommandBudget(TimeSpan.FromSeconds(1));

            foreach (var phase in new[] { QualityInterruptionPhase.ColdControl, QualityInterruptionPhase.SourceDrift, QualityInterruptionPhase.Timeout, QualityInterruptionPhase.CallerCancellation })
            {
                runtime.AfterSdkResult = null;
                runtime.BeginStage(clock.WorkBudget(75));
                var cold = phase == QualityInterruptionPhase.ColdControl;
                if (cold) runtime.DeleteOwnedBuildOutputs();
                var dllsBefore = Directory.EnumerateFiles(repo, "*.dll", SearchOption.AllDirectories).ToArray();
                if (cold && dllsBefore.Length != 0) throw new IOException("Owned cold deletion left a compiled DLL.");
                var nonce = Guid.NewGuid().ToString("N");
                var trxRoot = Path.Combine(repo, ".fixture-trx"); Directory.CreateDirectory(trxRoot);
                var barrier = Path.Combine(trxRoot, phase + "-" + nonce); Directory.CreateDirectory(barrier);
                RequireOwnedPlain(runtime, barrier);
                var entered = Path.Combine(barrier, "entered-" + nonce);
                var release = Path.Combine(barrier, "release-" + nonce);
                var trx = Path.Combine(trxRoot, phase + "-" + nonce + ".trx");
                var environment = new Dictionary<string, string>(runtime.DotnetEnvironment)
                {
                    ["DELIVERY_FORGE_FIXTURE_BARRIER_DIRECTORY"] = barrier,
                    ["DELIVERY_FORGE_FIXTURE_BARRIER_NONCE"] = nonce
                };
                var args = new List<string> { "test", project, "-c", "Release", "--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "-p:RestoreLockedMode=true", "-p:RestoreSources=" + runtime.PackageCache };
                if (!cold) args.AddRange(["--no-build", "--no-restore"]);
                args.AddRange(["--logger", "trx;LogFileName=" + trx]);
                var invocation = new CommandInvocation(runtime.Dotnet, args.AsReadOnly(), repo, environment);
                var phaseRoot = Path.Combine(artifacts.Root, "quality-interruption-" + phase); Directory.CreateDirectory(phaseRoot);
                var configBytes = CanonicalJson.Canonicalize(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    fixture = true, phase = phase.ToString(), seed = 11018, nonce, invocation, protectedBase,
                    budgetId = QualityInterruptionClock.ProfileId, totalSeconds = 720, commandSeconds = 75,
                    selfLimitSeconds = 180, sdkVersion = detection.Capability.Version, sdkIdentity = detection.Capability.ExecutableIdentity
                }));
                var configPath = Path.Combine(phaseRoot, "configuration.json"); await WriteNew(configPath, configBytes, cancellationToken);
                var policy = await new ProtectedGitPolicyResolver(runtime, repo, protectedBase).ResolveAsync(".forge/evidence-policy.json", cancellationToken);
                var realReader = new GitRepositoryIdentityReader(runtime, repo, protectedBase);
                var before = await realReader.ReadAsync(cancellationToken);
                var lateBarrier = phase == QualityInterruptionPhase.Timeout ? new AfterReadBarrier(realReader, clock, cancellationToken) : null;
                IRepositoryIdentityReader reader = lateBarrier is null ? realReader : lateBarrier;
                CommandResult? observedPrimary = null;
                runtime.AfterSdkResult = result =>
                {
                    observedPrimary ??= result;
                    runtime.BeginStage(clock.WorkBudget(30));
                };
                var request = new GateRequest("test", policy, detection.Capability, EvidenceAdapterKind.DotNet,
                    OwnedQualityInterruptionRuntime.Hash(configBytes), before, invocation, "issue11-quality-interruption-fixture",
                    TimeSpan.FromSeconds(75), phaseRoot);
                using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var gateTask = new GateRunner(runtime, reader, repo, protectedBase).RunAsync(request, caller.Token);
                DateTimeOffset enteredAt;
                byte[]? modifiedSource = null;
                string? modifiedArchive = null, dirtyStatus = null;
                CommandResult? late = null;
                string? lateArchive = null;
                SortedDictionary<string, string>? hashesBeforeLate = null, hashesAfterLate = null;
                try
                {
                    enteredAt = await WaitEntered(runtime, entered, nonce, clock, cancellationToken);
                    if (phase == QualityInterruptionPhase.SourceDrift)
                    {
                        RequireOwnedPlain(runtime, calculator);
                        if (!File.ReadAllBytes(calculator).SequenceEqual(originalSource)) throw new IOException("Tracked original source changed before intended drift.");
                        modifiedSource = Encoding.UTF8.GetBytes("namespace RealDotNet.Subject; public static class Calculator { public static int Add(int left, int right) => left + right + 2; }\n");
                        await File.WriteAllBytesAsync(calculator, modifiedSource, cancellationToken);
                        modifiedArchive = Path.Combine(phaseRoot, "actual-modified-Calculator.cs.txt"); await WriteNew(modifiedArchive, modifiedSource, cancellationToken);
                        dirtyStatus = await SaveText(phaseRoot, "actual-dirty-git-status.txt", await runtime.GitAsync(cancellationToken, "status", "--porcelain=v1", "--untracked-files=all"), cancellationToken);
                        if (new FileInfo(dirtyStatus).Length == 0) throw new IOException("Intended tracked source drift did not produce dirty Git status.");
                    }
                    if (phase is QualityInterruptionPhase.ColdControl or QualityInterruptionPhase.SourceDrift)
                        await WriteNew(release, Encoding.UTF8.GetBytes(nonce), cancellationToken);
                    else if (phase == QualityInterruptionPhase.CallerCancellation)
                        caller.Cancel();
                    if (lateBarrier is not null)
                    {
                        await lateBarrier.Reached.WaitAsync(clock.WorkBudget(105), cancellationToken);
                        if (observedPrimary is not { TimedOut: true, Cancelled: false, OwnedProcessQuiescent: true })
                            throw new InvalidOperationException("Primary must actually time out and settle before a distinct late SDK execution.");
                        hashesBeforeLate = CaptureGateFiles(phaseRoot);
                        runtime.BeginStage(clock.WorkBudget(75));
                        var lateEnvironment = new Dictionary<string, string>(runtime.DotnetEnvironment)
                        {
                            ["DELIVERY_FORGE_FIXTURE_BARRIER_DIRECTORY"] = "",
                            ["DELIVERY_FORGE_FIXTURE_BARRIER_NONCE"] = ""
                        };
                        // Empty environment values disable the barrier in the separate real producer.
                        var lateInvocation = invocation with { Environment = lateEnvironment };
                        late = await runtime.ExecuteAsync(lateInvocation, TimeSpan.FromSeconds(75), cancellationToken);
                        OwnedQualityInterruptionRuntime.RequireSuccessful(late, "distinct sequential late TRX producer");
                        if (late.StartedAt < observedPrimary.CompletedAt) throw new InvalidOperationException("Late SDK execution overlapped the primary command.");
                        RequireOwnedPlain(runtime, trx);
                        lateArchive = Path.Combine(phaseRoot, "actual-distinct-late.trx"); await WriteNew(lateArchive, await File.ReadAllBytesAsync(trx, cancellationToken), cancellationToken);
                        hashesAfterLate = CaptureGateFiles(phaseRoot);
                        if (!hashesBeforeLate.SequenceEqual(hashesAfterLate)) throw new IOException("Late SDK producer changed the gate's already captured physical artifacts.");
                        runtime.BeginStage(clock.WorkBudget(30));
                        lateBarrier.Release();
                    }
                }
                catch (Exception primary)
                {
                    caller.Cancel(); lateBarrier?.Release();
                    using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0.001, Math.Min(60, clock.Remaining.TotalSeconds))));
                    try { await gateTask.WaitAsync(settle.Token); }
                    catch (Exception failure)
                    {
                        using var diagnostic = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0.001, Math.Min(2, clock.Remaining.TotalSeconds))));
                        try { await artifacts.WriteNewJsonAsync("interruption-settlement-failure-" + nonce + ".json", new { fixture = true, primary = primary.ToString(), failure = failure.ToString(), runtime.AllCommandsQuiescent }, diagnostic.Token); }
                        catch (Exception diagnosticFailure) { throw new AggregateException("Primary interruption failure, uncertain settlement and diagnostic failure retained.", primary, failure, diagnosticFailure); }
                        throw new AggregateException("Primary interruption failure and uncertain settlement retained.", primary, failure);
                    }
                    throw;
                }
                var gate = await gateTask.WaitAsync(clock.WorkBudget(30), cancellationToken);
                if (!gate.Execution.OwnedProcessQuiescent || observedPrimary != gate.Execution)
                    throw new InvalidOperationException("Published execution is not the genuine positively quiesced primary result.");
                var capturedReceipt = Path.Combine(phaseRoot, "actual-returned-receipt.json");
                var receiptBytes = await File.ReadAllBytesAsync(gate.ReceiptPath, cancellationToken);
                await WriteNew(capturedReceipt, receiptBytes, cancellationToken);
                if (phase == QualityInterruptionPhase.SourceDrift)
                {
                    if (gate.Outcome != GateOutcome.Error || !gate.SourceChanged || gate.After.IsClean || modifiedSource is null)
                        throw new InvalidOperationException("Actual dirty source must remain through public gate refusal before cleanup restoration.");
                    RequireOwnedPlain(runtime, calculator);
                    if (!File.ReadAllBytes(calculator).SequenceEqual(modifiedSource)) throw new IOException("Dirty source changed before exact cleanup-only restoration.");
                    await File.WriteAllBytesAsync(calculator, originalSource, cancellationToken);
                    if (await realReader.ReadAsync(cancellationToken) != before) throw new IOException("Cleanup-only original bytes did not restore the exact observed clean Git identity.");
                }
                var dllCopy = Path.Combine(phaseRoot, "actual-Subject.dll");
                var dll = Path.Combine(repo, "Subject", "bin", "Release", "net10.0", "Subject.dll"); RequireOwnedPlain(runtime, dll);
                await WriteNew(dllCopy, await File.ReadAllBytesAsync(dll, cancellationToken), cancellationToken);
                var reclaimDenied = false; var reclaimCommands = 0; string? reclaimOutcome = null;
                var receiptBeforeActivity = OwnedQualityInterruptionRuntime.Hash(receiptBytes);
                if (late is not null)
                {
                    RequireOwnedPlain(runtime, trx);
                    await File.WriteAllBytesAsync(trx, await File.ReadAllBytesAsync(lateArchive!, cancellationToken), cancellationToken);
                    var sdkCommands = runtime.SdkCommands;
                    try { await new GateRunner(runtime, realReader, repo, protectedBase).RunAsync(request, cancellationToken); }
                    catch (EvidenceWriteException exception) { reclaimDenied = true; reclaimOutcome = "ClaimRejected"; await SaveText(phaseRoot, "actual-claim-refusal.txt", exception.ToString(), cancellationToken); }
                    reclaimCommands = runtime.SdkCommands - sdkCommands;
                    if (!reclaimDenied || reclaimCommands != 0) throw new InvalidOperationException("Sealed output claim allowed reuse or another SDK command.");
                    if (!File.ReadAllBytes(gate.ReceiptPath).SequenceEqual(receiptBytes)) throw new IOException("Late activity changed the immutable returned receipt.");
                }
                var proof = new
                {
                    fixture = true, productionCertified = false, firstFullRealRunCertified = false, phase = phase.ToString(),
                    integratedBaseCommit = artifacts.IntegratedSource.Commit, implementationHead = artifacts.IntegratedSource.HeadCommit,
                    budgetId = QualityInterruptionClock.ProfileId, totalBudgetSeconds = 720, elapsedSeconds = clock.Elapsed.TotalSeconds,
                    observedSdkVersion = detection.Capability.Version, versionStdoutPath = versionPath, helpStdoutPath = helpPath,
                    helpExitCode = detection.HelpResult!.ExitCode, sdkExecutableSha256 = detection.Capability.ExecutableIdentity,
                    configurationSha256 = OwnedQualityInterruptionRuntime.Hash(configBytes), configurationPath = configPath,
                    protectedPolicySha256 = OwnedQualityInterruptionRuntime.Hash(policyBytes),
                    protectedBaseCommit = protectedBase, observedHeadCommit = gate.Before.HeadCommit, observedTreeId = gate.Before.TreeId,
                    initialRestoreExitCode = restore.ExitCode, initialRestoreOwnedQuiescent = restore.OwnedProcessQuiescent, initialRestoreMode = "locked",
                    initialRestoreStdoutPath = restoreOut, initialRestoreStderrPath = restoreErr,
                    assetsSha256 = OwnedQualityInterruptionRuntime.HashFile(assets), lockSha256 = OwnedQualityInterruptionRuntime.HashFile(lockPath), barrierSourceSha256 = barrierSourceHash,
                    nonce, enteredPath = entered, enteredAt, releasePath = release, releasedAfterEntered = File.Exists(release),
                    ownedBinAndObjRemoved = cold, generatedDllsBefore = dllsBefore, producedDllPath = dllCopy, dllHashAfter = OwnedQualityInterruptionRuntime.HashFile(dllCopy),
                    mutationAfterEntered = modifiedSource is not null, sourceDirtyThroughAfterRead = gate.SourceChanged && !gate.After.IsClean,
                    restoredOnlyAfterRefusalPersisted = modifiedSource is not null && gate.Outcome == GateOutcome.Error && File.Exists(capturedReceipt),
                    originalTrackedSourceSha256 = OwnedQualityInterruptionRuntime.Hash(originalSource), modifiedTrackedSourceSha256 = modifiedSource is null ? null : OwnedQualityInterruptionRuntime.Hash(modifiedSource),
                    modifiedSourceArchivePath = modifiedArchive, restoredTrackedSourcePath = calculator, dirtyGitStatusPath = dirtyStatus,
                    workloadSelfLimitSeconds = 180, requestedCommandDeadlineSeconds = 75, primaryReleaseWritten = File.Exists(release),
                    callerCancelledAfterEntered = phase == QualityInterruptionPhase.CallerCancellation && caller.IsCancellationRequested,
                    resultAwaitedWithUncancelledOuterToken = !cancellationToken.IsCancellationRequested,
                    secondActualRepositoryReadBarrierReached = lateBarrier?.WasReached ?? false,
                    primaryQuiescentBeforeLateStart = late is not null && gate.Execution.OwnedProcessQuiescent && late.StartedAt >= gate.Execution.CompletedAt,
                    lateExitCode = late?.ExitCode, lateOwnedProcessQuiescent = late?.OwnedProcessQuiescent, lateTimedOut = late?.TimedOut, lateCancelled = late?.Cancelled,
                    lateStartedAt = late?.StartedAt, lateCompletedAt = late?.CompletedAt, lateTrxArchivePath = lateArchive,
                    lateTrxSha256 = lateArchive is null ? null : OwnedQualityInterruptionRuntime.HashFile(lateArchive), lateLoggerRequestedPath = late is null ? null : trx,
                    capturedArtifactHashesBeforeLate = hashesBeforeLate, capturedArtifactHashesAfterLate = hashesAfterLate,
                    receiptSha256BeforeLateActivity = receiptBeforeActivity, receiptSha256AfterLateActivity = OwnedQualityInterruptionRuntime.HashFile(gate.ReceiptPath),
                    outputReclaimDenied = reclaimDenied, outputReclaimSdkCommandCount = reclaimCommands, outputReclaimOutcome = reclaimOutcome,
                    lateReportAttributedToPrimary = false,
                    limitation = "Actual compiled test/native SDK only. Distinct sequential late execution is not primary-process late completion; no model-worker or production certification."
                };
                var provenance = Path.Combine(phaseRoot, "provenance.json"); await WriteNew(provenance, JsonSerializer.SerializeToUtf8Bytes(proof), cancellationToken);
                phases.Add(phase, new(gate, entered, nonce, trx, provenance, capturedReceipt));
                if (phase == QualityInterruptionPhase.Timeout)
                {
                    var lateProof = Path.Combine(phaseRoot, "late-provenance.json"); await WriteNew(lateProof, JsonSerializer.SerializeToUtf8Bytes(proof), cancellationToken);
                    phases.Add(QualityInterruptionPhase.LateArtifact, new(gate, entered, nonce, trx, lateProof, capturedReceipt));
                }
                runtime.CommandBudget(TimeSpan.FromSeconds(1));
            }
            return new ReadOnlyDictionary<QualityInterruptionPhase, QualityInterruptionSnapshot>(phases);
        }
        finally
        {
            artifacts.RetainArtifacts("Actual interruption runtime retained: " + runtime.Root);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0.001, Math.Min(60, clock.Remaining.TotalSeconds))));
            await artifacts.WriteNewJsonAsync("quality-interruption-retained-runtime.json", new { fixture = true, runtime.Root,
                runtime.AllCommandsQuiescent, elapsedSeconds = clock.Elapsed.TotalSeconds, retained = true,
                limitation = "No recursive deletion or production authority follows from gate/file observations." }, cleanup.Token);
        }
    }

    private static async Task<DateTimeOffset> WaitEntered(OwnedQualityInterruptionRuntime runtime, string path, string nonce, QualityInterruptionClock clock, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp(); var bound = clock.WorkBudget(60);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= bound) throw new TimeoutException("Actual compiled test never published this phase's entered nonce within60 seconds.");
            if (File.Exists(path))
            {
                RequireOwnedPlain(runtime, path);
                try
                {
                    if (await File.ReadAllTextAsync(path, token) == nonce)
                    {
                        token.ThrowIfCancellationRequested();
                        if (Stopwatch.GetElapsedTime(started) >= bound) throw new TimeoutException("Compiled entry was observed after the original bound.");
                        return DateTimeOffset.UtcNow;
                    }
                }
                catch (IOException) { /* CreateNew producer still holds its actual entered-file write. */ }
            }
            await Task.Delay(20, token);
        }
    }
    private static void RequireOwnedPlain(OwnedQualityInterruptionRuntime runtime, string path)
    {
        runtime.EnsureOwner();
        path = Path.GetFullPath(path);
        if (!path.StartsWith(runtime.Repository + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Phase artifact escaped the exact live owned repository.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Phase file/ancestor is redirected."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (current == runtime.Root) break;
        }
    }
    private static SortedDictionary<string, string> CaptureGateFiles(string phaseRoot)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var directory = Path.Combine(phaseRoot, "test");
        foreach (var name in new[] { "stdout.txt", "stderr.txt", "evidence.trx" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) values.Add(name, OwnedQualityInterruptionRuntime.HashFile(path));
        }
        return values;
    }
    private static Task WriteNew(string path, byte[] bytes, CancellationToken token) => OwnedQualityInterruptionRuntime.WriteNewAsync(path, bytes, token);
    private static async Task<string> SaveText(string root, string name, string value, CancellationToken token)
    {
        var path = Path.Combine(root, name); await WriteNew(path, Encoding.UTF8.GetBytes(value), token); return path;
    }

    // Test timing only: both reads delegate and return the exact real Git identity; no synthesized facts.
    private sealed class AfterReadBarrier(IRepositoryIdentityReader actual, QualityInterruptionClock clock, CancellationToken outer) : IRepositoryIdentityReader
    {
        private int _reads;
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Reached => _reached.Task;
        public bool WasReached { get; private set; }
        public void Release() => _release.TrySetResult();
        public async Task<RepositoryIdentity> ReadAsync(CancellationToken cancellationToken)
        {
            var value = await actual.ReadAsync(cancellationToken);
            if (Interlocked.Increment(ref _reads) == 2)
            {
                WasReached = true; _reached.TrySetResult();
                // One subsequent75s command plus30s diagnostics, bounded by the original720s wall.
                await _release.Task.WaitAsync(clock.WorkBudget(105), outer);
            }
            return value;
        }
    }
}
