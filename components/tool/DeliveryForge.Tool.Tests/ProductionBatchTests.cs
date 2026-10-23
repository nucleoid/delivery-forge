using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
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

    public static bool IsLinux => OperatingSystem.IsLinux();

    // Actual default batch route. Both leaf admissions are selected before
    // reading either absent plan; no valid host/scope or Root authority exists.
    [Theory(Timeout = 30000, Skip = "Linux batch extent admission requires Linux.", SkipUnless = nameof(IsLinux))]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task Linux_batch_admits_all_worker_extents_before_any_plan_or_durable_selection(int unsupportedNode)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-batch-extent-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repo"); Directory.CreateDirectory(repository);
        AdmissionFile Write<T>(string name, T value)
        {
            var path = Path.Combine(root, name); var raw = JsonSerializer.SerializeToUtf8Bytes(value, BoundedFiles.Json);
            File.WriteAllBytes(path, raw); return new(path, raw.LongLength, BoundedFiles.Sha(raw));
        }
        var policy = Policy() with { MaximumTime = TimeSpan.FromSeconds(15) };
        var demand = Demand with { TimeLimit = TimeSpan.FromSeconds(15) };
        var nodes = new List<ProductionBatchNodeSelection>(); var stores = new List<string>();
        for (var index = 0; index < 2; index++)
        {
            var prefix = "node" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var absent = Path.Combine(root, prefix + "-absent-worker");
            var host = Write(prefix + "-host.json", new InstalledHostRootAcceptance("UNVERIFIED-REFUSAL-DATA", "linux", "codex.cli", "fixture",
                new(absent, index == unsupportedNode ? OwnedDuplexProcess.MaximumExactLinuxImageBytes + 1 : 64, new string('a', 64), "fixture"),
                new(absent + "-tool", 1, new string('b', 64)), [], []));
            var store = Path.Combine(root, prefix + "-store"); stores.Add(store);
            var selectedRoot = Write(prefix + "-root.json", new StandaloneRootSelection("DeliveryForgeStandaloneRoot-v1", store,
                new(Path.Combine(root, prefix + "-work"), Path.Combine(root, prefix + "-results"), ["source.cs"], []), host));
            var leaf = new TrustedConfiguration("fixture/repo", repository, "refs/heads/main", Path.Combine(root, "absent-git"),
                AdapterVersion: "fixture", MaximumCeiling: "implement", OperationSeconds: 20,
                ExpectedBundleIdentity: "sha256:" + new string('b', 64), ExpectedContractIdentity: "sha256:" + new string('c', 64), ExpectedWorkItem: "#" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ExpectedPlanCeiling: "implement",
                StandaloneRoot: selectedRoot, Coordination: new(new Uri("https://example.invalid/mcp"), "fixture", "principal", Path.Combine(root, "absent-key"), Path.Combine(root, prefix + "-checkpoint"), Pin("absent-deployment")), LocalSingle: new(policy, demand));
            nodes.Add(new(prefix, new(Path.Combine(root, prefix + "-absent-plan"), 1, new string('a', 64)), Write(prefix + "-config.json", leaf), demand));
        }
        var storeRoot = Path.Combine(root, "batch-store");
        var batch = new ProductionBatchSelection(storeRoot, policy, Fairness, nodes);
        var containing = Write("containing.json", new TrustedConfiguration("fixture/repo", repository, "refs/heads/main", Path.Combine(root, "absent-git"),
            MaximumCeiling: "implement", OperationSeconds: 20, ProductionBatch: batch));
        var beforeOut = Console.Out; var beforeError = Console.Error; using var output = new StringWriter(); using var errors = new StringWriter();
        Console.SetOut(output); Console.SetError(errors);
        try
        {
            Assert.Equal(1, await Program.Main(["implement", "--plan", nodes[0].Plan.Path, "--config", containing.Path, "--json"])
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.Empty(output.ToString());
            var expected = unsupportedNode < 0 ? "Actual pinned Linux scope required for every batch node." : "Selected Linux worker image must fit the 64-byte through eleven-MiB exact-image profile before dispatch.";
            Assert.Contains(expected, errors.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(storeRoot)); Assert.All(stores, store => Assert.False(Directory.Exists(store)));
            Assert.All(nodes, node => Assert.False(File.Exists(node.Plan.Path)));
            Assert.False(File.Exists(Path.Combine(storeRoot, "batch-selection.json")));
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

    [Theory(Timeout = 150000)]
    [InlineData("implement", true, false)]
    [InlineData("plan", false, false)]
    [InlineData("plan", false, true)]
    public async Task Actual_CLI_plan_preserves_explicit_intent_and_only_implement_artifact_is_schedulable(string mode, bool schedulable, bool fromIssue)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "forge-cli-plan-mode-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "parent"); Directory.CreateDirectory(repository);
        var gitName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, gitName)).First(File.Exists);
        var executor = new ProcessCommandExecutor();
        async Task<string> Git(params string[] arguments)
        {
            var nullPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var argv = new[] { "--no-replace-objects", "-c", "core.hooksPath=" + nullPath, "-c", "core.fsmonitor=false", "-c", "user.name=PlanFixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false" }.Concat(arguments).ToArray();
            var actual = await executor.ExecuteAsync(new(git, argv, repository, new Dictionary<string, string>
            {
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = nullPath,
                ["GIT_CONFIG_SYSTEM"] = nullPath,
                ["GIT_NO_REPLACE_OBJECTS"] = "1",
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_OPTIONAL_LOCKS"] = "0"
            }), original.Remaining, original.Token);
            original.Check(); Assert.True(actual.OwnedProcessQuiescent && actual.ExitCode == 0 && !actual.TimedOut && !actual.Cancelled, root + ": " + actual.StandardError);
            return actual.StandardOutput;
        }
        await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "actual unchanged planning evidence\n", original.Token);
        await Git("init", "-q", "-b", "main"); await Git("add", "--", "tracked.txt"); await Git("commit", "-q", "-m", "actual CLI planning base");
        var request = new PlanningRequest("fixture/cli-plan", "#1", mode, "Update the one tracked file", ["tracked.txt"], [], ["Only tracked.txt changes"], "implement");
        var intent = new PlanIntent(request, ["tracked.txt"], [new("tracked.txt", "content", "update")], [new("#1", [], "root")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], "cli-plan-mode");
        var configPath = Path.Combine(root, "config.json"); var intentPath = Path.Combine(root, "intent.json"); var outputPath = Path.Combine(root, "frozen-plan.json");
        await File.WriteAllBytesAsync(configPath, JsonSerializer.SerializeToUtf8Bytes(new TrustedConfiguration(request.Repository, repository, "refs/heads/main", git, MaximumCeiling: "implement", OperationSeconds: 120), BoundedFiles.Json), original.Token);
        await File.WriteAllBytesAsync(intentPath, JsonSerializer.SerializeToUtf8Bytes(intent, BoundedFiles.Json), original.Token);
        var issue = fromIssue ? new PlanIssueFixture(intent) : null;
        var application = new ToolApplication(issues: issue, actualOriginal: original);
        var selection = fromIssue ? new[] { "--issue", "fixture/cli-plan#1" } : new[] { "--intent-file", intentPath };
        var result = await application.ExecuteAsync(CommandLine.Parse(new[] { "plan", "--repo", repository }.Concat(selection).Concat(["--config", configPath, "--plan-out", outputPath]).ToArray()), original.Token);
        Assert.Equal(fromIssue ? 1 : 0, issue?.Calls ?? 0);
        Assert.Equal("COMPLETE", result.Outcome); Assert.Equal("PLAN_FROZEN", result.Code); Assert.False(result.RuntimeCertified);
        var bytes = await File.ReadAllBytesAsync(outputPath, original.Token);
        using var json = JsonDocument.Parse(bytes); var envelope = json.RootElement; var material = envelope.GetProperty("plan");
        Assert.Equal(mode, material.GetProperty("mode").GetString()); Assert.Equal("implement", material.GetProperty("requestedCeiling").GetString());
        var imported = await PlanImporter.ImportAndReconcileAsync(bytes, new(repository, "refs/heads/main", git),
            new(envelope.GetProperty("identity").GetString()!, envelope.GetProperty("contractIdentity").GetString()!, request.Repository, "#1", repository, "refs/heads/main", "implement"),
            original.Remaining, cancellationToken: original.Token);
        Assert.True(imported.DownstreamReady, string.Join("; ", imported.Diagnostics)); var frozen = Assert.IsType<FrozenPlan>(imported.Plan);
        using var contract = JsonDocument.Parse(frozen.PlanContractBytes); Assert.Equal(mode, contract.RootElement.GetProperty("mode").GetString());
        var work = Path.Combine(root, "assigned"); var results = Path.Combine(root, "results");
        var worker = new FrozenWorkerPlan("cli-mode-worker", frozen.ContractIdentity, frozen.BaseCommit, frozen.Repository, repository, work, results,
            "fixture-writer", ["tracked.txt"], [], "codex.cli", "fixture", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
        var store = new RunStore(Path.Combine(root, "store")); var worktrees = new WorktreeManager(git);
        await using var processes = new ProcessControl(); using var coordinator = new ExecutionCoordinator(store, worktrees, processes);
        var slots = new ResourceSlots(1, 1, 64, 0, 0, 0); var budget = ResourceBudget.Measure(slots, new("cli-mode-fixture", true, slots, TimeSpan.FromSeconds(60), 1, 0));
        LocalSingleScheduler Create() => LocalSingleScheduler.Create(frozen, worker, new(slots, [], TimeSpan.FromSeconds(60), 1, 0), budget, store, coordinator, worktrees, new GitRepositoryContextReader(git));
        if (schedulable) Assert.NotNull(Create()); else Assert.Throws<SchedulingException>(() => Create());
        Assert.Empty(await Git("status", "--porcelain=v1", "--untracked-files=all"));
        Assert.Equal("actual unchanged planning evidence\n", await File.ReadAllTextAsync(Path.Combine(repository, "tracked.txt"), original.Token));
        Assert.False(Directory.Exists(work)); Assert.False(Directory.Exists(results)); original.Check();
    }

    [Theory]
    [InlineData("implement", "plan", "implement")]
    [InlineData("implement", "implement", "plan")]
    [InlineData("implement", "pr", "implement")]
    [InlineData("pr", "pr", "pr")]
    [InlineData("merge", "merge", "merge")]
    [InlineData("resume", "implement", "implement")]
    public async Task CLI_plan_invalid_or_untrusted_execution_intent_refuses_before_Git_or_output(string mode, string requested, string maximum)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-cli-plan-refusal-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "parent"); Directory.CreateDirectory(repository);
        var request = new PlanningRequest("fixture/cli-plan", "#1", mode, "Refused intent", ["source.cs"], [], ["No effects"], requested);
        var intent = new PlanIntent(request, ["source.cs"], [], [], [], new("none", "none", "none", "none", "none", "none", "none", "none", []), [], "refused");
        var configPath = Path.Combine(root, "config.json"); var intentPath = Path.Combine(root, "intent.json"); var output = Path.Combine(root, "absent-output.json");
        await File.WriteAllBytesAsync(configPath, JsonSerializer.SerializeToUtf8Bytes(new TrustedConfiguration(request.Repository, repository, "refs/heads/main", Path.Combine(root, "absent-git"), MaximumCeiling: maximum), BoundedFiles.Json), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(intentPath, JsonSerializer.SerializeToUtf8Bytes(intent, BoundedFiles.Json), TestContext.Current.CancellationToken);
        var application = new ToolApplication();
        var refused = await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse(["plan", "--repo", repository, "--intent-file", intentPath, "--config", configPath, "--plan-out", output]), TestContext.Current.CancellationToken));
        Assert.Equal("Intent identity/mode/ceiling differs from protected selection.", refused.Message);
        Assert.Empty(application.RetainedExternalCalls); Assert.False(File.Exists(output)); Assert.False(File.Exists(Path.Combine(root, "absent-git")));
    }

    [Fact]
    public async Task CLI_plan_valid_explicit_implement_intent_reaches_selected_Git_before_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-cli-plan-guard-control-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "parent"); Directory.CreateDirectory(repository);
        var git = Path.Combine(root, "absent-git"); var output = Path.Combine(root, "absent-output.json");
        var intent = new PlanIntent(new("fixture/cli-plan", "#1", "implement", "Accepted guard control", ["source.cs"], [], ["No effects"], "implement"),
            ["source.cs"], [], [], [], new("none", "none", "none", "none", "none", "none", "none", "none", []), [], "guard-control");
        var config = Path.Combine(root, "config.json"); var input = Path.Combine(root, "intent.json");
        await File.WriteAllBytesAsync(config, JsonSerializer.SerializeToUtf8Bytes(new TrustedConfiguration("fixture/cli-plan", repository, "refs/heads/main", git, MaximumCeiling: "implement"), BoundedFiles.Json), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(input, JsonSerializer.SerializeToUtf8Bytes(intent, BoundedFiles.Json), TestContext.Current.CancellationToken);
        var application = new ToolApplication();
        var refused = await Assert.ThrowsAsync<PlanningException>(() => application.ExecuteAsync(CommandLine.Parse(["plan", "--repo", repository, "--intent-file", input, "--config", config, "--plan-out", output]), TestContext.Current.CancellationToken));
        Assert.Equal($"The configured Git executable '{Path.GetFullPath(git)}' does not exist.", refused.Message);
        Assert.Empty(application.RetainedExternalCalls); Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CLI_issue_content_cannot_choose_implement_intent_even_under_implement_ceiling()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-cli-issue-mode-refusal-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "parent"); Directory.CreateDirectory(repository);
        var output = Path.Combine(root, "absent-output.json"); var config = Path.Combine(root, "config.json");
        var issue = new PlanIssueFixture(new(new("fixture/cli-plan", "#1", "implement", "Untrusted implement intent", ["source.cs"], [], ["No effects"], "implement"),
            ["source.cs"], [], [], [], new("none", "none", "none", "none", "none", "none", "none", "none", []), [], "issue-refusal"));
        await File.WriteAllBytesAsync(config, JsonSerializer.SerializeToUtf8Bytes(new TrustedConfiguration("fixture/cli-plan", repository, "refs/heads/main", Path.Combine(root, "absent-git"), MaximumCeiling: "implement"), BoundedFiles.Json), TestContext.Current.CancellationToken);
        var application = new ToolApplication(issues: issue);
        var refused = await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse(["plan", "--repo", repository, "--issue", "fixture/cli-plan#1", "--config", config, "--plan-out", output]), TestContext.Current.CancellationToken));
        Assert.Equal("Intent identity/mode/ceiling differs from protected selection.", refused.Message);
        Assert.Equal(1, issue.Calls); var actualIssue = Assert.Single(application.RetainedExternalCalls);
        Assert.Equal("ActualIssueRead", actualIssue.Kind); Assert.True(actualIssue.InvocationReturned);
        Assert.NotNull(actualIssue.ActualTask); Assert.True(actualIssue.ActualTask.IsCompletedSuccessfully);
        Assert.False(File.Exists(output)); Assert.False(File.Exists(Path.Combine(root, "absent-git")));
    }

    sealed class PlanIssueFixture(PlanIntent content) : IIssueContentPort
    {
        internal int Calls { get; private set; }
        public Task<PlanIntent> ReadPublicIssueAsync(string issue, TimeSpan remaining, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Assert.Equal("fixture/cli-plan#1", issue); Assert.True(remaining > TimeSpan.Zero);
            Calls++; return Task.FromResult(content);
        }
    }

    sealed class ReaderParentFixture : ICoordinatedEffectAdmission
    {
        internal int Entries;
        public void CheckCurrent(string runId) { }
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) { Entries++; return invoke(); }
        public void RetainUncertain(string runId, string reason) => throw new IOException(reason);
    }
}
