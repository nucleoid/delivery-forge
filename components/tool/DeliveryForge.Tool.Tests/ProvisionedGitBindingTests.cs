using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;
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
        var effectsBefore = fixture.Parent.Inner.EnteredCount;
        // The initial manager has no admission field, as in the real native
        // port. The shared binding must enroll its actual task through parent.
        var observed = await fixture.InitialManager.ObserveAsync(fixture.Worker, TestContext.Current.CancellationToken);
        Assert.True(observed.IsClean);
        var actualBirths = fixture.InitialManager.ActualOwnedGitInvocations - before;
        Assert.True(actualBirths > 0);
        Assert.Equal(actualBirths, (long)(fixture.Parent.NativeAdmissionAttempts - admittedBefore));
        Assert.Equal(1 + actualBirths, (long)(fixture.Parent.Inner.EnteredCount - effectsBefore));
        Assert.False(fixture.Original.HasExplicitUnknown);
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Grouped_checked_git_birth_retains_actual_capture_task_and_single_admission()
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        var before = fixture.Manager.ActualOwnedGitInvocations;
        var admissions = fixture.Parent.NativeAdmissionAttempts;
        var effects = fixture.Parent.Inner.EnteredCount;
        fixture.Parent.ObserveNativeReturn = true;
        var actual = binding.ExecuteWorktreeGitAsync(fixture.VersionInvocation(), TestContext.Current.CancellationToken);
        Assert.Same(actual, fixture.Parent.ObservedNativeTask);
        Assert.Equal(effects + 1, fixture.Parent.Inner.EnteredCount);
        Assert.Equal(admissions + 1, fixture.Parent.NativeAdmissionAttempts);
        var capture = await actual.WaitAsync(fixture.Original.Remaining, TestContext.Current.CancellationToken);
        Assert.Equal(0, capture.Command.ExitCode); Assert.False(capture.Command.Cancelled); Assert.False(capture.Command.TimedOut);
        Assert.True(capture.Command.OwnedProcessQuiescent); Assert.True(capture.RootExitObserved);
        Assert.True(capture.StandardOutputDrainCompleted); Assert.True(capture.StandardErrorDrainCompleted);
        Assert.False(capture.CaptureBoundExceeded);
        Assert.True(capture.Command.LifecycleDiagnostics?.OwnedTree is { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 });
        Assert.StartsWith("git version ", Encoding.UTF8.GetString(capture.StandardOutputBytes), StringComparison.Ordinal);
        Assert.Empty(capture.StandardErrorBytes);
        Assert.Equal(before + 1, fixture.Manager.ActualOwnedGitInvocations);
        await Task.WhenAll(fixture.Parent.Inner.OriginalObservers).WaitAsync(fixture.Original.Remaining, TestContext.Current.CancellationToken);
        Assert.True(fixture.Parent.Inner.AllKnownSettled); Assert.False(fixture.Original.HasExplicitUnknown);
        // Actual native Task identity, single admission and known closure only;
        // this does not claim the native Task was still pending at invoke-return.
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Grouped_checked_git_birth_rechecks_changed_metadata_before_native_capture()
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        var before = fixture.Manager.ActualOwnedGitInvocations;
        var admissions = fixture.Parent.NativeAdmissionAttempts;
        var effects = fixture.Parent.Inner.EnteredCount;
        var path = Path.Combine(binding.GitDirectory, "commondir");
        var changed = Encoding.UTF8.GetBytes(fixture.Foreign + "\n");
        using (var metadata = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            metadata.SetLength(0); metadata.Write(changed);
        }
        Assert.Equal(changed, File.ReadAllBytes(path));
        // Call the new actual native entry directly so a manager's earlier
        // precheck cannot mask a missing in-admission metadata verification.
        await Assert.ThrowsAsync<WorktreeBoundaryException>(async () =>
            await binding.ExecuteWorktreeGitAsync(fixture.VersionInvocation(), TestContext.Current.CancellationToken));
        Assert.Equal(before, fixture.Manager.ActualOwnedGitInvocations);
        Assert.Equal(admissions + 1, fixture.Parent.NativeAdmissionAttempts);
        Assert.Equal(effects + 1, fixture.Parent.Inner.EnteredCount);
        Assert.False(fixture.Parent.Inner.AllKnownSettled); // Existing entered-prefix refusal remains conservative.
        Assert.False(fixture.Original.HasExplicitUnknown);
        // The changed fixture and failed original admission stay retained.
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Grouped_review_image_verification_refuses_foreign_original_before_body_or_admission()
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        using var foreign = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var before = fixture.Manager.ActualOwnedGitInvocations;
        var admissions = fixture.Parent.NativeAdmissionAttempts;
        var effects = fixture.Parent.Inner.EnteredCount; var imageBodies = 0;
        Assert.Throws<WorktreeBoundaryException>(() => binding.CheckAndVerifyReviewImage(foreign,
            fixture.Parent, "owned", () => imageBodies++));
        Assert.Equal(0, imageBodies); Assert.Equal(before, fixture.Manager.ActualOwnedGitInvocations);
        Assert.Equal(admissions, fixture.Parent.NativeAdmissionAttempts); Assert.Equal(effects, fixture.Parent.Inner.EnteredCount);
        Assert.True(fixture.Parent.Inner.AllKnownSettled); Assert.True(fixture.Parent.Current);
        Assert.False(fixture.Original.HasExplicitUnknown); Assert.False(foreign.HasExplicitUnknown);
        // A real second lifetime is negative DATA. No source/reviewer owner,
        // live issuer, native completion or production authority is fabricated.
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

    [Theory(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    [InlineData("directories")]
    [InlineData("git-file")]
    [InlineData("common-file")]
    public async Task Review_metadata_accepts_actual_provisioned_directories_and_refuses_file_substitution(string selected)
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        Assert.True(Directory.Exists(binding.GitDirectory)); Assert.True(Directory.Exists(binding.CommonDirectory));
        var before = fixture.Manager.ActualOwnedGitInvocations;
        if (selected == "directories")
            InstalledReviewHost.RequirePlainMetadataDirectories(binding.GitDirectory, binding.CommonDirectory);
        else
        {
            var file = Path.Combine(fixture.Root, "metadata-file"); File.WriteAllText(file, "not a metadata directory");
            Assert.Throws<IOException>(() => InstalledReviewHost.RequirePlainMetadataDirectories(
                selected == "git-file" ? file : binding.GitDirectory,
                selected == "common-file" ? file : binding.CommonDirectory));
        }
        Assert.Equal(before, fixture.Manager.ActualOwnedGitInvocations);
        Assert.False(fixture.Original.HasExplicitUnknown);
        // Real provisioned directory/type guard only; no native worker/reviewer
        // result or installed acceptance is manufactured by this control.
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Owned_provisioning_preserves_raw_LF_source_and_protected_seed_without_changing_parent_conversion_config()
    {
        using var fixture = await Fixture.CreateAsync(forceCheckoutConversion: true);
        Assert.Equal("true", await fixture.GitAt(fixture.Repository, "config", "--local", "--get", "core.autocrlf"));
        Assert.Equal("crlf", await fixture.GitAt(fixture.Repository, "config", "--local", "--get", "core.eol"));
        foreach (var path in new[] { "subject.txt", ".forge/protected-policy.json" })
        {
            var expected = File.ReadAllBytes(Path.Combine(fixture.Repository, path));
            var actual = File.ReadAllBytes(Path.Combine(fixture.Worker, path));
            Assert.DoesNotContain((byte)'\r', expected);
            Assert.Equal(expected, actual);
            Assert.Contains((byte)'\n', actual);
        }
        var observed = await fixture.Manager.ObserveAsync(fixture.Worker, TestContext.Current.CancellationToken);
        Assert.True(observed.IsClean);
        Assert.Equal("true", await fixture.GitAt(fixture.Repository, "config", "--local", "--get", "core.autocrlf"));
        Assert.Equal("crlf", await fixture.GitAt(fixture.Repository, "config", "--local", "--get", "core.eol"));
        Assert.False(fixture.Original.HasExplicitUnknown);
        // Actual creation-owned Git + physical byte/config regression only;
        // this does not supply a reviewer receipt or installed host authority.
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
        var result = fault switch { "worktree" => Path.Combine(fixture.Worker, "out"), "parent-checkout" => Path.Combine(fixture.Repository, "out"), _ => Path.Combine(fixture.Root, "worker-results") };
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
        var workerBefore = File.ReadAllBytes(Path.Combine(fixture.Worker, "subject.txt"));
        var parentBefore = File.ReadAllBytes(Path.Combine(fixture.Repository, "subject.txt"));
        var origin = fixture.Original.OriginalStarted; var whole = fixture.Original.WholeLimit; var work = fixture.Original.WorkLimit;
        var refused = await bridge.ImplementAsync(assignment.Plan, assignment.Instructions, fixture.Original, fixture.Original.Token);
        Assert.Equal("INCOMPLETE", refused.Outcome); Assert.Equal(0, port.Starts); Assert.Empty(adapter.ActualCalls);
        Assert.Equal(nativeBefore, fixture.Manager.ActualOwnedGitInvocations); Assert.Null(fixture.Parent.ActualWriter);
        Assert.Empty((await store.RecoverAsync(assignment.Plan.RunId, fixture.Original.Token)).Records);
        Assert.False(fixture.Original.HasExplicitUnknown); Assert.False(fixture.Original.HasRetainedUnknown);
        Assert.Equal(origin, fixture.Original.OriginalStarted); Assert.Equal(whole, fixture.Original.WholeLimit); Assert.Equal(work, fixture.Original.WorkLimit);
        if (fault == "file") Assert.Equal("preserved file", File.ReadAllText(result));
        if (fault == "retained-evidence") Assert.Equal("retained original evidence", File.ReadAllText(sentinel));
        Assert.Equal(workerBefore, File.ReadAllBytes(Path.Combine(fixture.Worker, "subject.txt")));
        Assert.Equal(parentBefore, File.ReadAllBytes(Path.Combine(fixture.Repository, "subject.txt")));
        if (fault is "worktree" or "parent-checkout")
        {
            Assert.False(Directory.Exists(result)); Assert.False(File.Exists(result));
            Assert.True((await fixture.Manager.ObserveAsync(fixture.Worker, fixture.Original.Token)).IsClean);
        }
    }
    static AgentRequest ConfinedRequest(Fixture fixture, string run = "owned") => WorkerEnvelope.PrepareWorker(new(
        run, "sha256:" + new string('1', 64), new string('a', 40), "fixture/repo", fixture.Repository,
        fixture.Worker, Path.Combine(fixture.Root, "result"), "PRIVATE-WRITER", ["subject.txt"], [],
        "codex.cli", "0.163.0-alpha.10.2", [AgentCapability.Launch, AgentCapability.CompletionEvidence]));
    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Confined_roots_come_from_the_actual_linked_worktree_and_exclude_parent_and_common_metadata()
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        await binding.EnablePrivateObjectWritesAsync(TestContext.Current.CancellationToken);
        var roots = DeliveryForge.Tool.Codex.CodexAppServerPort.LinuxWorkspaceWriteRoots(fixture.InitialManager, fixture.Original, ConfinedRequest(fixture));
        Assert.Equal(new[] { binding.Worktree, binding.GitDirectory }, roots);
        Assert.True(File.Exists(Path.Combine(fixture.Worker, ".git"))); Assert.False(Directory.Exists(Path.Combine(fixture.Worker, ".git")));
        Assert.DoesNotContain(fixture.Repository, roots); Assert.DoesNotContain(binding.CommonDirectory, roots);
        Assert.DoesNotContain(Path.Combine(binding.CommonDirectory, "refs"), roots);
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Actual_private_git_commit_is_observed_only_after_its_original_writer_closes_without_changing_common_objects()
    {
        const string protectedPolicy = """
            {"Fixture":false,"Advisory":false,"AllowedFamilies":[{"Author":"gpt","Reviewer":"claude"}],"AllowedScopes":["source"],"AllowExactReuse":false,"Budget":{"MaximumRounds":2,"MaximumDuration":"00:00:30","MaximumTokens":4096,"MaximumCost":1.0},"ExemptionApprovers":[]}
            """;
        using var fixture = await Fixture.CreateAsync(forceCheckoutConversion: true, protectedPolicy: protectedPolicy);
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        using var writer = fixture.Manager.AcquireWriter(fixture.Worker, "owned", "private-object-writer");
        var commonBefore = ObjectBodies(binding.CommonObjectsPath);
        var baseHead = (await fixture.Manager.ObserveAsync(fixture.Worker, fixture.Original.Token)).HeadCommit;
        await binding.EnablePrivateObjectWritesAsync(fixture.Original.Token);
        File.WriteAllText(Path.Combine(fixture.Worker, "subject.txt"), "private committed candidate\n");
        File.WriteAllText(Path.Combine(fixture.Worker, ".forge", "protected-policy.json"), "{}");
        var actual = fixture.OpenOverlayGit(binding, "-c", "user.name=PrivateObjectFixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgSign=false", "-c", "gc.auto=0", "-c", "maintenance.auto=false", "commit", "-q", "-am", "private candidate");
        binding.AttachPrivateObjectWriter(actual);
        var before = fixture.Manager.ActualOwnedGitInvocations;
        Assert.Throws<WorktreeBoundaryException>(binding.RequireValidatedPrivateObjects);
        Assert.Equal(before, fixture.Manager.ActualOwnedGitInvocations);
        await DrainAndRetire(actual, fixture.Original);
        binding.ValidatePrivateObjectsAfterRetirement();
        var candidate = await fixture.Manager.ObserveAsync(fixture.Worker, fixture.Original.Token);
        Assert.True(candidate.IsClean); Assert.NotEqual(baseHead, candidate.HeadCommit);
        await fixture.Manager.EnsureDescendsFromAsync(fixture.Worker, baseHead, candidate.HeadCommit, fixture.Original.Token);
        Assert.Equal(commonBefore, ObjectBodies(binding.CommonObjectsPath));
        Assert.NotEmpty(Directory.GetFiles(binding.ObjectsPath, "*", SearchOption.AllDirectories));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(fixture.Worker, ".forge", "protected-policy.json")));
        // The existing review owner consumes BOTH namespaces, while protected
        // policy is still resolved from the original base, never the candidate.
        var planJson = JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ContractFixtures", "plan.json")))!.AsObject();
        planJson["baseCommit"] = baseHead;
        planJson["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(planJson.ToJsonString()));
        var plan = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(planJson.ToJsonString()));
        var evidence = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ContractFixtures", "evidence-policy.json"));
        var image = File.ReadAllBytes(fixture.GitImage);
        var sourceOwner = new ProductionReviewSourceOwner(fixture.Executor, fixture.Original, fixture.Parent, "owned", fixture.Worker,
            binding.GitDirectory, binding.CommonDirectory, fixture.GitImage, image.Length, Convert.ToHexStringLower(SHA256.HashData(image)),
            await fixture.GitAt(fixture.Repository, "--version"), baseHead, candidate.HeadCommit,
            () => actual.OriginalRetirementTask is { IsCompletedSuccessfully: true } closed && closed.Result.KnownClosed && !actual.HasUnknownIo,
            ContractValidator.ParseAndValidate(plan).Identity, ContractValidator.ParseAndValidate(evidence).Identity,
            ".forge/protected-policy.json", 1048576, binding);
        using var review = await ReviewAuthorityHost.CreateProductionFromOwnedSourceAsync(sourceOwner, plan, evidence,
            ".forge/protected-policy.json", 1048576, fixture.Original.Token);
        Assert.Equal(candidate.HeadCommit, review.ObjectView.FrozenHeadCommit);
        Assert.Equal(baseHead, review.Policy.SourceRevision);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(protectedPolicy))), review.Policy.ContentIdentity);
        Assert.True(review.Policy.Trusted); Assert.False(review.Policy.Fixture);
        Assert.False(fixture.Original.HasExplicitUnknown);
    }

    [Theory(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    [InlineData("pack")]
    [InlineData("routing")]
    [InlineData("shadow")]
    [InlineData("corrupt")]
    public async Task Private_namespace_refusals_happen_before_any_trusted_parent_Git_birth(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        string? shadowRelative = null; byte[]? shadowBody = null;
        if (change == "shadow")
        {
            var source = Directory.GetFiles(binding.CommonObjectsPath, "*", SearchOption.AllDirectories)
                .First(path => Path.GetFileName(Path.GetDirectoryName(path)!).Length == 2 && Path.GetFileName(path).Length == 38);
            shadowRelative = Path.GetRelativePath(binding.CommonObjectsPath, source); shadowBody = File.ReadAllBytes(source);
            await fixture.GitAt(fixture.Repository, "gc", "--prune=now");
            Assert.False(File.Exists(source)); // The protected OID now lives in a base pack.
        }
        await binding.EnablePrivateObjectWritesAsync(fixture.Original.Token);
        var actual = fixture.OpenOverlayGit(binding, "--version");
        binding.AttachPrivateObjectWriter(actual);
        await DrainAndRetire(actual, fixture.Original);
        if (change == "pack") Directory.CreateDirectory(Path.Combine(binding.ObjectsPath, "pack"));
        else if (change == "routing")
        {
            Directory.CreateDirectory(Path.Combine(binding.ObjectsPath, "info"));
            File.WriteAllText(Path.Combine(binding.ObjectsPath, "info", "alternates"), fixture.Foreign + "\n");
        }
        else if (change == "shadow")
        {
            var path = Path.Combine(binding.ObjectsPath, shadowRelative!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, shadowBody!);
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(binding.ObjectsPath, "ab"));
            File.WriteAllBytes(Path.Combine(binding.ObjectsPath, "ab", new string('0', 38)), [1, 2, 3, 4, 5, 6]);
        }
        var before = fixture.Manager.ActualOwnedGitInvocations;
        Assert.Throws<WorktreeBoundaryException>(binding.ValidatePrivateObjectsAfterRetirement);
        await Assert.ThrowsAsync<CoordinationUncertainException>(() => fixture.Manager.ObserveAsync(fixture.Worker, fixture.Original.Token));
        Assert.Equal(before, fixture.Manager.ActualOwnedGitInvocations);
        Assert.False(fixture.Original.HasExplicitUnknown);
    }

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Private_writer_cannot_be_adopted_from_another_real_original()
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        await binding.EnablePrivateObjectWritesAsync(fixture.Original.Token);
        using var foreign = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var actual = OwnedDuplexProcess.Open(fixture.VersionInvocation(), foreign);
        Assert.Throws<WorktreeBoundaryException>(() => binding.AttachPrivateObjectWriter(actual));
        await DrainAndRetire(actual, foreign);
        binding.RequireValidatedPrivateObjects();
        Assert.False(fixture.Original.HasExplicitUnknown); Assert.False(foreign.HasExplicitUnknown);
    }

    static string[] ObjectBodies(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
        .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    static async Task DrainAndRetire(OwnedDuplexProcess actual, ToolLifetime original)
    {
        Assert.Null(actual.StartupFailure);
        using var bytes = new MemoryStream();
        var output = actual.Output.CopyToAsync(bytes, 16384, original.Token);
        actual.AttachOriginalOutputConsumer(output);
        await output.WaitAsync(original.Remaining, original.Token);
        var closed = await actual.RetireAsync(false).WaitAsync(original.Remaining, original.Token);
        Assert.True(closed.KnownClosed); Assert.Equal(0u, closed.ActiveProcesses); Assert.Equal(0, closed.ExitCode);
        Assert.True(closed.OutputEof); Assert.True(closed.ErrorEof); Assert.True(closed.InputCloseReturned); Assert.True(closed.DispositionReturned);
        Assert.Empty(closed.Failures); Assert.False(actual.HasUnknownIo);
    }
    [Theory(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    [InlineData("marker")]
    [InlineData("backpointer")]
    [InlineData("common")]
    [InlineData("parent")]
    [InlineData("owner")]
    [InlineData("run")]
    public async Task Confined_policy_roots_refuse_stale_or_foreign_original_binding_without_a_native_birth(string change)
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        await binding.EnablePrivateObjectWritesAsync(TestContext.Current.CancellationToken);
        if (change is "marker" or "backpointer" or "common")
        {
            var path = change == "marker" ? Path.Combine(fixture.Worker, ".git") : Path.Combine(binding.GitDirectory, change == "backpointer" ? "gitdir" : "commondir");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            file.SetLength(0); file.Write(Encoding.UTF8.GetBytes(change == "marker" ? "gitdir: " + fixture.Foreign + "\n" : fixture.Foreign + "\n"));
        }
        if (change == "parent") fixture.Parent.Current = false;
        using var foreign = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var before = fixture.InitialManager.ActualOwnedGitInvocations;
        void Select() => DeliveryForge.Tool.Codex.CodexAppServerPort.LinuxWorkspaceWriteRoots(
            fixture.InitialManager, change == "owner" ? foreign : fixture.Original, ConfinedRequest(fixture, change == "run" ? "foreign" : "owned"));
        if (change == "parent")
            Assert.Equal("No current original parent effect authority.", Assert.Throws<CoordinationUncertainException>(Select).Message);
        else
        {
            var failure = Assert.Throws<WorktreeBoundaryException>(Select);
            if (change == "owner") Assert.Equal("Confined Git roots belong to another original.", failure.Message);
            if (change == "run") Assert.Equal("Confined Git roots belong to another run.", failure.Message);
            if (change is "marker" or "backpointer" or "common") Assert.Equal("Original provisioned Git metadata association changed.", failure.Message);
        }
        Assert.Equal(before, fixture.InitialManager.ActualOwnedGitInvocations);
    }

    [Theory(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confined_configuration_refusal_persists_before_parent_Git_without_another_birth(bool afterSelection)
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        await binding.EnablePrivateObjectWritesAsync(TestContext.Current.CancellationToken);
        if (afterSelection) _ = DeliveryForge.Tool.Codex.CodexAppServerPort.LinuxWorkspaceWriteRoots(fixture.InitialManager, fixture.Original, ConfinedRequest(fixture));
        File.WriteAllText(Path.Combine(binding.GitDirectory, "config.worktree"), "[include]\npath = worker-controlled\n");
        var before = fixture.InitialManager.ActualOwnedGitInvocations;
        if (afterSelection)
            Assert.Equal("Confined conformance worktree configuration is unsupported.",
                (await Assert.ThrowsAsync<WorktreeBoundaryException>(() => fixture.Manager.ObserveAsync(fixture.Worker, TestContext.Current.CancellationToken))).Message);
        else
            Assert.Equal("Confined conformance worktree configuration is unsupported.", Assert.Throws<WorktreeBoundaryException>(() =>
                DeliveryForge.Tool.Codex.CodexAppServerPort.LinuxWorkspaceWriteRoots(fixture.InitialManager, fixture.Original, ConfinedRequest(fixture))).Message);
        Assert.Equal(before, fixture.InitialManager.ActualOwnedGitInvocations);
    }
    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Linux native Git requires its separately selected owned scope.")]
    public async Task Unselected_binding_does_not_acquire_the_conformance_configuration_refusal()
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = fixture.Manager.RequireOriginalBinding(fixture.Worker);
        File.WriteAllText(Path.Combine(binding.GitDirectory, "config.worktree"), "[test]\nvalue = unchanged-default\n");
        var before = fixture.InitialManager.ActualOwnedGitInvocations;
        binding.Check();
        Assert.Equal(before, fixture.InitialManager.ActualOwnedGitInvocations);
    }
    sealed class PostBirthParent : ICoordinatedSynchronousEffectAdmission
    {
        internal readonly ParentEffectAdmission Inner;
        internal bool RefuseWriterReturn;
        internal bool Current = true;
        internal int NativeAdmissionAttempts;
        internal bool ObserveNativeReturn;
        internal Task? ObservedNativeTask;
        internal WriterLease? ActualWriter;
        internal PostBirthParent() => Inner = new(new object(), run => Current && run == "owned", _ => Current = false);
        public void CheckCurrent(string runId) => Inner.CheckCurrent(runId);
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke)
        {
            NativeAdmissionAttempts++;
            if (!ObserveNativeReturn) return Inner.EnterAsync(runId, invoke);
            return Inner.EnterAsync(runId, () =>
            {
                var actual = invoke();
                ObservedNativeTask = actual;
                return actual;
            });
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
        internal static async Task<Fixture> CreateAsync(bool forceCheckoutConversion = false, string? protectedPolicy = null)
        {
            var value = new Fixture();
            try
            {
                Directory.CreateDirectory(value.Repository); Directory.CreateDirectory(value.Foreign);
                await value.Git("init", "-q", "-b", "main");
                File.WriteAllText(Path.Combine(value.Repository, "subject.txt"), "original\n");
                await value.Git("add", "--", "subject.txt");
                if (forceCheckoutConversion)
                {
                    Directory.CreateDirectory(Path.Combine(value.Repository, ".forge"));
                    File.WriteAllText(Path.Combine(value.Repository, ".forge", "protected-policy.json"), protectedPolicy ?? "{\"guard\":\"base\"}\n");
                    await value.Git("add", "--", ".forge/protected-policy.json");
                }
                await value.Git("-c", "user.name=BindingFixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "original");
                var head = await value.Git("rev-parse", "HEAD");
                if (forceCheckoutConversion)
                {
                    await value.Git("config", "--local", "core.autocrlf", "true");
                    await value.Git("config", "--local", "core.eol", "crlf");
                }
                value.InitialManager = new WorktreeManager(value.GitImage, value.Executor, value.Original);
                value.Manager = value.InitialManager.WithParentAdmission(value.Parent, "owned");
                await value.Manager.PrepareAsync(value.Repository, value.Worker, head, TestContext.Current.CancellationToken);
                var observed = await value.Manager.ObserveAsync(value.Worker, TestContext.Current.CancellationToken);
                Assert.Equal(head, observed.HeadCommit); Assert.True(observed.IsClean);
                return value;
            }
            catch { value.Dispose(); throw; }
        }
        internal CommandInvocation VersionInvocation()
        {
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(row => !((string)row.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => (string)row.Key, row => row.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = "NUL";
            environment["GIT_CONFIG_SYSTEM"] = "NUL"; environment["GIT_TERMINAL_PROMPT"] = "0";
            return new(GitImage, ["--version"], Worker, environment) { EnvironmentIsComplete = true };
        }
        internal OwnedDuplexProcess OpenOverlayGit(WorktreeManager.OwnedGitBinding binding, params string[] arguments)
        {
            var environment = VersionInvocation().Environment!.ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
            environment["GIT_DIR"] = binding.GitDirectory; environment["GIT_COMMON_DIR"] = binding.CommonDirectory;
            environment["GIT_WORK_TREE"] = binding.Worktree; environment["GIT_INDEX_FILE"] = binding.IndexPath;
            environment["GIT_OBJECT_DIRECTORY"] = binding.ObjectsPath; environment["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = binding.CommonObjectsPath;
            return OwnedDuplexProcess.Open(new(GitImage, ["-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", .. arguments],
                Worker, environment)
            { EnvironmentIsComplete = true }, Original);
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
