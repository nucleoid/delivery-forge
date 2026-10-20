using System.Globalization;
using System.Text.Json;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Planning;
namespace DeliveryForge.Tool.Tests;

// Two separately admitted live UAT cases. Genuine native fixture process and
// actual original parent, never supported AI-host or production publication.
[Trait("Category", "LiveContinuumAcceptance")]
public sealed class ParentNativeContinuumLiveTests
{
    public sealed record Profile(string Kind, TrustedConfiguration Configuration, AdmissionFile PlanBundle, LocalWorkerPaths Paths, string RunStore, StandaloneHostSelection Native, IReadOnlyList<AdmissionFile> NativeClosure, AdmissionFile? LinuxScope, ParentCoordinationConfiguration OtherPrincipal, string EvidenceOutput, AdmissionFile? InstalledQualityProfile = null, ProtectedBaselineRegistrySelection? BaselineRegistry = null, string? RegistryPolicyRelativePath = null);
    sealed class Evidence(AdmissionFile pin, ToolLifetime original) : IContinuumDeploymentEvidenceSource
    {
        public Task<ContinuumDeploymentEvidence> ObserveAsync(CancellationToken token = default) { token.ThrowIfCancellationRequested(); var raw = InstalledHostAdmission.ReadExact(pin, original, 65536); _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(raw); return Task.FromResult(JsonSerializer.Deserialize<ContinuumDeploymentEvidence>(raw, BoundedFiles.Json) ?? throw new IOException("Null actual operator evidence.")); }
    }
    sealed class Frame
    {
        static readonly List<Frame> Retained = [];
        internal readonly ToolLifetime Original = new(TimeSpan.FromSeconds(600), TestContext.Current.CancellationToken);
        internal readonly ToolLifetime ObserverOriginal = new(TimeSpan.FromSeconds(600), TestContext.Current.CancellationToken);
        internal readonly List<Task> Tasks = []; internal string ProfileSha256 = ""; internal Profile Selected = null!; internal ParentNativeContractComposition Owner = null!; internal FrozenPlan Plan = null!;
        internal ContinuumMcpTransport? A, B; internal readonly List<(CoordinationResource Resource, AcquireTransportResult Lease)> Successors = [];
        internal LeaseOwner? Other; internal bool OwnerDisposed, Completed; internal Exception? Failure;
        internal T Keep<T>(T task) where T : Task { if (Tasks.Count >= 4096) throw new IOException("Finite live-frame operation bound."); Tasks.Add(task); return task; }
        internal static async Task<Frame> Open(string name, bool hold = false)
        {
            var f = new Frame(); lock (Retained) { if (Retained.Count >= 8) throw new IOException("Finite retained live frames exhausted."); Retained.Add(f); }
            var key = "DELIVERY_FORGE_PARENT_NATIVE_" + name; var path = Environment.GetEnvironmentVariable(key) ?? throw new IOException("Actual independently admitted native-parent live profile required.");
            var expected = Environment.GetEnvironmentVariable(key + "_SHA256") ?? throw new IOException("Exact independent profile digest required.");
            var bytes = BoundedFiles.Read(path, f.Original, 65536); Assert.Equal(expected, BoundedFiles.Sha(bytes)); f.ProfileSha256 = expected; _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(bytes);
            f.Selected = JsonSerializer.Deserialize<Profile>(bytes, BoundedFiles.Json) ?? throw new IOException("Null profile."); var p = f.Selected;
            if (p.Kind != "RootAdmittedGenuineNativeParentFixture-v1" || p.Configuration.Adapter != ParentNativeContractPort.AdapterId || p.Configuration.Coordination is null || p.Configuration.Coordination.Project != "delivery-forge-uat" || p.OtherPrincipal.Project != p.Configuration.Coordination.Project || p.OtherPrincipal.Endpoint != p.Configuration.Coordination.Endpoint || p.OtherPrincipal.PrincipalId == p.Configuration.Coordination.PrincipalId || p.Paths.AllowedPaths.Count != 1 || p.Paths.Exclusions.Count != 0) throw new IOException("Exact distinct-principal isolated fixture selection required.");
            foreach (var protectedPath in new[] { p.RunStore, p.EvidenceOutput, p.Configuration.Coordination.CheckpointRoot, p.Configuration.Coordination.PrivateKeyPath, p.Configuration.Coordination.DeploymentEvidence.Path, p.OtherPrincipal.PrivateKeyPath, p.OtherPrincipal.DeploymentEvidence.Path }) BoundedFiles.Separate(p.Configuration.RepositoryRoot, protectedPath);
            var workerRoots = new[] { p.Configuration.RepositoryRoot, p.Paths.Worktree, p.Paths.ResultDirectory };
            foreach (var worker in workerRoots) BoundedFiles.Plain(worker, true);
            BoundedFiles.Separate(p.Configuration.RepositoryRoot, p.Paths.Worktree); BoundedFiles.Separate(p.Configuration.RepositoryRoot, p.Paths.ResultDirectory); BoundedFiles.Separate(p.Paths.Worktree, p.Paths.ResultDirectory);
            var privatePaths = new[] { path, p.RunStore, p.EvidenceOutput, p.PlanBundle.Path, p.Configuration.Coordination.CheckpointRoot, p.Configuration.Coordination.PrivateKeyPath, p.Configuration.Coordination.DeploymentEvidence.Path, p.OtherPrincipal.PrivateKeyPath, p.OtherPrincipal.DeploymentEvidence.Path };
            foreach (var worker in workerRoots) foreach (var protectedPath in privatePaths) BoundedFiles.Separate(worker, protectedPath);
            foreach (var file in p.NativeClosure) foreach (var worker in workerRoots) BoundedFiles.Separate(worker, file.Path);
            if (p.LinuxScope is not null) foreach (var worker in workerRoots) BoundedFiles.Separate(worker, p.LinuxScope.Path);
            BoundedFiles.Separate(p.RunStore, p.Configuration.Coordination.CheckpointRoot); BoundedFiles.Separate(p.RunStore, p.EvidenceOutput); BoundedFiles.Separate(p.Configuration.Coordination.CheckpointRoot, p.EvidenceOutput);
            foreach (var fresh in new[] { p.RunStore, p.Configuration.Coordination.CheckpointRoot, p.Paths.ResultDirectory }) { BoundedFiles.Plain(fresh, true); if (!Directory.Exists(fresh) || Directory.EnumerateFileSystemEntries(fresh).Take(1).Any()) throw new IOException("Fresh existing empty private store/checkpoint/results required."); }
            if (File.Exists(p.EvidenceOutput)) throw new IOException("Fresh evidence output required.");
            LinuxScopeSelection? scope = null; if (OperatingSystem.IsLinux()) { var pin = p.LinuxScope ?? throw new IOException("Actual Linux scope required."); scope = JsonSerializer.Deserialize<LinuxScopeSelection>(InstalledHostAdmission.ReadExact(pin, f.Original, 65536), BoundedFiles.Json) ?? throw new IOException("Null scope."); }
            f.Owner = await f.Keep(ParentNativeContractComposition.CreateAsync(p.Configuration, f.Original, p.RunStore, p.Native, p.NativeClosure, scope, hold ? ParentNativeContractMode.HoldUntilSameOriginalStop : ParentNativeContractMode.Complete)).WaitAsync(f.Original.Remaining, TestContext.Current.CancellationToken);
            var c = p.Configuration; var expectedPlan = new PlanImportExpectations(c.ExpectedBundleIdentity!, c.ExpectedContractIdentity!, c.Repository, c.ExpectedWorkItem!, c.RepositoryRoot, c.MutableReference, c.ExpectedPlanCeiling!);
            var imported = await f.Keep(PlanImporter.ImportAndReconcileOwnedAsync(InstalledHostAdmission.ReadExact(p.PlanBundle, f.Original, 524288), new(c.RepositoryRoot, c.MutableReference, c.GitExecutable), expectedPlan, TimeSpan.FromSeconds(120), f.Owner.Executor, f.Original, cancellationToken: f.Original.Token)).WaitAsync(f.Original.Remaining, TestContext.Current.CancellationToken);
            Assert.True(imported.DownstreamReady, string.Join(";", imported.Diagnostics)); f.Plan = imported.Plan!; return f;
        }
        internal ContinuumMcpTransport Wire(ParentCoordinationConfiguration c) => new(c.Endpoint, c.Project, c.PrincipalId, new ContinuumApiKeyFile(c.PrivateKeyPath), new Evidence(c.DeploymentEvidence, ObserverOriginal), TimeSpan.FromSeconds(10));
        internal async Task<(string Run, IReadOnlyList<LeaseEvidence> Leases)> CompleteWorker()
        {
            var result = await Keep(Owner.ImplementAsync(Plan, Selected.Paths)).WaitAsync(Original.Remaining, TestContext.Current.CancellationToken); Assert.Equal("COMPLETE", result.Result.Outcome); Assert.Equal("CONTRACT_FIXTURE_COMPLETE", result.Result.Code);
            Assert.Equal(HostObservationRealm.ContractFixture, Owner.Bridge.Realm); Assert.Equal(ParentNativeContractPort.AdapterId, Owner.Bridge.AdapterProfile.AdapterId);
            Assert.True(Owner.Bridge.ObserveCoordinationWorkSettled(result.Assignment.RunId)); var frame = Assert.Single(Owner.Bridge.RetainedFrames); Assert.True(frame.OwnerCompleted); Assert.False(frame.Unknown);
            var data = Owner.Parent.ObserveContractFixtureData(); Assert.False(data.Revoked); Assert.Equal(result.Assignment.RunId, data.RunId); Assert.Equal(4, data.Leases.Count); Assert.Equal(4, data.Leases.Select(lease => lease.Resource.ResourceKey).Distinct().Count());
            var output = BoundedFiles.Read(Path.Combine(result.Assignment.Worktree, Selected.Paths.AllowedPaths[0]), Original, 4096); Assert.NotNull(frame.Accepted); Assert.Equal(System.Text.Encoding.UTF8.GetBytes("genuine native parent contract fixture " + frame.Accepted.RuntimeTaskIdentity + "\n"), output);
            return (data.RunId, data.Leases);
        }
        internal async Task<(string Run, IReadOnlyList<LeaseEvidence> Leases, Task<(ToolResult Result, FrozenWorkerPlan Assignment)> Implementation, DeliveryForge.Execution.Git.WorktreeObservation Before)> StartHeldWorker()
        {
            var before = await Keep(Owner.ObserveWorkAsync(Selected.Paths.Worktree)).WaitAsync(Original.Remaining, TestContext.Current.CancellationToken); Assert.True(before.IsClean); Assert.Equal(Plan.BaseCommit, before.HeadCommit);
            var implementation = Keep(Owner.ImplementAsync(Plan, Selected.Paths)); var started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                Original.Check(); if (implementation.IsCompleted) throw new IOException("Held original implement ended before external loss."); if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(30)) throw new IOException("Actual accepted pending native task missing.");
                var frame = Owner.Bridge.RetainedFrames.SingleOrDefault(); var native = Owner.Native.OriginalProcess; var wait = Owner.Adapter.ActualCalls.SingleOrDefault(call => call.Kind == "Wait");
                if (frame?.Accepted is not null && wait?.ActualTask is { IsCompleted: false } && Owner.Native.ActiveRead is { IsCompleted: false } && native is not null && native.StartupFailure is null && native.HasRetainedOriginalOwner && native.OriginalRetirementTask is null && native.ActualIo.Any(row => row.Kind == "stdout-read" && row.InvocationReturned && row.ActualTask is { IsCompleted: false }))
                {
                    Assert.False(frame.OwnerCompleted); Assert.False(frame.OwnerStopped); var binding = Owner.Native.ActualBinding; Assert.NotNull(binding); Assert.Equal(frame.Accepted.RequestIdentity, binding.RequestIdentity); Assert.Equal(frame.Accepted.RuntimeRunIdentity, binding.RuntimeRunIdentity); Assert.Equal(frame.Accepted.RuntimeTaskIdentity, binding.RuntimeTaskIdentity);
                    var data = Owner.Parent.ObserveContractFixtureData(); Assert.False(data.Revoked); Assert.Equal(frame.RunId, data.RunId); Assert.Equal(4, data.Leases.Count); return (data.RunId, data.Leases, implementation, before);
                }
                await Keep(Task.Delay(25, Original.Token));
            }
        }
        internal async Task CloseOwner() { await Keep(Owner.DisposeAsync().AsTask()).WaitAsync(Original.CleanupRemaining, TestContext.Current.CancellationToken); OwnerDisposed = true; }
        internal async Task Finish(object evidence)
        {
            Assert.True(OwnerDisposed); Assert.All(Tasks, task => Assert.True(task.IsCompletedSuccessfully));
            if (A is not null) await Keep(A.DisposeAsync().AsTask()).WaitAsync(ObserverOriginal.Remaining, TestContext.Current.CancellationToken);
            if (B is not null) await Keep(B.DisposeAsync().AsTask()).WaitAsync(ObserverOriginal.Remaining, TestContext.Current.CancellationToken);
            Assert.False(Original.HasRetainedUnknown); BoundedFiles.WriteNew(Selected.EvidenceOutput, JsonSerializer.SerializeToUtf8Bytes(new { Kind = "ActualParentNativeFixtureCaseEnvelope-v1", ProfileSha256, ToolAssemblySha256 = BoundedFiles.Sha(BoundedFiles.Read(typeof(ParentCoordinationHost).Assembly.Location, ObserverOriginal, 16777216)), Fixture = true, ProductionHostAccepted = false, ProductionPublicationAccepted = false, ProductionSchedulingAccepted = false, Case = evidence }, BoundedFiles.Json), ObserverOriginal); Completed = true; Original.Dispose(); ObserverOriginal.Dispose(); lock (Retained) Retained.Remove(this);
        }
    }
    // Genuine approved artifacts are mandatory. This is issuer conformance only;
    // historical baseline metrics do not become a current quality execution.
    [Fact(Timeout = 650000)]
    public async Task Actual_owner_pinned_protected_registry_issues_baseline_after_real_artifact_validation()
    {
        var f = await Frame.Open("REGISTRY");
        NormalizedEvidence? comparison = null;
        try
        {
            var actual = await f.CompleteWorker();
            var selected = f.Selected.InstalledQualityProfile ?? throw new IOException("Real exact nonfixture installed profile required.");
            var registrySelection = f.Selected.BaselineRegistry ?? throw new IOException("Actual complete-artifact owner approval and protected registry required; never fabricated in tests.");
            var policyPath = f.Selected.RegistryPolicyRelativePath ?? throw new IOException("Actual protected policy locator required.");
            ProtectedBaselineRegistry.ValidateSelection(registrySelection);
            foreach (var worker in new[] { f.Selected.Configuration.RepositoryRoot, f.Selected.Paths.Worktree, f.Selected.Paths.ResultDirectory })
                foreach (var input in new[] { selected.Path, registrySelection.OwnerApproval.Path, registrySelection.ArtifactRoot }) BoundedFiles.Separate(worker, input);
            var files = new InstalledToolFiles(f.Original, TimeSpan.FromSeconds(300), new InstalledToolHostScope.Credits(), f.Owner.Parent, actual.Run);
            var profile = InstalledToolProfile.LoadPinned(selected.Path, "sha256:" + selected.Sha256, files);
            Assert.False(profile.Fixture);
            var executor = new InstalledToolGateProvider.ActualExecutor(files, profile.Data.Environment, profile.Data.GitExecutablePath, f.Owner.Parent, actual.Run);
            var policy = await f.Keep(new ProtectedGitPolicyResolver(executor, f.Selected.Paths.Worktree, f.Plan.BaseCommit).ResolveAsync(policyPath, f.Original.Token)).WaitAsync(f.Original.Remaining, TestContext.Current.CancellationToken);
            var registry = await f.Keep(ProtectedBaselineRegistry.ResolveAsync(executor, files, f.Selected.Paths.Worktree, policy, registrySelection, f.Original.Token)).WaitAsync(f.Original.Remaining, TestContext.Current.CancellationToken);
            Assert.Equal(f.Plan.BaseCommit, registry.Baseline.SourceRevision);
            Assert.Equal(registrySelection.RegistryCommit, registry.RegistryCommit);
            Assert.NotEmpty(registry.Baseline.Metrics);
            var advisory = new ReviewedBaseline(registry.Baseline.BaselineIdentity, registry.Baseline.PolicyIdentity,
                registry.Baseline.SourceRevision, registry.Baseline.ContentIdentity, false, registry.Baseline.Metrics);
            Assert.Equal(GateOutcome.Incomplete, BaselineComparer.Compare(advisory.Metrics, advisory, policy).Outcome);
            comparison = files.Effect(() => { registry.RecheckApproval(files); return BaselineComparer.Compare(registry.Baseline.Metrics, registry.Baseline, policy); });
            files.ReleaseKnown();
            await f.CloseOwner();
            await f.Finish(new
            {
                Kind = "ActualParentProtectedBaselineIssuerConformance-v1",
                Fixture = true,
                actual.Run,
                ActualNativeWorkerCompleted = true,
                GenuineLeases = 4,
                registry.RegistryCommit,
                BaselineH0 = registry.Baseline.SourceRevision,
                registry.ArtifactManifestIdentity,
                ActualComparison = comparison.Outcome.ToString(),
                CurrentQualityExecuted = false,
                OwnerApprovalInvented = false
            });
        }
        catch (Exception error) { f.Failure = error; f.Original.RetainUnknown(); f.ObserverOriginal.RetainUnknown(); throw; }
        // Known owner/IO disposition precedes the causal RED assertion. RED can be
        // an ordinary assertion failure without pretending an unclosed frame quiet.
        Assert.NotNull(comparison);
        Assert.Equal(GateOutcome.Pass, comparison.Outcome);
    }

    // Actual native worker/four leases and real Git positive precede revocation.
    // No fake executor, Git process, parent authority or clock. UNEXECUTED.
    [Fact(Timeout = 650000)]
    public async Task Actual_quality_git_after_yield_requires_parent_admission_before_each_birth()
    {
        var f = await Frame.Open("QUALITY_GIT");
        try
        {
            var actual = await f.CompleteWorker();
            var git = new DeliveryForge.Execution.Git.WorktreeManager(f.Selected.Configuration.GitExecutable, f.Owner.Executor, f.Original)
                .WithParentAdmission(f.Owner.Parent, actual.Run);
            var positive = await f.Keep(git.ObserveAsync(f.Selected.Paths.Worktree, f.Original.Token));
            Assert.True(positive.IsClean); Assert.NotEqual(f.Plan.BaseCommit, positive.HeadCommit); Assert.Equal(Assert.Single(f.Owner.Bridge.RetainedFrames).Completion!.HeadCommit, positive.HeadCommit);
            var births = git.ActualOwnedGitInvocations; Assert.True(births > 0);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = f.Keep(f.Owner.Parent.EnterAsync(actual.Run, async () =>
            {
                entered.SetResult(true); await release.Task.ConfigureAwait(false);
                try { await git.EnsureDescendsFromAsync(f.Selected.Paths.Worktree, f.Plan.BaseCommit, positive.HeadCommit, f.Original.Token).ConfigureAwait(false); return (Exception?)null; }
                catch (CoordinationUncertainException denied) { return denied; }
            }));
            await f.Keep(entered.Task).WaitAsync(f.Original.Remaining, TestContext.Current.CancellationToken);
            var quiet = await f.Keep(f.Owner.Parent.ObserveAsync(actual.Leases[0].Owner, f.Original.Token));
            Assert.False(quiet.Quiescent); Assert.True(f.Owner.Parent.ObserveContractFixtureData().Revoked);
            // Observing the refusal must not itself end WORK before the resumed
            // operation reaches parent admission. Bound this wait while WORK is
            // still current; owner disposition starts cleanup afterwards.
            var refusalWait = TimeSpan.FromTicks(Math.Min(f.Original.Remaining.Ticks, TimeSpan.FromSeconds(10).Ticks));
            release.SetResult(true);
            var denied = await operation.WaitAsync(refusalWait, TestContext.Current.CancellationToken);
            Assert.IsType<CoordinationUncertainException>(denied); Assert.Equal(births, git.ActualOwnedGitInvocations);
            await f.CloseOwner();
            await f.Finish(new { Kind = "ActualParentCurrentnessGitRevokedBirthControl-v1", Fixture = true, actual.Run, GenuineLeases = 4, NativeWorkerCompleted = true, PositiveGitBirths = births, BirthsAfterRevocation = 0, ParentObservationQuiet = false });
        }
        catch (Exception error) { f.Failure = error; f.Original.RetainUnknown(); f.ObserverOriginal.RetainUnknown(); throw; }
    }
    // Genuine admitted live parent/leases/native completion, then actual pending effect.
    // No fake quality executor/result. Root must supply a fresh QUALITY profile per row.
    [Theory(Timeout = 650000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_quality_io_after_entered_task_yields_rechecks_revoked_parent(bool persist)
    {
        var f = await Frame.Open(persist ? "QUALITY_WRITE" : "QUALITY_READ");
        try
        {
            var actual = await f.CompleteWorker();
            var selected = f.Selected.InstalledQualityProfile ?? throw new IOException("Fresh pinned genuine installed profile required for quality IO control.");
            foreach (var worker in new[] { f.Selected.Configuration.RepositoryRoot, f.Selected.Paths.Worktree, f.Selected.Paths.ResultDirectory }) BoundedFiles.Separate(worker, selected.Path);
            var files = new InstalledToolFiles(f.Original, TimeSpan.FromSeconds(120), new InstalledToolHostScope.Credits(), f.Owner.Parent, actual.Run);
            var raw = files.Read(selected.Path, 1048576);
            Assert.Equal(selected.Bytes, raw.LongLength); Assert.Equal(selected.Sha256, BoundedFiles.Sha(raw));
            var profile = InstalledToolProfile.LoadPinned(selected.Path, "sha256:" + selected.Sha256, files);
            Assert.NotNull(profile); // Genuine current profile read before revocation is mandatory.
            var target = f.Selected.EvidenceOutput + ".quality-after-await.json";
            Assert.False(File.Exists(target));
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = f.Keep(f.Owner.Parent.EnterAsync(actual.Run, async () =>
            {
                entered.SetResult(true);
                await release.Task.ConfigureAwait(false);
                try
                {
                    if (persist) files.WriteNew(target, raw);
                    else _ = InstalledToolProfile.LoadPinned(selected.Path, "sha256:" + selected.Sha256, files);
                    return (Exception?)null;
                }
                catch (CoordinationUncertainException refused) { return refused; }
            }));
            await f.Keep(entered.Task).WaitAsync(f.Original.Remaining, TestContext.Current.CancellationToken);
            Assert.False(operation.IsCompleted);
            var current = f.Owner.Parent.ObserveContractFixtureData();
            Assert.False(current.Revoked); Assert.Equal(4, current.Leases.Count);
            // Actual production quiescence observation closes this parent's admission.
            // Pending real effect makes quiet=false; it is not a native completion claim.
            var quiet = await f.Keep(f.Owner.Parent.ObserveAsync(actual.Leases[0].Owner, f.Original.Token));
            Assert.False(quiet.Quiescent); Assert.True(f.Owner.Parent.ObserveContractFixtureData().Revoked);
            // Do not start cleanup merely to wait for the causal refusal.
            var refusalWait = TimeSpan.FromTicks(Math.Min(f.Original.Remaining.Ticks, TimeSpan.FromSeconds(10).Ticks));
            release.SetResult(true);
            var refusal = await operation.WaitAsync(refusalWait, TestContext.Current.CancellationToken);
            Assert.IsType<CoordinationUncertainException>(refusal);
            Assert.False(File.Exists(target));
            await f.CloseOwner();
            await f.Finish(new { Kind = "ActualParentInstalledQualityRevokedIoControl-v1", Fixture = true, actual.Run, Persist = persist, ActualNativeWorkerCompleted = true, GenuineLeases = 4, EnteredPendingEffect = true, ParentObservationQuiet = false, RefusedAfterAwait = true, ResultPublished = false });
        }
        catch (Exception error) { f.Failure = error; f.Original.RetainUnknown(); f.ObserverOriginal.RetainUnknown(); throw; }
    }

    [Fact(Timeout = 650000)]
    public async Task Actual_parent_native_fixture_completion_releases_four_current_leases()
    {
        var f = await Frame.Open("COMPLETE"); try
        {
            var actual = await f.CompleteWorker(); f.B = f.Wire(f.Selected.OtherPrincipal); _ = await f.Keep(f.B.DetectCapabilitiesAsync(f.Original.Token));
            await f.CloseOwner(); var observer = new LeaseOwner(f.Selected.OtherPrincipal.PrincipalId, Guid.NewGuid().ToString("D"));
            foreach (var lease in actual.Leases) { var seen = await f.Keep(f.B.InspectAsync(new(lease.Resource, observer, Guid.NewGuid().ToString("D")), f.ObserverOriginal.Token)); Assert.Equal(TransportOutcome.Inspected, seen.Outcome); Assert.False(seen.Held); }
            await f.Finish(new { Kind = "ActualParentNativeFixtureCompletion-v1", Fixture = true, ProductionHostAccepted = false, ProductionPublicationAccepted = false, actual.Run, ResourceCount = actual.Leases.Count, OwnerDisposed = true });
        }
        catch (Exception error) { f.Failure = error; f.Original.RetainUnknown(); f.ObserverOriginal.RetainUnknown(); throw; }
    }
    [Fact(Timeout = 650000)]
    public async Task Actual_parent_external_loss_blocks_effects_and_preserves_four_successors()
    {
        var f = await Frame.Open("LOSS"); try
        {
            var actual = await f.CompleteWorker(); var dispatched = 0; var published = 0;
            _ = await f.Keep(f.Owner.Parent.EnterAsync(actual.Run, () => Task.FromResult(++dispatched))); _ = await f.Keep(f.Owner.Parent.EnterAsync(actual.Run, () => Task.FromResult(++published)));
            f.A = f.Wire(f.Selected.Configuration.Coordination!); f.B = f.Wire(f.Selected.OtherPrincipal); _ = await f.Keep(f.A.DetectCapabilitiesAsync(f.Original.Token)); _ = await f.Keep(f.B.DetectCapabilitiesAsync(f.Original.Token)); f.Other = new(f.Selected.OtherPrincipal.PrincipalId, Guid.NewGuid().ToString("D"));
            foreach (var lease in actual.Leases)
            {
                var released = await f.Keep(f.A.ReleaseAsync(new(lease.LeaseId, lease.Owner, lease.FencingToken, Guid.NewGuid().ToString("D")), f.Original.Token)); Assert.Equal(TransportOutcome.Released, released.Outcome);
                var next = await f.Keep(f.B.AcquireAsync(new(lease.Resource, f.Other, TimeSpan.FromSeconds(300), Guid.NewGuid().ToString("D")), f.Original.Token)); Assert.Equal(TransportOutcome.Acquired, next.Outcome); Assert.NotNull(next.LeaseId); Assert.NotNull(next.FencingToken); Assert.True(long.Parse(next.FencingToken, CultureInfo.InvariantCulture) > long.Parse(lease.FencingToken, CultureInfo.InvariantCulture)); f.Successors.Add((lease.Resource, next));
            }
            // Genuine current source interval is60sec; bounded real wait, no clock/TTL manipulation.
            var started = System.Diagnostics.Stopwatch.GetTimestamp(); while (!f.Owner.Parent.ObserveContractFixtureData().Revoked) { f.Original.Check(); if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(90)) throw new IOException("Actual renewal did not revoke within bounded genuine interval."); await f.Keep(Task.Delay(250, f.Original.Token)); }
            var lost = f.Owner.Parent.ObserveContractFixtureData(); Assert.NotNull(lost.RenewalTask); Assert.True(lost.RenewalTask.IsCompletedSuccessfully); var actualRenewal = await lost.RenewalTask; Assert.Null(actualRenewal.Authority); Assert.Equal("renew-response-invalid", actualRenewal.Snapshot.Reason);
            Assert.Equal(actual.Leases.Select(lease => lease.Generation).Order(StringComparer.Ordinal), lost.RenewalInputs.Select(lease => lease.Generation).Order(StringComparer.Ordinal));
            Assert.Contains(lost.WireRenewals, row => row.Outcome == TransportOutcome.LeaseLost && actual.Leases.Any(lease => lease.LeaseId == row.LeaseId && lease.Owner.RunId == row.RunId && lease.FencingToken == row.FencingToken && lease.Resource.Scope == row.Scope && lease.Resource.ResourceKey == row.Resource));
            Assert.NotNull(lost.StopTask); await f.Keep(lost.StopTask).WaitAsync(f.Original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.True(lost.StopTask.IsCompletedSuccessfully);
            var stopped = f.Owner.Parent.ObserveContractFixtureData(); Assert.NotNull(stopped.StopResult); Assert.Equal("INCOMPLETE", stopped.StopResult.Outcome); Assert.Equal("STOP_RETAINED", stopped.StopResult.Code); var terminalFrame = Assert.Single(f.Owner.Bridge.RetainedFrames); Assert.True(terminalFrame.OwnerCompleted); Assert.False(terminalFrame.OwnerStopped); var terminalFailure = Assert.IsType<WorkerEnvelopeException>(terminalFrame.Primary); Assert.Equal("Cannot stop a run in terminal state LocalComplete.", terminalFailure.Message); Assert.True(f.Owner.Bridge.ObserveCoordinationWorkSettled(actual.Run));
            var dispatchDenied = Assert.Throws<TimeoutException>(() => { _ = f.Owner.Parent.EnterAsync(actual.Run, () => Task.FromResult(++dispatched)); }); Assert.Equal("Original tool WORK endpoint expired.", dispatchDenied.Message); var publicationDenied = Assert.Throws<TimeoutException>(() => { _ = f.Owner.Parent.EnterAsync(actual.Run, () => Task.FromResult(++published)); }); Assert.Equal("Original tool WORK endpoint expired.", publicationDenied.Message); Assert.Equal(1, dispatched); Assert.Equal(1, published);
            await f.CloseOwner(); foreach (var next in f.Successors) { var seen = await f.Keep(f.B.InspectAsync(new(next.Resource, f.Other, Guid.NewGuid().ToString("D")), f.ObserverOriginal.Token)); Assert.True(seen.CallerOwnsLease); Assert.Equal(next.Lease.LeaseId, seen.LeaseId); Assert.Equal(next.Lease.FencingToken, seen.FencingToken); }
            foreach (var next in f.Successors) { var released = await f.Keep(f.B.ReleaseAsync(new(next.Lease.LeaseId!, f.Other, next.Lease.FencingToken!, Guid.NewGuid().ToString("D")), f.ObserverOriginal.Token)); Assert.Equal(TransportOutcome.Released, released.Outcome); }
            await f.Finish(new { Kind = "ActualParentNativeFixtureExternalLoss-v1", Fixture = true, ExternalLeaseLossInjection = true, ProductionHostAccepted = false, ProductionPublicationAccepted = false, actual.Run, SuccessorsPreserved = 4, Dispatches = dispatched, DryRunPublications = published, ActualRenewalIntervalSeconds = 60, StopTerminalRefused = "STOP_RETAINED/INCOMPLETE", NativeOriginallySettled = true });
        }
        catch (Exception error) { f.Failure = error; f.Original.RetainUnknown(); f.ObserverOriginal.RetainUnknown(); throw; }
    }
    [Fact(Timeout = 650000)]
    public async Task Actual_parent_active_native_lease_loss_stops_same_original_and_preserves_successors()
    {
        var f = await Frame.Open("ACTIVE", hold: true); try
        {
            var actual = await f.StartHeldWorker(); var dispatched = 0; var published = 0;
            _ = await f.Keep(f.Owner.Parent.EnterAsync(actual.Run, () => Task.FromResult(++dispatched))); _ = await f.Keep(f.Owner.Parent.EnterAsync(actual.Run, () => Task.FromResult(++published)));
            f.A = f.Wire(f.Selected.Configuration.Coordination!); f.B = f.Wire(f.Selected.OtherPrincipal); _ = await f.Keep(f.A.DetectCapabilitiesAsync(f.Original.Token)); _ = await f.Keep(f.B.DetectCapabilitiesAsync(f.Original.Token)); f.Other = new(f.Selected.OtherPrincipal.PrincipalId, Guid.NewGuid().ToString("D"));
            foreach (var lease in actual.Leases)
            {
                var released = await f.Keep(f.A.ReleaseAsync(new(lease.LeaseId, lease.Owner, lease.FencingToken, Guid.NewGuid().ToString("D")), f.Original.Token)); Assert.Equal(TransportOutcome.Released, released.Outcome);
                var next = await f.Keep(f.B.AcquireAsync(new(lease.Resource, f.Other, TimeSpan.FromSeconds(300), Guid.NewGuid().ToString("D")), f.Original.Token)); Assert.Equal(TransportOutcome.Acquired, next.Outcome); Assert.NotNull(next.LeaseId); Assert.NotNull(next.FencingToken); Assert.True(long.Parse(next.FencingToken, CultureInfo.InvariantCulture) > long.Parse(lease.FencingToken, CultureInfo.InvariantCulture)); f.Successors.Add((lease.Resource, next));
            }
            // Genuine current source interval is60sec; bounded real wait, no clock/TTL manipulation.
            var started = System.Diagnostics.Stopwatch.GetTimestamp(); while (!f.Owner.Parent.ObserveContractFixtureData().Revoked) { f.Original.Check(); if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(90)) throw new IOException("Actual renewal did not revoke within bounded genuine interval."); await f.Keep(Task.Delay(250, f.Original.Token)); }
            var lost = f.Owner.Parent.ObserveContractFixtureData(); Assert.NotNull(lost.RenewalTask); Assert.True(lost.RenewalTask.IsCompletedSuccessfully); var actualRenewal = await lost.RenewalTask; Assert.Null(actualRenewal.Authority); Assert.Equal("renew-response-invalid", actualRenewal.Snapshot.Reason);
            Assert.Equal(actual.Leases.Select(lease => lease.Generation).Order(StringComparer.Ordinal), lost.RenewalInputs.Select(lease => lease.Generation).Order(StringComparer.Ordinal));
            Assert.Contains(lost.WireRenewals, row => row.Outcome == TransportOutcome.LeaseLost && actual.Leases.Any(lease => lease.LeaseId == row.LeaseId && lease.Owner.RunId == row.RunId && lease.FencingToken == row.FencingToken && lease.Resource.Scope == row.Scope && lease.Resource.ResourceKey == row.Resource));
            Assert.NotNull(lost.StopTask); await f.Keep(lost.StopTask).WaitAsync(f.Original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.True(lost.StopTask.IsCompletedSuccessfully);
            var stopped = f.Owner.Parent.ObserveContractFixtureData(); Assert.NotNull(stopped.StopResult); Assert.Equal("COMPLETE", stopped.StopResult.Outcome); Assert.Equal("OWNER_STOP", stopped.StopResult.Code);
            var interrupted = await actual.Implementation.WaitAsync(f.Original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.Equal("INCOMPLETE", interrupted.Result.Outcome); Assert.Equal("HOST_OPERATION_RETAINED", interrupted.Result.Code);
            var bridgeCall = f.Owner.InterruptedBridgeCall; Assert.NotNull(bridgeCall); Assert.True(bridgeCall.IsFaulted);
            var bridgeFailure = Assert.IsType<TimeoutException>(f.Owner.InterruptedBridgeFailure); Assert.Equal("Original tool WORK endpoint expired.", bridgeFailure.Message);
            Assert.Same(bridgeFailure, Assert.Single(bridgeCall.Exception!.InnerExceptions));
            var stoppedFrame = Assert.Single(f.Owner.Bridge.RetainedFrames); Assert.False(stoppedFrame.OwnerCompleted); Assert.True(stoppedFrame.OwnerStopped); Assert.False(stoppedFrame.Unknown);
            Assert.Same(bridgeFailure, stoppedFrame.Worker.Post); Assert.Null(stoppedFrame.Worker.Primary);
            var operationFailure = Assert.IsType<AggregateException>(stoppedFrame.Primary); Assert.Equal(2, operationFailure.InnerExceptions.Count); var interruptedFailure = Assert.IsType<TimeoutException>(operationFailure.InnerExceptions[0]); Assert.Equal("Original tool WORK endpoint expired.", interruptedFailure.Message); Assert.Same(interruptedFailure, f.Owner.Native.InterruptedWaitFailure); var postFailure = Assert.IsType<TimeoutException>(operationFailure.InnerExceptions[1]); Assert.Equal("Original tool WORK endpoint expired.", postFailure.Message);
            var waitCall = Assert.Single(f.Owner.Adapter.ActualCalls, call => call.Kind == "Wait"); Assert.NotNull(waitCall.ActualTask); Assert.True(waitCall.ActualTask.IsFaulted); Assert.Contains(waitCall.ActualTask.Exception!.InnerExceptions, error => ReferenceEquals(error, interruptedFailure));
            var native = f.Owner.Native.OriginalProcess; Assert.NotNull(native); var retirement = native.OriginalRetirementTask; Assert.NotNull(retirement); Assert.True(retirement.IsCompletedSuccessfully); var retired = await retirement; Assert.True(retired.KnownClosed); Assert.True(retired.RootExited); Assert.Equal(0u, retired.ActiveProcesses); Assert.True(retired.OutputEof); Assert.True(retired.ErrorEof); Assert.True(retired.InputCloseReturned); Assert.True(retired.DispositionReturned); Assert.False(native.HasUnknownIo);
            Assert.NotEmpty(retired.Failures); Assert.All(retired.Failures, failure => Assert.Equal("System.TimeoutException: Original tool WORK endpoint expired.", failure)); Assert.True(f.Owner.Bridge.ObserveCoordinationWorkSettled(actual.Run));
            var observed = await f.Keep(f.Owner.ObserveStoppedWorkAsync(f.Selected.Paths.Worktree, actual.Before.Snapshot, f.Selected.Paths.AllowedPaths)).WaitAsync(f.Original.CleanupRemaining, TestContext.Current.CancellationToken);
            Assert.Equal(actual.Before.HeadCommit, observed.HeadCommit); Assert.Equal(actual.Before.TreeId, observed.TreeId); Assert.False(observed.IsClean); var changed = Assert.Single(observed.StatusEntries); Assert.EndsWith(" " + f.Selected.Paths.AllowedPaths[0], changed);
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes("genuine native parent contract fixture " + stoppedFrame.Accepted!.RuntimeTaskIdentity + "\n"); Assert.Equal(BoundedFiles.Sha(expectedBytes), observed.Snapshot.Files[f.Selected.Paths.AllowedPaths[0]]);
            var dispatchDenied = Assert.Throws<TimeoutException>(() => { _ = f.Owner.Parent.EnterAsync(actual.Run, () => Task.FromResult(++dispatched)); }); Assert.Equal("Original tool WORK endpoint expired.", dispatchDenied.Message); var publicationDenied = Assert.Throws<TimeoutException>(() => { _ = f.Owner.Parent.EnterAsync(actual.Run, () => Task.FromResult(++published)); }); Assert.Equal("Original tool WORK endpoint expired.", publicationDenied.Message); Assert.Equal(1, dispatched); Assert.Equal(1, published);
            await f.CloseOwner(); foreach (var next in f.Successors) { var seen = await f.Keep(f.B.InspectAsync(new(next.Resource, f.Other, Guid.NewGuid().ToString("D")), f.ObserverOriginal.Token)); Assert.True(seen.CallerOwnsLease); Assert.Equal(next.Lease.LeaseId, seen.LeaseId); Assert.Equal(next.Lease.FencingToken, seen.FencingToken); }
            foreach (var next in f.Successors) { var released = await f.Keep(f.B.ReleaseAsync(new(next.Lease.LeaseId!, f.Other, next.Lease.FencingToken!, Guid.NewGuid().ToString("D")), f.ObserverOriginal.Token)); Assert.Equal(TransportOutcome.Released, released.Outcome); }
            await f.Finish(new { Kind = "ActualParentActiveNativeFixtureExternalLoss-v1", Fixture = true, ExternalLeaseLossInjection = true, ProductionHostAccepted = false, ProductionPublicationAccepted = false, actual.Run, SuccessorsPreserved = 4, Dispatches = dispatched, DryRunPublications = published, ActualRenewalIntervalSeconds = 60, SameOwnedStop = "COMPLETE/OWNER_STOP", NativeInterrupted = true, InterruptedReadFailure = f.Owner.Native.InterruptedWaitFailure!.GetType().FullName, OriginalWaitFaultRetained = true, PublicBridgeCallFaultRetained = true, PublicBridgeReturnedResult = false, RetainedCoreOutcome = interrupted.Result.Outcome, BridgePrimaryAndPostFailure = stoppedFrame.Primary!.GetType().FullName, NativeRetirement = retired });
        }
        catch (Exception error) { f.Failure = error; f.Original.RetainUnknown(); f.ObserverOriginal.RetainUnknown(); throw; }
    }
}
