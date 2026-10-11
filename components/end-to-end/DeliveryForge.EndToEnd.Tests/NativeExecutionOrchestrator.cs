using System.Security.Cryptography;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Scheduling;

namespace DeliveryForge.EndToEnd.Tests;

internal static class NativeExecutionOrchestrator
{
    public static async Task ObserveAsync(OwnedScenarioRun artifacts, OwnedEphemeralRuntime runtime, string scenario,
        List<string> receipts, Dictionary<string, bool> invariants, CancellationToken cancellationToken)
    {
        if (scenario.StartsWith("cleanup.", StringComparison.Ordinal))
        {
            await CleanupNegativeAsync(artifacts, runtime, scenario, receipts, invariants, cancellationToken);
            return;
        }
        var context = await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", cancellationToken);
        var exact = await runtime.Reader.ReadFileAsync(context, "subject.txt", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var request = new PlanningRequest("fixture/delivery-forge", "#11", "implement", "Exercise owned native task contracts",
            ["subject.txt"], ["excluded"], ["Bounded owned native task; no model worker claim"], "implement");
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(exact, now, [])];
        var draft = new PlanDraft(request, context, evidence, [new("subject.txt", "fixture", "native contract")], [new("#11", [], "root")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], IntakePlanner.Assess(request, evidence));
        var plan = PlanFreezer.Freeze(draft, "owned-native-fixture", now);
        var worker = new FrozenWorkerPlan(runtime.RuntimeId, plan.ContractIdentity, plan.BaseCommit, plan.Repository,
            runtime.Repository, runtime.Worktree, runtime.Results, Guid.NewGuid().ToString("N"), ["subject.txt"], ["excluded"],
            "fixture-owned-native-task", "1", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
        var preparedData = WorkerEnvelope.PrepareWorker(worker);
        var binding = new AgentRunBinding(preparedData.AdapterId, preparedData.AdapterVersion, preparedData.RunId,
            preparedData.RequestIdentity, "fixture-runtime:" + runtime.RuntimeId, "fixture-task:" + runtime.RuntimeId);
        var host = new NativeFixtureControl(runtime, binding, scenario == "execution.unknown-host");
        using var execution = new ExecutionCoordinator(runtime.Store, runtime.Worktrees, runtime.Processes, host);
        var scheduler = LocalSingleScheduler.Create(plan, worker, SchedulingScenarioFixture.Demand, SchedulingScenarioFixture.Budget(),
            runtime.Store, execution, runtime.Worktrees, runtime.Reader);
        var ticket = await scheduler.ReserveAsync(cancellationToken);
        var prepared = await scheduler.ProvisionAndPrepareAsync(ticket, cancellationToken);
        var observation = await runtime.Worktrees.ObserveAsync(runtime.Worktree, cancellationToken);
        var accepted = new AgentAcceptedReceipt(WorkerEnvelope.SchemaVersion, prepared.RunId, prepared.RequestIdentity,
            prepared.PlanIdentity, prepared.BaseCommit, prepared.Worktree, prepared.AdapterId, prepared.AdapterVersion,
            binding.RuntimeRunIdentity, binding.RuntimeTaskIdentity, DateTimeOffset.UtcNow,
            new[] { AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Pause, AgentCapability.Stop, AgentCapability.Resume }
                .Select(capability => new AgentCapabilityEvidence(capability, AgentCapabilityStatus.Supported,
                    "FIXTURE native-task contract", "fixture-native-session:" + runtime.RuntimeId)).ToArray());
        await execution.AcceptAsync(accepted, cancellationToken);
        runtime.BindSession(execution, host, binding, prepared);
        var writerExclusive = false;
        try { using var foreign = runtime.Worktrees.AcquireWriter(runtime.Worktree, "foreign", "foreign"); }
        catch (DeliveryForge.Execution.Git.WorktreeBoundaryException) { writerExclusive = true; }
        var childInitiallyLive = false; var childFinallyQuiet = true; var paused = false;
        var lateDenied = false; var resumeDenied = false; var startDenied = false;
        var closedBeforeFresh = false; var closedAfterFresh = false; var localQuietUnknown = false;
        var beforeCount = 0; var afterCount = 0;
        try
        {
            if (scenario != "execution.real-provision")
            {
                var executable = OperatingSystem.IsWindows() ? FindPowerShell() : FindExecutable("sleep");
                var arguments = OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" } : new[] { "30" };
                var launch = new ProcessLaunch(executable, arguments);
                var child = await execution.StartTaskProcessAsync(worker.RunId, launch, cancellationToken);
                var live = runtime.Processes.Observe(child.Identity);
                await artifacts.WriteNewJsonAsync("native-execution-identity.json", new { fixture = true, child.Identity, live }, cancellationToken);
                childInitiallyLive = live.Outcome == ProcessControlOutcome.LiveOwned;
                if (!childInitiallyLive) throw new InvalidOperationException("Actual registered task is not LiveOwned: " + live.Reason);
                beforeCount = (await runtime.Store.RecoverAsync(worker.RunId, cancellationToken)).Records.Count(item => item.Kind == "process-registered");
                if (scenario == "execution.unknown-host")
                {
                    try { await scheduler.PauseAsync(cancellationToken); }
                    catch (SchedulingException) { }
                    localQuietUnknown = runtime.Processes.Observe(child.Identity).Outcome == ProcessControlOutcome.AlreadyExited;
                    closedBeforeFresh = await scheduler.StopAsync(cancellationToken);
                    var deniedCleanup = await scheduler.ReconcileCleanupAsync(cancellationToken);
                    invariants["unknown-host-retains-capacity"] = !closedBeforeFresh && !deniedCleanup;
                    invariants["local-exit-not-host-proof"] = localQuietUnknown && !deniedCleanup;
                    host.EnableFreshOwnedFixtureObservations();
                    closedAfterFresh = await scheduler.ReconcileCleanupAsync(cancellationToken);
                    invariants["fresh-bound-cleanup-required"] = closedAfterFresh && host.Observations >= 3;
                }
                else
                {
                    if (scenario == "execution.pause-live-child")
                    {
                        await scheduler.PauseAsync(cancellationToken);
                        paused = (await runtime.Store.RecoverAsync(worker.RunId, cancellationToken)).Records.Any(item => item.Kind == "paused");
                        try { _ = await scheduler.ProvisionAndPrepareAsync(ticket, cancellationToken); }
                        catch (SchedulingException) { lateDenied = true; }
                    }
                    if (!await scheduler.StopAsync(cancellationToken)) throw new InvalidOperationException(scheduler.CleanupLimitation);
                    try { _ = await scheduler.ResumeAsync(ticket, cancellationToken); }
                    catch (SchedulingException) { resumeDenied = true; }
                    try { _ = await execution.StartTaskProcessAsync(worker.RunId, launch, cancellationToken); }
                    catch (WorkerEnvelopeException) { startDenied = true; }
                }
                childFinallyQuiet = runtime.Processes.Observe(child.Identity).Outcome == ProcessControlOutcome.AlreadyExited;
                afterCount = (await runtime.Store.RecoverAsync(worker.RunId, cancellationToken)).Records.Count(item => item.Kind == "process-registered");
            }
            else if (!await scheduler.StopAsync(cancellationToken)) throw new InvalidOperationException(scheduler.CleanupLimitation);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            host.EnableFreshOwnedFixtureObservations();
            if (!await scheduler.StopAsync(cleanup.Token) && !await scheduler.ReconcileCleanupAsync(cleanup.Token))
                throw new InvalidOperationException("Native fixture closure remains uncertain: " + scheduler.CleanupLimitation);
        }
        invariants["real-owned-worktree-provisioned"] = observation.IsClean && observation.HeadCommit == plan.BaseCommit;
        invariants["exact-prepared-request-bound"] = prepared.RequestIdentity == WorkerEnvelope.PrepareWorker(worker).RequestIdentity;
        invariants["writer-exclusive-observed"] = writerExclusive;
        invariants["actual-native-child-paused"] = childInitiallyLive && childFinallyQuiet && paused;
        invariants["paused-blocks-new-dispatch"] = lateDenied;
        invariants["fresh-bound-fixture-host-observed"] = host.Observations > 0;
        invariants["terminal-stop-recorded"] = (await runtime.Store.RecoverAsync(worker.RunId, cancellationToken)).Records.Any(item => item.Kind == "stopped");
        invariants["stopped-run-cannot-resume"] = resumeDenied;
        invariants["stopped-run-cannot-start-child"] = startDenied && beforeCount == afterCount;
        var registered = await runtime.GitAsync(cancellationToken, "worktree", "list", "--porcelain");
        var history = await runtime.Store.RecoverAsync(worker.RunId, cancellationToken);
        var record = await artifacts.Store.AppendAsync(artifacts.RunId, "scenario-execution-observation", new
        {
            fixture = true, productionCertified = false, firstFullRealRunCertified = false,
            integratedBaseCommit = artifacts.IntegratedSource.Commit, implementationHead = artifacts.IntegratedSource.HeadCommit,
            frozenBaseCommit = plan.BaseCommit, preparedHeadCommit = observation.HeadCommit, prepared.RequestIdentity,
            fixtureNativeTaskOnly = true, worktreeRegistered = registered.Replace('\\', '/').Contains(runtime.Worktree.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase),
            childInitiallyLive, childFinallyQuiescent = childFinallyQuiet, pauseRecorded = paused,
            processRegistrationsBefore = beforeCount, processRegistrationsAfter = afterCount,
            localChildQuiescentWhileHostUnknown = localQuietUnknown, closedBeforeFreshHostObservation = closedBeforeFresh,
            closedAfterFreshBoundHostObservation = closedAfterFresh, journalHashes = history.Records.Select(item => item.Sha256).ToArray(),
            limitation = "Exact public contract fixture/native task only; no host-invoked model worker, quality PASS or full-profile certification."
        }, cancellationToken);
        receipts.Add(record.Sha256);
        execution.Dispose(); // Actual exclusive writer handle must settle before filesystem cleanup.
        if (!await runtime.TryCleanupAsync(cancellationToken)) throw new InvalidOperationException("Closed native runtime was not safely cleanable.");
    }

    private static async Task CleanupNegativeAsync(OwnedScenarioRun artifacts, OwnedEphemeralRuntime runtime, string scenario,
        List<string> receipts, Dictionary<string, bool> invariants, CancellationToken cancellationToken)
    {
        var outside = Path.Combine(runtime.Prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        await using (var marker = new FileStream(Path.Combine(outside, "owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await System.Text.Json.JsonSerializer.SerializeAsync(marker,
                new { fixture = true, outside, ownerRunId = artifacts.RunId, nonce = Guid.NewGuid().ToString("N"), retainedSentinel = true }, cancellationToken: cancellationToken);
        await using (var ownedSentinel = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await ownedSentinel.WriteAsync(System.Text.Encoding.UTF8.GetBytes("outside-owned-sentinel"), cancellationToken);
        var reparse = false; var reparseReason = "not requested";
        if (scenario == "cleanup.changed-owner-marker") await File.AppendAllTextAsync(runtime.MarkerPath, "changed-owner", cancellationToken);
        else
        {
            try { Directory.CreateSymbolicLink(Path.Combine(runtime.Root, "foreign-link"), outside); reparse = true; reparseReason = "actual symlink created"; }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                reparseReason = exception.GetType().Name + ": " + exception.Message;
                await File.WriteAllTextAsync(Path.Combine(runtime.Root, "foreign-unowned-entry.txt"), "foreign marker", cancellationToken);
            }
        }
        var refused = !await runtime.TryCleanupAsync(cancellationToken);
        var markerHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(runtime.MarkerPath, cancellationToken)));
        invariants["changed-marker-cleanup-refused"] = refused;
        invariants["bad-marker-retained"] = markerHash != runtime.ExpectedMarkerSha256 && Directory.Exists(runtime.Root);
        invariants["foreign-entry-cleanup-refused"] = refused;
        invariants["outside-sentinel-preserved"] = File.Exists(sentinel) && await File.ReadAllTextAsync(sentinel, cancellationToken) == "outside-owned-sentinel";
        invariants["reparse-capability-honest"] = reparse || reparseReason != "not requested";
        var record = await artifacts.Store.AppendAsync(artifacts.RunId, "scenario-execution-observation", new
        {
            fixture = true, productionCertified = false, integratedBaseCommit = artifacts.IntegratedSource.Commit,
            implementationHead = artifacts.IntegratedSource.HeadCommit, outsideSentinel = sentinel, actualMarkerSha256 = markerHash,
            expectedMarkerSha256 = runtime.ExpectedMarkerSha256, cleanupRefused = refused,
            reparseCreated = reparse, reparseCapabilityOutcome = reparse ? "OBSERVED" : "INCOMPLETE", reparseCapabilityReason = reparseReason,
            limitation = "Outside sentinel and altered/foreign runtime retained, no cleanup authority from marker data."
        }, cancellationToken);
        receipts.Add(record.Sha256);
    }
    private static string FindExecutable(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, name)).First(File.Exists);
    private static string FindPowerShell()
    {
        try { return FindExecutable("pwsh.exe"); }
        catch (InvalidOperationException) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"); }
    }
}
