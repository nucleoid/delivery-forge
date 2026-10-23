using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Planning;
using DeliveryForge.Scheduling;

namespace DeliveryForge.Tool;

// The installed root constructs all actual owners here. Selection/checkpoint
// DATA never supplies callbacks, accepted sessions, writers or lease authority.
internal sealed partial class ProductionBatchOwner(TrustedConfiguration containing, AdmissionFile containingPin,
    ToolLifetime original, StandaloneResources resources) : IAsyncDisposable
{
    sealed record Node(ProductionBatchNodeSelection Selection, TrustedConfiguration Configuration, StandaloneRootSelection Root,
        LinuxScopeSelection? Scope, FrozenPlan Plan, IReadOnlyDictionary<string, string> Prerequisites);
    readonly ProductionBatchSelection selected = CaptureSelection(containing);
    readonly ProductionBatchControl control = new();
    readonly object stateGate = new();
    readonly List<Task> actualTasks = [];
    readonly List<StandaloneOwnerComposition> actualOwners = [];
    readonly HashSet<StandaloneOwnerComposition> retired = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, string> completedHeads = new(StringComparer.Ordinal);
    StandaloneOwnerComposition? active;
    bool entered, disposed;

    static ProductionBatchSelection CaptureSelection(TrustedConfiguration config)
    {
        var value = config.ProductionBatch ?? throw new ToolUsageException("Explicit production batch required.");
        ValidateSelection(config, value);
        return value with
        {
            Nodes = Array.AsReadOnly(value.Nodes.Select(node => node with
            { Demand = node.Demand with { ExclusiveResources = Array.AsReadOnly(node.Demand.ExclusiveResources.ToArray()) } }).ToArray())
        };
    }
    void Retain(Task actual) { lock (stateGate) actualTasks.Add(actual); }

    internal static void ValidateSelection(TrustedConfiguration config, ProductionBatchSelection selection)
    {
        if (selection.Nodes is null || selection.Nodes.Count is < 2 or > 32 || selection.Policy is null || selection.Fairness is null ||
            !selection.Fairness.Approved || string.IsNullOrWhiteSpace(selection.Fairness.Identity) || selection.Fairness.Identity.Length > 256 ||
            selection.Fairness.MaximumBypasses is < 1 or > 1024 || config.HistoricalQuality is not null ||
            config.LocalSingle is not null || config.StandaloneRoot is not null || config.InstalledQuality is not null ||
            config.InstalledReview is not null || config.LocalRealGolden is not null)
            throw new ToolUsageException("Batch container requires bounded nodes and explicit aggregate policy, separate from leaf owner selections.");
        _ = BoundedFiles.Plain(selection.StoreRoot, true);
        if (selection.ControlPath is { } controlPath) ProductionBatchControlInput.ValidatePath(selection.StoreRoot, controlPath);
        var validation = ResourceBudget.Measure(selection.Policy.Caps, selection.Policy); // DATA validation, not measured capacity.
        var plans = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var configs = new HashSet<string>(plans.Comparer);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in selection.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.NodeId) || node.NodeId != node.NodeId.Trim() || node.NodeId.Length > 256 || node.NodeId.Any(char.IsControl) || !nodeIds.Add(node.NodeId) || node.Plan is null || node.Configuration is null || node.RiskClass is < 0 or > 100 ||
                !ValidPin(node.Plan, 524288) || !ValidPin(node.Configuration, 65536) || !validation.CanAllocate(node.Demand) ||
                !plans.Add(Path.GetFullPath(node.Plan.Path)) || !configs.Add(Path.GetFullPath(node.Configuration.Path)))
                throw new ToolUsageException("Batch nodes require unique finite immutable plan/configuration pins and policy-bounded demands.");
        }
    }
    static bool ValidPin(AdmissionFile pin, int maximum) => pin.Path is not null && Path.IsPathFullyQualified(pin.Path) &&
        pin.Bytes is > 0 && pin.Bytes <= maximum && pin.Sha256 is { Length: 64 } &&
        pin.Sha256.All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));
    static bool Same(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    async Task<T> Observe<T>(Task<T> actual)
    {
        Retain(actual);
        try { var value = actual.IsCompleted ? await actual.ConfigureAwait(false) : await actual.WaitAsync(original.Remaining).ConfigureAwait(false); original.Check(); return value; }
        finally { if (!actual.IsCompleted) original.RetainUnknown(); }
    }
    T Read<T>(AdmissionFile pin, int maximum)
    {
        var bytes = InstalledHostAdmission.ReadExact(pin, original, maximum);
        _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(bytes); original.Check();
        return JsonSerializer.Deserialize<T>(bytes, BoundedFiles.Json) ?? throw new IOException("Pinned batch input is null.");
    }
    internal async Task<ToolResult> RunAsync(ToolCommand command)
    {
        original.Check();
        if (entered) throw new ToolUsageException("Actual production batch already entered; no replay."); entered = true;
        ValidateSelection(containing, selected);
        if (command.Mode == ToolMode.Resume && command.Has("dry-run"))
        {
            if (command.Get("run-dir") is not string root || !Same(root, selected.StoreRoot)) throw new ToolUsageException("Batch history inspection requires its exact explicit store root.");
            return new("DRY_RUN", "BATCH_RECOVERY_DATA_ONLY", "Immutable queue data reconstructed; no ticket, session or lease was restored.", await ReadQueueDataAsync().ConfigureAwait(false));
        }
        if (command.Mode != ToolMode.Implement || command.Has("run-dir") || command.Has("result-dir") || command.Has("replay-out"))
            throw new ToolUsageException("Production batch uses implement and its explicitly pinned node destinations; ambiguous per-run overrides are refused.");
        if (command.Get("repo") is string guard && !Same(guard, containing.RepositoryRoot)) throw new ToolUsageException("Batch repository guard differs.");
        if (!command.Has("dry-run"))
        {
            StandaloneOwnerComposition.RequireFreshExecutionStore(selected.StoreRoot, original);
            if (selected.ControlPath is { } controlPath) ProductionBatchControlInput.RequireInitiallyAbsent(controlPath, original);
        }
        var anchor = selected.Nodes.SingleOrDefault(node => Same(node.Plan.Path, command.Get("plan")!))
            ?? throw new ToolUsageException("--plan must select one exact pinned batch anchor; it cannot select or replace batch members.");
        var measured = resources.MeasureScheduling(original);
        var budget = ResourceBudget.Measure(measured, selected.Policy);
        if (selected.Policy.MaximumTime > original.WorkLimit) throw new ToolUsageException("Aggregate policy exceeds the same original WORK endpoint.");
        var nodes = new List<Node>();
        foreach (var selection in selected.Nodes)
        {
            _ = InstalledHostAdmission.ReadExact(containingPin, original, 65536);
            var config = TrustedConfiguration.LoadSelected(selection.Configuration.Path, original, selection.Configuration);
            ValidateLeaf(containing, selected, selection, config, original);
            var root = Read<StandaloneRootSelection>(config.StandaloneRoot!, 65536);
            StandaloneEntrypoint.SeparateSelection(config, selection.Configuration.Path, root);
            if (!command.Has("dry-run")) StandaloneOwnerComposition.RequireFreshExecutionStore(root.StoreRoot, original);
            LinuxScopeSelection? scope = OperatingSystem.IsLinux()
                ? Read<LinuxScopeSelection>(root.LinuxScopeProfile ?? throw new ToolUsageException("Actual pinned Linux scope required for every batch node."), 65536) : null;
            ProcessCommandExecutor executor = scope is null ? new() : new(scope, original);
            var bytes = InstalledHostAdmission.ReadExact(selection.Plan, original, 524288);
            var imported = await Observe(PlanImporter.ImportAndReconcileOwnedAsync(bytes,
                new(config.RepositoryRoot, config.MutableReference, config.GitExecutable),
                new(config.ExpectedBundleIdentity!, config.ExpectedContractIdentity!, config.Repository, config.ExpectedWorkItem!, config.RepositoryRoot, config.MutableReference, config.ExpectedPlanCeiling!),
                Min(original.Remaining, TimeSpan.FromSeconds(120)), executor, original, cancellationToken: original.Token)).ConfigureAwait(false);
            if (imported.Plan is null || !imported.DownstreamReady)
                return ToolResult.Incomplete("BATCH_PLAN_RECONCILIATION_INCOMPLETE", "Every selected plan must freshly reconcile before batch creation; no host was created.", imported.Diagnostics);
            nodes.Add(new(selection, config, root, scope, imported.Plan, ReadFrozenPrerequisites(imported.Plan, selection.NodeId)));
        }
        var anchorNode = nodes.Single(node => ReferenceEquals(node.Selection, anchor));
        if (containing.ExpectedBundleIdentity != anchorNode.Plan.Identity || containing.ExpectedContractIdentity != anchorNode.Plan.ContractIdentity ||
            containing.ExpectedWorkItem != anchorNode.Plan.WorkItem || containing.ExpectedPlanCeiling != anchorNode.Configuration.ExpectedPlanCeiling)
            throw new ToolUsageException("Container independent expectations differ from the exact anchor plan.");
        ValidateNamespaces(nodes);
        var ids = nodes.Select(node => node.Selection.NodeId).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != nodes.Count || nodes.Select(node => node.Plan.WorkItem).Distinct(StringComparer.Ordinal).Count() != nodes.Count)
            throw new ToolUsageException("Batch work items and pinned DAG root identities must be unique.");
        foreach (var node in nodes) RefuseInternalPrerequisites(node.Prerequisites, ids);
        var queue = new ProductionBatchQueue(nodes.Select(node => new ProductionQueueNode(node.Selection.NodeId,
            Footprint(node), node.Selection.Demand, node.Selection.RiskClass,
            node.Prerequisites.Keys.Where(ids.Contains).ToArray())).ToArray(), selected.Fairness);
        if (command.Has("dry-run")) return new("DRY_RUN", "PRODUCTION_BATCH_PREPARED", "Pinned plans and bounded queue reconciled; no host, lease or worker was created.", new { Nodes = queue.Snapshot(), RuntimeCertified = false });
        // One immutable local journal selection; never a distributed lock or
        // ownership proof. A used destination requires recovery, never reset.
        var marker = Path.Combine(selected.StoreRoot, "batch-selection.json");
        var store = new RunStore(selected.StoreRoot); var runId = "batch-" + Guid.NewGuid().ToString("N");
        var selectionIdentity = SelectionIdentity(selected);
        var operatorInput = selected.ControlPath is { } inputPath
            ? new ProductionBatchControlInput(inputPath, runId, selectionIdentity, original, control) : null;
        var planIdentities = nodes.ToDictionary(node => node.Selection.NodeId, node => node.Plan.Identity, StringComparer.Ordinal);
        BoundedFiles.WriteNew(marker, JsonSerializer.SerializeToUtf8Bytes(new BatchMarker("ProductionBatchSelectionData-v1", runId, containingPin.Sha256, selectionIdentity), BoundedFiles.Json), original);
        await Persist().ConfigureAwait(false);
        if (operatorInput?.ReadBoundary() == true) await Persist().ConfigureAwait(false);
        var candidate = control.Paused || control.Stopped ? null : queue.SelectCandidate(budget);
        while (!control.Paused && !control.Stopped && candidate is not null)
        {
            if (operatorInput?.ReadBoundary() == true) await Persist().ConfigureAwait(false);
            if (control.Paused || control.Stopped) break;
            original.Check(); _ = InstalledHostAdmission.ReadExact(containingPin, original, 65536);
            var node = nodes.Single(item => item.Selection.NodeId == candidate.Id);
            if (!await IsIntegratedAsync(node).ConfigureAwait(false))
            {
                queue.RecordData(candidate.Id, "ReplanRequired");
                await Persist().ConfigureAwait(false);
                if (operatorInput?.ReadBoundary() == true) await Persist().ConfigureAwait(false);
                candidate = control.Paused || control.Stopped ? null : queue.SelectCandidate(budget);
                continue;
            }
            if (control.Paused || control.Stopped) break;
            await Persist().ConfigureAwait(false); // Durable selected intent before any owner/lease/worker creation.
            if (operatorInput?.ReadBoundary() == true) await Persist().ConfigureAwait(false);
            if (control.Paused || control.Stopped) break;
            var admission = InstalledHostAdmission.ReadRootAccepted(node.Root.InstalledHostAdmission, node.Configuration, original, node.Selection.Configuration);
            // Acknowledged input fences owner/coordination construction itself,
            // not merely the later native adapter start.
            if (operatorInput?.ReadBoundary() == true) await Persist().ConfigureAwait(false);
            if (control.Paused || control.Stopped) break;
            var actualOwner = new StandaloneOwnerComposition(node.Configuration, node.Root.StoreRoot, node.Root.WorkerPaths, original, admission.Host, admission.Profile,
                node.Scope, node.Selection.Configuration, resources, budget, node.Selection.NodeId, admission.TrustedRuntime);
            lock (stateGate) { active = actualOwner; actualOwners.Add(actualOwner); }
            actualOwner.ActualBridge.BindBatchControl(control);
            await Observe(InitializeNode(actualOwner)).ConfigureAwait(false);
            var options = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["plan"] = node.Selection.Plan.Path,
                ["config"] = node.Selection.Configuration.Path,
                ["json"] = null
            };
            _ = await Observe(actualOwner.Application.ExecuteAsync(new(ToolMode.Implement, options), original.Token)).ConfigureAwait(false);
            // Outcomes come from the exact actual owner frame, never from the
            // caller or a serialized ToolResult claiming successful work.
            var completion = await Observe(actualOwner.ActualBridge.ReadActualBatchCompletionAsync(original)).ConfigureAwait(false);
            completedHeads.Add(candidate.Id, completion.HeadCommit);
            queue.RecordData(candidate.Id, completion.Phase);
            await Persist().ConfigureAwait(false);
            // Read and persist controls before the queue is advanced. Selection
            // mutates DATA exactly once; the cached choice is used next turn.
            if (operatorInput?.ReadBoundary() == true) await Persist().ConfigureAwait(false);
            var following = control.Paused || control.Stopped ? null : queue.SelectCandidate(budget);
            // The final/pause/stopped owner goes to normal root cleanup. Never
            // enter cleanup and then request further WORK from this batch.
            if (following is null) break;
            var retiring = actualOwner.RetireSettledBatchNodeAsync(completion); Retain(retiring);
            try { await retiring.ConfigureAwait(false); }
            finally { if (!retiring.IsCompleted) original.RetainUnknown(); }
            lock (stateGate) { retired.Add(actualOwner); active = null; }
            candidate = following;
        }
        var snapshot = queue.Snapshot();
        return snapshot.Values.All(phase => phase == "Pass")
            ? new("COMPLETE", "COORDINATED_BATCH_LOCAL_COMPLETE", "Actual coordinated node pipelines completed; installed profile certification remains separate.", new { RunId = runId, Nodes = snapshot, SchedulingCertified = false })
            : ToolResult.Incomplete("COORDINATED_BATCH_BLOCKED", "Pending integration, nonpassing prerequisite, capacity, pause or stop prevents further node dispatch.", new { RunId = runId, Nodes = snapshot, control.Paused, control.Stopped });

        async Task Persist()
        {
            original.Check();
            await Observe(store.AppendAsync(runId, "production-batch-observation", new BatchObservation(1, containingPin.Sha256, selectionIdentity,
                selected.Policy.Identity, selected.Fairness.Identity, original.OriginalStarted, original.WholeLimit, original.WorkLimit,
                original.Remaining, planIdentities, queue.Snapshot(), new Dictionary<string, string>(completedHeads, StringComparer.Ordinal),
                control.Paused, control.Stopped), original.Token)).ConfigureAwait(false);
        }
    }
    static async Task<bool> InitializeNode(StandaloneOwnerComposition owner) { await owner.InitializeCoordinationAsync().ConfigureAwait(false); return true; }
    static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
    internal static void RefuseInternalPrerequisites(IReadOnlyDictionary<string, string> prerequisites, IReadOnlySet<string> selectedNodeIds)
    {
        if (prerequisites.Keys.Any(selectedNodeIds.Contains))
            throw new ToolUsageException("This batch profile accepts only independent selected roots and already-integrated external commit prerequisites; internal edges require integration and a newly frozen plan.");
    }
    internal static IReadOnlyDictionary<string, string> ReadFrozenPrerequisites(FrozenPlan plan, string nodeId) => LocalSingleScheduler.ReadCoordinatedPrerequisites(plan, nodeId);
    internal static void ValidateLeaf(TrustedConfiguration parent, ProductionBatchSelection batch, ProductionBatchNodeSelection selection, TrustedConfiguration leaf, ToolLifetime original)
    {
        if (leaf.ProductionBatch is not null || leaf.HistoricalQuality is not null || leaf.Coordination is null || leaf.StandaloneRoot is null ||
            leaf.LocalSingle is null || leaf.LocalSingle.Policy != batch.Policy || !SameDemand(leaf.LocalSingle.Demand, selection.Demand) ||
            leaf.OperationSeconds != parent.OperationSeconds || selection.Demand.TimeLimit != original.WorkLimit ||
            leaf.Repository != parent.Repository || !Same(leaf.RepositoryRoot, parent.RepositoryRoot) ||
            leaf.MutableReference != parent.MutableReference || !Same(leaf.GitExecutable, parent.GitExecutable) ||
            TrustedConfiguration.Ceiling(leaf.MaximumCeiling) > TrustedConfiguration.Ceiling(parent.MaximumCeiling) ||
            string.IsNullOrWhiteSpace(leaf.ExpectedBundleIdentity) || string.IsNullOrWhiteSpace(leaf.ExpectedContractIdentity) ||
            string.IsNullOrWhiteSpace(leaf.ExpectedWorkItem) || string.IsNullOrWhiteSpace(leaf.ExpectedPlanCeiling))
            throw new ToolUsageException("Batch leaf requires exact independent expectations, actual coordination, shared policy and the existing aggregate time profile; nesting is refused.");
    }
    internal static bool SameDemand(RunDemand? left, RunDemand? right) => left is not null && right is not null && left.Slots == right.Slots && left.TimeLimit == right.TimeLimit &&
        left.TokenLimit == right.TokenLimit && left.CostLimit == right.CostLimit && left.ExclusiveResources is not null && right.ExclusiveResources is not null && left.ExclusiveResources.SequenceEqual(right.ExclusiveResources, StringComparer.Ordinal);
    static ChangeFootprint Footprint(Node node)
    {
        using var document = JsonDocument.Parse(node.Plan.CanonicalBytes);
        var paths = document.RootElement.GetProperty("plan").GetProperty("changeMap").EnumerateArray().Select(item => item.GetProperty("path").GetString()!).ToArray();
        // Match the actual parent's deliberately conservative repository-write lease.
        return new(paths, ["repository-write:" + node.Configuration.Repository]);
    }
    async Task<bool> IsIntegratedAsync(Node node)
    {
        ProcessCommandExecutor executor = node.Scope is null ? new() : new(node.Scope, original);
        var reader = new GitRepositoryContextReader(node.Configuration.GitExecutable, executor, original);
        var context = await Observe(reader.ReadAsync(node.Configuration.RepositoryRoot, node.Configuration.MutableReference, original.Token)).ConfigureAwait(false);
        if (!node.Plan.ReconcileBase(context).DownstreamReady || context.Dirty || context.Shallow) return false;
        foreach (var dependency in node.Prerequisites)
            if (completedHeads.TryGetValue(dependency.Key, out var actualHead) && actualHead != dependency.Value ||
                !await Observe(reader.IsAncestorAsync(context, dependency.Value, original.Token)).ConfigureAwait(false)) return false;
        return true;
    }
    void ValidateNamespaces(IReadOnlyList<Node> nodes)
    {
        var destinations = new List<string> { selected.StoreRoot };
        foreach (var node in nodes)
        {
            destinations.AddRange([node.Root.StoreRoot, node.Root.WorkerPaths.Worktree, node.Root.WorkerPaths.ResultDirectory, node.Configuration.Coordination!.CheckpointRoot]);
            if (node.Configuration.InstalledQuality is { } quality) destinations.Add(quality.ArtifactRoot);
            if (node.Configuration.InstalledReview is { } review) destinations.Add(review.ArtifactRoot);
            if (node.Configuration.LocalRealGolden is { } golden) destinations.Add(golden.ArtifactRoot);
        }
        for (var index = 0; index < destinations.Count; index++)
        {
            BoundedFiles.Separate(containing.RepositoryRoot, destinations[index]);
            BoundedFiles.Separate(containingPin.Path, destinations[index]);
            for (var other = index + 1; other < destinations.Count; other++) BoundedFiles.Separate(destinations[index], destinations[other]);
            foreach (var node in nodes)
            {
                foreach (var input in ProtectedInputs(node)) BoundedFiles.Separate(destinations[index], input);
            }
        }
    }
    static IEnumerable<string> ProtectedInputs(Node node)
    {
        yield return node.Selection.Plan.Path; yield return node.Selection.Configuration.Path;
        yield return node.Configuration.StandaloneRoot!.Path; yield return node.Root.InstalledHostAdmission.Path;
        yield return node.Configuration.Coordination!.PrivateKeyPath; yield return node.Configuration.Coordination.DeploymentEvidence.Path;
        if (node.Root.LinuxScopeProfile is { } linux) yield return linux.Path;
        if (node.Configuration.PolicyFile is { } policy) yield return policy;
        if (node.Configuration.InstalledQuality is { } quality)
        {
            foreach (var gate in quality.Gates) yield return gate.Profile.Path;
            if (quality.Registry is { } registry) { yield return registry.OwnerApproval.Path; yield return registry.ArtifactRoot; }
        }
        if (node.Configuration.InstalledReview is { } review) yield return review.GitImage.Path;
    }
    // Restrictive latch only; actual retained owners still own physical cleanup.
    internal void StopNewDispatch() => control.Stop();
    internal void Pause() => control.Pause();
    internal async Task<ToolResult> StopAsync()
    {
        control.Stop();
        StandaloneOwnerComposition? owner; lock (stateGate) owner = active;
        var frames = owner?.ActualBridge.RetainedFrames;
        if (owner is null || frames is not { Count: 1 }) return ToolResult.Incomplete("BATCH_STOPPED", "No new batch node may start; no accepted node frame is available for native control.");
        var actual = owner.ActualBridge.StopRetainedAsync(frames[0].RunId); Retain(actual);
        try { return await actual.WaitAsync(original.CleanupRemaining).ConfigureAwait(false); }
        finally { if (!actual.IsCompleted) original.RetainUnknown(); }
    }
    public async ValueTask DisposeAsync()
    {
        StandaloneOwnerComposition[] remaining;
        lock (stateGate)
        {
            if (disposed) throw new IOException("Batch disposition already entered; no retry."); disposed = true;
            if (actualTasks.Any(task => !task.IsCompleted) || original.HasExplicitUnknown) { original.RetainUnknown(); return; }
            remaining = actualOwners.Where(owner => !retired.Contains(owner)).ToArray();
        }
        foreach (var owner in remaining) await owner.DisposeAsync().ConfigureAwait(false);
    }
}
