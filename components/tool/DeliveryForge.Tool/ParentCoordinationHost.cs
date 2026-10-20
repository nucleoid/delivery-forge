using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeliveryForge.Coordination;
using DeliveryForge.Execution;
using DeliveryForge.Planning;

namespace DeliveryForge.Tool;

// Configuration selects independently pinned operator evidence; no availability or authority flags.
public sealed record ParentCoordinationConfiguration(Uri Endpoint, string Project, string PrincipalId,
    string PrivateKeyPath, string CheckpointRoot, AdmissionFile DeploymentEvidence);

// One original parent, one selected plan/run, one finite local writer lifetime. No takeover/scheduler claim.
public sealed class ParentCoordinationHost : ICoordinatedSynchronousEffectAdmission, ICoordinatedBoundedCurrentness, IOwnedRunQuiescenceSource, IMonotonicClock, IAsyncDisposable
{
    readonly object gate = new();
    readonly ParentCoordinationConfiguration selected;
    readonly ToolLifetime original;
    readonly ContinuumMcpTransport transport;
    readonly FileCoordinationCheckpointStore checkpoints;
    readonly CancellationTokenSource renewalStop;
    readonly ParentEffectAdmission effects;
    readonly ParentRenewalHandoff renewalHandoff;
    readonly List<Task> coordinationTasks = [];
    readonly CancellationTokenRegistration cancelled;
    ContinuumCoordinationAdapter? reducer;
    OwnerHostBridge? bridge;
    LeaseOwner? owner;
    IReadOnlyList<LeaseEvidence> fixtureObservedLeases = Array.Empty<LeaseEvidence>();
    CoordinationResource? resource;
    IReadOnlyList<CoordinationResource>? resources;
    string? parentRunId;
    ParentCheckpointBinding? parentBinding;
    DispatchAuthority? authority;
    ReleaseContinuation? releaseContinuation;
    ToolResult? fixtureStopResult;
    Task<RenewalResult>? fixtureRenewalTask;
    IReadOnlyList<LeaseEvidence> fixtureRenewalInputs = Array.Empty<LeaseEvidence>();
    Task? renewal, lossStop, transportClose, checkpointClose;
    bool bound, acquiring, opened, closed, revoked, disposeEntered, disposed;
    Exception? failure, dispositionFailure;
    public string DomainId { get; } = Guid.NewGuid().ToString("D");
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(original.OriginalStarted);
    public QuiescenceSourceBinding Binding => new("actual-tool-parent", DomainId, DomainId,
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), true);
    internal static bool OriginalCanAdmitEffects(ToolLifetime actual)
    {
        actual.Check(); return !actual.HasExplicitUnknown;
    }

    ParentCoordinationHost(ParentCoordinationConfiguration settings, ToolLifetime life,
        ContinuumMcpTransport actualTransport, FileCoordinationCheckpointStore actualStore)
    {
        selected = settings; original = life; transport = actualTransport; checkpoints = actualStore;
        renewalHandoff = new(gate, original);
        effects = new(gate, runId => OriginalCanAdmitEffects(original) && opened && !closed && !revoked && parentRunId == runId && authority is not null && reducer is not null && reducer.MayDispatch(authority), Revoke, renewalHandoff);
        renewalStop = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        cancelled = life.Token.Register(() => Revoke(new OperationCanceledException("Original parent cancellation revoked admission.")));
    }

    internal static async Task<ParentCoordinationHost> CreateAsync(ParentCoordinationConfiguration settings, ToolLifetime life)
    {
        life.Check();
        if (!settings.Endpoint.IsAbsoluteUri || settings.Endpoint.Scheme != "https" || !string.IsNullOrEmpty(settings.Endpoint.UserInfo))
            throw new ToolUsageException("Explicit authenticated HTTPS Continuum endpoint required.");
        BoundedFiles.Plain(settings.PrivateKeyPath);
        var root = Path.GetFullPath(settings.CheckpointRoot); BoundedFiles.Plain(root, true);
        var evidence = new PinnedOperatorEvidence(settings.DeploymentEvidence, life);
        var observed = await evidence.ObserveAsync(life.Token).ConfigureAwait(false);
        if (observed.Endpoint != settings.Endpoint || observed.Project != settings.Project || observed.PrincipalId != settings.PrincipalId)
            throw new ToolUsageException("Pinned operator evidence differs from parent endpoint/project/principal selection.");
        var creation = new CreationFrame(); lock (Creations) { if (Creations.Count >= 16) throw new ToolUsageException("Finite retained parent creation bound."); Creations.Add(creation); }
        try
        {
            creation.StoreEntered = true;
            var store = new FileCoordinationCheckpointStore(root); creation.Store = store; creation.StoreReturned = true;
            creation.TransportEntered = true;
            var wire = new ContinuumMcpTransport(settings.Endpoint, settings.Project, settings.PrincipalId,
                new ContinuumApiKeyFile(settings.PrivateKeyPath), evidence, TimeSpan.FromSeconds(10));
            creation.Transport = wire; creation.TransportReturned = true;
            var host = new ParentCoordinationHost(settings, life, wire, store); creation.Host = host; host.creation = creation;
            // Enroll the actual owner before any awaited network operation; failure keeps all originals reachable.
            Retain(host);
            try
            {
                var task = ContinuumCoordinationAdapter.CreateProductionAsync(wire,
                    new(host, host, store, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)), life.Token);
                host.coordinationTasks.Add(task);
                host.reducer = await task.WaitAsync(life.Remaining).ConfigureAwait(false); life.Check();
                return host;
            }
            catch (Exception error) { host.failure = error; host.Revoke(error); life.RetainUnknown(); throw; }
        }
        catch (Exception error) { creation.Failure = error; life.RetainUnknown(); throw; }
    }

    sealed class CreationFrame { internal bool StoreEntered, StoreReturned, TransportEntered, TransportReturned; internal FileCoordinationCheckpointStore? Store; internal ContinuumMcpTransport? Transport; internal ParentCoordinationHost? Host; internal Exception? Failure; }
    static readonly List<CreationFrame> Creations = [];
    CreationFrame? creation;
    static readonly List<ParentCoordinationHost> Originals = [];
    static void Retain(ParentCoordinationHost host) { lock (Originals) Originals.Add(host); }
    internal void BindBridge(OwnerHostBridge actual)
    {
        lock (gate) { if (bridge is not null || opened || closed) throw new ToolUsageException("Original parent bridge binds once."); bridge = actual; }
    }

    internal void BindSelection(FrozenPlan plan, FrozenWorkerPlan assignment, TrustedConfiguration config, ToolLifetime life)
    {
        lock (gate)
        {
            original.Check();
            if (!ReferenceEquals(life, original) || config.Coordination != selected || bridge is null || bound || closed || revoked ||
                plan.Identity != config.ExpectedBundleIdentity || plan.ContractIdentity != assignment.PlanIdentity ||
                plan.Repository != config.Repository || assignment.Repository != config.Repository || plan.WorkItem != config.ExpectedWorkItem ||
                string.IsNullOrWhiteSpace(assignment.RunId) || assignment.RunId.Length > 256)
                throw new ToolUsageException("Exact imported plan/configuration/original parent run binding required.");
            var repository = Regex.Match(plan.Repository, "^([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            var number = plan.WorkItem.StartsWith(plan.Repository + "#", StringComparison.Ordinal)
                ? plan.WorkItem[plan.Repository.Length..] : plan.WorkItem;
            if (!repository.Success || !Regex.IsMatch(number, "^#[1-9][0-9]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) ||
                !long.TryParse(number[1..], out var issue))
                throw new ToolUsageException("Exact independently selected repository and #issue required; no arbitrary scope/resource inference.");
            parentRunId = assignment.RunId;
            // Existing worker run grammar is run-<N>; wire run UUID is generated here and durably bound, never adopted from DATA.
            owner = new(selected.PrincipalId, Guid.NewGuid().ToString("D"));
            resource = CoordinationResource.GitHubIssue(selected.Project, repository.Groups[1].Value, repository.Groups[2].Value, issue);
            if (!plan.BaseReferenceExactBranchVerified || plan.BaseReferenceWasDetached || plan.BaseReference != config.MutableReference)
                throw new ToolUsageException("Parent cannot protect an unverified, detached or mismatched branch footprint.");
            var branch = plan.BaseReference.StartsWith("refs/heads/", StringComparison.Ordinal) ? plan.BaseReference : "refs/heads/" + plan.BaseReference;
            var workspace = BoundedFiles.Plain(assignment.Worktree, true).Replace('\\', '/');
            if (OperatingSystem.IsWindows()) workspace = workspace.ToUpperInvariant();
            var planBytes = plan.CanonicalBytes;
            if (planBytes.Length > 524288) throw new ToolUsageException("Bounded complete frozen footprint required.");
            using var document = JsonDocument.Parse(planBytes);
            var changes = document.RootElement.GetProperty("plan").GetProperty("changeMap");
            if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() is < 1 or > 128)
                throw new ToolUsageException("Complete finite frozen change footprint required.");
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var change in changes.EnumerateArray())
            {
                var path = change.GetProperty("path").GetString();
                if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\\') || path.StartsWith('/') || path.Split('/').Any(piece => piece is "" or "." or "..") || path.Any(char.IsControl))
                    throw new ToolUsageException("Unsupported noncanonical change footprint refused before Start.");
                paths.Add(path);
            }
            if (assignment.AllowedPaths.Any(path => !paths.Contains(path))) throw new ToolUsageException("Actual assigned paths exceed the protected frozen footprint.");
            var branchResource = CoordinationResource.GitHubBranch(selected.Project, repository.Groups[1].Value, repository.Groups[2].Value, branch);
            var workspaceResource = CoordinationResource.GitHubWorkspace(selected.Project, repository.Groups[1].Value, repository.Groups[2].Value, workspace);
            // Deliberately conservative: repository-write serializes ALL change-map paths, not only identical path hashes.
            var writeResource = CoordinationResource.GitHubRepositoryWrite(selected.Project, repository.Groups[1].Value, repository.Groups[2].Value);
            resources = Array.AsReadOnly(new CoordinationResource[] { resource!, branchResource, workspaceResource, writeResource });
            var footprintHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(paths.ToArray())));
            parentBinding = new(1, parentRunId, owner.RunId, plan.Identity, plan.ContractIdentity, plan.Repository, plan.WorkItem,
                resource!, Array.AsReadOnly(new[] { branchResource, workspaceResource, writeResource }), branch, workspaceResource.ResourceKey, footprintHash);
            bound = true;
        }
    }

    internal async Task AcquireAsync(string runId)
    {
        lock (gate)
        {
            original.Check();
            if (!bound || parentRunId != runId || reducer is null || acquiring || opened || closed || revoked)
                throw new ToolUsageException("Fresh exact parent selection required before acquisition; no retry.");
            acquiring = true;
        }
        try
        {
            var binding = checkpoints.BindParentAsync(parentBinding!, original.Token); lock (gate) coordinationTasks.Add(binding);
            await binding.WaitAsync(original.Remaining).ConfigureAwait(false); original.Check();
            var task = reducer!.AcquireAllAsync(resources!, owner!, ContinuumCoordinationAdapter.DefaultTtl, original.Token);
            lock (gate) coordinationTasks.Add(task);
            var result = await task.WaitAsync(original.Remaining).ConfigureAwait(false); original.Check();
            lock (gate)
            {
                releaseContinuation = result.ReleaseContinuation;
                if (result.Authority is null || revoked || closed) throw new CoordinationUncertainException("Parent acquisition did not establish current dispatch authority.");
                authority = result.Authority; fixtureObservedLeases = result.Snapshot.Leases.ToArray(); opened = true;
                renewal = RenewLoop();
            }
        }
        catch (Exception error) { Revoke(error); throw; }
    }

    // Outgoing immutable DATA for a privileged external-loss control, never
    // DispatchAuthority, private owner reconstruction or a production API.
    internal (string RunId, IReadOnlyList<LeaseEvidence> Leases, bool Revoked, Task<RenewalResult>? RenewalTask, IReadOnlyList<LeaseEvidence> RenewalInputs, Task? StopTask, ToolResult? StopResult, IReadOnlyList<ContinuumMcpTransport.ActualRenewalData> WireRenewals) ObserveContractFixtureData()
    {
        lock (gate)
        {
            if (bridge?.AdapterProfile.AdapterId != ParentNativeContractPort.AdapterId || bridge.Realm != HostObservationRealm.ContractFixture || parentRunId is null)
                throw new ToolUsageException("Closed native contract fixture owner required.");
            return (parentRunId, Array.AsReadOnly(fixtureObservedLeases.ToArray()), revoked, fixtureRenewalTask, Array.AsReadOnly(fixtureRenewalInputs.ToArray()), lossStop, fixtureStopResult, transport.ReadActualRenewalData());
        }
    }

    public void RetainUncertain(string runId, string reason) { lock (gate) { if (parentRunId != runId) throw new CoordinationUncertainException("Foreign effect uncertainty cannot bind this parent."); effects.RetainUncertain(runId, reason); } }
    public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) => effects.EnterAsync(runId, invoke);
    public T EnterSynchronous<T>(string runId, Func<T> invoke) => effects.EnterSynchronous(runId, () =>
    {
        try { return invoke(); }
        finally { if (original.HasExplicitUnknown) effects.RetainUncertain(runId, "Original retained an unknown resource during synchronous work."); }
    });
    public void CheckCurrent(string runId) => effects.CheckCurrent(runId);
    public void CheckCurrent(string runId, long existingStarted, TimeSpan existingWorkLimit) => effects.CheckCurrent(runId, existingStarted, existingWorkLimit);

    async Task RenewLoop()
    {
        try
        {
            // Same original clock/cancellation, finite iterations; no persistent timer/server or renewed budget.
            for (var iteration = 0; iteration < 20; iteration++)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), renewalStop.Token).ConfigureAwait(false);
                original.Check(); DispatchAuthority actual; ParentRenewalHandoff.Pending pending;
                lock (gate)
                {
                    if (closed || revoked || authority is null) return;
                    actual = authority;
                    fixtureRenewalInputs = fixtureObservedLeases.ToArray();
                    if (fixtureRenewalInputs.Count == 0) throw new CoordinationUncertainException("Original grants unavailable before renewal.");
                    // Close births before the reducer synchronously withdraws the old authority.
                    pending = renewalHandoff.Begin(parentRunId!, fixtureRenewalInputs.Min(lease => lease.SafeDeadline));
                }
                var task = reducer!.RenewAllAsync(actual, renewalStop.Token); lock (gate) { coordinationTasks.Add(task); fixtureRenewalTask = task; }
                var result = await task.WaitAsync(original.Remaining).ConfigureAwait(false); original.Check();
                // Cleanup evidence remains reachable even when a late handoff is refused.
                lock (gate) releaseContinuation = result.ReleaseContinuation;
                renewalHandoff.Install(pending, () =>
                {
                    if (result.Authority is null || closed || revoked || original.HasExplicitUnknown) throw new CoordinationUncertainException("Actual renewal did not return new current authority.");
                    authority = result.Authority; fixtureObservedLeases = result.Snapshot.Leases.ToArray();
                });
            }
            throw new TimeoutException("Finite original renewal iterations exhausted.");
        }
        catch (OperationCanceledException) when (renewalStop.IsCancellationRequested && closed) { }
        catch (Exception error) { Revoke(error); }
    }

    void Revoke(Exception error)
    {
        lock (gate)
        {
            failure ??= error; revoked = true;
            renewalHandoff.Revoke(error);
            // Admission closes before any stop task can run. Never discard an entered uncertain original.
            if (opened && !closed && bridge is not null && owner is not null && lossStop is null)
                lossStop = StopOwnedAfterRevocation(bridge, parentRunId!);
        }
    }
    async Task StopOwnedAfterRevocation(OwnerHostBridge actualBridge, string runId)
    {
        try { var result = await actualBridge.StopRetainedAsync(runId).ConfigureAwait(false); lock (gate) fixtureStopResult = result; }
        catch (Exception error) { lock (gate) failure ??= error; original.RetainUnknown(); }
    }

    public Task<QuiescenceEvidence> ObserveAsync(LeaseOwner requested, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (owner is null || owner != requested || bridge is null) throw new CoordinationUncertainException("No original parent owner for quiescence.");
            // Quiescence closes admission permanently for this single lifecycle, before the sample escapes the gate.
            revoked = true;
            renewalHandoff.Revoke(new CoordinationUncertainException("Original parent entered quiescence."));
            // Only this coordination observation is excluded; actual effects remain.
            var quiet = !original.HasExplicitUnknown && effects.AllKnownSettled &&
                bridge.ObserveCoordinationWorkSettled(parentRunId!);
            return Task.FromResult(new QuiescenceEvidence(owner, Binding.IssuerId, Binding.SourceId, DomainId,
                Elapsed, Guid.NewGuid().ToString("D"), quiet));
        }
    }

    internal async Task RetireSettledBatchNodeAsync(string runId)
    {
        // This successful forward transition is deliberately separate from
        // failure/final cleanup: CleanupRemaining permanently closes WORK.
        while (true)
        {
            effects.CheckCurrent(runId);
            lock (gate)
            {
                original.Check();
                if (renewalHandoff.Snapshot(runId) is not null) continue;
                if (parentRunId != runId || disposeEntered || closed || revoked || failure is not null || lossStop is not null ||
                    original.HasExplicitUnknown || reducer is null || authority is null || !reducer.MayDispatch(authority) ||
                    bridge is null || !bridge.ObserveCoordinationWorkSettled(runId) || !effects.AllKnownSettled)
                    throw new CoordinationUncertainException("Only this known-settled current original can release a completed batch node.");
                disposeEntered = true; closed = true;
                renewalHandoff.Revoke(new CoordinationUncertainException("Completed batch node permanently closed admission."));
                renewalStop.Cancel();
                break;
            }
        }
        try
        {
            if (renewal is not null) await renewal.WaitAsync(original.Remaining).ConfigureAwait(false);
            original.Check();
            lock (gate)
                if (coordinationTasks.Any(task => !task.IsCompletedSuccessfully) || !effects.AllKnownSettled || original.HasExplicitUnknown)
                    throw new CoordinationUncertainException("Completed node still has an unsettled original operation.");
            var releasing = reducer!.ReleaseAllAsync(authority!, original.Token); lock (gate) coordinationTasks.Add(releasing);
            var result = await releasing.WaitAsync(original.Remaining).ConfigureAwait(false); original.Check();
            if (result.Continuation is not null || result.Snapshot.Phase != CoordinationLifecyclePhase.Closed)
                throw new CoordinationUncertainException("Completed node resource release did not close its actual lifecycle.");
            transportClose = transport.DisposeAsync().AsTask(); await transportClose.WaitAsync(original.Remaining).ConfigureAwait(false); original.Check();
            checkpointClose = checkpoints.DisposeAsync().AsTask(); await checkpointClose.WaitAsync(original.Remaining).ConfigureAwait(false); original.Check();
            cancelled.Dispose(); renewalStop.Dispose(); disposed = true;
            lock (Originals) Originals.Remove(this);
            lock (Creations) if (creation is not null) Creations.Remove(creation);
        }
        catch (Exception error) { dispositionFailure = error; original.RetainUnknown(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposeEntered) { if (!disposed || dispositionFailure is not null) throw new CoordinationUncertainException("Original parent disposition remains nonpass; no repeat."); return; }
            disposeEntered = true; closed = true;
            renewalHandoff.Revoke(new CoordinationUncertainException("Original parent disposition closed admission."));
            renewalStop.Cancel();
        }
        try
        {
            if (renewal is not null) await renewal.WaitAsync(original.CleanupRemaining).ConfigureAwait(false);
            if (lossStop is not null) await lossStop.WaitAsync(original.CleanupRemaining).ConfigureAwait(false);
            lock (gate)
            {
                if (original.HasExplicitUnknown || coordinationTasks.Any(task => !task.IsCompletedSuccessfully) || !effects.AllKnownSettled)
                    throw new CoordinationUncertainException("Original effect/coordination task remains unsettled or failed; owners retained.");
            }
            Task<ReleaseResult>? releasing = null;
            if (releaseContinuation is not null) releasing = reducer!.ContinueReleaseAsync(releaseContinuation, CancellationToken.None);
            else if (authority is not null) releasing = reducer!.ReleaseAllAsync(authority, CancellationToken.None);
            if (releasing is not null)
            {
                lock (gate) coordinationTasks.Add(releasing);
                var result = await releasing.WaitAsync(original.CleanupRemaining).ConfigureAwait(false);
                if (result.Continuation is not null || result.Snapshot.Phase != CoordinationLifecyclePhase.Closed)
                    throw new CoordinationUncertainException("Actual original lease cleanup remains incomplete; owners retained.");
            }
            _ = original.CleanupRemaining;
            transportClose = transport.DisposeAsync().AsTask();
            await transportClose.WaitAsync(original.CleanupRemaining).ConfigureAwait(false);
            _ = original.CleanupRemaining;
            checkpointClose = checkpoints.DisposeAsync().AsTask();
            await checkpointClose.WaitAsync(original.CleanupRemaining).ConfigureAwait(false);
            _ = original.CleanupRemaining;
            cancelled.Dispose(); renewalStop.Dispose(); disposed = true;
            lock (Originals) Originals.Remove(this);
            lock (Creations) if (creation is not null) Creations.Remove(creation);
        }
        catch (Exception error) { dispositionFailure = error; original.RetainUnknown(); throw; }
    }

    sealed class PinnedOperatorEvidence(AdmissionFile exact, ToolLifetime life) : IContinuumDeploymentEvidenceSource
    {
        public Task<ContinuumDeploymentEvidence> ObserveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = InstalledHostAdmission.ReadExact(exact, life, 65536);
            _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(bytes); life.Check();
            var value = JsonSerializer.Deserialize<ContinuumDeploymentEvidence>(bytes, BoundedFiles.Json)
                ?? throw new ToolUsageException("Pinned operator deployment evidence is null."); life.Check();
            return Task.FromResult(value);
        }
    }
}
