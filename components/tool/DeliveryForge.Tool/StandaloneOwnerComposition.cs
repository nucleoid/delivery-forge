using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
namespace DeliveryForge.Tool;

// The trusted same-process root supplies an independently selected/conformed
// profile, not a JSON representation of an accepted session, writer or coordinator.
// The actual concrete native port is constructed here against the same owner.
// This concrete composition creates and retains the real existing owners.
public sealed record StandaloneHostSelection(string Executable, long Bytes, string Sha256, string Version);
public sealed class StandaloneOwnerComposition : IAsyncDisposable
{
    static readonly List<StandaloneOwnerComposition> Unknown = [];
    readonly ToolLifetime original; readonly ProcessControl processes; readonly ExecutionCoordinator coordinator; readonly OwnerHostBridge bridge; readonly TrustedConfiguration configuration; ParentCoordinationHost? coordination; Task? coordinationCreation, batchProcessClose, parentBatchRetirement, parentCleanup;
    bool coordinationEntered, disposeEntered, disposed, reviewCloseEntered, reviewClosed, coordinatorCloseEntered, coordinatorClosed, processCloseEntered, processCloseReturned;
    public ToolApplication Application { get; }
    public OwnerHostBridge ActualBridge => bridge;
    public StandaloneOwnerComposition(TrustedConfiguration config, string actualStoreRoot, LocalWorkerPaths workerPaths, ToolLifetime actualOriginal, StandaloneHostSelection selectedHost, AgentAdapterProfile actualConformedProfile, LinuxScopeSelection? selectedLinuxScope = null, AdmissionFile? actualConfigurationPin = null, StandaloneResources? actualResources = null)
        : this(config, actualStoreRoot, workerPaths, actualOriginal, selectedHost, actualConformedProfile, selectedLinuxScope, actualConfigurationPin, actualResources, null, null) { }
    internal StandaloneOwnerComposition(TrustedConfiguration config, string actualStoreRoot, LocalWorkerPaths workerPaths, ToolLifetime actualOriginal, StandaloneHostSelection selectedHost, AgentAdapterProfile actualConformedProfile, LinuxScopeSelection? selectedLinuxScope, AdmissionFile? actualConfigurationPin, StandaloneResources? actualResources, DeliveryForge.Scheduling.ResourceBudget? aggregateBudget, string? batchNodeId, InstalledHostAdmission.RuntimeTrust? runtimeTrust = null)
    {
        actualOriginal.Check(); original = actualOriginal; configuration = config;
        // Refusal-only DATA admission, before store inspection or any owner/dispatch.
        // The port still reserves and verifies the actual selected image at birth.
        if (OperatingSystem.IsLinux() && selectedHost is { } linuxHost &&
            (linuxHost.Bytes < 64 || linuxHost.Bytes > OwnedDuplexProcess.MaximumExactLinuxImageBytes))
            throw new ToolUsageException("INCOMPLETE: Selected Linux worker image must fit the 64-byte through eleven-MiB exact-image profile before dispatch.");
        RequireFreshExecutionStore(actualStoreRoot, actualOriginal);
        if ((aggregateBudget is null) != (batchNodeId is null)) throw new ToolUsageException("Batch node binding and shared budget must be supplied together.");
        ArgumentNullException.ThrowIfNull(actualConformedProfile); ArgumentNullException.ThrowIfNull(selectedHost); ArgumentNullException.ThrowIfNull(config);
        if (actualConformedProfile.AdapterId != config.Adapter || actualConformedProfile.AdapterVersion != config.AdapterVersion || selectedHost.Version != config.AdapterVersion) throw new ToolUsageException("Actual independently conformed version/profile differs from trusted selection.");
        var executor = OperatingSystem.IsLinux() ? new ProcessCommandExecutor(selectedLinuxScope ?? throw new ToolUsageException("Actual selected Linux scope profile required before native use."), actualOriginal) : new ProcessCommandExecutor();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed root supports chosen Windows/Linux profiles only.");
        var worktrees = new WorktreeManager(config.GitExecutable, executor, actualOriginal);
        // Construct each exact supported port HERE with this SAME worktree
        // manager/executor/original. An externally built legacy Git consumer
        // cannot be smuggled in through a port factory or session DTO.
        IHostToolSessionPort actualNativePort = config.Adapter switch
        {
            "codex.cli" => new DeliveryForge.Tool.Codex.CodexAppServerPort(new(selectedHost.Executable, selectedHost.Bytes, selectedHost.Sha256, selectedHost.Version), worktrees, actualConformedProfile, selectedLinuxScope, runtimeTrust),
            "claude-code.cli" => new DeliveryForge.Tool.Claude.ClaudeStreamPort(new(selectedHost.Executable, selectedHost.Bytes, selectedHost.Sha256, selectedHost.Version), worktrees, actualConformedProfile, selectedLinuxScope),
            "pi.cli" => new DeliveryForge.Tool.Pi.PiRpcPort(new(selectedHost.Executable, selectedHost.Bytes, selectedHost.Sha256, selectedHost.Version), worktrees, actualConformedProfile, selectedLinuxScope),
            _ => throw new ToolUsageException("Optional host has no actual standalone native transport.")
        };
        NamedHostAdapter adapter = config.Adapter switch { "codex.cli" => new CodexHostAdapter(actualNativePort), "claude-code.cli" => new ClaudeHostAdapter(actualNativePort), "pi.cli" => new PiHostAdapter(actualNativePort), _ => throw new ToolUsageException("No real standalone transport for selected optional host.") };
        processes = new ProcessControl(); var store = new RunStore(BoundedFiles.Plain(actualStoreRoot, true)); coordinator = new(store, worktrees, processes, adapter); bridge = new(coordinator, adapter, requireCoordination: true);
        if (config.LocalSingle is { } scheduling)
        {
            if (actualConfigurationPin is null || actualResources is null) throw new ToolUsageException("LocalSingle requires the independent exact configuration and actual measured caller owner.");
            var measured = actualResources.MeasureScheduling(actualOriginal);
            var budget = aggregateBudget ?? DeliveryForge.Scheduling.ResourceBudget.Measure(measured, scheduling.Policy);
            if (budget.PolicyIdentity != scheduling.Policy.Identity) throw new ToolUsageException("Batch node differs from its same-original aggregate policy.");
            if (scheduling.Demand.TimeLimit > actualOriginal.WorkLimit || scheduling.Policy.MaximumTime > actualOriginal.WorkLimit || !budget.CanAllocate(scheduling.Demand)) throw new ToolUsageException("Scheduling selection exceeds the same original measured resource/time policy.");
            bridge.BindLocalSingleFactory((plan, assignment, parent) =>
            {
                actualOriginal.Check();
                _ = InstalledHostAdmission.ReadExact(actualConfigurationPin, actualOriginal, 65536);
                var admitted = worktrees.WithParentAdmission(parent, assignment.RunId);
                var admittedStore = coordinator.BindOriginalScheduling(actualOriginal, parent, assignment.RunId, admitted);
                var reader = new DeliveryForge.Planning.GitRepositoryContextReader(config.GitExecutable, executor, actualOriginal).WithParentAdmission(parent, assignment.RunId);
                return batchNodeId is null
                    ? DeliveryForge.Scheduling.LocalSingleScheduler.Create(plan, assignment, scheduling.Demand, budget, admittedStore, coordinator, admitted, reader)
                    : DeliveryForge.Scheduling.LocalSingleScheduler.CreateCoordinatedNode(plan, assignment, scheduling.Demand, budget, admittedStore, coordinator, admitted, reader, batchNodeId);
            });
        }
        if (config.LocalRealGolden is not null && config.LocalSingle is null) throw new ToolUsageException("Local-real requires an independently approved finite LocalSingle policy; no direct execution fallback.");
        if (config.InstalledQuality is not null) bridge.BindInstalledQuality(new(config, actualConfigurationPin ?? throw new ToolUsageException("Containing quality requires the actual pinned root configuration."), actualOriginal, worktrees, selectedLinuxScope));
        if (config.HistoricalQuality is not null) bridge.BindHistoricalQuality(new(config, actualConfigurationPin ?? throw new ToolUsageException("Historical gates require the actual pinned root configuration."), actualOriginal, selectedLinuxScope));
        if (config.InstalledReview is not null) bridge.BindInstalledReview(new(config, actualConfigurationPin ?? throw new ToolUsageException("Containing review requires the actual pinned root configuration."), actualOriginal, executor, selectedLinuxScope, runtimeTrust));
        if (config.LocalRealGolden is { } golden)
        {
            BoundedFiles.Separate(actualStoreRoot, golden.ArtifactRoot);
            bridge.BindLocalRealGolden(new(config, actualConfigurationPin ?? throw new ToolUsageException("Local-real requires the actual pinned root configuration."), actualOriginal));
        }
        Application = new(bridge, assignments: new LocalWorkerAssignmentPort(actualOriginal, workerPaths), actualExecutor: executor, actualOriginal: actualOriginal, actualConfigurationPin: actualConfigurationPin);
        actualOriginal.Check();
    }
    internal static void RequireFreshExecutionStore(string actualStoreRoot, ToolLifetime original)
    {
        original.Check(); var root = BoundedFiles.Plain(actualStoreRoot, true);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("A new standalone owner requires a fresh execution store namespace; retained history is read-only recovery DATA, not a new live owner.");
        original.Check();
    }
    internal async Task InitializeCoordinationAsync()
    {
        original.Check(); if (coordinationEntered) throw new ToolUsageException("Original coordination initialization already entered; no retry."); coordinationEntered = true;
        if (configuration.Coordination is null) return; // Bridge itself refuses actual dispatch without the owner.
        var settings = configuration.Coordination;
        foreach (var path in new[] { settings.CheckpointRoot, settings.PrivateKeyPath, settings.DeploymentEvidence.Path })
        {
            BoundedFiles.Separate(configuration.RepositoryRoot, path);
        }
        var actual = ParentCoordinationHost.CreateAsync(settings, original); coordinationCreation = actual;
        try { coordination = await actual.WaitAsync(original.Remaining).ConfigureAwait(false); original.Check(); bridge.BindCoordination(coordination); }
        catch { if (!actual.IsCompleted) original.RetainUnknown(); throw; }
    }
    void CloseReviewOnce(bool cleanup)
    {
        if (!reviewCloseEntered)
        {
            // A deterministic refusal before first entry must leave cleanup's
            // first disposition available under the SAME original endpoint.
            if (!cleanup) original.Check();
            reviewCloseEntered = true;
            try { bridge.DisposeInstalledReviewWhenSettled(cleanup); }
            finally
            {
                // Only the actual host records callback return, before its
                // independent WORK postcheck. Never infer return from a throw.
                reviewClosed = bridge.InstalledReviewDispositionReturned;
                if (!reviewClosed && !bridge.InstalledReviewDispositionEntered) reviewCloseEntered = false;
            }
        }
        if (!reviewClosed) throw new IOException("Original review disposition did not return; no retry.");
    }
    void CloseCoordinatorOnce()
    {
        if (!coordinatorCloseEntered) { coordinatorCloseEntered = true; coordinator.Dispose(); coordinatorClosed = true; }
        if (!coordinatorClosed) throw new IOException("Original coordinator disposition did not return; no retry.");
    }
    async Task CloseProcessesAsync(bool cleanup)
    {
        if (!processCloseEntered) { processCloseEntered = true; batchProcessClose ??= processes.DisposeAsync().AsTask(); processCloseReturned = true; }
        if (!processCloseReturned || batchProcessClose is null) throw new IOException("Original process-controller close did not return its actual Task; no retry.");
        await ParentCoordinationHost.WaitActualDispositionTaskAsync(batchProcessClose, original, cleanup).ConfigureAwait(false);
    }
    async Task CompleteCleanupAsync()
    {
        _ = original.CleanupRemaining;
        if (coordinationCreation is not null && !coordinationCreation.IsCompleted || bridge.RetainedFrames.Any(frame => frame.Unknown) || original.HasExplicitUnknown)
            throw new IOException("Original composition retains unfinished or unknown work; owners retained.");
        CloseReviewOnce(true);
        if (coordination is not null && !coordination.DispositionKnownClosed)
        {
            // If no forward disposition entered, this is its first normal
            // cleanup. A known-closed parent is observed without another close.
            parentCleanup ??= coordination.DisposeAsync().AsTask();
            await ParentCoordinationHost.WaitActualDispositionTaskAsync(parentCleanup, original, true).ConfigureAwait(false);
        }
        if (original.HasRetainedUnknown) throw new IOException("Original coordination retained uncertainty; dependent native owners retained.");
        CloseCoordinatorOnce();
        await CloseProcessesAsync(true).ConfigureAwait(false);
        disposed = true;
        _ = original.CleanupRemaining;
    }
    void RetainFailedDisposition()
    {
        original.RetainUnknown(); lock (Unknown) if (!Unknown.Contains(this)) Unknown.Add(this);
    }
    internal async Task RetireSettledBatchNodeAsync(OwnerHostBridge.BatchNodeCompletion completion)
    {
        original.Check(); bridge.RequireActualBatchCompletion(completion, original);
        if (disposeEntered || coordination is null || coordinationCreation is not { IsCompletedSuccessfully: true })
            throw new IOException("Actual settled batch node cannot retire an uncreated or already disposed owner.");
        disposeEntered = true;
        try
        {
            CloseReviewOnce(false); original.Check();
            parentBatchRetirement = coordination.RetireSettledBatchNodeAsync(completion.RunId);
            // This concrete parent enforces WORK and SAME cleanup internally.
            // An outer WORK-only wait must not preempt its cleanup/quiescence.
            await parentBatchRetirement.ConfigureAwait(false);
            bridge.RequireActualBatchCompletion(completion, original);
            CloseCoordinatorOnce(); original.Check();
            await CloseProcessesAsync(false).ConfigureAwait(false); original.Check();
            disposed = true;
        }
        catch (Exception primary)
        {
            try { original.BeginFailureCleanup(); await CompleteCleanupAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                RetainFailedDisposition();
                throw new AggregateException("Completed node WORK retirement and SAME composition cleanup failure.", primary, cleanup);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposeEntered)
        {
            if (!disposed) throw new IOException("Original composition disposition remains nonpass; no retry.");
            return; // Known closure is observed, never replayed.
        }
        disposeEntered = true;
        Exception? primary = null, post = null;
        try { await CompleteCleanupAsync().ConfigureAwait(false); }
        catch (Exception error) { primary = error; RetainFailedDisposition(); }
        try { _ = original.CleanupRemaining; } catch (Exception error) { post = error; RetainFailedDisposition(); }
        if (primary is not null && post is not null) throw new AggregateException("Actual standalone first disposition and independent SAME original post.", primary, post);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
    }
}