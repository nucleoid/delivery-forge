using DeliveryForge.Coordination;
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
    private RunStore forwardStore;
    private readonly AsyncLocal<RunStore?> cleanupStore = new();
    private RunStore _store => cleanupStore.Value ?? forwardStore;
    private WorktreeManager _worktrees;
    private DeliveryForge.Evidence.IOwnedDuplexBudget? original;
    private string? originalRun;
    internal RunStore BindOriginalScheduling(DeliveryForge.Evidence.IOwnedDuplexBudget owner, ICoordinatedSynchronousEffectAdmission parent, string runId, WorktreeManager admittedWorktrees)
    {
        lock (_writers)
        {
            ThrowIfDisposed();
            if (original is not null || !ReferenceEquals(parent, _coordinationAdmission) || !_writers.IsEmpty || !_pendingControls.IsEmpty) throw new WorkerEnvelopeException("Scheduling binds the same idle original coordinator once.");
            owner.CheckWork(); parent.CheckCurrent(runId);
            forwardStore = _store.WithOriginalAdmission(owner, parent, runId); _worktrees = admittedWorktrees; original = owner; originalRun = runId;
            return _store;
        }
    }
    private readonly ProcessControl _processes;
    private readonly IAgentControlPort _agentControl;
    private readonly IExecutionFactSource _factSource;
    private readonly ConcurrentDictionary<string, WriterLease> _writers = new(StringComparer.Ordinal);
    private static readonly List<(ExecutionCoordinator Owner, WriterLease Lease)> UnsettledWriterDispositions = [];
    private readonly ConcurrentDictionary<Task, string> _pendingControls = new();
    private bool _disposed;
    private ICoordinatedEffectAdmission? _coordinationAdmission;
    public void BindCoordinationAdmission(ICoordinatedEffectAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        lock (_writers)
        {
            ThrowIfDisposed();
            if (_coordinationAdmission is not null || !_writers.IsEmpty || !_pendingControls.IsEmpty)
                throw new WorkerEnvelopeException("Coordination must bind the original idle coordinator exactly once.");
            _coordinationAdmission = admission;
        }
    }
    // Actual same-owner objects only. This does not adopt registrations or quiescence from stored JSON.
    public bool ObserveOriginalProcessesSettled(string runId) => !_disposed && !HasPendingControlOperations(runId) &&
        _processes.GetOwnedIdentities(runId).Count == 0;

    internal WorktreeManager.OwnedGitBinding RequireOriginalWorktreeBinding(string runId, DeliveryForge.Evidence.IOwnedDuplexBudget sameOriginal)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(original, sameOriginal) || originalRun != runId || _coordinationAdmission is null ||
            !_writers.TryGetValue(runId, out var writer) || writer.ObserveCurrentNamespaceIdentity().Outcome != WriterNamespaceIdentityOutcome.Match)
            throw new WorktreeBoundaryException("No current actual original writer/provisioning binding.");
        var binding = _worktrees.RequireOriginalBinding(runId, sameOriginal, _coordinationAdmission);
        if (!binding.OwnsWriter(writer)) throw new WorktreeBoundaryException("Provisioning does not retain this exact original writer.");
        return binding;
    }

    internal Task ReleaseCompletedWriterAsync(string runId, AgentCompletionReceipt actualCompletion, Func<bool> originalQuiet, CancellationToken token) =>
        RunExclusiveAsync(runId, async () =>
        {
            await ReleaseCompletedWriterCoreAsync(runId, actualCompletion, originalQuiet, token).ConfigureAwait(false);
            return true;
        }, token);
    async Task ReleaseCompletedWriterCoreAsync(string runId, AgentCompletionReceipt actualCompletion, Func<bool> originalQuiet, CancellationToken token)
    {
        if (original is null || runId != originalRun || _coordinationAdmission is not ICoordinatedSynchronousEffectAdmission parent) throw new WorkerEnvelopeException("No actual scheduled owner.");
        original.CheckWork(); parent.CheckCurrent(runId);
        if (!originalQuiet() || !ObserveOriginalProcessesSettled(runId)) throw new WorkerEnvelopeException("Original completion is not settled.");
        var recovered = await _store.RecoverAsync(runId, token).ConfigureAwait(false);
        var completed = recovered.Records.LastOrDefault(record => record.Kind == "worker-completed") ?? throw new WorkerEnvelopeException("Actual completed worker record absent.");
        if (!System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_store.ReadPayload<AgentCompletionReceipt>(completed)).AsSpan().SequenceEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(actualCompletion))) throw new WorkerEnvelopeException("Completed original receipt differs.");
        var prepared = GetPrepared(recovered); EnsureWriterOwnership(prepared.Request);
        parent.EnterSynchronous(runId, () =>
        {
            original.CheckWork();
            if (!originalQuiet() || !ObserveOriginalProcessesSettled(runId) || !_writers.TryGetValue(runId, out var writer)) throw new WorkerEnvelopeException("Original writer/closure changed.");
            if (writer.ObserveCurrentNamespaceIdentity().Outcome != WriterNamespaceIdentityOutcome.Match) throw new WorkerEnvelopeException("Original writer namespace is no longer current.");
            DisposeOriginalWriter(runId, writer);
            if (!((ICollection<KeyValuePair<string, WriterLease>>)_writers).Remove(new(runId, writer))) throw new WorkerEnvelopeException("Actual writer identity changed during disposition.");
            original.CheckWork();
            return true;
        });
        original.CheckWork(); parent.CheckCurrent(runId);
    }
    // Cleanup-only for this actual completed worker after its separate evidence
    // capture failed. Saved history alone cannot supply the original binding,
    // exact live writer or the caller's current native-settlement observation.
    internal Task ReleaseCompletedWriterAfterFailureAsync(string runId, AgentCompletionReceipt actualCompletion, Func<bool> actualClosed) =>
        RunExclusiveAsync(runId, async () =>
        {
            if (original is null || originalRun != runId)
                throw new WorkerEnvelopeException("Same original completed cleanup owner required.");
            _ = original.CleanupRemaining;
            if (!actualClosed() || !ObserveOriginalProcessesSettled(runId)) throw new WorkerEnvelopeException("Actual completed worker is not known closed.");
            var recovered = await _store.RecoverAsync(runId, CancellationToken.None).ConfigureAwait(false);
            var record = recovered.Records.LastOrDefault(row => row.Kind == "worker-completed")
                ?? throw new WorkerEnvelopeException("Actual original completed record absent.");
            if (!System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_store.ReadPayload<AgentCompletionReceipt>(record)).AsSpan().SequenceEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(actualCompletion)))
                throw new WorkerEnvelopeException("Completed cleanup receipt differs from this original.");
            EnsureWriterOwnership(GetPrepared(recovered).Request);
            _ = original.CleanupRemaining;
            if (!actualClosed() || !ObserveOriginalProcessesSettled(runId) || !_writers.TryGetValue(runId, out var writer) ||
                writer.ObserveCurrentNamespaceIdentity().Outcome != WriterNamespaceIdentityOutcome.Match)
                throw new WorkerEnvelopeException("Actual completed writer/closure changed before first disposition.");
            DisposeOriginalWriter(runId, writer);
            if (!((ICollection<KeyValuePair<string, WriterLease>>)_writers).Remove(new(runId, writer)))
                throw new WorkerEnvelopeException("Actual completed cleanup writer identity changed.");
            _ = original.CleanupRemaining; return true;
        }, CancellationToken.None, cleanup: true);

    internal Task ReleaseStoppedWriterAsync(string runId, Func<bool> actualStopped) =>
        RunExclusiveAsync(runId, async () =>
        {
            await ReleaseStoppedWriterCoreAsync(runId, actualStopped).ConfigureAwait(false);
            return true;
        }, CancellationToken.None, cleanup: true);
    async Task ReleaseStoppedWriterCoreAsync(string runId, Func<bool> actualStopped)
    {
        if (original is null || originalRun != runId || !actualStopped() || !ObserveOriginalProcessesSettled(runId)) throw new WorkerEnvelopeException("Same original stopped observation required.");
        _ = original.CleanupRemaining;
        var recovered = await forwardStore.WithOriginalCleanup().RecoverAsync(runId).ConfigureAwait(false);
        var stopped = recovered.Records.LastOrDefault(record => record.Kind == "stopped") ?? throw new WorkerEnvelopeException("Actual stopped record absent.");
        if (!_store.ReadPayload<QuiescenceRecord>(stopped).Quiescent) throw new WorkerEnvelopeException("Actual stopped record is not quiescent.");
        EnsureWriterOwnership(GetPrepared(recovered).Request);
        _ = original.CleanupRemaining;
        if (!actualStopped() || !ObserveOriginalProcessesSettled(runId) || !_writers.TryGetValue(runId, out var writer)) throw new WorkerEnvelopeException("Stopped writer/closure changed.");
        if (writer.ObserveCurrentNamespaceIdentity().Outcome != WriterNamespaceIdentityOutcome.Match) throw new WorkerEnvelopeException("Stopped writer namespace is no longer current.");
        DisposeOriginalWriter(runId, writer);
        if (!((ICollection<KeyValuePair<string, WriterLease>>)_writers).Remove(new(runId, writer))) throw new WorkerEnvelopeException("Actual stopped writer identity changed.");
        _ = original.CleanupRemaining;
    }

    internal void DisposeOriginalWriter(string runId, WriterLease writer)
    {
        // A retained unknown first close cannot be retried or re-notified by
        // a later disposition attempt on this exact coordinator/lease pair.
        lock (UnsettledWriterDispositions)
            if (UnsettledWriterDispositions.Any(row => ReferenceEquals(row.Owner, this) && ReferenceEquals(row.Lease, writer)))
                throw new WorktreeBoundaryException("Actual writer first disposition remains retained; no retry authority.");
        try
        {
            writer.Dispose();
            // Dispose is sticky even when the first close threw. A repeated
            // no-op cannot turn that exact unreturned close into settlement.
            if (!writer.FirstCloseReturned)
                throw new WorktreeBoundaryException("Actual writer first disposition has not returned; retain its original owner.");
        }
        catch (Exception failure)
        {
            // The lease's first attempt is sticky. Keep the actual owner even if
            // the coordinator is disposed; never retry an unknown first close.
            bool firstRetained;
            lock (UnsettledWriterDispositions)
            {
                firstRetained = !UnsettledWriterDispositions.Any(row => ReferenceEquals(row.Owner, this) && ReferenceEquals(row.Lease, writer));
                if (firstRetained) UnsettledWriterDispositions.Add((this, writer));
            }
            var failures = new List<Exception> { failure };
            if (firstRetained)
            {
                try { if (original is DeliveryForge.Evidence.IOwnedBudgetUnknownRetention retained) retained.PreserveUnknown(); }
                catch (Exception observation) { failures.Add(observation); }
                try { _coordinationAdmission?.RetainUncertain(runId, "Actual writer first disposition did not return."); }
                catch (Exception observation) { failures.Add(observation); }
            }
            if (failures.Count > 1) throw new AggregateException("Writer first disposition and independent uncertainty observations failed.", failures);
            throw;
        }
    }


    public ExecutionCoordinator(
        RunStore store,
        WorktreeManager worktrees,
        ProcessControl processes,
        IAgentControlPort? agentControl = null,
        IExecutionFactSource? factSource = null)
    {
        forwardStore = store;
        _worktrees = worktrees;
        _processes = processes;
        _agentControl = agentControl ?? new UnsupportedAgentControlPort();
        _factSource = factSource ?? new RequestExecutionFactSource();
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string StoreRootPath => _store.RootPath;

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
        catch (Exception primary)
        {
            try { _worktrees.RetireFailedPreparationWriter(writer); }
            catch (Exception close) { throw new AggregateException("Worker preparation and original writer first disposition failed.", primary, close); }
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
        if (_coordinationAdmission is not null && !_processes.OwnsDescendantGroups)
            throw new WorkerEnvelopeException("Coordinated auxiliary process birth is unsupported: the actual controller owns roots, not descendant groups.");
        ThrowIfDisposed();
        var recovery = await _store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
        EnsureState(recovery, WorkflowState.Executing, "start a task process");
        EnsureWriterOwnership(GetPrepared(recovery).Request);
        var owned = _coordinationAdmission is null ? _processes.StartOwned(launch, runId) :
            await _coordinationAdmission.EnterAsync(runId, () => Task.FromResult(_processes.StartOwned(launch, runId))).ConfigureAwait(false);
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
            () => QuiesceAndRecordAsync(runId, WorkflowState.Stopped, "stopped", deadline, cancellationToken), cancellationToken, cleanup: true);

    public Task<ResumeResult> ResumeAsync(string runId, CancellationToken cancellationToken = default) =>
        ResumeAsync(runId, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<ResumeResult> ResumeAsync(string runId, TimeSpan controlDeadline, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(runId, () => ResumeCoreAsync(runId, controlDeadline, cancellationToken), cancellationToken);

    public bool HasPendingControlOperations(string runId) => _pendingControls.Any(pair => pair.Value == runId && !pair.Key.IsCompleted);

    public async Task<bool> AwaitPendingControlSettlementAsync(string runId, TimeSpan deadline, CancellationToken cancellationToken = default)
    {
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromDays(1)) throw new WorkerEnvelopeException("Control settlement requires a finite positive deadline.");
        var pending = _pendingControls.Where(pair => pair.Value == runId).Select(pair => pair.Key).ToArray();
        if (pending.Length == 0) return true;
        try { await Task.WhenAll(pending).WaitAsync(deadline, cancellationToken).ConfigureAwait(false); }
        catch (Exception) { }
        return !HasPendingControlOperations(runId);
    }

    public Task<QuiescenceResult> ReconcileStopAsync(string runId, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(runId, () => QuiesceAndRecordAsync(runId, WorkflowState.Stopped, "stopped", deadline, cancellationToken, reconcileTerminal: true), cancellationToken, cleanup: true);

    // This cold cleanup entry cannot turn an active or unknown journal into terminal authority.
    public Task<QuiescenceResult> ReconcileTerminalStopAsync(string runId, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(runId, async () =>
        {
            ThrowIfDisposed();
            var recovered = await _store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
            if (GetState(recovered) is not (WorkflowState.Stopped or WorkflowState.Blocked or WorkflowState.Failed or WorkflowState.LocalComplete))
                throw new WorkerEnvelopeException("Cold terminal cleanup requires a known terminal worker history; stored state is not quiescence proof.");
            return await QuiesceAndRecordAsync(runId, WorkflowState.Stopped, "stopped", deadline, cancellationToken, reconcileTerminal: true).ConfigureAwait(false);
        }, cancellationToken);

    // Observation only: no prepared/accepted record, writer, reservation or dispatch is created.
    public Task<bool> ReconcileUnstartedStopAsync(AgentRequest request, string parentCheckout, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(request.RunId, async () =>
        {
            ThrowIfDisposed();
            if (request.SchemaVersion != WorkerEnvelope.SchemaVersion || request.RequestIdentity != WorkerEnvelope.ComputeRequestIdentity(request))
                throw new WorkerEnvelopeException("Unstarted observation requires the original exact request data.");
            if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromDays(1))
                throw new WorkerEnvelopeException("Unstarted observation requires a finite positive deadline.");
            if ((await _store.RecoverAsync(request.RunId, cancellationToken).ConfigureAwait(false)).Records.Count != 0 ||
                HasPendingControlOperations(request.RunId) || _processes.GetOwnedIdentities(request.RunId).Count != 0 || _writers.ContainsKey(request.RunId))
                return false;
            await _worktrees.ValidateUnprovisionedPathsAsync(parentCheckout, request.Worktree, request.ResultDirectory, cancellationToken).ConfigureAwait(false);
            var binding = new AgentRunBinding(request.AdapterId, request.AdapterVersion, request.RunId, request.RequestIdentity, "", "");
            var task = _agentControl.ObserveUnstartedAsync(binding, deadline, cancellationToken)
                ?? throw new WorkerEnvelopeException("Unstarted host observation returned no task.");
            _pendingControls.TryAdd(task, request.RunId);
            _ = task.ContinueWith(completed =>
            {
                _ = completed.Exception;
                _pendingControls.TryRemove(completed, out _);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var observed = await task.WaitAsync(deadline, cancellationToken).ConfigureAwait(false);
            if (observed.Binding != binding || observed.Action != AgentControlAction.Stop || !observed.CapabilitySupported ||
                observed.Activity != AgentActivityState.Quiescent || string.IsNullOrWhiteSpace(observed.EvidenceReference))
                return false;
            // Observe both sides of the host call; missing manifests/files never replace the host proof.
            await _worktrees.ValidateUnprovisionedPathsAsync(parentCheckout, request.Worktree, request.ResultDirectory, cancellationToken).ConfigureAwait(false);
            return (await _store.RecoverAsync(request.RunId, cancellationToken).ConfigureAwait(false)).Records.Count == 0 &&
                !HasPendingControlOperations(request.RunId) && _processes.GetOwnedIdentities(request.RunId).Count == 0 && !_writers.ContainsKey(request.RunId);
        }, cancellationToken);

    public Task<QuiescenceResult> ReconcilePausedAsync(string runId, TimeSpan deadline, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(runId, async () =>
        {
            ThrowIfDisposed();
            var recovered = await _store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
            EnsureState(recovered, WorkflowState.Paused, "reconcile paused ownership");
            var prepared = GetPrepared(recovered);
            var durable = _store.ReadPayload<QuiescenceRecord>(recovered.Records.Last(record => record.Kind is "paused" or "paused-reconciled")).Checkpoint
                ?? throw new RunStoreCorruptionException("Paused recovery requires the original durable checkpoint.");
            var actual = await _worktrees.ObserveAsync(prepared.Request.Worktree, cancellationToken).ConfigureAwait(false);
            await _worktrees.EnsureDescendsFromAsync(prepared.Request.Worktree, prepared.Request.BaseCommit, actual.HeadCommit, cancellationToken).ConfigureAwait(false);
            var facts = await ObserveOwnedFactsAsync(prepared.Request, actual, false, cancellationToken).ConfigureAwait(false);
            var reconciliation = Reconciler.ReconcileResume(new(durable, actual,
                _worktrees.IsSameDirectory(actual.RootPath, prepared.Request.Worktree), TryEnsureWriterOwnership(prepared.Request),
                facts.RequiredArtifactPaths.All(File.Exists), OwnedFactsEqual(facts, durable.OwnedFacts), ObserveRegisteredProcesses(runId, recovered)));
            if (reconciliation.Action != ReconciliationAction.ResumeDispatch)
                throw new WorkerEnvelopeException("Fresh paused ownership is not recoverable: " + string.Join("; ", reconciliation.Reasons));
            return await QuiesceAndRecordAsync(runId, WorkflowState.Paused, "paused-reconciled", deadline, cancellationToken, reconcilePaused: true).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<ResumeResult> ResumeCoreAsync(string runId, TimeSpan controlDeadline, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var recovery = await _store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
        EnsureState(recovery, WorkflowState.Paused, "resume");
        var prepared = GetPrepared(recovery);
        var pause = recovery.Records.Last(record => record.Kind is "paused" or "paused-reconciled");
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
            adapterObservation = await InvokeControlAsync(ToBinding(accepted), AgentControlAction.Resume, controlDeadline, cancellationToken).ConfigureAwait(false);
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
        TimeSpan deadline, CancellationToken cancellationToken, bool reconcileTerminal = false, bool reconcilePaused = false)
    {
        ThrowIfDisposed();
        var recovery = await _store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
        if (recovery.Records.Count == 0) throw new WorkerEnvelopeException("No durable prepared request exists for this run.");
        var current = GetState(recovery);
        if (HasPendingControlOperations(runId)) throw new WorkerEnvelopeException("An earlier host control is still unsettled; fresh cleanup cannot outrun its possible side effects.");
        if (requestedState == WorkflowState.Paused)
            EnsureState(recovery, reconcilePaused ? WorkflowState.Paused : WorkflowState.Executing, "pause");
        else if (!reconcileTerminal && current is WorkflowState.Stopped or WorkflowState.Blocked or WorkflowState.Failed or WorkflowState.LocalComplete)
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
                    adapterControl = await InvokeControlAsync(ToBinding(accepted), action, deadline, cancellationToken).ConfigureAwait(false);
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
        if (HasPendingControlOperations(runId))
        {
            quiescent = false;
            // The original host task can still produce side effects. Optional fresh Git/fact
            // observations cannot establish quiescence here and must not delay this refusal.
            limitations.Add("Fresh checkpoint was not observed while the original host control remains unsettled; current bound cleanup is required after actual task settlement.");
        }
        else try
            {
                var worktree = await (requestedState == WorkflowState.Stopped
                    ? _worktrees.ObserveOwnedCleanupAsync(prepared.Request.Worktree, cancellationToken)
                    : _worktrees.ObserveAsync(prepared.Request.Worktree, cancellationToken)).ConfigureAwait(false);
                if (requestedState == WorkflowState.Stopped)
                    await _worktrees.EnsureDescendsFromOwnedCleanupAsync(prepared.Request.Worktree, prepared.Request.BaseCommit, worktree.HeadCommit, cancellationToken).ConfigureAwait(false);
                else await _worktrees.EnsureDescendsFromAsync(prepared.Request.Worktree, prepared.Request.BaseCommit, worktree.HeadCommit, cancellationToken).ConfigureAwait(false);
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

        var terminalReconciliation = reconcileTerminal && current is WorkflowState.Stopped or WorkflowState.Blocked or WorkflowState.Failed or WorkflowState.LocalComplete;
        var target = quiescent ? requestedState : WorkflowState.Blocked;
        if (!terminalReconciliation && !(reconcilePaused && quiescent))
            WorkflowTransition.EnsureAllowed(current, target, new TransitionEvidence(GetAuthorization(prepared.Request)));

        var kind = terminalReconciliation ? "cleanup-reconciled" : quiescent ? requestedKind : "blocked-quiescence";
        var payload = new QuiescenceRecord(requestedKind, quiescent, results, adapterControl, checkpoint, limitations);
        var record = await _store.AppendAsync(runId, kind, payload, CancellationToken.None).ConfigureAwait(false);
        return new QuiescenceResult(quiescent, results, record);
    }

    private async Task<AgentControlResult> InvokeControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan deadline, CancellationToken cancellationToken)
    {
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromDays(1)) throw new WorkerEnvelopeException("Host control requires an explicit finite positive deadline.");
        var task = _agentControl.ControlAsync(binding, action, deadline, cancellationToken)
            ?? throw new WorkerEnvelopeException("Host control returned no task.");
        _pendingControls.TryAdd(task, binding.RunId);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _pendingControls.TryRemove(completed, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(deadline, cancellationToken).ConfigureAwait(false);
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
        if (original is not null)
        {
            var binding = _worktrees.RequireOriginalBinding(request.Worktree, cleanup: cleanupStore.Value is not null);
            return binding.RunId == request.RunId && _writers.TryGetValue(request.RunId, out var originalWriter) &&
                binding.OwnsWriter(originalWriter) && originalWriter.WriterToken == request.WriterToken && originalWriter.ObserveCurrentNamespaceIdentity().Outcome == WriterNamespaceIdentityOutcome.Match;
        }
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
                "paused-reconciled" when state == WorkflowState.Paused => state,
                "cleanup-reconciled" when state is WorkflowState.Stopped or WorkflowState.Blocked or WorkflowState.Failed or WorkflowState.LocalComplete => state,
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

    private async Task<T> RunExclusiveAsync<T>(
        string runId, Func<Task<T>> action, CancellationToken cancellationToken, bool cleanup = false)
    {
        if (original is not null && runId != originalRun) throw new WorkerEnvelopeException("Original scheduler cannot adopt another run.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cleanup ? default : original?.Token ?? default);
        if (original is not null)
        {
            if (!cleanup) original.CheckWork();
            var remaining = cleanup ? original.CleanupRemaining : original.WorkLimit - System.Diagnostics.Stopwatch.GetElapsedTime(original.OriginalStarted);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Original coordinator deadline expired before flow-lock admission.");
            linked.CancelAfter(remaining);
        }
        var flowLock = FlowLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await flowLock.WaitAsync(linked.Token).ConfigureAwait(false);
        var prior = cleanupStore.Value;
        try
        {
            if (original is not null && cleanup) cleanupStore.Value = forwardStore.WithOriginalCleanup(); else original?.CheckWork();
            var value = await action().ConfigureAwait(false);
            if (original is not null && cleanup) _ = original.CleanupRemaining; else original?.CheckWork();
            return value;
        }
        finally { cleanupStore.Value = prior; flowLock.Release(); }
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
