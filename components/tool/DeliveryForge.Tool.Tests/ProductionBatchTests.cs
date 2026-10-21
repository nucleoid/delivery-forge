using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Coordination;
using DeliveryForge.Planning;
using DeliveryForge.Scheduling;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// Queue/control DATA tests do not certify a real lease, worker or production run.
public sealed class ProductionBatchTests
{
    static ResourceSlots Slots => new(1, 1, 64, 1, 1, 1);
    static RunDemand Demand => new(Slots, [], TimeSpan.FromSeconds(1100), 10, 0);
    static BudgetPolicy Policy(long tokens = 100) => new("owner-policy", true, Slots, TimeSpan.FromSeconds(1100), tokens, 0);
    static FairnessPolicy Fairness => new("owner-fairness", true, 2);
    static ProductionQueueNode Node(string id, params string[] dependencies) => new(id, new([id + ".cs"], []), Demand, 0, dependencies);
    static AdmissionFile Pin(string name) => new(Path.Combine(Path.GetTempPath(), "batch-data-" + name), 1, new string('a', 64));
    static ProductionBatchSelection Selection() => new(Path.Combine(Path.GetTempPath(), "batch-data-store"), Policy(), Fairness,
        [new("a", Pin("a-plan"), Pin("a-config"), Demand), new("b", Pin("b-plan"), Pin("b-config"), Demand)]);

    [Theory]
    [InlineData("COMPLETE")]
    [InlineData("INCOMPLETE")]
    [InlineData("ERROR")]
    [InlineData("BLOCKED")]
    [InlineData("FAIL")]
    public void Quality_result_text_cannot_authorize_paid_review_without_actual_private_same_frame_completion(string outcome)
    {
        var copied = new ToolResult(outcome, "PROTECTED_QUALITY_PASSED_INDEPENDENT_REVIEW_REQUIRED", "Caller claims quality passed");
        Assert.False(OwnerHostBridge.MayEnterIndependentReviewAfterQuality(copied, null, null));
    }

    [Fact]
    public void Quality_absent_route_keeps_its_existing_independent_review_requirement()
    {
        // This predicate grants no review authority; the actual bridge still
        // requires its completed native author, parent and configured reviewer.
        Assert.True(OwnerHostBridge.MayEnterIndependentReviewAfterQuality(null, null, null));
    }

    [Theory]
    [InlineData("Fail")]
    [InlineData("Incomplete")]
    [InlineData("LocalComplete")]
    public void Nonpassing_prerequisite_blocks_dependent_while_independent_candidate_progresses(string phase)
    {
        var queue = new ProductionBatchQueue([Node("a"), Node("b", "a"), Node("c")], Fairness);
        var budget = ResourceBudget.Measure(Slots, Policy());
        Assert.Equal("a", queue.SelectCandidate(budget)!.Id);
        queue.RecordData("a", phase);
        Assert.Equal("c", queue.SelectCandidate(budget)!.Id);
        queue.RecordData("c", "Pass");
        Assert.Null(queue.SelectCandidate(budget));
        Assert.Equal("Pending", queue.Snapshot()["b"]);
    }

    [Fact]
    public void Selection_is_not_a_second_allocation_and_releasing_slots_does_not_refill_aggregate_tokens()
    {
        var queue = new ProductionBatchQueue([Node("a"), Node("b")], Fairness);
        var budget = ResourceBudget.Measure(Slots, Policy(tokens: 10));
        var candidate = queue.SelectCandidate(budget)!;
        Assert.True(budget.CanAllocate(candidate.Demand));
        using (budget.Allocate(candidate.Demand)) { Assert.False(budget.CanAllocate(candidate.Demand)); Assert.Null(queue.SelectCandidate(budget)); }
        queue.RecordData(candidate.Id, "Pass");
        Assert.Null(queue.SelectCandidate(budget));
        Assert.Equal("Pending", queue.Snapshot()["b"]);
    }

    [Fact]
    public async Task Pause_after_selection_fences_actual_invocation_and_preserves_existing_task_identity()
    {
        var queue = new ProductionBatchQueue([Node("a"), Node("b")], Fairness);
        Assert.NotNull(queue.SelectCandidate(ResourceBudget.Measure(Slots, Policy())));
        var control = new ProductionBatchControl(); var calls = 0;
        var actual = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Same(actual.Task, control.EnterNodeStart(() => { calls++; return actual.Task; }));
        control.Pause();
        Assert.Throws<ToolUsageException>(() => { _ = control.EnterNodeStart(() => { calls++; return Task.FromResult(2); }); });
        Assert.Equal(1, calls); Assert.False(actual.Task.IsCompleted);
        actual.SetResult(1); Assert.Equal(1, await actual.Task);
        control.Stop(); Assert.True(control.Stopped);
    }

    [Fact]
    public void Aggregate_queue_cycle_is_rejected_with_its_actual_chain()
    {
        var error = Assert.Throws<SchedulingException>(() => new ProductionBatchQueue([Node("a", "b"), Node("b", "a")], Fairness));
        Assert.Contains("a -> b -> a", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("node-clock")]
    [InlineData("operation-clock")]
    [InlineData("missing-coordination")]
    [InlineData("nested-batch")]
    public void Leaf_cannot_shrink_or_replace_the_shared_original_profile_or_ignore_real_coordination(string change)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(1200), TestContext.Current.CancellationToken);
        var selected = Selection(); var node = selected.Nodes[0];
        var repo = Path.GetTempPath(); var git = Path.Combine(repo, "explicit-unused-git");
        var parent = new TrustedConfiguration("fixture/repo", repo, "refs/heads/main", git, MaximumCeiling: "implement", OperationSeconds: 1200, ProductionBatch: selected);
        var leaf = parent with
        {
            ProductionBatch = null,
            StandaloneRoot = Pin("root"),
            ExpectedBundleIdentity = "sha256:" + new string('b', 64),
            ExpectedContractIdentity = "sha256:" + new string('c', 64),
            ExpectedWorkItem = "#1",
            ExpectedPlanCeiling = "implement",
            Coordination = new(new Uri("https://example.invalid/mcp"), "project", "principal", Path.Combine(repo, "key"), Path.Combine(repo, "checkpoint"), Pin("deployment")),
            LocalSingle = new(selected.Policy, node.Demand)
        };
        // A separate JSON parse has different array objects. Demand equality is
        // structural; no identity/approval is inferred from record reference equality.
        var parsed = JsonSerializer.Deserialize<TrustedConfiguration>(JsonSerializer.SerializeToUtf8Bytes(leaf, BoundedFiles.Json), BoundedFiles.Json)!;
        ProductionBatchOwner.ValidateLeaf(parent, selected, node, parsed, original);
        switch (change)
        {
            case "node-clock": node = node with { Demand = node.Demand with { TimeLimit = TimeSpan.FromSeconds(1099) } }; parsed = parsed with { LocalSingle = new(selected.Policy, node.Demand) }; break;
            case "operation-clock": parsed = parsed with { OperationSeconds = 1199 }; break;
            case "missing-coordination": parsed = parsed with { Coordination = null }; break;
            default: parsed = parsed with { ProductionBatch = selected }; break;
        }
        Assert.Throws<ToolUsageException>(() => ProductionBatchOwner.ValidateLeaf(parent, selected, node, parsed, original));
        original.Check(); Assert.False(original.HasExplicitUnknown);
    }

    [Fact]
    public void Historical_terminal_data_never_refills_or_rewrites_original_budget_and_outcomes()
    {
        var selected = Selection(); var plans = new Dictionary<string, string> { ["a"] = "sha256:" + new string('a', 64), ["b"] = "sha256:" + new string('b', 64) };
        var phases = new Dictionary<string, string> { ["a"] = "Pending", ["b"] = "Pending" };
        var identity = ProductionBatchOwner.SelectionIdentity(selected);
        var initial = new BatchObservation(1, new string('a', 64), identity, selected.Policy.Identity, selected.Fairness.Identity,
            123, TimeSpan.FromSeconds(1200), TimeSpan.FromSeconds(1100), TimeSpan.FromSeconds(1090), plans, phases, new Dictionary<string, string>(), false, false);
        var selectedA = initial with { ObservedWorkRemaining = TimeSpan.FromSeconds(1080), Phases = new Dictionary<string, string> { ["a"] = "Selected", ["b"] = "Pending" } };
        var completed = selectedA with { ObservedWorkRemaining = TimeSpan.FromSeconds(1070), Phases = new Dictionary<string, string> { ["a"] = "Pass", ["b"] = "Pending" }, CompletedHeads = new Dictionary<string, string> { ["a"] = new string('c', 40) } };
        ProductionBatchOwner.ValidateHistory([initial, selectedA, completed], initial.ConfigurationSha256, identity, selected, plans);
        Assert.Throws<IOException>(() => ProductionBatchOwner.ValidateHistory([initial, selectedA, completed with { OriginalStarted = 124 }], initial.ConfigurationSha256, identity, selected, plans));
        Assert.Throws<IOException>(() => ProductionBatchOwner.ValidateHistory([initial, selectedA, completed with { ObservedWorkRemaining = TimeSpan.FromSeconds(1099) }], initial.ConfigurationSha256, identity, selected, plans));
        Assert.Throws<IOException>(() => ProductionBatchOwner.ValidateHistory([initial, selectedA, completed, completed with { Phases = phases }], initial.ConfigurationSha256, identity, selected, plans));
        var data = new ReconstructedBatchData("batch-" + new string('1', 32), completed.Phases, plans, selected.Policy.Identity, false, false, ["DATA only"]);
        Assert.False(data.MayDispatch);
    }

    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_default_entrypoint_refuses_reused_execution_store_before_missing_host_admission_or_plan_read(bool batchContainer)
    {
        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-store-reuse-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repository"); var store = Path.Combine(root, "prior-store");
        Directory.CreateDirectory(repo); Directory.CreateDirectory(store);
        var sentinel = Path.Combine(store, "retained-history.txt"); await File.WriteAllTextAsync(sentinel, "retained original bytes", TestContext.Current.CancellationToken);
        var selection = new StandaloneRootSelection("DeliveryForgeStandaloneRoot-v1", store,
            new(Path.Combine(root, "work"), Path.Combine(root, "result"), ["source.cs"], []), Pin("must-not-read-admission"));
        var rootBytes = JsonSerializer.SerializeToUtf8Bytes(selection, BoundedFiles.Json); var rootPath = Path.Combine(root, "root.json"); File.WriteAllBytes(rootPath, rootBytes);
        var config = new TrustedConfiguration("fixture/repo", repo, "refs/heads/main", Environment.ProcessPath ?? throw new IOException("Current host image path unavailable."),
            MaximumCeiling: "implement", OperationSeconds: 20, StandaloneRoot: new(rootPath, rootBytes.LongLength, BoundedFiles.Sha(rootBytes)));
        var commandPlan = Path.Combine(root, "must-not-read-plan");
        if (batchContainer)
        {
            var selectedBatch = Selection() with { StoreRoot = store };
            config = config with { StandaloneRoot = null, ProductionBatch = selectedBatch };
            commandPlan = selectedBatch.Nodes[0].Plan.Path;
        }
        var configPath = Path.Combine(root, "config.json"); File.WriteAllBytes(configPath, JsonSerializer.SerializeToUtf8Bytes(config, BoundedFiles.Json));
        var beforeOut = Console.Out; var beforeError = Console.Error; using var output = new StringWriter(); using var error = new StringWriter();
        Console.SetOut(output); Console.SetError(error);
        try
        {
            Assert.Equal(1, await Program.Main(["implement", "--plan", commandPlan, "--config", configPath, "--json"])
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.Empty(output.ToString()); Assert.Contains("fresh execution store namespace", error.ToString(), StringComparison.Ordinal);
            Assert.Equal("retained original bytes", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
            Assert.Single(Directory.EnumerateFileSystemEntries(store)); Assert.False(Directory.Exists(selection.WorkerPaths.Worktree));
        }
        finally { Console.SetOut(beforeOut); Console.SetError(beforeError); }
    }

    [Fact(Timeout = 150000)]
    public async Task Actual_Git_issued_frozen_plan_uses_pinned_arbitrary_root_and_only_its_semantic_prerequisites()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-batch-dag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); // Retain the real fixture and failure evidence.
        var gitName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, gitName)).First(File.Exists);
        var executor = new ProcessCommandExecutor();
        async Task Git(params string[] arguments)
        {
            var nullPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var argv = new[] { "--no-replace-objects", "-c", "core.hooksPath=" + nullPath, "-c", "core.fsmonitor=false", "-c", "user.name=BatchFixture", "-c", "user.email=fixture@example.invalid" }.Concat(arguments).ToArray();
            var actual = await executor.ExecuteAsync(new(git, argv, root, new Dictionary<string, string>
            {
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = nullPath,
                ["GIT_CONFIG_SYSTEM"] = nullPath,
                ["GIT_NO_REPLACE_OBJECTS"] = "1",
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_OPTIONAL_LOCKS"] = "0"
            }), original.Remaining, original.Token);
            original.Check(); Assert.True(actual.OwnedProcessQuiescent && actual.ExitCode == 0 && !actual.TimedOut && !actual.Cancelled, root + ": " + actual.StandardError);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "actual repository evidence\n", original.Token);
        await Git("init", "-q", "-b", "main"); await Git("add", "--", "tracked.txt"); await Git("commit", "-q", "-m", "actual batch root");
        var reader = new GitRepositoryContextReader(git);
        var context = await reader.ReadAsync(root, "refs/heads/main", original.Token);
        var file = await reader.ReadFileAsync(context, "tracked.txt", original.Token);
        var now = DateTimeOffset.UtcNow;
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, now, [], EvidenceRequirement.Required)];
        var request = new PlanningRequest("fixture/batch", "#1", "implement", "Bind real semantic dependency data", ["tracked.txt"], ["outside"], ["Actual pinned root differs from issue identity"], "implement");
        var frozen = PlanFreezer.Freeze(new(request, context, evidence, [new("tracked.txt", "content", "update")],
            [new("selected-root", ["required"], "Root work itself does not require a fabricated integration commit"),
             new("required", [], "commit:" + context.Commit), new("unrelated", [], "Independent work, not a semantic dependency")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", ["inspect diagnostics"]), [], IntakePlanner.Assess(request, evidence)), "actual-batch-dag", now);
        Assert.True(frozen.DownstreamReady, root); Assert.NotEqual("selected-root", frozen.WorkItem);
        var dependencies = ProductionBatchOwner.ReadFrozenPrerequisites(frozen, "selected-root");
        Assert.Equal(context.Commit, Assert.Single(dependencies).Value); Assert.True(dependencies.ContainsKey("required"));
        Assert.Empty(ProductionBatchOwner.ReadFrozenPrerequisites(frozen, "unrelated"));
        // The actual frozen prerequisite is already integrated, but selecting it
        // as another batch root cannot promise future integration/replanning.
        Assert.Throws<ToolUsageException>(() => ProductionBatchOwner.RefuseInternalPrerequisites(dependencies,
            new HashSet<string>(["selected-root", "required"], StringComparer.Ordinal)));
        ProductionBatchOwner.RefuseInternalPrerequisites(dependencies,
            new HashSet<string>(["selected-root", "unrelated"], StringComparer.Ordinal));
        ProductionBatchOwner.RefuseInternalPrerequisites(ProductionBatchOwner.ReadFrozenPrerequisites(frozen, "unrelated"),
            new HashSet<string>(["selected-root", "unrelated"], StringComparer.Ordinal));
        Assert.Throws<SchedulingException>(() => ProductionBatchOwner.ReadFrozenPrerequisites(frozen, "absent"));
        var actualSource = new GitRepositoryContextReader(git, executor, original); var fixtureParent = new ReaderParentFixture();
        var admitted = actualSource.WithParentAdmission(fixtureParent, "batch-reader-binding-fixture");
        Assert.Throws<PlanningException>(() => actualSource.WithParentAdmission(fixtureParent, "another-run"));
        await Assert.ThrowsAsync<PlanningException>(() => actualSource.ReadAsync(root, "refs/heads/main", original.Token));
        Assert.Equal(0, fixtureParent.Entries);
        Assert.NotSame(actualSource, admitted);
        // No owned native birth is authorized by this public binding control.
        // The explicit installed two-node case exercises the admitted reader's
        // real positive path with its Root-selected Linux scope where required.
        original.Check();
    }
    sealed class ReaderParentFixture : ICoordinatedEffectAdmission
    {
        internal int Entries;
        public void CheckCurrent(string runId) { }
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) { Entries++; return invoke(); }
        public void RetainUncertain(string runId, string reason) => throw new IOException(reason);
    }
}
