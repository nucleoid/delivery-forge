using DeliveryForge.Evidence;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using System.Text.Json;
namespace DeliveryForge.Tool.Tests;

public sealed class OwnedCleanupObservationTests
{
    public sealed record LocalProfile(string Kind, AdmissionFile Git, string ParentCheckout, string Worktree, string ResultDirectory, string RunStore, string BaseCommit, string Repository, string PlanIdentity, string BundleIdentity, string RelativePath, StandaloneHostSelection Native, IReadOnlyList<AdmissionFile> NativeClosure, AdmissionFile? LinuxScope);
    sealed class LocalFrame
    {
        internal static readonly List<LocalFrame> Retained = [];
        internal readonly ToolLifetime Original = new(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        internal readonly string RunId = "run-" + Guid.NewGuid().ToString("N");
        internal bool BindingCurrent = true;
        internal ParentEffectAdmission Parent = null!;
        internal WorktreeManager InitialGit = null!, AdmittedGit = null!;
        internal readonly List<Task> Tasks = []; internal ExecutionCoordinator? Coordinator; internal ProcessControl? Processes; internal OwnerHostBridge? Bridge; internal ParentNativeContractPort? Port; internal Exception? Failure;
        internal T Keep<T>(T task) where T : Task { Tasks.Add(task); if (Tasks.Count > 4096) throw new IOException("Finite local original task ledger exhausted; entered task retained."); return task; }
    }
    [Fact(Timeout = 90000)]
    public async Task Actual_same_owned_native_stop_writes_checkpoint_under_original_cleanup_only()
    {
        var f = new LocalFrame(); lock (LocalFrame.Retained) { if (LocalFrame.Retained.Count >= 8) throw new IOException("Finite retained local controls exhausted."); LocalFrame.Retained.Add(f); }
        try
        {
            var path = Environment.GetEnvironmentVariable("DELIVERY_FORGE_PARENT_NATIVE_LOCAL") ?? throw new IOException("Exact Root-admitted local native control profile required."); var hash = Environment.GetEnvironmentVariable("DELIVERY_FORGE_PARENT_NATIVE_LOCAL_SHA256") ?? throw new IOException("Independent local profile digest required.");
            var raw = BoundedFiles.Read(path, f.Original, 65536); Assert.Equal(hash, BoundedFiles.Sha(raw)); _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(raw); var selected = JsonSerializer.Deserialize<LocalProfile>(raw, BoundedFiles.Json) ?? throw new IOException("Null local control profile.");
            if (selected.Kind != "RootAdmittedLocalNativeCleanupControl-v2" || selected.NativeClosure.Count != 5 || selected.RelativePath.Length is < 1 or > 1024 || selected.RelativePath.Contains('\\') || selected.RelativePath.StartsWith('/') || selected.RelativePath.Split('/').Any(piece => piece is "" or "." or "..")) throw new IOException("Finite canonical local fixture task required.");
            foreach (var root in new[] { selected.ParentCheckout, selected.Worktree }) foreach (var privatePath in new[] { path, selected.RunStore, selected.ResultDirectory }) BoundedFiles.Separate(root, privatePath);
            foreach (var root in new[] { selected.ParentCheckout, selected.Worktree }) foreach (var pin in selected.NativeClosure.Append(selected.Git).Concat(selected.LinuxScope is AdmissionFile localScope ? new[] { localScope } : Array.Empty<AdmissionFile>())) BoundedFiles.Separate(root, pin.Path);
            BoundedFiles.Separate(selected.ParentCheckout, selected.Worktree); BoundedFiles.Separate(selected.RunStore, selected.ResultDirectory);
            BoundedFiles.Plain(selected.Worktree, true);
            if (Directory.Exists(selected.Worktree) || File.Exists(selected.Worktree))
                throw new IOException("Local cleanup v2 requires the exact independently admitted absent worktree; no existing binding adoption.");
            foreach (var fresh in new[] { selected.RunStore, selected.ResultDirectory }) { BoundedFiles.Plain(fresh, true); if (!Directory.Exists(fresh) || Directory.EnumerateFileSystemEntries(fresh).Take(1).Any()) throw new IOException("Fresh existing empty local control stores required."); }
            _ = InstalledHostAdmission.ReadExact(selected.Git, f.Original, 268435456);
            LinuxScopeSelection? linux = null; if (OperatingSystem.IsLinux()) { var pin = selected.LinuxScope ?? throw new IOException("Actual local Linux scope required."); linux = JsonSerializer.Deserialize<LinuxScopeSelection>(InstalledHostAdmission.ReadExact(pin, f.Original, 65536), BoundedFiles.Json) ?? throw new IOException("Null local scope."); }
            var executor = OperatingSystem.IsLinux() ? new ProcessCommandExecutor(linux!, f.Original) : new ProcessCommandExecutor();
            var git = f.InitialGit = new WorktreeManager(selected.Git.Path, executor, f.Original);
            // A finite private fixture parent owns real provisioning/native Git,
            // not Continuum or a production host capability. The initial view
            // shares the actual binding but cannot acquire a writer.
            f.Parent = new(new object(), run => f.BindingCurrent && run == f.RunId, _ => f.BindingCurrent = false);
            var admitted = f.AdmittedGit = git.WithParentAdmission(f.Parent, f.RunId);
            await f.Keep(admitted.PrepareAsync(selected.ParentCheckout, selected.Worktree, selected.BaseCommit,
                f.Original.Token)).WaitAsync(f.Original.Remaining, TestContext.Current.CancellationToken);
            f.Original.Check();
            var before = await f.Keep(git.ObserveAsync(selected.Worktree, f.Original.Token)).WaitAsync(f.Original.Remaining, TestContext.Current.CancellationToken); Assert.True(before.IsClean); Assert.Equal(selected.BaseCommit, before.HeadCommit);
            f.Port = new ParentNativeContractPort(selected.Native, selected.NativeClosure, git, executor, selected.Git.Path, linux, ParentNativeContractMode.HoldUntilSameOriginalStop); var adapter = new ParentNativeContractAdapter(f.Port); f.Processes = new ProcessControl(); var store = new RunStore(selected.RunStore); f.Coordinator = new(store, admitted, f.Processes, adapter); f.Bridge = new(f.Coordinator, adapter);
            var assignment = new FrozenWorkerPlan(f.RunId, selected.PlanIdentity, selected.BaseCommit, selected.Repository, selected.ParentCheckout, selected.Worktree, selected.ResultDirectory, Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), [selected.RelativePath], [], ParentNativeContractPort.AdapterId, ParentNativeContractPort.Version, [AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Stop, AgentCapability.EvidenceExport]);
            var request = WorkerEnvelope.PrepareWorker(assignment); var instructions = HostInstructionContext.FromParentSelection(request, selected.BundleIdentity, "deterministic genuine native contract fixture; await same-original Stop", ["only assigned nonce-bound file; no AI/provider/publication"], ["write assigned file then hold until original owner stop"]);
            var originalStarted = f.Original.OriginalStarted; var implementation = f.Keep(f.Bridge.ImplementAsync(assignment, instructions, f.Original, f.Original.Token));
            var started = System.Diagnostics.Stopwatch.GetTimestamp(); while (true) { f.Original.Check(); if (implementation.IsCompleted || System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20)) throw new IOException("Actual native pending task missing."); var frame = f.Bridge.RetainedFrames.SingleOrDefault(); if (frame?.Accepted is not null && f.Port.ActiveRead is { IsCompleted: false } && adapter.ActualCalls.Any(call => call.Kind == "Wait" && call.ActualTask is { IsCompleted: false }) && f.Port.OriginalProcess is { OriginalRetirementTask: null, HasRetainedOriginalOwner: true }) break; await f.Keep(Task.Delay(25, f.Original.Token)); }
            var invocationsBeforeStop = git.ActualOwnedGitInvocations; var stop = f.Keep(f.Bridge.StopRetainedAsync(assignment.RunId)); var stopped = await stop.WaitAsync(f.Original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.Equal("COMPLETE", stopped.Outcome); Assert.Equal("OWNER_STOP", stopped.Code);
            var interrupted = await implementation.WaitAsync(f.Original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.Equal("INCOMPLETE", interrupted.Outcome); Assert.Equal("HOST_OPERATION_RETAINED", interrupted.Code); Assert.Equal(originalStarted, f.Original.OriginalStarted); Assert.Throws<TimeoutException>(() => f.Original.Check()); Assert.True(git.ActualOwnedGitInvocations > invocationsBeforeStop);
            var actualFrame = Assert.Single(f.Bridge.RetainedFrames); Assert.True(actualFrame.OwnerStopped); Assert.False(actualFrame.OwnerCompleted); Assert.False(actualFrame.Unknown); Assert.True(f.Bridge.ObserveCoordinationWorkSettled(assignment.RunId)); var operation = Assert.IsType<AggregateException>(actualFrame.Primary); Assert.Equal(2, operation.InnerExceptions.Count); Assert.Same(f.Port.InterruptedWaitFailure, operation.InnerExceptions[0]); Assert.All(operation.InnerExceptions, error => { Assert.IsType<TimeoutException>(error); Assert.Equal("Original tool WORK endpoint expired.", error.Message); });
            var native = f.Port.OriginalProcess!; var retirement = native.OriginalRetirementTask; Assert.NotNull(retirement); Assert.True(retirement.IsCompletedSuccessfully); var retired = await retirement; Assert.True(retired.KnownClosed); Assert.True(retired.RootExited); Assert.Equal(0u, retired.ActiveProcesses); Assert.True(retired.OutputEof && retired.ErrorEof && retired.InputCloseReturned && retired.DispositionReturned); Assert.False(native.HasUnknownIo);
            var recovered = await f.Keep(store.RecoverAsync(assignment.RunId, TestContext.Current.CancellationToken)).WaitAsync(f.Original.CleanupRemaining, TestContext.Current.CancellationToken); var record = recovered.Records.Last(); Assert.Equal("stopped", record.Kind); var checkpoint = store.ReadPayload<QuiescenceRecord>(record); Assert.True(checkpoint.Quiescent); Assert.NotNull(checkpoint.Checkpoint); Assert.Equal(before.HeadCommit, checkpoint.Checkpoint.HeadCommit); Assert.Equal(before.TreeId, checkpoint.Checkpoint.TreeId); Assert.Equal(request.RequestIdentity, checkpoint.Checkpoint.RequestIdentity); Assert.False(checkpoint.Checkpoint.StatusEntries.Count == 0);
            var expected = System.Text.Encoding.UTF8.GetBytes("genuine native parent contract fixture " + actualFrame.Accepted!.RuntimeTaskIdentity + "\n"); Assert.Equal(BoundedFiles.Sha(expected), checkpoint.Checkpoint.Snapshot.Files[selected.RelativePath]); git.EnsureAllowedChanges(before.Snapshot, checkpoint.Checkpoint.Snapshot, [selected.RelativePath], []);
            Assert.All(f.Tasks, task => Assert.True(task.IsCompletedSuccessfully)); f.Coordinator.Dispose(); await f.Keep(f.Processes.DisposeAsync().AsTask()).WaitAsync(f.Original.CleanupRemaining, TestContext.Current.CancellationToken); _ = f.Original.CleanupRemaining; Assert.False(f.Original.HasRetainedUnknown); f.Original.Dispose(); lock (LocalFrame.Retained) LocalFrame.Retained.Remove(f);
        }
        catch (Exception error) { f.Failure = error; f.Original.RetainUnknown(); throw; }
    }
    [Fact(Timeout = 10000)]
    public async Task Actual_expired_original_cleanup_refuses_before_any_owned_git_invocation()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var image = Environment.ProcessPath ?? throw new IOException("Actual existing process image required for prebirth refusal.");
        var manager = new WorktreeManager(image, new ProcessCommandExecutor(), original);
        var started = original.OriginalStarted; _ = original.CleanupRemaining; Assert.Equal(started, original.OriginalStarted);
        Assert.Throws<TimeoutException>(() => original.Check());
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<TimeoutException>(() => manager.ObserveOwnedCleanupAsync(Path.GetTempPath(), TestContext.Current.CancellationToken));
        Assert.Equal(0, manager.ActualOwnedGitInvocations);
    }
}
