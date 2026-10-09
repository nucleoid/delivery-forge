using System.Collections.Concurrent;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Execution;

public sealed record PreparedWorkerRecord(AgentRequest Request, FileSnapshot BeforeSnapshot, CommittedRevision PreparedRevision);
public sealed record ProcessRegistration(ProcessIdentity Identity, DateTimeOffset RegisteredAt);
public sealed record AgentCheckpoint(
    string RequestIdentity, string HeadCommit, string TreeId, IReadOnlyList<string> StatusEntries,
    FileSnapshot Snapshot, ObservedProcessState ProcessState, IReadOnlyList<string> ArtifactPaths,
    ExecutionOwnedFacts OwnedFacts, IReadOnlyList<string> Limitations, string NextReconciliation);
public sealed record QuiescenceRecord(
    string RequestedState, bool Quiescent, IReadOnlyList<ProcessControlResult> Processes,
    AgentControlResult? AdapterControl, AgentCheckpoint? Checkpoint, IReadOnlyList<string> Limitations);
public sealed record QuiescenceResult(bool Quiescent, IReadOnlyList<ProcessControlResult> Processes, StoredRunRecord Record);
public sealed record ResumeResult(ReconciliationResult Reconciliation, StoredRunRecord? Record);
public sealed record AgentResumeRecord(string RequestIdentity, AgentControlResult AdapterObservation);

public sealed class ExecutionCoordinator : IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FlowLocks = new(StringComparer.Ordinal);
    private readonly RunStore _store;
    private readonly WorktreeManager _worktrees;
    private readonly ProcessControl _processes;
    private readonly IAgentControlPort _agentControl;
    private readonly IExecutionFactSource _factSource;
    private readonly ConcurrentDictionary<string, WriterLease> _writers = new(StringComparer.Ordinal);
    private bool _disposed;

    public ExecutionCoordinator(
        RunStore store,
        WorktreeManager worktrees,
        ProcessControl processes,
        IAgentControlPort? agentControl = null,
        IExecutionFactSource? factSource = null)
    {
        _store = store;
        _worktrees = worktrees;
        _processes = processes;
        _agentControl = agentControl ?? new UnsupportedAgentControlPort();
        _factSource = factSource ?? new RequestExecutionFactSource();
    }

    public Task<(AgentRequest Request, StoredRunRecord Record)> PrepareWorkerAsync(
        FrozenWorkerPlan plan, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(plan.RunId, () => PrepareWorkerCoreAsync(plan, cancellationToken), cancellationToken);

    private async Task<(AgentRequest Request, StoredRunRecord Record)> PrepareWorkerCoreAsync(
        FrozenWorkerPlan plan, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var existing = await _store.RecoverAsync(plan.RunId, cancellationToken).ConfigureAwait(false);
        if (existing.Records.Count != 0) throw new WorkerEnvelopeException("A run may be prepared exactly once.");
        var request = WorkerEnvelope.PrepareWorker(plan);
        await _worktrees.ValidateAssignedWorktreeAsync(plan.ParentCheckout, request.Worktree, cancellationToken).ConfigureAwait(false);
        var observation = await _worktrees.ObserveAsync(request.Worktree, cancellationToken).ConfigureAwait(false);
        if (!observation.IsClean || observation.HeadCommit != request.BaseCommit)
            throw new WorkerEnvelopeException("Preparation requires the exact clean frozen base head and tree.");

        var writer = _worktrees.AcquireWriter(request.Worktree, request.RunId, request.WriterToken);
        try
        {
            var prepared = new PreparedWorkerRecord(request, observation.Snapshot,
                new CommittedRevision(observation.HeadCommit, observation.TreeId));
            var record = await _store.AppendAsync(plan.RunId, "worker-prepared", prepared, cancellationToken).ConfigureAwait(false);
            if (!_writers.TryAdd(plan.RunId, writer))
                throw new WorktreeBoundaryException("A writer is already registered for this run in this coordinator.");
            return (request, record);
        }
        catch
        {
            writer.Abandon();
            throw;
        }
    }

    public Task<StoredRunRecord> AcceptAsync(
        AgentAcceptedReceipt receipt, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(receipt.RunId, () => AcceptCoreAsync(receipt, cancellationToken), cancellationToken);

    private async Task<StoredRunRecord> AcceptCoreAsync(
        AgentAcceptedReceipt receipt, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var recovery = await _store.RecoverAsync(receipt.RunId, cancellationToken).ConfigureAwait(false);
        EnsureState(recovery, WorkflowState.Ready, "accept");
        var prepared = GetPrepared(recovery);
        EnsureWriterOwnership(prepared.Request);
        WorkerEnvelope.ValidateAccepted(prepared.Request, receipt);
        WorkflowTransition.EnsureAllowed(WorkflowState.Ready, WorkflowState.Executing,
            new TransitionEvidence(GetAuthorization(prepared.Request)));
        return await _store.AppendAsync(receipt.RunId, "worker-accepted", receipt, cancellationToken).ConfigureAwait(false);
    }

    public Task<StoredRunRecord> CompleteAsync(
        AgentCompletionReceipt completion, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(completion.RunId, () => CompleteCoreAsync(completion, cancellationToken), cancellationToken);

    private async Task<StoredRunRecord> CompleteCoreAsync(
        AgentCompletionReceipt completion, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var recovery = await _store.RecoverAsync(completion.RunId, cancellationToken).ConfigureAwait(false);
        EnsureState(recovery, WorkflowState.Executing, "complete");
        var prepared = GetPrepared(recovery);
        EnsureWriterOwnership(prepared.Request);
        var accepted = GetAccepted(recovery);
        WorkerEnvelope.ValidateCompletion(prepared.Request, accepted, completion);
        var processState = ObserveRegisteredProcesses(completion.RunId, recovery);
        if (processState is ObservedProcessState.LiveOwned or ObservedProcessState.Unknown)
            throw new WorkerEnvelopeException($"Completion is forbidden while registered process activity is {processState}.");
        var observation = await _worktrees.ObserveAsync(prepared.Request.Worktree, cancellationToken).ConfigureAwait(false);
        if (!observation.IsClean)
            throw new WorkerEnvelopeException("Completion requires a clean worktree with no untracked or ignored files.");
        if (observation.HeadCommit != completion.HeadCommit || observation.TreeId != completion.TreeId)
            throw new WorkerEnvelopeException("Completion receipt does not match the freshly observed committed head/tree.");
        await _worktrees.EnsureDescendsFromAsync(prepared.Request.Worktree, prepared.Request.BaseCommit,
            observation.HeadCommit, cancellationToken).ConfigureAwait(false);
        _worktrees.EnsureAllowedChanges(prepared.BeforeSnapshot, observation.Snapshot,
            prepared.Request.AllowedPaths, prepared.Request.Exclusions);
        WorkflowTransition.EnsureAllowed(WorkflowState.Executing, WorkflowState.LocalComplete,
            new TransitionEvidence(GetAuthorization(prepared.Request), prepared.Request.BaseCommit,
                observation.HeadCommit, observation.TreeId));
        return await _store.AppendAsync(completion.RunId, "worker-completed", completion, cancellationToken).ConfigureAwait(false);
    }

    public Task<OwnedProcess> StartTaskProcessAsync(
        string runId, ProcessLaunch launch, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(runId, () => StartTaskProcessCoreAsync(runId, launch, cancellationToken), cancellationToken);

    private async Task<OwnedProcess> StartTaskProcessCoreAsync(
        string runId, ProcessLaunch launch, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var recovery = await _store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
        EnsureState(recovery, WorkflowState.Executing, "start a task process");
        EnsureWriterOwnership(GetPrepared(recovery).Request);
        var owned = _processes.StartOwned(launch, runId);
        try
        {
            await _store.AppendAsync(runId, "process-registered",
                new ProcessRegistration(owned.Identity, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            return owned;
        }
        catch
        {
            var stopped = await _processes.QuiesceAsync(owned.Identity, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
            if (stopped.Outcome is not (ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited))
            {
                try
                {
                    await _store.AppendAsync(runId, "process-registered",
                        new ProcessRegistration(owned.Identity, DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or RunStoreCorruptionException or RunStoreWriterConflictException)
                {
                    // The live identity remains in the in-memory registry; callers must treat the failed start as blocked.
                }
            }
            throw;
        }
    }

    public Task<QuiescenceResult> PauseAsync(
        string runId, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(runId,
            () => QuiesceAndRecordAsync(runId, WorkflowState.Paused, "paused", deadline, cancellationToken), cancellationToken);

    public Task<QuiescenceResult> StopAsync(
        string runId, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(runId,
            () => QuiesceAndRecordAsync(runId, WorkflowState.Stopped, "stopped", deadline, cancellationToken), cancellationToken);

    public Task<ResumeResult> ResumeAsync(string runId, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(runId, () => ResumeCoreAsync(runId, cancellationToken), cancellationToken);

    private async Task<ResumeResult> ResumeCoreAsync(string runId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var recovery = await _store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
        EnsureState(recovery, WorkflowState.Paused, "resume");
        var prepared = GetPrepared(recovery);
        var pause = recovery.Records.Last(record => record.Kind == "paused");
        var durable = _store.ReadPayload<QuiescenceRecord>(pause).Checkpoint
            ?? throw new RunStoreCorruptionException("Paused record has no durable checkpoint.");
        var actual = await _worktrees.ObserveAsync(prepared.Request.Worktree, cancellationToken).ConfigureAwait(false);
        await _worktrees.EnsureDescendsFromAsync(prepared.Request.Worktree, prepared.Request.BaseCommit,
            actual.HeadCommit, cancellationToken).ConfigureAwait(false);
        var actualFacts = await ObserveOwnedFactsAsync(prepared.Request, actual, false, cancellationToken).ConfigureAwait(false);
        var processState = ObserveRegisteredProcesses(runId, recovery);
        var writerOwnershipConfirmed = TryEnsureWriterOwnership(prepared.Request);
        var observation = new ResumeObservation(
            durable,
            actual,
            _worktrees.IsSameDirectory(actual.RootPath, prepared.Request.Worktree),
            writerOwnershipConfirmed,
            actualFacts.RequiredArtifactPaths.All(File.Exists),
            OwnedFactsEqual(actualFacts, durable.OwnedFacts),
            processState);
        var reconciliation = Reconciler.ReconcileResume(observation);
        if (reconciliation.Action != ReconciliationAction.ResumeDispatch)
            return new ResumeResult(reconciliation, null);
        var accepted = GetAccepted(recovery);
        if (!TryGetSupportedCapability(accepted, AgentCapability.Resume, out var capabilityLimitation))
            return BlockedResume(capabilityLimitation);
        var transitionEvidence = new TransitionEvidence(GetAuthorization(prepared.Request));
        WorkflowTransition.EnsureAllowed(WorkflowState.Paused, WorkflowState.Executing, transitionEvidence);
        AgentControlResult adapterObservation;
        try
        {
            adapterObservation = await _agentControl.ControlAsync(
                ToBinding(accepted), AgentControlAction.Resume, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await RecordBlockedResumeAsync(
                $"Fresh Resume adapter observation failed after control may have produced side effects: {exception.GetType().FullName}: {exception.Message}",
                null).ConfigureAwait(false);
        }
        if (adapterObservation is null)
            return await RecordBlockedResumeAsync(
                "Adapter Resume result is null and cannot prove bound live activity.", null).ConfigureAwait(false);
        if (!TryValidateControlResult(accepted, AgentControlAction.Resume, AgentActivityState.Live,
                adapterObservation, out var controlLimitation))
            return await RecordBlockedResumeAsync(controlLimitation, adapterObservation).ConfigureAwait(false);
        var record = await _store.AppendAsync(runId, "resumed",
            new AgentResumeRecord(prepared.Request.RequestIdentity, adapterObservation), CancellationToken.None).ConfigureAwait(false);
        return new ResumeResult(reconciliation, record);

        static ResumeResult BlockedResume(string reason) =>
            new(new ReconciliationResult(ReconciliationAction.Blocked, [reason]), null);

        async Task<ResumeResult> RecordBlockedResumeAsync(string reason, AgentControlResult? adapterControl)
        {
            WorkflowTransition.EnsureAllowed(WorkflowState.Paused, WorkflowState.Blocked,
                new TransitionEvidence(GetAuthorization(prepared.Request)));
            var payload = new QuiescenceRecord(
                "resumed", false, [], adapterControl, null, [reason]);
            var blockedRecord = await _store.AppendAsync(
                runId, "blocked-quiescence", payload, CancellationToken.None).ConfigureAwait(false);
            return new ResumeResult(
                new ReconciliationResult(ReconciliationAction.Blocked, [reason]), blockedRecord);
        }
    }

    private async Task<QuiescenceResult> QuiesceAndRecordAsync(
        string runId, WorkflowState requestedState, string requestedKind,
        TimeSpan deadline, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var recovery = await _store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
        if (recovery.Records.Count == 0) throw new WorkerEnvelopeException("No durable prepared request exists for this run.");
        var current = GetState(recovery);
        if (requestedState == WorkflowState.Paused)
            EnsureState(recovery, WorkflowState.Executing, "pause");
        else if (current is WorkflowState.Stopped or WorkflowState.Blocked or WorkflowState.Failed or WorkflowState.LocalComplete)
            throw new WorkerEnvelopeException($"Cannot stop a run in terminal state {current}.");

        var prepared = GetPrepared(recovery);
        EnsureWriterOwnership(prepared.Request);

        var registrations = recovery.Records.Where(record => record.Kind == "process-registered")
            .Select(record => _store.ReadPayload<ProcessRegistration>(record)).ToArray();
        var acceptedRecord = recovery.Records.SingleOrDefault(record => record.Kind == "worker-accepted");
        var accepted = acceptedRecord is null ? null : _store.ReadPayload<AgentAcceptedReceipt>(acceptedRecord);
        var identities = registrations.Select(registration => registration.Identity)
            .Concat(_processes.GetOwnedIdentities(runId)).Distinct().ToArray();
        var results = new List<ProcessControlResult>();
        foreach (var identity in identities)
        {
            try
            {
                results.Add(await _processes.QuiesceAsync(identity, deadline, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                results.Add(new ProcessControlResult(ProcessControlOutcome.ControlFailed,
                    $"Process control failed after it may have produced side effects: {exception.Message}"));
            }
        }

        var limitations = new List<string>();
        AgentControlResult? adapterControl = null;
        var action = requestedState == WorkflowState.Paused ? AgentControlAction.Pause : AgentControlAction.Stop;
        var adapterQuiescent = false;
        if (accepted is null)
        {
            limitations.Add("No accepted receipt exists; an in-flight unaccepted dispatch may exist and remote quiescence is unknown.");
        }
        else
        {
            var capability = action == AgentControlAction.Pause ? AgentCapability.Pause : AgentCapability.Stop;
            if (!TryGetSupportedCapability(accepted, capability, out var capabilityLimitation))
            {
                limitations.Add(capabilityLimitation);
            }
            else
            {
                try
                {
                    adapterControl = await _agentControl.ControlAsync(
                        ToBinding(accepted), action, deadline, cancellationToken).ConfigureAwait(false);
                    adapterQuiescent = TryValidateControlResult(
                        accepted, action, AgentActivityState.Quiescent, adapterControl, out var controlLimitation);
                    if (!adapterQuiescent) limitations.Add(controlLimitation);
                }
                catch (Exception exception)
                {
                    limitations.Add($"Adapter {action} control failed after it may have produced side effects: {exception.Message}");
                }
            }
        }
        var localQuiescent = results.All(result => result.Outcome is ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited);
        var quiescent = localQuiescent && adapterQuiescent;
        var observedProcessState = adapterControl?.Activity == AgentActivityState.Live ||
                                   results.Any(result => result.Outcome == ProcessControlOutcome.TimedOut)
            ? ObservedProcessState.LiveOwned
            : !adapterQuiescent || adapterControl?.Activity == AgentActivityState.Unknown ||
              results.Any(result => result.Outcome is ProcessControlOutcome.IdentityUnknown or
                  ProcessControlOutcome.UnsupportedPlatform or ProcessControlOutcome.ControlFailed)
                ? ObservedProcessState.Unknown
                : ObservedProcessState.Quiesced;
        AgentCheckpoint? checkpoint = null;
        try
        {
            var worktree = await _worktrees.ObserveAsync(prepared.Request.Worktree, cancellationToken).ConfigureAwait(false);
            await _worktrees.EnsureDescendsFromAsync(prepared.Request.Worktree, prepared.Request.BaseCommit,
                worktree.HeadCommit, cancellationToken).ConfigureAwait(false);
            var facts = await ObserveOwnedFactsAsync(prepared.Request, worktree, true, cancellationToken).ConfigureAwait(false);
            limitations.Add("External dependency, lease, and authorization-revocation facts belong to later adapters and are not claimed by the issue #5 core.");
            checkpoint = new AgentCheckpoint(prepared.Request.RequestIdentity, worktree.HeadCommit, worktree.TreeId,
                worktree.StatusEntries, worktree.Snapshot, observedProcessState, facts.RequiredArtifactPaths, facts, limitations.ToArray(),
                requestedState == WorkflowState.Paused
                    ? "Re-observe request/plan/base/authorization facts owned by this core, Git, declared artifacts, writer ownership, adapter activity, and process identity before resume dispatch."
                    : "Stopped terminally; preserve the assigned worktree and evidence until separately authorized cleanup.");
        }
        catch (Exception exception)
        {
            quiescent = false;
            limitations.Add($"Post-control checkpoint observation failed: {exception.Message}");
        }

        var target = quiescent ? requestedState : WorkflowState.Blocked;
        WorkflowTransition.EnsureAllowed(current, target, new TransitionEvidence(GetAuthorization(prepared.Request)));

        var kind = quiescent ? requestedKind : "blocked-quiescence";
        var payload = new QuiescenceRecord(requestedKind, quiescent, results, adapterControl, checkpoint, limitations);
        var record = await _store.AppendAsync(runId, kind, payload, CancellationToken.None).ConfigureAwait(false);
        return new QuiescenceResult(quiescent, results, record);
    }

    private ObservedProcessState ObserveRegisteredProcesses(string runId, RunRecovery recovery)
    {
        var registrations = recovery.Records.Where(record => record.Kind == "process-registered")
            .Select(record => _store.ReadPayload<ProcessRegistration>(record)).ToArray();
        var outcomes = registrations.Select(registration => registration.Identity)
            .Concat(_processes.GetOwnedIdentities(runId)).Distinct()
            .Select(identity => _processes.Observe(identity).Outcome).ToArray();
        if (outcomes.Length == 0) return ObservedProcessState.None;
        if (outcomes.Any(outcome => outcome == ProcessControlOutcome.IdentityUnknown)) return ObservedProcessState.Unknown;
        if (outcomes.Any(outcome => outcome == ProcessControlOutcome.LiveOwned)) return ObservedProcessState.LiveOwned;
        return ObservedProcessState.Quiesced;
    }

    private PreparedWorkerRecord GetPrepared(RunRecovery recovery)
    {
        var record = recovery.Records.SingleOrDefault(item => item.Kind == "worker-prepared")
            ?? throw new WorkerEnvelopeException("No durable prepared request exists for this run.");
        var prepared = _store.ReadPayload<PreparedWorkerRecord>(record);
        if (prepared.Request.RequestIdentity != WorkerEnvelope.ComputeRequestIdentity(prepared.Request))
            throw new RunStoreCorruptionException("Durable prepared request identity is invalid.");
        return prepared;
    }

    private AgentAcceptedReceipt GetAccepted(RunRecovery recovery)
    {
        var record = recovery.Records.SingleOrDefault(item => item.Kind == "worker-accepted")
            ?? throw new WorkerEnvelopeException("No durable accepted receipt exists for this run.");
        return _store.ReadPayload<AgentAcceptedReceipt>(record);
    }

    private void EnsureWriterOwnership(AgentRequest request)
    {
        if (!TryEnsureWriterOwnership(request))
            throw new WorktreeBoundaryException("One-writer ownership is not confirmed for this run.");
    }

    private bool TryEnsureWriterOwnership(AgentRequest request)
    {
        if (_writers.TryGetValue(request.RunId, out var current))
            return current.WriterToken == request.WriterToken;
        try
        {
            var recovered = _worktrees.RecoverWriter(request.Worktree, request.RunId, request.WriterToken);
            if (!_writers.TryAdd(request.RunId, recovered)) recovered.Dispose();
            return true;
        }
        catch (WorktreeBoundaryException)
        {
            return false;
        }
    }

    private static void EnsureState(RunRecovery recovery, WorkflowState expected, string operation)
    {
        var actual = GetState(recovery);
        if (actual != expected)
            throw new WorkerEnvelopeException($"Cannot {operation}: durable state is {actual}, expected {expected}.");
    }

    private static WorkflowState GetState(RunRecovery recovery)
    {
        var state = WorkflowState.Understanding;
        foreach (var record in recovery.Records)
        {
            state = record.Kind switch
            {
                "worker-prepared" when state == WorkflowState.Understanding => WorkflowState.Ready,
                "worker-accepted" when state == WorkflowState.Ready => WorkflowState.Executing,
                "paused" when state == WorkflowState.Executing => WorkflowState.Paused,
                "resumed" when state == WorkflowState.Paused => WorkflowState.Executing,
                "worker-completed" when state == WorkflowState.Executing => WorkflowState.LocalComplete,
                "stopped" => WorkflowState.Stopped,
                "blocked-quiescence" => WorkflowState.Blocked,
                "process-registered" => state,
                _ => throw new RunStoreCorruptionException($"Record {record.Sequence} kind '{record.Kind}' is out of order or unknown.")
            };
        }
        return state;
    }

    private async Task<ExecutionOwnedFacts> ObserveOwnedFactsAsync(
        AgentRequest request, WorktreeObservation worktree, bool requireFrozenMatch, CancellationToken cancellationToken)
    {
        var facts = await _factSource.ObserveAsync(request, worktree, cancellationToken).ConfigureAwait(false);
        if (requireFrozenMatch && (facts.RequestIdentity != WorkerEnvelope.ComputeRequestIdentity(request) ||
            facts.PlanIdentity != request.PlanIdentity || facts.BaseCommit != request.BaseCommit ||
            facts.AuthorizationCeiling != request.AuthorizationCeiling))
            throw new WorkerEnvelopeException("Fresh execution facts do not match the frozen issue-owned request/plan/base/authorization facts.");
        if (facts.RequiredArtifactPaths.Any(path => !Path.IsPathFullyQualified(path) || !File.Exists(path)))
            throw new WorkerEnvelopeException("A freshly declared required checkpoint artifact is missing.");
        return facts;
    }

    private static AgentRunBinding ToBinding(AgentAcceptedReceipt accepted) => new(
        accepted.AdapterId, accepted.AdapterVersion, accepted.RunId, accepted.RequestIdentity,
        accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity);

    private static bool TryGetSupportedCapability(
        AgentAcceptedReceipt accepted, AgentCapability capability, out string limitation)
    {
        var matches = accepted.Capabilities.Where(item => item.Capability == capability).ToArray();
        if (matches.Length == 1 && matches[0].Status == AgentCapabilityStatus.Supported &&
            !string.IsNullOrWhiteSpace(matches[0].Interface) &&
            !string.IsNullOrWhiteSpace(matches[0].EvidenceReference))
        {
            limitation = "";
            return true;
        }

        limitation = $"INCOMPLETE: accepted receipt does not contain exactly one evidenced Supported {capability} capability.";
        return false;
    }

    private static bool TryValidateControlResult(
        AgentAcceptedReceipt accepted,
        AgentControlAction action,
        AgentActivityState expectedActivity,
        AgentControlResult result,
        out string limitation)
    {
        if (result.Binding == ToBinding(accepted) && result.Action == action && result.CapabilitySupported &&
            result.Activity == expectedActivity && !string.IsNullOrWhiteSpace(result.EvidenceReference))
        {
            limitation = "";
            return true;
        }

        limitation = $"Adapter {action} result is unbound, mismatched, unsupported, unevidenced, or does not report {expectedActivity}.";
        return false;
    }

    private static bool OwnedFactsEqual(ExecutionOwnedFacts left, ExecutionOwnedFacts right) =>
        left.RequestIdentity == right.RequestIdentity && left.PlanIdentity == right.PlanIdentity &&
        left.BaseCommit == right.BaseCommit && left.AuthorizationCeiling == right.AuthorizationCeiling &&
        left.RequiredArtifactPaths.SequenceEqual(right.RequiredArtifactPaths, StringComparer.Ordinal);

    private static AuthorizationCeiling GetAuthorization(AgentRequest request)
    {
        if (!Enum.TryParse<AuthorizationCeiling>(request.AuthorizationCeiling, true, out var authorization))
            throw new WorkerEnvelopeException($"Unsupported authorization ceiling '{request.AuthorizationCeiling}'.");
        return authorization;
    }

    private static async Task<T> RunExclusiveAsync<T>(
        string runId, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var flowLock = FlowLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await flowLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { flowLock.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var writer in _writers.Values) writer.Dispose();
        _writers.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
