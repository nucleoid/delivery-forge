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
    IReadOnlyList<string> Limitations, string NextReconciliation);
public sealed record QuiescenceRecord(
    string RequestedState, bool Quiescent, IReadOnlyList<ProcessControlResult> Processes,
    AgentCheckpoint? Checkpoint, IReadOnlyList<string> Limitations);
public sealed record QuiescenceResult(bool Quiescent, IReadOnlyList<ProcessControlResult> Processes, StoredRunRecord Record);
public sealed record ResumeResult(ReconciliationResult Reconciliation, StoredRunRecord? Record);

public sealed class ExecutionCoordinator : IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FlowLocks = new(StringComparer.Ordinal);
    private readonly RunStore _store;
    private readonly WorktreeManager _worktrees;
    private readonly ProcessControl _processes;
    private readonly Dictionary<string, WriterLease> _writers = new(StringComparer.Ordinal);
    private bool _disposed;

    public ExecutionCoordinator(RunStore store, WorktreeManager worktrees, ProcessControl processes)
    {
        _store = store;
        _worktrees = worktrees;
        _processes = processes;
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

        var writer = _worktrees.AcquireWriter(request.ResultDirectory, request.RunId, request.WriterToken);
        try
        {
            var prepared = new PreparedWorkerRecord(request, observation.Snapshot,
                new CommittedRevision(observation.HeadCommit, observation.TreeId));
            var record = await _store.AppendAsync(plan.RunId, "worker-prepared", prepared, cancellationToken).ConfigureAwait(false);
            _writers.Add(plan.RunId, writer);
            return (request, record);
        }
        catch
        {
            writer.Dispose();
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
            new TransitionEvidence(AuthorizationCeiling.Implement));
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
            new TransitionEvidence(AuthorizationCeiling.Implement, prepared.Request.BaseCommit,
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
        var owned = _processes.StartOwned(launch);
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
        var processState = ObserveRegisteredProcesses(recovery);
        var writerOwnershipConfirmed = TryEnsureWriterOwnership(prepared.Request);
        var observation = new ResumeObservation(
            durable,
            actual,
            SamePath(actual.RootPath, prepared.Request.Worktree),
            writerOwnershipConfirmed,
            durable.ArtifactPaths.All(File.Exists),
            processState);
        var reconciliation = Reconciler.ReconcileResume(observation);
        if (reconciliation.Action != ReconciliationAction.ResumeDispatch)
            return new ResumeResult(reconciliation, null);
        WorkflowTransition.EnsureAllowed(WorkflowState.Paused, WorkflowState.Executing,
            new TransitionEvidence(AuthorizationCeiling.Implement));
        var record = await _store.AppendAsync(runId, "resumed", new { requestIdentity = prepared.Request.RequestIdentity }, cancellationToken)
            .ConfigureAwait(false);
        return new ResumeResult(reconciliation, record);
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

        var registrations = recovery.Records.Where(record => record.Kind == "process-registered")
            .Select(record => _store.ReadPayload<ProcessRegistration>(record)).ToArray();
        var results = new List<ProcessControlResult>();
        foreach (var registration in registrations)
            results.Add(await _processes.QuiesceAsync(registration.Identity, deadline, cancellationToken).ConfigureAwait(false));
        var quiescent = results.All(result => result.Outcome is ProcessControlOutcome.Quiesced or ProcessControlOutcome.AlreadyExited);
        var prepared = GetPrepared(recovery);
        var observedProcessState = results.Any(result => result.Outcome == ProcessControlOutcome.IdentityUnknown)
            ? ObservedProcessState.Unknown
            : results.Any(result => result.Outcome == ProcessControlOutcome.TimedOut)
                ? ObservedProcessState.LiveOwned
                : registrations.Length == 0 ? ObservedProcessState.None : ObservedProcessState.Quiesced;
        AgentCheckpoint? checkpoint = null;
        var limitations = new List<string>();
        try
        {
            var worktree = await _worktrees.ObserveAsync(prepared.Request.Worktree, cancellationToken).ConfigureAwait(false);
            checkpoint = new AgentCheckpoint(prepared.Request.RequestIdentity, worktree.HeadCommit, worktree.TreeId,
                worktree.StatusEntries, worktree.Snapshot, observedProcessState, [], [],
                requestedState == WorkflowState.Paused
                    ? "Re-observe Git, artifacts, ownership, authorization, and process identity before resume dispatch."
                    : "Stopped terminally; preserve the assigned worktree and evidence until separately authorized cleanup.");
        }
        catch (WorktreeBoundaryException exception)
        {
            quiescent = false;
            limitations.Add($"Worktree observation failed: {exception.Message}");
        }

        var target = quiescent ? requestedState : WorkflowState.Blocked;
        WorkflowTransition.EnsureAllowed(current, target, new TransitionEvidence(AuthorizationCeiling.Implement));

        var kind = quiescent ? requestedKind : "blocked-quiescence";
        var payload = new QuiescenceRecord(requestedKind, quiescent, results, checkpoint, limitations);
        var record = await _store.AppendAsync(runId, kind, payload, cancellationToken).ConfigureAwait(false);
        return new QuiescenceResult(quiescent, results, record);
    }

    private ObservedProcessState ObserveRegisteredProcesses(RunRecovery recovery)
    {
        var registrations = recovery.Records.Where(record => record.Kind == "process-registered")
            .Select(record => _store.ReadPayload<ProcessRegistration>(record)).ToArray();
        if (registrations.Length == 0) return ObservedProcessState.None;
        var outcomes = registrations.Select(registration => _processes.Observe(registration.Identity).Outcome).ToArray();
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
            _writers.Add(request.RunId,
                _worktrees.RecoverWriter(request.ResultDirectory, request.RunId, request.WriterToken));
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

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
