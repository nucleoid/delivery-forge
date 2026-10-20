using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;

namespace DeliveryForge.Scheduling;

public sealed class LocalDispatchReservation
{
    internal LocalDispatchReservation(string runId) => RunId = runId;
    public string RunId { get; }
}

// One process-local parent lane, not distributed ownership. No native agent is launched here.
public sealed partial class LocalSingleScheduler
{
    private static readonly ConcurrentDictionary<string, byte> ObservedClosedRuns = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly SemaphoreSlim LocalParentLane = new(1, 1);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly FrozenPlan _plan;
    private readonly FrozenWorkerPlan _worker;
    private readonly RunDemand _demand;
    private readonly ResourceBudget _budget;
    private readonly RunStore _store;
    private readonly ExecutionCoordinator _execution;
    private readonly WorktreeManager _worktrees;
    private readonly GitRepositoryContextReader _reader;
    private readonly IReadOnlyList<string> _prerequisiteCommits;
    private readonly TimeSpan _controlTimeout;
    private ResourceReservation? _resources;
    private LocalDispatchReservation? _ticket;
    private bool _laneHeld;
    private bool _prepared;
    private bool _uncertain;
    private bool _nativeQuiescenceKnown = true;
    private long _allocatedAt;
    private DateTimeOffset _startedAtUtc;
    private DateTimeOffset _expiresAtUtc;
    private LocalRunTimeBudget? _timeBudget;
    private bool _relinquished;
    private int _paused;
    private int _stopped;
    private long _controlGeneration;
    [System.Text.Json.Serialization.JsonIgnore]
    public string CleanupLimitation { get; private set; } = "";

    private LocalSingleScheduler(FrozenPlan plan, FrozenWorkerPlan worker, RunDemand demand, ResourceBudget budget, RunStore store,
        ExecutionCoordinator execution, WorktreeManager worktrees, GitRepositoryContextReader reader, IReadOnlyList<string> prerequisites, TimeSpan controlTimeout)
    {
        _plan = plan; _worker = worker; _demand = demand; _budget = budget; _store = store;
        _execution = execution; _worktrees = worktrees; _reader = reader; _prerequisiteCommits = prerequisites;
        _controlTimeout = controlTimeout;
    }

    public static LocalSingleScheduler Create(FrozenPlan plan, FrozenWorkerPlan worker, RunDemand demand, ResourceBudget budget,
        RunStore store, ExecutionCoordinator execution, WorktreeManager worktrees, GitRepositoryContextReader reader, TimeSpan? controlTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(worker); ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(budget); ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(worktrees); ArgumentNullException.ThrowIfNull(reader);
        if (!plan.DownstreamReady || !plan.BaseReferenceExactBranchVerified || plan.BaseReferenceWasDetached ||
            worker.PlanIdentity != plan.ContractIdentity || worker.BaseCommit != plan.BaseCommit || worker.Repository != plan.Repository ||
            !string.Equals(store.RootPath, execution.StoreRootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new SchedulingException("Local worker must bind the ready frozen plan, exact base, repository, and parent execution store.");
        var bytes = plan.CanonicalBytes;
        if (bytes.Length > 16 * 1024 * 1024) throw new SchedulingException("Frozen scheduling bundle exceeds the finite input bound.");
        using var document = JsonDocument.Parse(bytes);
        var material = document.RootElement.GetProperty("plan");
        var ceiling = material.GetProperty("requestedCeiling").GetString();
        if (ceiling is not ("implement" or "pr" or "merge") || worker.AuthorizationCeiling != ceiling || material.GetProperty("mode").GetString() == "plan")
            throw new SchedulingException("Worker execution cannot substitute or escalate the immutable frozen authorization ceiling.");
        var paths = material.GetProperty("changeMap").EnumerateArray().Select(item => item.GetProperty("path").GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var excluded = material.GetProperty("scope").GetProperty("excluded").EnumerateArray().Select(item => item.GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length is < 1 or > 4096 || paths.Any(path => path.Any(character => "*?[]{}".Contains(character))) ||
            !paths.SequenceEqual(worker.AllowedPaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) ||
            !excluded.SequenceEqual(worker.Exclusions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            throw new SchedulingException("Worker scope must exactly match the frozen change map and exclusions; unresolved glob maps are unavailable.");
        var prerequisites = new List<string>();
        var dependencies = material.GetProperty("dependencyDag");
        if (dependencies.GetArrayLength() is < 1 or > 4096) throw new SchedulingException("Frozen dependencies exceed the scheduling bound.");
        foreach (var node in dependencies.EnumerateArray())
        {
            var condition = node.GetProperty("integrationCondition").GetString()!;
            if (node.GetProperty("id").GetString() == plan.WorkItem && condition == "root") continue;
            if (!condition.StartsWith("commit:", StringComparison.Ordinal) || condition.Length is not (47 or 71) || !condition[7..].All(Uri.IsHexDigit))
                throw new SchedulingException("Dependency readiness requires a frozen commit:<full-object-id> condition; descriptive/open-PR claims are unavailable.");
            prerequisites.Add(condition[7..]);
        }
        var captured = worker with { AllowedPaths = Array.AsReadOnly(paths), Exclusions = Array.AsReadOnly(excluded), RequiredCapabilities = Array.AsReadOnly(worker.RequiredCapabilities.ToArray()) };
        WorkerEnvelope.PrepareWorker(captured);
        if (!budget.CanAllocate(demand)) throw new SchedulingException("Approved finite local resource/time/token/cost capacity is unavailable.");
        var timeout = controlTimeout ?? TimeSpan.FromSeconds(5);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new SchedulingException("Host control requires a finite positive bound no greater than one minute.");
        return new(plan, captured, demand with { ExclusiveResources = Array.AsReadOnly(demand.ExclusiveResources.ToArray()) }, budget, store, execution, worktrees, reader, prerequisites.AsReadOnly(), timeout);
    }

    public async Task RelinquishPausedAsync(CancellationToken cancellationToken = default)
    {
        await WaitSerialAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_uncertain || !_nativeQuiescenceKnown || !_prepared || !_laneHeld || Volatile.Read(ref _paused) == 0 ||
                Volatile.Read(ref _stopped) != 0 || _execution.HasPendingControlOperations(_worker.RunId))
                throw new SchedulingException("Only this positively quiesced paused parent can relinquish its live ticket.");
            await PersistAsync("paused-transfer", cancellationToken).ConfigureAwait(false);
            _relinquished = true;
            Release();
        }
        finally { _serial.Release(); }
    }

    public async Task<LocalDispatchReservation> RecoverPausedAsync(CancellationToken cancellationToken = default)
    {
        await WaitSerialAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureControls();
            if (_ticket is not null || _laneHeld || !await LocalParentLane.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                throw new SchedulingException("Cold reconciliation requires the sole available local parent lane.");
            _laneHeld = true;
            try
            {
                var journal = await _store.RecoverAsync(JournalRunId, cancellationToken).ConfigureAwait(false);
                var record = journal.Records.LastOrDefault(item => item.Kind == "local-scheduler-checkpoint")
                    ?? throw new SchedulingException("Missing local journal cannot recreate a reservation.");
                var saved = _store.ReadPayload<LocalCheckpoint>(record);
                var now = DateTimeOffset.UtcNow;
                if (saved.Version != 2 || saved.Profile != "local-single" || saved.Phase is not ("paused" or "paused-transfer" or "recovered-paused") ||
                    !saved.Paused || saved.Stopped || saved.RunId != _worker.RunId || saved.PlanIdentity != _plan.Identity ||
                    saved.RequestPlanIdentity != _plan.ContractIdentity || saved.BaseCommit != _worker.BaseCommit ||
                    saved.RequestIdentity != WorkerEnvelope.PrepareWorker(_worker).RequestIdentity || saved.Ceiling != _worker.AuthorizationCeiling ||
                    saved.BudgetPolicyIdentity != _budget.PolicyIdentity || !SameDemand(saved.Demand, _demand) ||
                    saved.ExpiresAtUtc - saved.StartedAtUtc != _demand.TimeLimit || now < saved.StartedAtUtc || now >= saved.ExpiresAtUtc)
                    throw new SchedulingException("Immutable paused journal/plan/budget/clock differs or its original finite lifetime expired.");
                var execution = await _store.RecoverAsync(_worker.RunId, cancellationToken).ConfigureAwait(false);
                var preparedRecord = execution.Records.SingleOrDefault(item => item.Kind == "worker-prepared")
                    ?? throw new SchedulingException("Cold recovery requires the original immutable prepared worker.");
                var prepared = _store.ReadPayload<PreparedWorkerRecord>(preparedRecord);
                if (prepared.Request.RequestIdentity != saved.RequestIdentity || prepared.Request.RequestIdentity != WorkerEnvelope.ComputeRequestIdentity(prepared.Request))
                    throw new SchedulingException("Immutable worker request differs from the parent recovery journal.");
                _prepared = true;
                await RejectUnreconciledPriorRunsAsync(cancellationToken, recoveringSameRun: true).ConfigureAwait(false);
                _resources = _budget.Allocate(_demand);
                _allocatedAt = Stopwatch.GetTimestamp();
                _startedAtUtc = saved.StartedAtUtc;
                _expiresAtUtc = saved.ExpiresAtUtc;
                _timeBudget = LocalRunTimeBudget.Adopt(_demand.TimeLimit, _startedAtUtc, _expiresAtUtc, DateTimeOffset.UtcNow);
                using var deadline = OriginalDeadline(cancellationToken);
                deadline.CancelAfter(Remaining());
                await CheckFreshBaseAsync(deadline.Token).ConfigureAwait(false);
                var observed = await _execution.ReconcilePausedAsync(_worker.RunId, _controlTimeout, deadline.Token).ConfigureAwait(false);
                if (!observed.Quiescent) throw new SchedulingException("Fresh host/child/writer observations did not prove paused quiescence.");
                Interlocked.Exchange(ref _paused, 1);
                await PersistAsync("recovered-paused", deadline.Token).ConfigureAwait(false);
                _ticket = new LocalDispatchReservation(_worker.RunId);
                return _ticket;
            }
            catch (Exception exception)
            {
                // Existing child records make failed adoption cleanup-only; never release by stored claims.
                _uncertain = true;
                if (!_prepared) Release();
                throw new SchedulingException("Cold paused reconciliation failed: " + exception.Message, exception);
            }
        }
        finally { _serial.Release(); }
    }

    // Cleanup-only cold entry: never returns a reservation or resumes terminal work.
    public async Task<bool> ReconcileColdTerminalAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _controlGeneration);
        Interlocked.Exchange(ref _stopped, 1);
        await WaitSerialAsync(cancellationToken).ConfigureAwait(false);
        var cleanupLaneHeld = false;
        try
        {
            if (_laneHeld || _ticket is not null || _resources is not null || _prepared || _relinquished)
                throw new SchedulingException("Cold terminal cleanup cannot replace a live parent's reservation or paused transfer.");
            if (!await LocalParentLane.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                throw new SchedulingException("Cold terminal observation requires the sole available local parent lane.");
            cleanupLaneHeld = true;
            ObservedClosedRuns.TryRemove(ClosedNamespaceKey(_worker.RunId), out _);
            ObservedClosedRuns.TryRemove(ClosedNamespaceKey(JournalRunId), out _);
            using var deadline = OriginalDeadline(cancellationToken);
            if (original is not null) throw new SchedulingException("Cold cleanup cannot replace the retained original scheduling owner.");
            deadline.CancelAfter(TimeSpan.FromMinutes(2)); // Cleanup remains possible after the original dispatch lifetime expires.
            var journal = await _store.RecoverAsync(JournalRunId, deadline.Token).ConfigureAwait(false);
            var record = journal.Records.LastOrDefault(item => item.Kind == "local-scheduler-checkpoint")
                ?? throw new SchedulingException("Cold cleanup requires the immutable original local scheduler journal.");
            var saved = _store.ReadPayload<LocalCheckpoint>(record);
            if (saved.Version != 2 || saved.Profile != "local-single" ||
                saved.Phase is not ("reserved" or "prepared" or "paused" or "paused-transfer" or "recovered-paused" or "resuming" or "resumed" or "stopped") ||
                saved.RunId != _worker.RunId || saved.PlanIdentity != _plan.Identity || saved.RequestPlanIdentity != _plan.ContractIdentity ||
                saved.BaseCommit != _worker.BaseCommit || saved.RequestIdentity != WorkerEnvelope.PrepareWorker(_worker).RequestIdentity ||
                saved.Ceiling != _worker.AuthorizationCeiling || saved.BudgetPolicyIdentity != _budget.PolicyIdentity || !SameDemand(saved.Demand, _demand) ||
                saved.StartedAtUtc == default || saved.ExpiresAtUtc - saved.StartedAtUtc != _demand.TimeLimit)
                throw new SchedulingException("Cold cleanup journal/namespace/request/plan/resource identity is unknown or differs.");
            var execution = await _store.RecoverAsync(_worker.RunId, deadline.Token).ConfigureAwait(false);
            var preparedRecord = execution.Records.SingleOrDefault(item => item.Kind == "worker-prepared");
            var prepared = preparedRecord is null ? null : _store.ReadPayload<PreparedWorkerRecord>(preparedRecord);
            if (prepared is not null && (prepared.Request.RunId != _worker.RunId || prepared.Request.RequestIdentity != saved.RequestIdentity ||
                prepared.Request.RequestIdentity != WorkerEnvelope.ComputeRequestIdentity(prepared.Request)))
                throw new SchedulingException("Cold cleanup worker differs from the exact original local request.");
            if (prepared is null && (execution.Records.Count != 0 || saved.Phase != "stopped" || !saved.Stopped))
                throw new SchedulingException("Missing preparation is not proof of an unstarted stopped reservation; ambiguous history is refused.");
            await CheckFreshBaseAsync(deadline.Token).ConfigureAwait(false);
            if (!await _execution.AwaitPendingControlSettlementAsync(_worker.RunId, _controlTimeout, deadline.Token).ConfigureAwait(false))
                throw new SchedulingException("Pending host controls cannot be outrun by cold terminal cleanup.");
            var quiescent = prepared is null
                ? await _execution.ReconcileUnstartedStopAsync(WorkerEnvelope.PrepareWorker(_worker), _worker.ParentCheckout, _controlTimeout, deadline.Token).ConfigureAwait(false)
                : (await _execution.ReconcileTerminalStopAsync(_worker.RunId, _controlTimeout, deadline.Token).ConfigureAwait(false)).Quiescent;
            if (!quiescent || _execution.HasPendingControlOperations(_worker.RunId))
                throw new SchedulingException("Fresh child/writer/bound host observations did not prove terminal closure.");
            _startedAtUtc = saved.StartedAtUtc;
            _expiresAtUtc = saved.ExpiresAtUtc;
            Interlocked.Exchange(ref _paused, 0);
            await PersistAsync("stopped", deadline.Token).ConfigureAwait(false);
            MarkObservedClosed();
            _uncertain = false;
            CleanupLimitation = "";
            return true;
        }
        catch (Exception exception)
        {
            CleanupLimitation = DescribeFailure(exception);
            return false;
        }
        finally
        {
            // There was no adopted reservation. Unproved histories still block this store through discovery;
            // transient observation serialization must not strand unrelated stores or manufacture capacity.
            if (cleanupLaneHeld) LocalParentLane.Release();
            _serial.Release();
        }
    }

    public async Task<bool> ReconcileCleanupAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _controlGeneration);
        Interlocked.Exchange(ref _stopped, 1);
        await WaitSerialAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_laneHeld) return true;
            if (!_nativeQuiescenceKnown || !await _execution.AwaitPendingControlSettlementAsync(_worker.RunId, _controlTimeout, cancellationToken).ConfigureAwait(false)) return false;
            if (!_prepared) return false;
            var result = await _execution.ReconcileStopAsync(_worker.RunId, _controlTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Quiescent || _execution.HasPendingControlOperations(_worker.RunId)) return false;
            await PersistAsync("stopped", cancellationToken).ConfigureAwait(false);
            MarkObservedClosed();
            _uncertain = false;
            Release();
            return true;
        }
        catch (Exception) { _uncertain = true; return false; }
        finally { _serial.Release(); }
    }

    public async Task<LocalDispatchReservation> ReserveAsync(CancellationToken cancellationToken = default)
    {
        await WaitSerialAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureControls();
            if (_ticket is not null || _laneHeld) throw new SchedulingException("This local issue already owns or retains a reservation.");
            if (!await LocalParentLane.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new SchedulingException("LocalSingle permits one isolated issue, even with spare agent/CPU slots.");
            _laneHeld = true;
            try
            {
                using var deadline = OriginalDeadline(cancellationToken);
                deadline.CancelAfter(original is null ? _demand.TimeLimit : Min(_demand.TimeLimit, OriginalRemaining()));
                if ((await _store.RecoverAsync(_worker.RunId, deadline.Token).ConfigureAwait(false)).Records.Count != 0 ||
                    (await _store.RecoverAsync(JournalRunId, deadline.Token).ConfigureAwait(false)).Records.Count != 0)
                    throw new SchedulingException("Existing manifests/checkpoints are recovery data; they cannot create new live reservations.");
                await RejectUnreconciledPriorRunsAsync(deadline.Token).ConfigureAwait(false);
                await CheckFreshBaseAsync(deadline.Token).ConfigureAwait(false);
                EnsureControls();
                _resources = _budget.Allocate(_demand);
                _allocatedAt = Stopwatch.GetTimestamp();
                _startedAtUtc = DateTimeOffset.UtcNow;
                _expiresAtUtc = _startedAtUtc + _demand.TimeLimit;
                _timeBudget = LocalRunTimeBudget.Adopt(_demand.TimeLimit, _startedAtUtc, _expiresAtUtc, _startedAtUtc);
                await PersistAsync("reserved", deadline.Token).ConfigureAwait(false);
                _ticket = new LocalDispatchReservation(_worker.RunId);
                return _ticket;
            }
            catch (Exception exception)
            {
                CleanupLimitation = "Reservation failed: " + DescribeFailure(exception);
                Interlocked.Exchange(ref _stopped, 1);
                if (_nativeQuiescenceKnown && !_uncertain && !_store.HasUnsettledOriginalFiles && actualOriginalUnknown?.Invoke() != true) Release(); else _uncertain = true;
                throw;
            }
        }
        finally { _serial.Release(); }
    }

    public async Task<AgentRequest> ProvisionAndPrepareAsync(LocalDispatchReservation reservation, CancellationToken cancellationToken = default)
    {
        await WaitSerialAsync(cancellationToken).ConfigureAwait(false);
        var transitionStarted = false;
        try
        {
            EnsureControls();
            if (!ReferenceEquals(reservation, _ticket) || !_laneHeld || _resources is null || _prepared) throw new SchedulingException("Provisioning requires this live parent's unused reservation.");
            transitionStarted = true;
            using var deadline = OriginalDeadline(cancellationToken);
            deadline.CancelAfter(Remaining());
            await CheckFreshBaseAsync(deadline.Token).ConfigureAwait(false);
            EnsureControls();
            _worktrees.ValidateDestination(_worker.ParentCheckout, _worker.Worktree);
            provisioningEntered = true;
            _nativeQuiescenceKnown = false;
            await _worktrees.PrepareAsync(_worker.ParentCheckout, _worker.Worktree, _worker.BaseCommit, deadline.Token).ConfigureAwait(false);
            _nativeQuiescenceKnown = true;
            await CheckFreshBaseAsync(deadline.Token).ConfigureAwait(false);
            EnsureControls();
            _nativeQuiescenceKnown = false;
            preparationEntered = true;
            var prepared = await _execution.PrepareWorkerAsync(_worker, deadline.Token).ConfigureAwait(false);
            _prepared = true;
            _nativeQuiescenceKnown = true;
            await PersistAsync("prepared", deadline.Token).ConfigureAwait(false);
            EnsureControls();
            return prepared.Request;
        }
        catch
        {
            if (transitionStarted && (!_nativeQuiescenceKnown || _prepared)) _uncertain = true;
            throw;
        }
        finally { _serial.Release(); }
    }

    public async Task<AgentRequest> ResumeAsync(LocalDispatchReservation reservation, CancellationToken cancellationToken = default)
    {
        await WaitSerialAsync(cancellationToken).ConfigureAwait(false);
        var enteredValidResume = false;
        try
        {
            if (_relinquished || _uncertain || Volatile.Read(ref _stopped) != 0 || Volatile.Read(ref _paused) == 0 || !_prepared ||
                !ReferenceEquals(reservation, _ticket) || !_laneHeld || _resources is null)
                throw new SchedulingException("Resume requires this live parent's paused ticket; checkpoint data cannot recreate it.");
            enteredValidResume = true;
            var controlGeneration = Interlocked.Read(ref _controlGeneration);
            using var deadline = OriginalDeadline(cancellationToken);
            deadline.CancelAfter(Remaining());
            await CheckFreshBaseAsync(deadline.Token).ConfigureAwait(false);
            EnsureResumeGeneration(controlGeneration);
            await PersistAsync("resuming", deadline.Token).ConfigureAwait(false);
            EnsureResumeGeneration(controlGeneration);
            var resumed = await _execution.ResumeAsync(_worker.RunId, _controlTimeout, deadline.Token).ConfigureAwait(false);
            if (resumed.Reconciliation.Action != ReconciliationAction.ResumeDispatch || resumed.Record is null)
            {
                _uncertain = true;
                throw new SchedulingException("Execution live-fact reconciliation did not authorize resume; capacity remains reserved.");
            }
            var recovery = await _store.RecoverAsync(_worker.RunId, deadline.Token).ConfigureAwait(false);
            var prepared = _store.ReadPayload<PreparedWorkerRecord>(recovery.Records.Single(record => record.Kind == "worker-prepared"));
            if (prepared.Request.PlanIdentity != _worker.PlanIdentity || prepared.Request.AuthorizationCeiling != _worker.AuthorizationCeiling ||
                prepared.Request.RequestIdentity != WorkerEnvelope.ComputeRequestIdentity(prepared.Request))
                throw new SchedulingException("Recovered worker differs from the immutable parent plan or ceiling.");
            EnsureResumeGeneration(controlGeneration);
            Interlocked.Exchange(ref _paused, 0);
            await PersistAsync("resumed", deadline.Token).ConfigureAwait(false);
            EnsureResumeGeneration(controlGeneration);
            return prepared.Request;
        }
        catch { if (enteredValidResume) _uncertain = true; throw; }
        finally { _serial.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _controlGeneration);
        Interlocked.Exchange(ref _paused, 1);
        await WaitSerialAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_prepared)
            {
                var result = await _execution.PauseAsync(_worker.RunId, _controlTimeout, cancellationToken).ConfigureAwait(false);
                if (!result.Quiescent) { _uncertain = true; throw new SchedulingException("Pause could not prove child quiescence; local capacity remains reserved."); }
            }
            else _ticket = null;
            await PersistAsync("paused", cancellationToken).ConfigureAwait(false);
        }
        catch { _uncertain = true; throw; }
        finally { _serial.Release(); }
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _controlGeneration);
        Interlocked.Exchange(ref _stopped, 1);
        await WaitSerialAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_laneHeld) return true;
            if (_uncertain || !_nativeQuiescenceKnown)
            {
                CleanupLimitation = _uncertain ? "Parent uncertainty retains local capacity." : "Native operation quiescence is not yet proved.";
                return false;
            }
            if (_prepared)
            {
                var result = await _execution.StopAsync(_worker.RunId, _controlTimeout, cancellationToken).ConfigureAwait(false);
                if (!result.Quiescent) { _uncertain = true; return false; }
            }
            await PersistAsync("stopped", cancellationToken).ConfigureAwait(false);
            MarkObservedClosed();
            Release();
            return true;
        }
        catch (Exception exception) { CleanupLimitation = DescribeFailure(exception); _uncertain = true; return false; }
        finally { _serial.Release(); }
    }

    private async Task RejectUnreconciledPriorRunsAsync(CancellationToken cancellationToken, bool recoveringSameRun = false)
    {
        foreach (var runId in await _store.DiscoverRunIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (recoveringSameRun && (runId == _worker.RunId || runId == JournalRunId)) continue;
            if (ObservedClosedRuns.ContainsKey(ClosedNamespaceKey(runId))) continue;
            var recovery = await _store.RecoverAsync(runId, cancellationToken).ConfigureAwait(false);
            if (recovery.Records.Any(record => record.Kind is "local-scheduler-checkpoint" or "worker-prepared"))
                throw new SchedulingException("Prior local run requires fresh child/writer/host reconciliation; stored terminal or active claims cannot authorize another run.");
        }
    }
    private async Task CheckFreshBaseAsync(CancellationToken cancellationToken)
    {
        _nativeQuiescenceKnown = false;
        var context = await _reader.ReadAsync(_worker.ParentCheckout, _plan.BaseReference, cancellationToken).ConfigureAwait(false);
        _nativeQuiescenceKnown = true;
        if (!_plan.ReconcileBase(context).DownstreamReady || context.Dirty || context.Shallow)
            throw new SchedulingException("Fresh checkout/base drift or incomplete history requires a new reconciled frozen plan before dispatch.");
        foreach (var commit in _prerequisiteCommits)
        {
            _nativeQuiescenceKnown = false;
            var integrated = await _reader.IsAncestorAsync(context, commit, cancellationToken).ConfigureAwait(false);
            _nativeQuiescenceKnown = true;
            if (!integrated) throw new SchedulingException("Prerequisite commit is not integrated into the dependent frozen base.");
        }
    }

    private TimeSpan Remaining()
    {
        var remaining = (_timeBudget ?? throw new SchedulingException("No positively observed local lifetime has been adopted."))
            .Remaining(Stopwatch.GetElapsedTime(_allocatedAt), DateTimeOffset.UtcNow);
        return original is null ? remaining : Min(remaining, OriginalRemaining());
    }
    private string JournalRunId => _worker.RunId + "-scheduler";
    private void EnsureControls()
    {
        CheckOriginal();
        if (_relinquished || _uncertain || Volatile.Read(ref _paused) != 0 || Volatile.Read(ref _stopped) != 0) throw new SchedulingException("Paused/stopped/uncertain/relinquished parent cannot grant new dispatch.");
    }
    private async Task PersistAsync(string phase, CancellationToken cancellationToken)
    {
        try
        {
            await _store.AppendAsync(JournalRunId, "local-scheduler-checkpoint", new LocalCheckpoint(2, "local-single", phase, _plan.Identity, _plan.ContractIdentity,
                _worker.RunId, _worker.BaseCommit, WorkerEnvelope.PrepareWorker(_worker).RequestIdentity, _budget.PolicyIdentity, _worker.AuthorizationCeiling, _demand,
                _startedAtUtc, _expiresAtUtc, Volatile.Read(ref _paused) != 0, Volatile.Read(ref _stopped) != 0), cancellationToken).ConfigureAwait(false);
        }
        catch { _uncertain = true; throw; }
    }
    private void MarkObservedClosed()
    {
        ObservedClosedRuns.TryAdd(ClosedNamespaceKey(_worker.RunId), 0);
        ObservedClosedRuns.TryAdd(ClosedNamespaceKey(JournalRunId), 0);
    }
    private string ClosedNamespaceKey(string runId) => Path.Combine(_store.RootPath, "runs", _store.GetRunNamespaceIdentity(runId));
    private void EnsureResumeGeneration(long expected)
    {
        if (_relinquished || Volatile.Read(ref _stopped) != 0 || Interlocked.Read(ref _controlGeneration) != expected)
            throw new SchedulingException("Pause, stop or parent transfer arrived during resume; no dispatch request can be returned.");
    }
    private static string DescribeFailure(Exception exception)
    {
        var diagnostic = exception.GetType().Name + ": " + exception.Message;
        return diagnostic.Length <= 4096 ? diagnostic : diagnostic[..4096];
    }
    private static bool SameDemand(RunDemand saved, RunDemand actual) => saved is not null && saved.Slots == actual.Slots &&
        saved.TimeLimit == actual.TimeLimit && saved.TokenLimit == actual.TokenLimit && saved.CostLimit == actual.CostLimit &&
        saved.ExclusiveResources is not null && saved.ExclusiveResources.SequenceEqual(actual.ExclusiveResources, StringComparer.Ordinal);
    private sealed record LocalCheckpoint(int Version, string Profile, string Phase, string PlanIdentity, string RequestPlanIdentity,
        string RunId, string BaseCommit, string RequestIdentity, string BudgetPolicyIdentity, string Ceiling, RunDemand Demand,
        DateTimeOffset StartedAtUtc, DateTimeOffset ExpiresAtUtc, bool Paused, bool Stopped);
    private void Release()
    {
        _ticket = null; _resources?.Dispose(); _resources = null;
        if (_laneHeld) { _laneHeld = false; LocalParentLane.Release(); }
    }
}
