using System.Text;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Tool.Tests;

// Actual owned Git/file conformance only. This does not issue worker completion,
// independent review, accepted baseline or publication authority.
public sealed class ProvisionedGitBindingTests
{
    public static bool Windows => OperatingSystem.IsWindows();

    [Theory(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    [InlineData("marker")]
    [InlineData("backpointer")]
    [InlineData("common")]
    public async Task Original_metadata_redirection_refuses_before_even_the_worker_config_probe(string role)
    {
        using var fixture = await Fixture.CreateAsync();
        using var writer = fixture.Manager.AcquireWriter(fixture.Worker, "owned", "retained-writer");
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        var marker = File.ReadAllBytes(Path.Combine(fixture.Worker, ".git"));
        var path = role switch
        {
            "marker" => Path.Combine(fixture.Worker, ".git"),
            "backpointer" => Path.Combine(binding.GitDirectory, "gitdir"),
            _ => Path.Combine(binding.GitDirectory, "commondir")
        };
        var before = fixture.Manager.ActualOwnedGitInvocations;
        // Git marks the linked-worktree marker Hidden on Windows. Open that
        // existing fixture file rather than FileMode.Create, preserving its
        // attributes and actually installing the selected negative metadata.
        var changed = Encoding.UTF8.GetBytes(role == "marker" ? "gitdir: " + fixture.Foreign + "\n" : fixture.Foreign + "\n");
        using (var metadata = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            metadata.SetLength(0);
            metadata.Write(changed);
        }
        Assert.Equal(changed, File.ReadAllBytes(path));
        if (role != "marker") Assert.Equal(marker, File.ReadAllBytes(Path.Combine(fixture.Worker, ".git")));
        Assert.Equal(WriterNamespaceIdentityOutcome.Match, writer.ObserveCurrentNamespaceIdentity().Outcome);
        await Assert.ThrowsAsync<WorktreeBoundaryException>(() => fixture.Manager.ObserveAsync(fixture.Worker, TestContext.Current.CancellationToken));
        Assert.Equal(before, fixture.Manager.ActualOwnedGitInvocations);
        Assert.False(fixture.Original.HasExplicitUnknown);
        TestContext.Current.TestOutputHelper!.WriteLine($"Original {role} association refused with zero later Git births; held writer remained the original.");
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Fresh_owner_cannot_adopt_existing_valid_worker_metadata_or_writer()
    {
        using var fixture = await Fixture.CreateAsync();
        var foreign = new WorktreeManager(fixture.GitImage, fixture.Executor, fixture.Original)
            .WithParentAdmission(fixture.Parent, "owned");
        var before = foreign.ActualOwnedGitInvocations;
        var originalWriterPath = fixture.Manager.RequireOriginalBinding(fixture.Worker).WriterPath;
        await Assert.ThrowsAsync<WorktreeBoundaryException>(() => foreign.ValidateAssignedWorktreeAsync(fixture.Repository, fixture.Worker, TestContext.Current.CancellationToken));
        Assert.Throws<WorktreeBoundaryException>(() => foreign.AcquireWriter(fixture.Worker, "owned", "adopted"));
        Assert.Equal(before, foreign.ActualOwnedGitInvocations);
        Assert.False(File.Exists(originalWriterPath));
        Assert.False(fixture.Original.HasExplicitUnknown);
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Initial_native_port_manager_uses_the_provisioned_parent_for_actual_birth()
    {
        using var fixture = await Fixture.CreateAsync();
        var before = fixture.InitialManager.ActualOwnedGitInvocations;
        var admittedBefore = fixture.Parent.NativeAdmissionAttempts;
        // The initial manager has no admission field, as in the real native
        // port. The shared binding must enroll its actual task through parent.
        var observed = await fixture.InitialManager.ObserveAsync(fixture.Worker, TestContext.Current.CancellationToken);
        Assert.True(observed.IsClean);
        var actualBirths = fixture.InitialManager.ActualOwnedGitInvocations - before;
        Assert.True(actualBirths > 0);
        Assert.Equal(actualBirths, (long)(fixture.Parent.NativeAdmissionAttempts - admittedBefore));
        Assert.False(fixture.Original.HasExplicitUnknown);
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Initial_readonly_manager_refuses_writer_birth_before_opening_any_lock()
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        var effects = fixture.Parent.Inner.EnteredCount;
        Assert.Throws<WorktreeBoundaryException>(() => fixture.InitialManager.AcquireWriter(fixture.Worker, "owned", "unadmitted"));
        Assert.False(File.Exists(binding.WriterPath));
        Assert.Equal(effects, fixture.Parent.Inner.EnteredCount);
        Assert.False(fixture.Original.HasExplicitUnknown);
        using var actual = fixture.Manager.AcquireWriter(fixture.Worker, "owned", "admitted");
        Assert.True(binding.OwnsWriter(actual));
        Assert.Equal(WriterNamespaceIdentityOutcome.Match, actual.ObserveCurrentNamespaceIdentity().Outcome);
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Provisioned_writer_postrefusal_closes_actual_handle_and_keeps_durable_failure_lock()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Parent.RefuseWriterReturn = true;
        Assert.Throws<IOException>(() => fixture.Manager.AcquireWriter(fixture.Worker, "owned", "postrefused-writer"));
        var actual = Assert.IsType<WriterLease>(fixture.Parent.ActualWriter);
        Assert.True(actual.FirstCloseReturned);
        Assert.Equal(WriterNamespaceIdentityOutcome.Unknown, actual.ObserveCurrentNamespaceIdentity().Outcome);
        var path = actual.DiagnosticLockPath;
        Assert.Equal("owned\npostrefused-writer\n", File.ReadAllText(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(fixture.Original.HasExplicitUnknown);
        Assert.True(fixture.Parent.Inner.AllKnownSettled);
    }

    [Theory(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bound_readonly_status_does_not_refresh_actual_index_bytes(bool cleanup)
    {
        using var fixture = await Fixture.CreateAsync();
        using var writer = fixture.Manager.AcquireWriter(fixture.Worker, "owned", "readonly-index");
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        var before = File.ReadAllBytes(binding.IndexPath);
        var subject = Path.Combine(fixture.Worker, "subject.txt");
        var content = File.ReadAllBytes(subject);
        var changedTimestamp = File.GetLastWriteTimeUtc(subject).AddMinutes(-5);
        // Change only the worktree stat cache input. An ordinary optional index
        // refresh can now rewrite bytes even though the tracked content is clean.
        File.SetLastWriteTimeUtc(subject, changedTimestamp);
        Assert.Equal(changedTimestamp, File.GetLastWriteTimeUtc(subject));
        Assert.Equal(content, File.ReadAllBytes(subject));
        var births = fixture.InitialManager.ActualOwnedGitInvocations;
        var observed = cleanup
            ? await fixture.InitialManager.ObserveOwnedCleanupAsync(fixture.Worker, TestContext.Current.CancellationToken)
            : await fixture.InitialManager.ObserveAsync(fixture.Worker, TestContext.Current.CancellationToken);
        Assert.True(observed.IsClean);
        Assert.True(fixture.InitialManager.ActualOwnedGitInvocations > births);
        Assert.Equal(before, File.ReadAllBytes(binding.IndexPath));
        Assert.Equal(content, File.ReadAllBytes(subject));
        Assert.Equal(changedTimestamp, File.GetLastWriteTimeUtc(subject));
        Assert.Equal(WriterNamespaceIdentityOutcome.Match, writer.ObserveCurrentNamespaceIdentity().Outcome);
        Assert.False(fixture.Original.HasExplicitUnknown);
    }

    [Theory(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_writer_failure_cleanup_uses_exact_live_owner_after_forward_revocation(bool redirect)
    {
        using var fixture = await Fixture.CreateAsync();
        var store = new RunStore(Path.Combine(fixture.Root, "completed-store"));
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(store, fixture.Manager, processes);
        coordinator.BindCoordinationAdmission(fixture.Parent);
        coordinator.BindOriginalScheduling(fixture.Original, fixture.Parent, "owned", fixture.Manager);
        var completion = await fixture.CompleteContractFixtureAsync(coordinator);
        var binding = coordinator.RequireOriginalWorktreeBinding("owned", fixture.Original);
        var writer = Assert.IsType<WriterLease>(fixture.Parent.ActualWriter);
        var lockPath = writer.DiagnosticLockPath;
        var bytes = Encoding.UTF8.GetBytes(writer.RunId + "\n" + writer.WriterToken + "\n");
        // The real Git/record/writer path is exercised with fixture envelope DATA;
        // no native worker, quality/review or production acceptance is issued.
        fixture.Parent.Current = false;
        _ = fixture.Original.CleanupRemaining;
        if (redirect)
        {
            var path = Path.Combine(binding.GitDirectory, "commondir");
            using (var metadata = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                metadata.SetLength(0); metadata.Write(Encoding.UTF8.GetBytes(fixture.Foreign + "\n"));
            }
            await Assert.ThrowsAsync<WorktreeBoundaryException>(() =>
                coordinator.ReleaseCompletedWriterAfterFailureAsync("owned", completion, () => true));
            Assert.False(writer.FirstCloseReturned);
            Assert.Equal(WriterNamespaceIdentityOutcome.Match, writer.ObserveCurrentNamespaceIdentity().Outcome);
            // Known fixture-owner Dispose later closes this actual held handle;
            // never restore/repair the foreign metadata or claim successful release.
        }
        else
        {
            await coordinator.ReleaseCompletedWriterAfterFailureAsync("owned", completion, () => true);
            Assert.True(writer.FirstCloseReturned);
            Assert.Equal(bytes, File.ReadAllBytes(lockPath));
            using var exclusive = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        Assert.False(fixture.Original.HasExplicitUnknown);
        Assert.DoesNotContain((await new RunStore(store.RootPath).RecoverAsync("owned", TestContext.Current.CancellationToken)).Records,
            row => row.Kind == "stopped");
    }

    sealed class ResultDirectoryPort(Fixture fixture) : IHostToolSessionPort
    {
        internal int Starts;
        AgentRequest? request;
        public AgentAdapterProfile Profile { get; } = new("codex.cli", "result-directory-fixture",
            new[] { AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Stop }
                .Select(capability => new AgentCapabilityEvidence(capability, AgentCapabilityStatus.Supported,
                    "typed filesystem contract fixture only", "fixture:result-directory")).ToArray());
        public HostObservationRealm Realm => HostObservationRealm.ContractFixture;
        public Task<AgentAcceptedReceipt> StartAsync(AgentRequest actual, TimeSpan remaining, CancellationToken token)
        {
            Starts++;
            Assert.True(Directory.Exists(actual.ResultDirectory));
            request = actual;
            return Task.FromResult(new AgentAcceptedReceipt(actual.SchemaVersion, actual.RunId, actual.RequestIdentity,
                actual.PlanIdentity, actual.BaseCommit, actual.Worktree, actual.AdapterId, actual.AdapterVersion,
                "result-fixture-run", "result-fixture-task", DateTimeOffset.UtcNow, Profile.Capabilities));
        }
        public async Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding binding, TimeSpan remaining, CancellationToken token)
        {
            var actual = request ?? throw new IOException("Actual fixture request missing.");
            var artifact = Path.Combine(actual.ResultDirectory, "actual-completion.json");
            BoundedFiles.WriteNew(artifact, Encoding.UTF8.GetBytes("result namespace reached by actual completion"), fixture.Original);
            File.WriteAllText(Path.Combine(fixture.Worker, "subject.txt"), "completed result-directory fixture\n");
            await fixture.GitAt(fixture.Worker, "add", "--", "subject.txt");
            await fixture.GitAt(fixture.Worker, "-c", "user.name=ResultFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "completed");
            var observed = await fixture.Manager.ObserveAsync(fixture.Worker, token);
            return new(actual.SchemaVersion, actual.RunId, actual.RequestIdentity, actual.AdapterId, actual.AdapterVersion,
                binding.RuntimeRunIdentity, binding.RuntimeTaskIdentity, "success", actual.Worktree,
                observed.HeadCommit, observed.TreeId, DateTimeOffset.UtcNow, [artifact], []);
        }
        public Task<AgentControlResult> ControlAsync(AgentRunBinding binding, AgentControlAction action, TimeSpan remaining, CancellationToken token) => throw new IOException("No control belongs to the completed directory fixture.");
        public Task<AgentControlResult> ObserveUnstartedAsync(AgentRunBinding binding, TimeSpan remaining, CancellationToken token) => throw new IOException("No unstarted adoption belongs to this fixture.");
    }
    static (FrozenWorkerPlan Plan, HostInstructionContext Instructions) ResultAssignment(Fixture fixture, string head, string result, string version)
    {
        var plan = new FrozenWorkerPlan("owned", "sha256:" + new string('a', 64), head, "fixture/repository", fixture.Repository,
            fixture.Worker, result, "result-directory-writer", ["subject.txt"], [], "codex.cli", version,
            [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
        var request = WorkerEnvelope.PrepareWorker(plan);
        return (plan, HostInstructionContext.FromParentSelection(request, "sha256:" + new string('b', 64),
            "physical directory contract fixture only", ["only subject.txt"], ["commit subject.txt and return actual completion artifact"]));
    }
    [Theory(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    [InlineData("absent")]
    [InlineData("empty")]
    public async Task Actual_owner_prepares_empty_results_before_start(string initial)
    {
        using var fixture = await Fixture.CreateAsync();
        var result = Path.Combine(fixture.Root, "worker-results");
        if (initial == "empty") Directory.CreateDirectory(result);
        var port = new ResultDirectoryPort(fixture); var adapter = new CodexHostAdapter(port);
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(fixture.Root, "run-store")), fixture.Manager, processes, adapter);
        var bridge = new OwnerHostBridge(coordinator, adapter);
        var head = (await fixture.Manager.ObserveAsync(fixture.Worker, fixture.Original.Token)).HeadCommit;
        var assignment = ResultAssignment(fixture, head, result, port.Profile.AdapterVersion);
        var origin = fixture.Original.OriginalStarted; var whole = fixture.Original.WholeLimit; var work = fixture.Original.WorkLimit;
        var completed = await bridge.ImplementAsync(assignment.Plan, assignment.Instructions, fixture.Original, fixture.Original.Token);
        Assert.Equal("COMPLETE", completed.Outcome); Assert.Equal(1, port.Starts);
        Assert.Equal("result namespace reached by actual completion", File.ReadAllText(Path.Combine(result, "actual-completion.json")));
        Assert.Equal(origin, fixture.Original.OriginalStarted); Assert.Equal(whole, fixture.Original.WholeLimit); Assert.Equal(work, fixture.Original.WorkLimit);
        Assert.True(Assert.Single(bridge.RetainedFrames).OwnerCompleted);
    }
    [Theory(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    [InlineData("file")]
    [InlineData("retained-evidence")]
    [InlineData("worktree")]
    [InlineData("parent-checkout")]
    public async Task Actual_owner_refuses_unsafe_or_populated_results_before_preparation(string fault)
    {
        using var fixture = await Fixture.CreateAsync();
        var result = fault switch { "worktree" => fixture.Worker, "parent-checkout" => fixture.Repository, _ => Path.Combine(fixture.Root, "worker-results") };
        var sentinel = Path.Combine(result, "retained.txt");
        if (fault == "file") File.WriteAllText(result, "preserved file");
        if (fault == "retained-evidence") { Directory.CreateDirectory(result); File.WriteAllText(sentinel, "retained original evidence"); }
        var port = new ResultDirectoryPort(fixture); var adapter = new CodexHostAdapter(port);
        var store = new RunStore(Path.Combine(fixture.Root, "run-store"));
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(store, fixture.Manager, processes, adapter);
        var bridge = new OwnerHostBridge(coordinator, adapter);
        var head = (await fixture.Manager.ObserveAsync(fixture.Worker, fixture.Original.Token)).HeadCommit;
        var assignment = ResultAssignment(fixture, head, result, port.Profile.AdapterVersion);
        var nativeBefore = fixture.Manager.ActualOwnedGitInvocations;
        var origin = fixture.Original.OriginalStarted; var whole = fixture.Original.WholeLimit; var work = fixture.Original.WorkLimit;
        var refused = await bridge.ImplementAsync(assignment.Plan, assignment.Instructions, fixture.Original, fixture.Original.Token);
        Assert.Equal("INCOMPLETE", refused.Outcome); Assert.Equal(0, port.Starts); Assert.Empty(adapter.ActualCalls);
        Assert.Equal(nativeBefore, fixture.Manager.ActualOwnedGitInvocations); Assert.Null(fixture.Parent.ActualWriter);
        Assert.Empty((await store.RecoverAsync(assignment.Plan.RunId, fixture.Original.Token)).Records);
        Assert.False(fixture.Original.HasExplicitUnknown); Assert.False(fixture.Original.HasRetainedUnknown);
        Assert.Equal(origin, fixture.Original.OriginalStarted); Assert.Equal(whole, fixture.Original.WholeLimit); Assert.Equal(work, fixture.Original.WorkLimit);
        if (fault == "file") Assert.Equal("preserved file", File.ReadAllText(result));
        if (fault == "retained-evidence") Assert.Equal("retained original evidence", File.ReadAllText(sentinel));
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(fixture.Worker, "subject.txt")));
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(fixture.Repository, "subject.txt")));
    }
    sealed class PostBirthParent : ICoordinatedSynchronousEffectAdmission
    {
        internal readonly ParentEffectAdmission Inner;
        internal bool RefuseWriterReturn;
        internal bool Current = true;
        internal int NativeAdmissionAttempts;
        internal WriterLease? ActualWriter;
        internal PostBirthParent() => Inner = new(new object(), run => Current && run == "owned", _ => Current = false);
        public void CheckCurrent(string runId) => Inner.CheckCurrent(runId);
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke)
        {
            NativeAdmissionAttempts++;
            return Inner.EnterAsync(runId, invoke);
        }
        public T EnterSynchronous<T>(string runId, Func<T> invoke)
        {
            var value = Inner.EnterSynchronous(runId, invoke);
            if (value is WriterLease born) ActualWriter = born;
            if (RefuseWriterReturn && value is WriterLease writer)
            {
                ActualWriter = writer;
                throw new IOException("Actual parent postcheck refused the returned original writer.");
            }
            return value;
        }
        public void RetainUncertain(string runId, string reason) => Inner.RetainUncertain(runId, reason);
    }

    sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "forge-provisioned-" + Guid.NewGuid().ToString("N"));
        internal string Repository => Path.Combine(Root, "repository");
        internal string Worker => Path.Combine(Root, "worker");
        internal string Foreign => Path.Combine(Root, "foreign");
        internal readonly ToolLifetime Original = new(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        internal readonly ProcessCommandExecutor Executor = new();
        internal readonly PostBirthParent Parent = new();
        internal readonly string GitImage = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, "git.exe")).First(File.Exists);
        internal WorktreeManager Manager = null!, InitialManager = null!;
        internal static async Task<Fixture> CreateAsync()
        {
            var value = new Fixture();
            try
            {
                Directory.CreateDirectory(value.Repository); Directory.CreateDirectory(value.Foreign);
                await value.Git("init", "-q", "-b", "main");
                File.WriteAllText(Path.Combine(value.Repository, "subject.txt"), "original\n");
                await value.Git("add", "--", "subject.txt");
                await value.Git("-c", "user.name=BindingFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "original");
                var head = await value.Git("rev-parse", "HEAD");
                value.InitialManager = new WorktreeManager(value.GitImage, value.Executor, value.Original);
                value.Manager = value.InitialManager.WithParentAdmission(value.Parent, "owned");
                await value.Manager.PrepareAsync(value.Repository, value.Worker, head, TestContext.Current.CancellationToken);
                var observed = await value.Manager.ObserveAsync(value.Worker, TestContext.Current.CancellationToken);
                Assert.Equal(head, observed.HeadCommit); Assert.True(observed.IsClean);
                return value;
            }
            catch { value.Dispose(); throw; }
        }
        async Task<string> Git(params string[] arguments) => await GitAt(Repository, arguments);
        internal async Task<string> GitAt(string directory, params string[] arguments)
        {
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(row => !((string)row.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => (string)row.Key, row => row.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = "NUL";
            environment["GIT_CONFIG_SYSTEM"] = "NUL"; environment["GIT_TERMINAL_PROMPT"] = "0";
            var capture = await Parent.EnterAsync("owned", () => Executor.ExecuteBoundedCaptureAsync(new(GitImage,
                ["-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", "-c", "commit.gpgSign=false", .. arguments], directory, environment)
            { EnvironmentIsComplete = true }, TimeSpan.FromSeconds(15), 1048576, Original.Token, new OwnedWorkPhaseBudget(Original)));
            Assert.Equal(0, capture.Command.ExitCode); Assert.True(capture.Command.OwnedProcessQuiescent);
            Assert.True(capture.RootExitObserved); Assert.True(capture.StandardOutputDrainCompleted); Assert.True(capture.StandardErrorDrainCompleted);
            Assert.True(capture.Command.LifecycleDiagnostics?.OwnedTree is { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 });
            return Encoding.UTF8.GetString(capture.StandardOutputBytes).Trim();
        }
        internal async Task<AgentCompletionReceipt> CompleteContractFixtureAsync(ExecutionCoordinator coordinator)
        {
            var head = await Git("rev-parse", "HEAD");
            var plan = new FrozenWorkerPlan("owned", "sha256:" + new string('a', 64), head, "fixture/repository", Repository,
                Worker, Path.Combine(Root, "fixture-result"), "actual-completed-writer", ["subject.txt"], [],
                "fixture.cleanup", "fixture-v1", [AgentCapability.Launch, AgentCapability.CompletionEvidence]);
            var (request, _) = await coordinator.PrepareWorkerAsync(plan, TestContext.Current.CancellationToken);
            var capabilities = request.RequiredCapabilities.Select(capability => new AgentCapabilityEvidence(capability,
                AgentCapabilityStatus.Supported, "contract fixture DATA only", "fixture:completed-writer-cleanup")).ToArray();
            var accepted = new AgentAcceptedReceipt(request.SchemaVersion, request.RunId, request.RequestIdentity, request.PlanIdentity,
                request.BaseCommit, request.Worktree, request.AdapterId, request.AdapterVersion, "fixture-run", "fixture-task", DateTimeOffset.UtcNow, capabilities);
            await coordinator.AcceptAsync(accepted, TestContext.Current.CancellationToken);
            File.WriteAllText(Path.Combine(Worker, "subject.txt"), "actual committed cleanup fixture\n");
            await GitAt(Worker, "add", "--", "subject.txt");
            await GitAt(Worker, "-c", "user.name=CleanupFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "completed");
            var observed = await Manager.ObserveAsync(Worker, TestContext.Current.CancellationToken);
            var completion = new AgentCompletionReceipt(request.SchemaVersion, request.RunId, request.RequestIdentity, request.AdapterId, request.AdapterVersion,
                accepted.RuntimeRunIdentity, accepted.RuntimeTaskIdentity, "success", request.Worktree, observed.HeadCommit, observed.TreeId, DateTimeOffset.UtcNow, [], []);
            await coordinator.CompleteAsync(completion, TestContext.Current.CancellationToken);
            return completion;
        }
        public void Dispose()
        {
            // Preserve actual provisioned metadata and every failure namespace;
            // physical closure does not authorize erasing diagnostic evidence.
            TestContext.Current.TestOutputHelper!.WriteLine("Retained actual provisioned Git fixture: " + Root);
            Original.Dispose();
        }
    }
}
