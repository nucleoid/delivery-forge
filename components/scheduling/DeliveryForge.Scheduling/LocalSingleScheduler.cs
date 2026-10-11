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
public sealed class LocalSingleScheduler
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
    private bool _relinquished;
    private int _paused;
    private int _stopped;

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
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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

    public async Task<bool> ReconcileCleanupAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _stopped, 1);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureControls();
            if (_ticket is not null || _laneHeld) throw new SchedulingException("This local issue already owns or retains a reservation.");
            if (!await LocalParentLane.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new SchedulingException("LocalSingle permits one isolated issue, even with spare agent/CPU slots.");
            _laneHeld = true;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(_demand.TimeLimit);
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
                await PersistAsync("reserved", deadline.Token).ConfigureAwait(false);
                _ticket = new LocalDispatchReservation(_worker.RunId);
                return _ticket;
            }
            catch
            {
                Interlocked.Exchange(ref _stopped, 1);
                if (_nativeQuiescenceKnown) Release(); else _uncertain = true;
                throw;
            }
        }
        finally { _serial.Release(); }
    }

    public async Task<AgentRequest> ProvisionAndPrepareAsync(LocalDispatchReservation reservation, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        var transitionStarted = false;
        try
        {
            EnsureControls();
            if (!ReferenceEquals(reservation, _ticket) || !_laneHeld || _resources is null || _prepared) throw new SchedulingException("Provisioning requires this live parent's unused reservation.");
            transitionStarted = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Remaining());
            await CheckFreshBaseAsync(deadline.Token).ConfigureAwait(false);
            EnsureControls();
            _worktrees.ValidateDestination(_worker.ParentCheckout, _worker.Worktree);
            _nativeQuiescenceKnown = false;
            await _worktrees.PrepareAsync(_worker.ParentCheckout, _worker.Worktree, _worker.BaseCommit, deadline.Token).ConfigureAwait(false);
            _nativeQuiescenceKnown = true;
            await CheckFreshBaseAsync(deadline.Token).ConfigureAwait(false);
            EnsureControls();
            _nativeQuiescenceKnown = false;
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
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_relinquished || _uncertain || Volatile.Read(ref _stopped) != 0 || Volatile.Read(ref _paused) == 0 || !_prepared ||
                !ReferenceEquals(reservation, _ticket) || !_laneHeld || _resources is null)
                throw new SchedulingException("Resume requires this live parent's paused ticket; checkpoint data cannot recreate it.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Remaining());
            await CheckFreshBaseAsync(deadline.Token).ConfigureAwait(false);
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
            if (Volatile.Read(ref _stopped) != 0) throw new SchedulingException("Stop arrived during resume reconciliation.");
            Interlocked.Exchange(ref _paused, 0);
            await PersistAsync("resumed", deadline.Token).ConfigureAwait(false);
            return prepared.Request;
        }
        catch { _uncertain = true; throw; }
        finally { _serial.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _paused, 1);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        Interlocked.Exchange(ref _stopped, 1);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_laneHeld) return true;
            if (_uncertain || !_nativeQuiescenceKnown) return false;
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
        catch { _uncertain = true; return false; }
        finally { _serial.Release(); }
    }

    private async Task RejectUnreconciledPriorRunsAsync(CancellationToken cancellationToken, bool recoveringSameRun = false)
    {
        foreach (var runId in await _store.DiscoverRunIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (recoveringSameRun && (runId == _worker.RunId || runId == JournalRunId)) continue;
            if (ObservedClosedRuns.ContainsKey(Path.Combine(_store.RootPath, runId))) continue;
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
        var now = DateTimeOffset.UtcNow;
        if (now < _startedAtUtc) throw new SchedulingException("Local budget clock moved backwards; fresh reconciliation is required.");
        var monotonic = _demand.TimeLimit - Stopwatch.GetElapsedTime(_allocatedAt);
        var wall = _expiresAtUtc - now;
        var remaining = monotonic < wall ? monotonic : wall;
        if (remaining <= TimeSpan.Zero) throw new SchedulingException("The immutable finite local run time budget is exhausted.");
        return remaining;
    }
    private string JournalRunId => _worker.RunId + "-scheduler";
    private void EnsureControls()
    {
        if (_relinquished || _uncertain || Volatile.Read(ref _paused) != 0 || Volatile.Read(ref _stopped) != 0) throw new SchedulingException("Paused/stopped/uncertain/relinquished parent cannot grant new dispatch.");
    }
    private async Task PersistAsync(string phase, CancellationToken cancellationToken)
    {
        try { await _store.AppendAsync(JournalRunId, "local-scheduler-checkpoint", new LocalCheckpoint(2, "local-single", phase, _plan.Identity, _plan.ContractIdentity,
            _worker.RunId, _worker.BaseCommit, WorkerEnvelope.PrepareWorker(_worker).RequestIdentity, _budget.PolicyIdentity, _worker.AuthorizationCeiling, _demand,
            _startedAtUtc, _expiresAtUtc, Volatile.Read(ref _paused) != 0, Volatile.Read(ref _stopped) != 0), cancellationToken).ConfigureAwait(false); }
        catch { _uncertain = true; throw; }
    }
    private void MarkObservedClosed()
    {
        ObservedClosedRuns.TryAdd(Path.Combine(_store.RootPath, _worker.RunId), 0);
        ObservedClosedRuns.TryAdd(Path.Combine(_store.RootPath, JournalRunId), 0);
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
