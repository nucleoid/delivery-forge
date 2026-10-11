using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Scheduling;

namespace DeliveryForge.EndToEnd.Tests;

internal static class ColdRecoveryOrchestrator
{
    public static async Task ObserveAsync(OwnedScenarioRun artifacts, OwnedEphemeralRuntime runtime, string scenario,
        List<string> receipts, Dictionary<string, bool> invariants, CancellationToken cancellationToken)
    {
        var plan = await FreezeAsync(runtime, cancellationToken);
        var worker = new FrozenWorkerPlan(runtime.RuntimeId, plan.ContractIdentity, plan.BaseCommit, plan.Repository,
            runtime.Repository, runtime.Worktree, runtime.Results, Guid.NewGuid().ToString("N"), ["subject.txt"], ["excluded"],
            "fixture-cold-native-task", "1", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
        var request = WorkerEnvelope.PrepareWorker(worker);
        var accepted = Accepted(request);
        var binding = new AgentRunBinding(accepted.AdapterId, accepted.AdapterVersion, accepted.RunId, accepted.RequestIdentity,
            accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity);
        var host = new ColdHostFixture(binding);
        var requiredPath = Path.Combine(runtime.Results, "required-fixture.txt");
        var requiredBytes = Encoding.UTF8.GetBytes("original-required-fixture-artifact");
        var facts = new ColdFactsFixture(request, scenario == "cold.missing-required-artifact" ? [requiredPath] : []);
        var original = new ExecutionCoordinator(runtime.Store, runtime.Worktrees, runtime.Processes, host, facts);
        ExecutionCoordinator? receiverExecution = null;
        LocalSingleScheduler? parent = null;
        LocalSingleScheduler? receiver = null;
        ProcessControl? freshController = null;
        var originalDisposed = false;
        var barrier = Path.Combine(runtime.Results, "release-child");
        var childArtifact = Path.Combine(runtime.Results, "child-completed.txt");
        AgentRequest? resumedRequest = null;
        var observedBase = plan.BaseCommit;
        var restoredForCleanup = false;
        var completionAfterRegistration = false;
        var freshOutcome = "not requested";
        Exception? primaryFailure = null;
        var delayed = scenario.StartsWith("cold.delayed-completion", StringComparison.Ordinal);
        try
        {
            LocalDispatchReservation? ticket = null;
            if (!delayed)
            {
                parent = LocalSingleScheduler.Create(plan, worker, Demand, Budget(), runtime.Store, original, runtime.Worktrees,
                    runtime.Reader, TimeSpan.FromSeconds(2));
                runtime.RetainColdOwner(parent, original);
                ticket = await parent.ReserveAsync(cancellationToken);
                request = await parent.ProvisionAndPrepareAsync(ticket, cancellationToken);
            }
            else
            {
                await runtime.Worktrees.PrepareAsync(runtime.Repository, runtime.Worktree, plan.BaseCommit, cancellationToken);
                request = (await original.PrepareWorkerAsync(worker, cancellationToken)).Request;
            }
            Directory.CreateDirectory(runtime.Results);
            if (facts.Required.Count != 0) await WriteCreateNewAsync(requiredPath, requiredBytes, cancellationToken);
            accepted = Accepted(request);
            await original.AcceptAsync(accepted, cancellationToken);
            var child = await original.StartTaskProcessAsync(worker.RunId, ChildLaunch(barrier, childArtifact), cancellationToken);
            var initiallyLive = runtime.Processes.Observe(child.Identity);
            if (initiallyLive.Outcome != ProcessControlOutcome.LiveOwned)
                throw new InvalidOperationException("Registered cold child is not LiveOwned: " + initiallyLive.Reason);
            var registered = await runtime.Store.RecoverAsync(worker.RunId, cancellationToken);
            if (registered.Records.Count(item => item.Kind == "process-registered") != 1)
                throw new InvalidOperationException("Child registration was not durably observed before its release barrier.");
            if (delayed)
            {
                await WriteCreateNewAsync(barrier, [1], cancellationToken);
                await WaitForActualExitAsync(artifacts, runtime.Processes, child.Identity, cancellationToken);
                completionAfterRegistration = File.Exists(childArtifact) &&
                    await File.ReadAllTextAsync(childArtifact, cancellationToken) == "owned-child-completed";
                if (!completionAfterRegistration) throw new InvalidOperationException("Actual barrier child did not produce its owned result.");
                original.Dispose(); originalDisposed = true;
                var freshStore = new RunStore(runtime.Store.RootPath);
                var controller = runtime.Processes;
                if (scenario.EndsWith("fresh-controller", StringComparison.Ordinal)) controller = freshController = new ProcessControl();
                receiverExecution = new(freshStore, runtime.Worktrees, controller, host, facts);
                var actual = await runtime.Worktrees.ObserveAsync(runtime.Worktree, cancellationToken);
                var completion = new AgentCompletionReceipt(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity,
                    request.AdapterId, request.AdapterVersion, accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity, "success",
                    request.Worktree, actual.HeadCommit, actual.TreeId, DateTimeOffset.UtcNow, [childArtifact],
                    ["FIXTURE native barrier task; fresh coordinator is not OS parent crash/model worker/full-profile evidence."]);
                invariants["actual-barrier-child-exited"] = runtime.Processes.Observe(child.Identity).Outcome == ProcessControlOutcome.AlreadyExited;
                if (freshController is null)
                {
                    await receiverExecution.CompleteAsync(completion, cancellationToken);
                    invariants["fresh-coordinator-completes-with-retained-controller"] =
                        (await freshStore.RecoverAsync(worker.RunId, cancellationToken)).Records.Count(item => item.Kind == "worker-completed") == 1;
                }
                else
                {
                    freshOutcome = freshController.Observe(child.Identity).Outcome.ToString();
                    var refused = false;
                    try { await receiverExecution.CompleteAsync(completion, cancellationToken); }
                    catch (WorkerEnvelopeException) { refused = true; }
                    invariants["fresh-controller-cannot-adopt-recorded-child"] = refused && freshOutcome == "IdentityUnknown" &&
                        !(await freshStore.RecoverAsync(worker.RunId, cancellationToken)).Records.Any(item => item.Kind == "worker-completed");
                }
            }
            else
            {
                if (scenario == "cold.stopped-no-resume")
                {
                    if (!await parent!.StopAsync(cancellationToken)) throw new InvalidOperationException(parent.CleanupLimitation);
                }
                else
                {
                    await parent!.PauseAsync(cancellationToken);
                    await parent.RelinquishPausedAsync(cancellationToken);
                }
                if (scenario != "cold.held-original-writer") { original.Dispose(); originalDisposed = true; }
                if (scenario == "cold.base-drift")
                {
                    await File.WriteAllTextAsync(Path.Combine(runtime.Repository, "subject.txt"), "actual base drift after transfer\n", cancellationToken);
                    await runtime.GitAsync(cancellationToken, "add", "--", "subject.txt");
                    await runtime.GitAsync(cancellationToken, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "cold receiver base drift");
                    observedBase = (await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", cancellationToken)).Commit;
                }
                if (scenario == "cold.missing-required-artifact")
                {
                    RequirePlainOwnedResult(runtime, requiredPath, requireAbsent: false);
                    var beforeDelete = await File.ReadAllBytesAsync(requiredPath, cancellationToken);
                    if (!beforeDelete.SequenceEqual(requiredBytes)) throw new IOException("Original owned required artifact changed before deletion.");
                    await artifacts.WriteNewJsonAsync("cold-required-before-delete.json", new
                    { fixture = true, requiredPath, sha256 = Convert.ToHexStringLower(SHA256.HashData(beforeDelete)), required = facts.Required }, cancellationToken);
                    File.Delete(requiredPath);
                }
                if (scenario == "cold.foreign-fresh-host") host.Foreign = true;
                var freshStore = new RunStore(runtime.Store.RootPath);
                receiverExecution = new(freshStore, runtime.Worktrees, runtime.Processes, host, facts);
                var receiverBudget = Budget();
                receiver = LocalSingleScheduler.Create(plan, worker, Demand, receiverBudget, freshStore, receiverExecution,
                    runtime.Worktrees, runtime.Reader, TimeSpan.FromSeconds(2));
                runtime.RetainColdOwner(receiver, receiverExecution);
                if (scenario == "cold.paused-new-ticket")
                {
                    var recoveredTicket = await receiver.RecoverPausedAsync(cancellationToken);
                    var deniedProvision = false; var deniedResume = false;
                    try { await receiver.ProvisionAndPrepareAsync(ticket!, cancellationToken); }
                    catch (SchedulingException) { deniedProvision = true; }
                    try { await receiver.ResumeAsync(ticket!, cancellationToken); }
                    catch (SchedulingException) { deniedResume = true; }
                    invariants["old-ticket-denied"] = deniedProvision && deniedResume && !ReferenceEquals(ticket, recoveredTicket);
                    resumedRequest = await receiver.ResumeAsync(recoveredTicket, cancellationToken);
                    invariants["new-ticket-resumes-exact-request"] = resumedRequest.RequestIdentity == request.RequestIdentity &&
                        WorkerEnvelope.ComputeRequestIdentity(resumedRequest) == request.RequestIdentity &&
                        resumedRequest.WriterToken == request.WriterToken && resumedRequest.AuthorizationCeiling == request.AuthorizationCeiling;
                }
                else
                {
                    var denied = false;
                    try { await receiver.RecoverPausedAsync(cancellationToken); }
                    catch (SchedulingException) { denied = true; }
                    if (!denied) throw new InvalidOperationException("Negative cold recovery unexpectedly issued a ticket.");
                    var noResume = host.ResumeCalls == 0;
                    var cleanupRefusedWhileUnproved = false;
                    if (scenario is "cold.held-original-writer" or "cold.missing-required-artifact" or "cold.foreign-fresh-host")
                        cleanupRefusedWhileUnproved = !await receiver.StopAsync(cancellationToken) &&
                            !await receiver.ReconcileCleanupAsync(cancellationToken);
                    switch (scenario)
                    {
                        case "cold.held-original-writer":
                            invariants["live-writer-refuses-adoption"] = !originalDisposed && cleanupRefusedWhileUnproved;
                            invariants["no-resume-before-writer-release"] = noResume; break;
                        case "cold.base-drift":
                            invariants["fresh-raw-base-drift-denied"] = observedBase != plan.BaseCommit;
                            invariants["no-resume-after-base-drift"] = noResume; break;
                        case "cold.missing-required-artifact":
                            invariants["pinned-artifact-absence-denied"] = !File.Exists(requiredPath) && cleanupRefusedWhileUnproved;
                            invariants["required-list-not-weakened"] = facts.Required.Count == 1 && facts.Required[0] == requiredPath; break;
                        case "cold.foreign-fresh-host":
                            invariants["foreign-or-unknown-host-denied"] = host.Foreign && cleanupRefusedWhileUnproved;
                            invariants["no-resume-from-foreign-quiescence"] = noResume; break;
                        case "cold.stopped-no-resume":
                            var oldDenied = false;
                            try { await parent!.ResumeAsync(ticket!, cancellationToken); } catch (SchedulingException) { oldDenied = true; }
                            invariants["terminal-journal-refuses-adoption"] = oldDenied;
                            invariants["cleanup-only-no-ticket-or-resume"] = noResume && await receiver.ReconcileColdTerminalAsync(cancellationToken); break;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            try
            {
                await WriteDiagnosticAsync(artifacts, "cold-primary-failure.json", new
                {
                    fixture = true, scenario, type = exception.GetType().FullName, message = Bound(exception.Message), stack = Bound(exception.StackTrace),
                    ownedObservations = runtime.Processes.GetAllOwnedIdentities().Select(identity => new { identity, observed = runtime.Processes.Observe(identity) }).ToArray(),
                    limitation = "Primary exception captured before cleanup; fresh public samples are data, never exit inference or repair."
                });
            }
            catch (Exception diagnosticFailure) { artifacts.RetainArtifacts("Primary cold diagnostic write failed: " + diagnosticFailure.GetType().Name); }
            throw;
        }
        finally
        {
            try
            {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            host.Foreign = false;
            if (facts.Required.Count != 0 && !File.Exists(requiredPath))
            {
                RequirePlainOwnedResult(runtime, requiredPath);
                await WriteCreateNewAsync(requiredPath, requiredBytes, cleanup.Token);
                if (!SHA256.HashData(await File.ReadAllBytesAsync(requiredPath, cleanup.Token)).SequenceEqual(SHA256.HashData(requiredBytes)))
                    throw new InvalidOperationException("Cleanup-only artifact restoration differs from original owned bytes.");
                restoredForCleanup = true;
            }
            if (receiver is null && parent is not null &&
                !await parent.StopAsync(cleanup.Token) && !await parent.ReconcileCleanupAsync(cleanup.Token))
                throw new InvalidOperationException("Original in-progress cold setup still retains its live owner: " + parent.CleanupLimitation);
            if (!originalDisposed) { original.Dispose(); originalDisposed = true; }
            if (receiver is not null && !await receiver.StopAsync(cleanup.Token) && !await receiver.ReconcileCleanupAsync(cleanup.Token))
                throw new InvalidOperationException("Actual cold receiver ownership remains retained: " + receiver.CleanupLimitation);
            if (parent is not null && !await parent.StopAsync(cleanup.Token) && !await parent.ReconcileCleanupAsync(cleanup.Token))
                throw new InvalidOperationException("Original cold parent ownership remains retained: " + parent.CleanupLimitation);
            receiverExecution?.Dispose();
            if (delayed)
            {
                using var ownedCleanup = new ExecutionCoordinator(new RunStore(runtime.Store.RootPath), runtime.Worktrees, runtime.Processes, host, facts);
                var settled = await ownedCleanup.ReconcileStopAsync(worker.RunId, TimeSpan.FromSeconds(2), cleanup.Token);
                if (!settled.Quiescent) throw new InvalidOperationException("Retained original controller did not prove fresh completion cleanup.");
            }
            if (freshController is not null) await freshController.DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                try
                {
                    await WriteDiagnosticAsync(artifacts, "cold-cleanup-failure.json", new
                    {
                        fixture = true, scenario, type = cleanupFailure.GetType().FullName, message = Bound(cleanupFailure.Message),
                        primaryType = primaryFailure?.GetType().FullName, primaryMessage = Bound(primaryFailure?.Message),
                        limitation = "Cleanup failure does not replace the saved primary failure or manufacture quiescence."
                    });
                }
                catch (Exception diagnosticFailure) { artifacts.RetainArtifacts("Cold cleanup diagnostic write failed: " + diagnosticFailure.GetType().Name); }
                if (primaryFailure is not null) throw new AggregateException("Primary cold failure and cleanup failure retained.", primaryFailure, cleanupFailure);
                throw;
            }
        }
        var history = await runtime.Store.RecoverAsync(worker.RunId, cancellationToken);
        invariants["fresh-store-coordinator-lifetime"] = receiverExecution is not null;
        invariants["no-native-relaunch"] = history.Records.Count(item => item.Kind == "process-registered") == 1 && runtime.Processes.GetAllOwnedIdentities().Count == 1;
        var record = await artifacts.Store.AppendAsync(artifacts.RunId, "scenario-cold-observation", new
        {
            fixture = true, productionCertified = false, actualOsParentCrash = false, scenario,
            integratedBaseCommit = artifacts.IntegratedSource.Commit, implementationHead = artifacts.IntegratedSource.HeadCommit,
            frozenBaseCommit = plan.BaseCommit, receiverObservedBaseCommit = observedBase, workerRunId = worker.RunId,
            request.RequestIdentity, request.WriterToken, request.AuthorizationCeiling,
            resumedRequestIdentity = resumedRequest?.RequestIdentity, resumedWriterToken = resumedRequest?.WriterToken,
            resumedAuthorizationCeiling = resumedRequest?.AuthorizationCeiling, hostCallLedger = host.Ledger.ToArray(), resumeHostCalls = host.ResumeCalls,
            journalHashes = history.Records.Select(item => item.Sha256).ToArray(), actualChildArtifactPath = childArtifact,
            completionAfterDurableRegistration = completionAfterRegistration, freshControllerProcessOutcome = freshOutcome,
            cleanupOnlyOriginalArtifactRestored = restoredForCleanup,
            limitation = "Fresh store/coordinator lifetime and explicit external fixture control only; retained-controller proof is not PID adoption, OS crash, model-worker, quality or full-profile certification."
        }, cancellationToken);
        receipts.Add(record.Sha256);
    }

    private static RunDemand Demand => new(new(2, 2, 4096, 0, 0, 0), [], TimeSpan.FromSeconds(90), 1000, 0m);
    private static ResourceBudget Budget() => SchedulingScenarioFixture.Budget();
    private static async Task<FrozenPlan> FreezeAsync(OwnedEphemeralRuntime runtime, CancellationToken cancellationToken)
    {
        var context = await runtime.Reader.ReadAsync(runtime.Repository, "refs/heads/main", cancellationToken);
        var file = await runtime.Reader.ReadFileAsync(context, "subject.txt", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var request = new PlanningRequest("fixture/delivery-forge", "#11", "implement", "Guard cold public-owner reconstruction",
            ["subject.txt"], ["excluded"], ["Original finite lifetime and fresh observations"], "implement");
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, now, [])];
        return PlanFreezer.Freeze(new PlanDraft(request, context, evidence, [new("subject.txt", "fixture", "cold contract")], [new("#11", [], "root")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], IntakePlanner.Assess(request, evidence)), "cold-owned-fixture", now);
    }
    private static AgentAcceptedReceipt Accepted(AgentRequest request) => new(WorkerEnvelope.SchemaVersion, request.RunId, request.RequestIdentity,
        request.PlanIdentity, request.BaseCommit, request.Worktree, request.AdapterId, request.AdapterVersion,
        "fixture-cold-runtime:" + request.RunId, "fixture-cold-task:" + request.RunId, DateTimeOffset.UtcNow,
        new[] { AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Pause, AgentCapability.Stop, AgentCapability.Resume }
            .Select(capability => new AgentCapabilityEvidence(capability, AgentCapabilityStatus.Supported,
                "FIXTURE external host-control contract only", "fixture-cold-session:" + request.RunId)).ToArray());

    private static ProcessLaunch ChildLaunch(string barrier, string artifact)
    {
        if (OperatingSystem.IsWindows())
        {
            var executable = FindExecutable("pwsh.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
            var release = barrier.Replace("'", "''", StringComparison.Ordinal); var output = artifact.Replace("'", "''", StringComparison.Ordinal);
            var script = "$clock=[Diagnostics.Stopwatch]::StartNew();while(!(Test-Path -LiteralPath '" + release + "')){if($clock.Elapsed.TotalSeconds -ge 15){exit 23};Start-Sleep -Milliseconds 20};" +
                "$stream=[IO.File]::Open('" + output + "',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$bytes=[Text.Encoding]::UTF8.GetBytes('owned-child-completed');$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()};exit 0";
            return new(executable, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
        }
        var python = FindExecutable("python3", "/usr/bin/python3");
        var code = "import os,sys,time\nend=time.monotonic()+15\nwhile not os.path.isfile(sys.argv[1]):\n if time.monotonic()>=end: sys.exit(23)\n time.sleep(.02)\nwith open(sys.argv[2],'xb') as f:\n f.write(b'owned-child-completed');f.flush();os.fsync(f.fileno())\n";
        return new(python, ["-c", code, barrier, artifact]);
    }
    private static string FindExecutable(string name, string fallback) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists)
        ?? (File.Exists(fallback) ? fallback : throw new PlatformNotSupportedException("INCOMPLETE actual fixture child capability missing: " + name));
    private static async Task WriteCreateNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken); await stream.FlushAsync(cancellationToken); stream.Flush(true);
    }
    private static string? Bound(string? value) => value is { Length: > 8192 } ? value[..8192] : value;
    private static async Task WriteDiagnosticAsync(OwnedScenarioRun artifacts, string name, object payload)
    {
        using var diagnosticDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await artifacts.WriteNewJsonAsync(name, payload, diagnosticDeadline.Token);
    }
    internal static async Task WaitForActualExitAsync(OwnedScenarioRun artifacts, ProcessControl processes, ProcessIdentity identity, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var started = Stopwatch.GetTimestamp();
        var observations = new List<ProcessControlResult>();
        Exception? pollFailure = null;
        try
        {
            while (true)
            {
                CheckOriginalBudget();
                if (observations.Count >= 256) throw new InvalidOperationException("Actual child exit sampler exceeded its finite observation bound.");
                var observed = processes.Observe(identity);
                observations.Add(observed);
                CheckOriginalBudget(); // A blocking observation returning late cannot become terminal success.
                if (observed.Outcome == ProcessControlOutcome.AlreadyExited) return;
                if (observed.Outcome is not (ProcessControlOutcome.LiveOwned or ProcessControlOutcome.IdentityUnknown))
                    throw new InvalidOperationException("Actual child exit remains unsupported: " + observed.Reason);
                // Unknown stays non-authorizing. No control/completion/cleanup/relaunch
                // occurs while this fixed original controller is sampled for fresh exit.
                await Task.Delay(20, deadline.Token);
            }
        }
        catch (Exception exception) { pollFailure = exception; throw; }
        finally
        {
            try
            {
                await WriteDiagnosticAsync(artifacts, "cold-child-exit-poll.json", new { fixture = true, identity, observations,
                    limitation = "Actual public original-controller observations; no native retry, ignored mismatch, PID adoption or cached-exit success." });
            }
            catch (Exception diagnosticFailure)
            {
                artifacts.RetainArtifacts("Actual exit poll diagnostic write failed: " + diagnosticFailure.GetType().Name);
                if (pollFailure is null) throw;
            }
        }
        void CheckOriginalBudget()
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                throw new OperationCanceledException("Original finite child-exit observation budget expired.", deadline.Token);
        }
    }
    private static void RequirePlainOwnedResult(OwnedEphemeralRuntime runtime, string path, bool requireAbsent = true)
    {
        if (Path.GetFullPath(path) != Path.Combine(runtime.Results, "required-fixture.txt")) throw new IOException("Cleanup artifact is not its exact live-owned path.");
        if (Path.GetRelativePath(runtime.Prefix, runtime.Root) != runtime.RuntimeId) throw new IOException("Cleanup artifact runtime scope changed.");
        for (var current = new DirectoryInfo(runtime.Results); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Cleanup artifact has a redirected ancestor.");
        if ((File.GetAttributes(runtime.MarkerPath) & FileAttributes.ReparsePoint) != 0 ||
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(runtime.MarkerPath))) != runtime.ExpectedMarkerSha256)
            throw new IOException("Cleanup artifact runtime owner marker differs.");
        if (!requireAbsent)
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Original required artifact is not the plain fixture-owned file.");
        }
        else if (Directory.EnumerateFileSystemEntries(runtime.Results).Any(entry => Path.GetFullPath(entry) == Path.GetFullPath(path)))
            throw new IOException("Cleanup artifact namespace is occupied, including a dangling link.");
    }

    private sealed class ColdHostFixture(AgentRunBinding binding) : IAgentControlPort
    {
        public bool Foreign { get; set; }
        public List<AgentControlResult> Ledger { get; } = [];
        public int ResumeCalls => Ledger.Count(item => item.Action == AgentControlAction.Resume);
        public Task<AgentControlResult> ControlAsync(AgentRunBinding supplied, AgentControlAction action, TimeSpan deadline, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(2)) throw new InvalidOperationException("Fixture host control deadline differs.");
            var returned = Foreign ? binding with { RuntimeTaskIdentity = "foreign-fixture-task" } : binding;
            var result = new AgentControlResult(returned, action, action == AgentControlAction.Resume ? AgentActivityState.Live : AgentActivityState.Quiescent,
                supplied == binding, "fixture-cold-host:" + Guid.NewGuid().ToString("N"),
                "FIXTURE external host only; this activity declaration never attests native child quiescence or model-worker capability.");
            Ledger.Add(result); return Task.FromResult(result);
        }
    }
    private sealed class ColdFactsFixture : IExecutionFactSource
    {
        private readonly AgentRequest _request;
        public IReadOnlyList<string> Required { get; }
        public ColdFactsFixture(AgentRequest request, string[] required) { _request = request; Required = Array.AsReadOnly(required.ToArray()); }
        public Task<ExecutionOwnedFacts> ObserveAsync(AgentRequest request, WorktreeObservation worktree, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestIdentity != _request.RequestIdentity || request.ResultDirectory != _request.ResultDirectory)
                throw new WorkerEnvelopeException("Fixture facts differ from their original live request.");
            return Task.FromResult(new ExecutionOwnedFacts(WorkerEnvelope.ComputeRequestIdentity(_request), _request.PlanIdentity,
                _request.BaseCommit, _request.AuthorizationCeiling, Required));
        }
    }
}
