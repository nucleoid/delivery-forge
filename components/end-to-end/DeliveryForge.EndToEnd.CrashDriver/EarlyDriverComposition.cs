using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Scheduling;

namespace DeliveryForge.EndToEnd.CrashDriver;

internal static class EarlyDriverComposition
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static async Task RunAsync(DriverInput input, RepositoryContext repository, CancellationToken token)
    {
        if (input.Stage is not ("plan-frozen" or "worker-prepared" or "host-accepted" or "owned-command-started"))
            throw new NotImplementedException("Later crash boundary has its own pending composition: " + input.Stage);
        if (input.Mode == "recover") { await RecoverAsync(input, token); return; }
        var reader = new GitRepositoryContextReader();
        var exact = await reader.ReadFileAsync(repository, "subject.txt", token);
        var now = DateTimeOffset.UtcNow;
        var request = new PlanningRequest("fixture/delivery-forge", "#11", "implement", "Actual early parent force-termination fixture",
            ["subject.txt"], ["excluded"], ["Native fixture only; no model-worker or production certification"], "implement");
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(exact, now, [])];
        var draft = new PlanDraft(request, repository, evidence, [new("subject.txt", "fixture", "actual crash boundary")], [new("#11", [], "root")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], IntakePlanner.Assess(request, evidence));
        var plan = PlanFreezer.Freeze(draft, "actual-crash-" + input.Nonce, now);
        var contract = await new AppendOnlyContractStore(Path.Combine(input.ArtifactRoot, "plan-contracts")).WriteImmutableAsync(plan.PlanContractBytes, token);
        var bundle = Path.Combine(input.ArtifactRoot, "plan-bundle.json"); await WriteNew(bundle, plan.CanonicalBytes, token);
        var runId = "crash-" + input.Nonce;
        var store = new RunStore(Path.Combine(input.RuntimeRoot, "crash-receipts-" + input.Nonce));
        var worktrees = new WorktreeManager();
        await using var processes = new ProcessControl();
        var fixtureHost = new FixtureControl(processes, freshUnknown: false);
        var execution = new ExecutionCoordinator(store, worktrees, processes, fixtureHost);
        LocalSingleScheduler? scheduler = null;
        AgentRequest? prepared = null;
        if (input.Stage != "plan-frozen")
        {
            var worker = new FrozenWorkerPlan(runId, plan.ContractIdentity, plan.BaseCommit, plan.Repository,
                Path.Combine(input.RuntimeRoot, "repository"), Path.Combine(input.RuntimeRoot, "crash-worktree-" + input.Nonce),
                Path.Combine(input.RuntimeRoot, "crash-results-" + input.Nonce), Guid.NewGuid().ToString("N"),
                ["subject.txt"], ["excluded"], "fixture-actual-crash-native-host", "1", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
            var slots = new ResourceSlots(2, 2, 4096, 1, 1, 0);
            var budget = ResourceBudget.Measure(slots, new("parent-approved-crash-fixture", true, slots, TimeSpan.FromSeconds(90), 1000, 0m));
            scheduler = LocalSingleScheduler.Create(plan, worker, new(new(1, 1, 512, 0, 0, 0), [], TimeSpan.FromSeconds(90), 100, 0m),
                budget, store, execution, worktrees, reader, TimeSpan.FromSeconds(2));
            prepared = await scheduler.ProvisionAndPrepareAsync(await scheduler.ReserveAsync(token), token);
            if (input.Stage is "host-accepted" or "owned-command-started")
            {
                var accepted = Accepted(prepared, input); fixtureHost.BindAccepted(accepted);
                await execution.AcceptAsync(accepted, token);
            }
            if (input.Stage == "owned-command-started")
            {
                var child = await execution.StartTaskProcessAsync(runId, new(Environment.ProcessPath!,
                    [Path.GetFullPath(Environment.GetCommandLineArgs()[0]), "child", input.Stage, input.RuntimeRoot, input.ArtifactRoot,
                        input.Nonce, input.RuntimeBaseCommit, input.IntegratedBaseCommit, "11019"]), token);
                await WaitFile(Path.Combine(input.ArtifactRoot, "child-entered-" + input.Nonce), TimeSpan.FromSeconds(5), token);
                var live = processes.Observe(child.Identity);
                if (live.Outcome != ProcessControlOutcome.LiveOwned) throw new InvalidOperationException("Actual driver child is not LiveOwned: " + live.Reason);
            }
        }
        var history = await store.RecoverAsync(runId, token);
        var schedulerHistory = await store.RecoverAsync(runId + "-scheduler", token);
        var files = new[] { contract, bundle }.Concat(history.Records.Select(item => item.Path)).Concat(schedulerHistory.Records.Select(item => item.Path))
            .Select(path => new DurableFile(path, HashFile(path))).ToArray();
        foreach (var file in files) if (HashFile(file.Path) != file.Sha256) throw new IOException("Durable stage changed before ready.");
        await WriteJson(Path.Combine(input.ArtifactRoot, "setup-complete-" + input.Nonce + ".json"), new { fixture = true, input.Stage, input.Nonce }, token);
        await WriteJson(Path.Combine(input.ArtifactRoot, "ready-" + input.Stage + "-" + input.Nonce + ".json"), new
        {
            fixture = true,
            productionCertified = false,
            input.Stage,
            input.Nonce,
            frozenBaseCommit = plan.BaseCommit,
            input.IntegratedBaseCommit,
            planContractPath = contract,
            planBundlePath = bundle,
            storeRoot = store.RootPath,
            runId,
            durableFiles = files,
            observedKinds = history.Records.Select(record => record.Kind).ToArray(),
            childNaturalSeconds = 15,
            plan.ContractIdentity,
            limitation = "Diagnostic bytes do not import live FrozenPlan/ticket/process authority."
        }, token);
        await WaitFile(Path.Combine(input.ArtifactRoot, "release-" + input.Nonce), TimeSpan.FromSeconds(15), token);
        if (await File.ReadAllTextAsync(Path.Combine(input.ArtifactRoot, "release-" + input.Nonce), token) != input.Nonce)
            throw new IOException("Normal release is not this exact driver nonce.");
        if (scheduler is not null)
        {
            if (input.Stage == "worker-prepared")
            {
                var accepted = Accepted(prepared!, input); fixtureHost.BindAccepted(accepted);
                await execution.AcceptAsync(accepted, token);
            }
            if (!await scheduler.StopAsync(token)) throw new InvalidOperationException("Normal-release actual owner closure failed: " + scheduler.CleanupLimitation);
        }
        execution.Dispose();
        await WriteJson(Path.Combine(input.ArtifactRoot, "normal-release-result.json"), new
        {
            fixture = true,
            input.Stage,
            actualReadyReached = true,
            normalReleaseInsteadOfKill = true,
            limitation = "Normal release calibration only; no model-worker certification."
        }, token);
    }

    private static async Task RecoverAsync(DriverInput input, CancellationToken token)
    {
        var readyPath = Path.Combine(input.ArtifactRoot, "ready-" + input.Stage + "-" + input.Nonce + ".json");
        using var ready = JsonDocument.Parse(await File.ReadAllBytesAsync(readyPath, token));
        var root = ready.RootElement;
        if (root.GetProperty("nonce").GetString() != input.Nonce || root.GetProperty("stage").GetString() != input.Stage)
            throw new IOException("Recovery diagnostic binding differs from this exact experiment.");
        var reopened = new List<object>();
        foreach (var file in root.GetProperty("durableFiles").EnumerateArray())
        {
            var path = file.GetProperty("path").GetString()!; var expected = file.GetProperty("sha256").GetString()!;
            RequirePlainWithin(path, input.RuntimeRoot, input.ArtifactRoot);
            var actual = HashFile(path); if (actual != expected) throw new IOException("Retained durable bytes changed before fresh recovery.");
            reopened.Add(new { path, beforeKillSha256 = expected, afterCrashSha256 = actual });
        }
        var planPath = root.GetProperty("planContractPath").GetString()!; RequirePlainWithin(planPath, input.RuntimeRoot, input.ArtifactRoot);
        var planData = ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(planPath, token));
        var storeRoot = root.GetProperty("storeRoot").GetString()!; RequirePlainWithin(storeRoot, input.RuntimeRoot, input.ArtifactRoot);
        var store = new RunStore(storeRoot); var runId = root.GetProperty("runId").GetString()!;
        if (runId != "crash-" + input.Nonce) throw new IOException("Stored run-id differs from this exact crash experiment.");
        var before = await store.RecoverAsync(runId, token);
        await using var processes = new ProcessControl();
        var host = new FixtureControl(processes, freshUnknown: true);
        var execution = new ExecutionCoordinator(store, new WorktreeManager(), processes, host);
        string refusal; string? ownerReason = null; string? childOutcome = null;
        var unknownChild = false;
        var signalsToOldNative = 0;
        if (input.Stage == "plan-frozen")
        {
            if (planData.SchemaName != "plan" || before.Records.Count != 0) throw new InvalidOperationException("Plan-only boundary has unexpected worker history.");
            // No configured public frozen-plan importer: validated Contract data is deliberately not an owner object.
            refusal = "frozen-plan-import-capability-unavailable";
            ownerReason = "This driver profile has no configured public FrozenPlan import capability; public ContractValidator produced data only. Fresh Git and empty actual worker history were observed; no new FrozenPlan/worker/ticket is issued.";
        }
        else if (input.Stage == "worker-prepared")
        {
            try { await execution.ReconcilePausedAsync(runId, TimeSpan.FromSeconds(2), token); throw new InvalidOperationException("Prepared data unexpectedly reconciled as paused authority."); }
            catch (WorkerEnvelopeException denied) { refusal = "prepared-is-not-paused-authority"; ownerReason = denied.Message; }
        }
        else
        {
            var outcome = await execution.ReconcileStopAsync(runId, TimeSpan.FromSeconds(2), token);
            signalsToOldNative = outcome.Processes.Count(item => item.Outcome != ProcessControlOutcome.IdentityUnknown);
            if (signalsToOldNative != 0) throw new InvalidOperationException("Fresh receiver returned an unexpected nonunknown process-control result.");
            if (outcome.Quiescent || host.Ledger.Count == 0 || host.Ledger.Any(item => item.Activity != AgentActivityState.Unknown))
                throw new InvalidOperationException("Fresh external fixture host unknown activity unexpectedly authorized closure.");
            ownerReason = JsonSerializer.Serialize(outcome, Json);
            if (input.Stage == "owned-command-started")
            {
                var registration = store.ReadPayload<ProcessRegistration>(before.Records.Single(item => item.Kind == "process-registered"));
                childOutcome = processes.Observe(registration.Identity).Outcome.ToString();
                unknownChild = childOutcome == nameof(ProcessControlOutcome.IdentityUnknown) && !outcome.Quiescent;
                if (!unknownChild) throw new InvalidOperationException("Fresh controller unexpectedly owned the stored child.");
                refusal = "fresh-controller-native-identity-unknown";
            }
            else refusal = "fresh-bound-host-observation-required";
        }
        var after = await store.RecoverAsync(runId, token);
        if (after.Records.Any(item => item.Kind is "worker-completed" or "resumed")) throw new InvalidOperationException("Recovery minted completion/resume from old bytes.");
        execution.Dispose();
        await WriteJson(Path.Combine(input.ArtifactRoot, "recovery-result.json"), new
        {
            fixture = true,
            productionCertified = false,
            input.Stage,
            input.Nonce,
            specificSafeRefusal = refusal,
            ownerReason,
            newWorkerLaunches = processes.GetAllOwnedIdentities().Count,
            resumeCalls = host.Ledger.Count(item => item.Action == AgentControlAction.Resume),
            signalOldNativeCalls = signalsToOldNative,
            publicationCalls = 0,
            destructiveCleanupCalls = 0,
            oldTicketOrReceiptBecameAuthority = false,
            freshControllerOwnedNativeCount = processes.GetAllOwnedIdentities().Count,
            observedWorkerPreparedCount = after.Records.Count(item => item.Kind == "worker-prepared"),
            observedWorkerAcceptedCount = after.Records.Count(item => item.Kind == "worker-accepted"),
            observedProcessRegisteredCount = after.Records.Count(item => item.Kind == "process-registered"),
            observedWorkerCompletedCount = after.Records.Count(item => item.Kind == "worker-completed"),
            independentlyReopenedDurableFiles = reopened,
            recordedChildFreshControllerOutcome = childOutcome,
            unknownChildRetainsResources = unknownChild,
            externalHostFixtureExplicitlyMarked = host.Ledger.Count != 0,
            hostCallLedger = host.Ledger,
            retained = true,
            limitation = "Unknown registry membership never adopts/signals stored PIDs; no publication/cleanup capability configured."
        }, token);
    }

    internal static async Task ChildAsync(DriverInput input, long originalStarted, CancellationToken token)
    {
        await WriteNew(Path.Combine(input.ArtifactRoot, "child-entered-" + input.Nonce), Encoding.UTF8.GetBytes(input.Nonce), token);
        if (input.Stage == "command-completed")
        {
            // Necessary actual prerequisite fixture: natural completion, not the missing crash orchestration.
            await WriteJson(Path.Combine(input.ArtifactRoot, "child-result-" + input.Nonce + ".json"),
                new { fixture = true, input.Nonce, input.Stage, actualNativeResult = "completed", productionCertified = false }, token);
            return;
        }
        while (Stopwatch.GetElapsedTime(originalStarted) < TimeSpan.FromSeconds(15)) await Task.Delay(20, token);
    }
    private static AgentAcceptedReceipt Accepted(AgentRequest request, DriverInput input) => new(WorkerEnvelope.SchemaVersion,
        request.RunId, request.RequestIdentity, request.PlanIdentity, request.BaseCommit, request.Worktree, request.AdapterId, request.AdapterVersion,
        "fixture-runtime:" + input.Nonce, "fixture-task:" + input.Nonce, DateTimeOffset.UtcNow,
        new[] { AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Stop, AgentCapability.Pause, AgentCapability.Resume }
            .Select(value => new AgentCapabilityEvidence(value, AgentCapabilityStatus.Supported, "FIXTURE external native-task contract; no model adapter", "fixture-session:" + input.Nonce)).ToArray());
    internal static async Task WriteJson(string path, object value, CancellationToken token)
    {
        var staging = path + ".publish-" + Guid.NewGuid().ToString("N");
        await WriteNew(staging, JsonSerializer.SerializeToUtf8Bytes(value, Json), token);
        RequirePlainAncestors(path);
        File.Move(staging, path, overwrite: false); // final diagnostic is visible only after its writer is closed/flushed
    }
    private static async Task WriteNew(string path, byte[] value, CancellationToken token)
    {
        RequirePlainAncestors(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RequirePlainAncestors(path);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(value, token); await stream.FlushAsync(token); stream.Flush(true);
    }
    private static async Task WaitFile(string path, TimeSpan limit, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        while (!File.Exists(path))
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= limit) throw new TimeoutException("Actual driver barrier did not arrive: " + Path.GetFileName(path));
            await Task.Delay(10, token);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Driver barrier is redirected.");
    }
    private static string HashFile(string path) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void RequirePlainWithin(string path, params string[] roots)
    {
        path = Path.GetFullPath(path);
        if (!roots.Any(root => path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new IOException("Driver diagnostic file escaped this retained experiment.");
        RequirePlainAncestors(path);
    }
    private static void RequirePlainAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Retained driver path is redirected."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private sealed record DurableFile(string Path, string Sha256);
    private sealed class FixtureControl(ProcessControl processes, bool freshUnknown) : IAgentControlPort
    {
        private AgentRunBinding? _binding;
        public List<AgentControlResult> Ledger { get; } = [];
        public void BindAccepted(AgentAcceptedReceipt accepted)
        {
            var binding = new AgentRunBinding(accepted.AdapterId, accepted.AdapterVersion, accepted.RunId,
                accepted.RequestIdentity, accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity);
            if (_binding is not null && _binding != binding) throw new InvalidOperationException("Fixture receiver is already pinned to a different accepted session.");
            if (freshUnknown) throw new InvalidOperationException("Fresh receiver cannot import old accepted-session authority.");
            _binding = binding;
        }
        public Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan deadline, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quiet = processes.ObserveOwned(binding.RunId).All(item => item.Outcome == ProcessControlOutcome.AlreadyExited);
            var matched = !freshUnknown && _binding == binding;
            var result = new AgentControlResult(binding, action, !matched ? AgentActivityState.Unknown : quiet ? AgentActivityState.Quiescent : AgentActivityState.Live,
                matched, "FIXTURE-current-control:" + Guid.NewGuid().ToString("N"), "Explicit external fixture; fresh recovery has no old host/control authority.");
            Ledger.Add(result); return Task.FromResult(result);
        }
    }
}
