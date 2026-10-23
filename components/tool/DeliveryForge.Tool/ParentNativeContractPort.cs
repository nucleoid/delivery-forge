using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
namespace DeliveryForge.Tool;

// Closed test-only identity. TrustedConfiguration.Parse and StandaloneEntrypoint
// reject it. No accepted AI-host profile, provider, scheduler or publication.
internal enum ParentNativeContractMode { Complete, HoldUntilSameOriginalStop }
internal sealed class ParentNativeContractPort : IHostToolSessionPort, IOriginalBoundHostPort, IHostInstructionPort
{
    internal const string AdapterId = "fixture.native-duplex";
    internal const string Version = "contract-v1";
    readonly ParentNativeContractMode mode;
    internal OwnedDuplexProcess? OriginalProcess => process;
    internal Task<int>? ActiveRead { get; private set; }
    internal Exception? InterruptedWaitFailure { get; private set; }
    internal AgentRunBinding? ActualBinding => binding;
    internal WorktreeObservation? BeforeStart { get; private set; }
    FrozenWorkerPlan? provisionedAssignment;
    readonly StandaloneHostSelection image; readonly IReadOnlyList<AdmissionFile> closure;
    readonly WorktreeManager git; readonly ProcessCommandExecutor executor; readonly string gitImage; readonly LinuxScopeSelection? linux;
    ToolLifetime? original; HostInstructionContext? instructions; AgentRequest? request; AgentRunBinding? binding;
    OwnedDuplexProcess? process; readonly List<Task> tasks = []; bool entered;
    internal ParentNativeContractPort(StandaloneHostSelection selected, IReadOnlyList<AdmissionFile> files, WorktreeManager worktrees, ProcessCommandExecutor actualExecutor, string actualGit, LinuxScopeSelection? scope, ParentNativeContractMode selectedMode)
    { if (!Enum.IsDefined(selectedMode)) throw new IOException("Unknown closed fixture mode."); mode = selectedMode; image = selected; closure = files.ToArray(); git = worktrees; executor = actualExecutor; gitImage = actualGit; linux = scope; }
    public HostObservationRealm Realm => HostObservationRealm.ContractFixture;
    public AgentAdapterProfile Profile { get; } = new(AdapterId, Version,
        new[] { AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Stop, AgentCapability.EvidenceExport }
        .Select(capability => new AgentCapabilityEvidence(capability, AgentCapabilityStatus.Supported, "deterministic native contract fixture only; never AI-host support", "fixture:native-duplex-only")).ToArray());
    public void BindOriginal(ToolLifetime actual) { if (original is not null) throw new IOException("Fixture original binds once."); original = actual; }
    public void BindInstructions(HostInstructionContext actual) { if (instructions is not null) throw new IOException("Fixture instructions bind once."); instructions = actual; }
    internal void BindProvisionedObservation(FrozenWorkerPlan assignment, WorktreeObservation observation)
    {
        Life.Check();
        if (entered || provisionedAssignment is not null || !observation.IsClean || observation.HeadCommit != assignment.BaseCommit || !git.IsSameDirectory(observation.RootPath, assignment.Worktree))
            throw new IOException("Exact clean same-original fixture provisioning observation required once before start.");
        provisionedAssignment = assignment; BeforeStart = observation;
    }
    T Keep<T>(T task) where T : Task { lock (tasks) { tasks.Add(task); if (tasks.Count > 16384) { Life.RetainUnknown(); throw new IOException("Finite retained fixture native task ledger exhausted; entered task retained."); } } return task; }
    ToolLifetime Life => original ?? throw new IOException("Original fixture lifetime missing.");
    async Task<string> Line(CancellationToken token) { var bytes = new List<byte>(); var one = new byte[1]; while (true) { Life.Check(); var read = process!.Output.ReadAsync(one, 0, 1, token); ActiveRead = read; var count = await Keep(read).ConfigureAwait(false); if (count == 0) throw new IOException("Fixture EOF before complete line."); if (one[0] == 10) return new UTF8Encoding(false, true).GetString(bytes.ToArray()); if (bytes.Count >= 4096) throw new IOException("Fixture line exceeds4096."); if (one[0] == 13) throw new IOException("Canonical LF-only fixture protocol required."); bytes.Add(one[0]); } }
    public async Task<AgentAcceptedReceipt> StartAsync(AgentRequest actual, TimeSpan remaining, CancellationToken token)
    {
        Life.Check(); if (entered) throw new IOException("Fixture start already entered; no replay."); entered = true;
        if (actual.AdapterId != AdapterId || actual.AdapterVersion != Version || actual.AllowedPaths.Count != 1 || actual.Exclusions.Count != 0 || remaining <= TimeSpan.Zero)
            throw new WorkerEnvelopeException("Exact single-path deterministic fixture assignment required.");
        (instructions ?? throw new IOException("Actual parent instructions required.")).Require(actual); request = actual;
        if (image.Version != Version || closure.Count != 5) throw new IOException("Exact normal five-file fixture closure required.");
        var directory = Path.GetDirectoryName(image.Executable)!;
        if (!Directory.EnumerateFileSystemEntries(directory).Take(6).Select(Path.GetFullPath).Order(StringComparer.Ordinal).SequenceEqual(closure.Select(pin => Path.GetFullPath(pin.Path)).Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new IOException("Fixture output membership changed.");
        foreach (var pin in closure) _ = InstalledHostAdmission.ReadExact(pin, Life, 268435456);
        if (!closure.Any(pin => Path.GetFullPath(pin.Path) == Path.GetFullPath(image.Executable) && pin.Bytes == image.Bytes && pin.Sha256 == image.Sha256)) throw new IOException("Fixture image not in pinned closure.");
        if (provisionedAssignment is null || BeforeStart is null || provisionedAssignment.RunId != actual.RunId || provisionedAssignment.Worktree != actual.Worktree || provisionedAssignment.BaseCommit != actual.BaseCommit)
            throw new IOException("Actual same-assignment prebirth provisioning observation missing.");
        var nonce = Guid.NewGuid().ToString("N"); var environment = new Dictionary<string, string> { ["DOTNET_PROCESSOR_COUNT"] = "2", ["DOTNET_GCHeapHardLimit"] = "0x100000000", ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1", ["MSBUILDDISABLENODEREUSE"] = "1" };
        process = OwnedDuplexProcess.Open(new(image.Executable, [mode == ParentNativeContractMode.Complete ? "parent-contract" : "parent-contract-hold", actual.Worktree, nonce], actual.Worktree, environment), Life, linux);
        if (process.StartupFailure is Exception error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        var ready = await Line(token).ConfigureAwait(false); var parts = ready.Split('|');
        if (parts.Length != 4 || parts[0] != "READY" || System.Numerics.BitOperations.PopCount(unchecked((ulong)long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture))) != 2 || parts[2] != "2" || parts[3] != "0x100000000") throw new IOException("Actual fixture prebirth resources differ.");
        var data = JsonSerializer.SerializeToUtf8Bytes(new { Path = actual.AllowedPaths[0], Git = gitImage, Nonce = nonce });
        await Keep(process.Input.WriteAsync(data, 0, data.Length, token)).ConfigureAwait(false); await Keep(process.Input.WriteAsync(new byte[] { 10 }, 0, 1, token)).ConfigureAwait(false); await Keep(process.Input.FlushAsync(token)).ConfigureAwait(false);
        if (mode == ParentNativeContractMode.HoldUntilSameOriginalStop && await Line(token).ConfigureAwait(false) != "STARTED|" + nonce) throw new IOException("Foreign active fixture STARTED.");
        binding = new(actual.AdapterId, actual.AdapterVersion, actual.RunId, actual.RequestIdentity, "native-contract-" + nonce, nonce);
        Life.Check(); return new(actual.SchemaVersion, actual.RunId, actual.RequestIdentity, actual.PlanIdentity, actual.BaseCommit, actual.Worktree, actual.AdapterId, actual.AdapterVersion, binding.RuntimeRunIdentity, binding.RuntimeTaskIdentity, DateTimeOffset.UtcNow, Profile.Capabilities);
    }
    void Bound(AgentRunBinding actual) { if (binding is null || actual != binding) throw new WorkerEnvelopeException("No same-original fixture binding."); }
    public async Task<AgentCompletionReceipt> WaitAsync(AgentRunBinding actual, TimeSpan remaining, CancellationToken token)
    {
        Bound(actual); Life.Check();
        if (mode == ParentNativeContractMode.HoldUntilSameOriginalStop)
        {
            try { var unexpected = await Line(token).ConfigureAwait(false); throw new IOException("Held fixture unexpectedly completed: " + unexpected); }
            catch (Exception error) { InterruptedWaitFailure = error; throw; }
        }
        var completed = await Line(token).ConfigureAwait(false);
        if (completed != "WRITTEN|" + actual.RuntimeTaskIdentity) throw new IOException("Foreign/malformed fixture completion.");
        var eof = new byte[1]; if (await Keep(process!.Output.ReadAsync(eof, 0, 1, token)).ConfigureAwait(false) != 0) throw new IOException("Trailing fixture output refused.");
        var closed = await Keep(process.RetireAsync(false)).ConfigureAwait(false); HostTransportRetirement.RequireSuccessful(closed);
        var expectedBytes = Encoding.UTF8.GetBytes("genuine native parent contract fixture " + actual.RuntimeTaskIdentity + "\n");
        var actualPath = Path.Combine(request!.Worktree, request.AllowedPaths[0]);
        if (!BoundedFiles.Read(actualPath, Life, 4096).AsSpan().SequenceEqual(expectedBytes)) throw new IOException("Actual native task bytes differ before deterministic parent commit.");
        await CommitFixtureAsync(request!, token).ConfigureAwait(false);
        if (!BoundedFiles.Read(actualPath, Life, 4096).AsSpan().SequenceEqual(expectedBytes)) throw new IOException("Actual native task bytes changed across parent commit.");
        foreach (var pin in closure) _ = InstalledHostAdmission.ReadExact(pin, Life, 268435456);
        if (Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(image.Executable)!).Take(6).Count() != 5) throw new IOException("Fixture output entry membership changed after native execution.");
        var observation = await Keep(git.ObserveAsync(request!.Worktree, token)).ConfigureAwait(false); Life.Check();
        if (!observation.IsClean || observation.HeadCommit == request.BaseCommit) throw new IOException("Fixture did not leave genuine clean committed work.");
        var artifact = Path.Combine(request.ResultDirectory, "parent-native-contract.json");
        BoundedFiles.WriteNew(artifact, JsonSerializer.SerializeToUtf8Bytes(new { Kind = "GenuineNativeParentContractFixture-v1", Fixture = true, ProductionHostAccepted = false, ProductionPublicationAccepted = false, actual.RunId, actual.RequestIdentity, Retirement = closed, ExpectedFileSha256 = BoundedFiles.Sha(expectedBytes), ActualFileSha256 = BoundedFiles.Sha(BoundedFiles.Read(actualPath, Life, 4096)), NativeTaskNonce = actual.RuntimeTaskIdentity, ParentDeterministicCommit = true, observation.HeadCommit, observation.TreeId }, BoundedFiles.Json), Life);
        return new(request.SchemaVersion, request.RunId, request.RequestIdentity, request.AdapterId, request.AdapterVersion, actual.RuntimeRunIdentity, actual.RuntimeTaskIdentity, "success", request.Worktree, observation.HeadCommit, observation.TreeId, DateTimeOffset.UtcNow, [artifact], ["Fixture-only genuine native task; no supported AI-host or publication certification."]);
    }
    async Task CommitFixtureAsync(AgentRequest actual, CancellationToken token)
    {
        // Genuine deterministic parent-side fixture commit through the existing
        // bounded creation-owned executor. Never an AI completion assertion.
        var filters = new HashSet<string>(StringComparer.Ordinal);
        var configured = await GitCapture(actual.Worktree, ["config", "--includes", "--name-only", "--get-regexp", "^filter\\."], [], token).ConfigureAwait(false);
        if (configured.Exit is not (0 or 1)) throw new IOException("Fixture Git filter inventory failed.");
        foreach (var key in configured.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(key => key.TrimEnd('\r')))
        {
            var separator = key.LastIndexOf('.'); if (!key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) || separator <= 7 || key.Any(char.IsControl)) throw new IOException("Ambiguous Git filter key.");
            var name = key[7..separator]; if (name.Length > 256 || name.Contains('=') || filters.Count >= 128) throw new IOException("Fixture filter bound exceeded."); filters.Add(name);
        }
        var overrides = new List<string>(); foreach (var filter in filters) foreach (var setting in new[] { "process=", "smudge=", "clean=", "required=false" }) { overrides.Add("-c"); overrides.Add("filter." + filter + "." + setting); }
        var added = await GitCapture(actual.Worktree, ["add", "--", actual.AllowedPaths[0]], overrides, token).ConfigureAwait(false); if (added.Exit != 0) throw new IOException("Fixture Git add failed.");
        var committed = await GitCapture(actual.Worktree, ["-c", "user.name=DeliveryForgeContractFixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgSign=false", "commit", "-m", "genuine parent native fixture write", "--", actual.AllowedPaths[0]], overrides, token).ConfigureAwait(false); if (committed.Exit != 0) throw new IOException("Fixture Git commit failed.");
    }
    async Task<(int Exit, string Text)> GitCapture(string cwd, IReadOnlyList<string> arguments, IReadOnlyList<string> filters, CancellationToken token)
    {
        Life.Check(); var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(row => (string)row.Key, row => row.Value?.ToString() ?? "", OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_CONFIG", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT" }) env.Remove(name);
        foreach (var name in env.Keys.Where(name => name.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal) || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal)).ToArray()) env.Remove(name);
        var disabled = OperatingSystem.IsWindows() ? "NUL" : "/dev/null"; env["GIT_NO_REPLACE_OBJECTS"] = "1"; env["GIT_GRAFT_FILE"] = disabled; env["GIT_NO_LAZY_FETCH"] = "1"; env["GIT_TERMINAL_PROMPT"] = "0";
        env["DOTNET_PROCESSOR_COUNT"] = "2"; env["DOTNET_GCHeapHardLimit"] = "0x100000000";
        var argv = new List<string> { "-c", "core.hooksPath=" + disabled, "-c", "core.fsmonitor=false" }; argv.AddRange(filters); argv.AddRange(arguments);
        var result = await Keep(executor.ExecuteBoundedCaptureAsync(new(gitImage, argv.ToArray(), cwd, env) { EnvironmentIsComplete = true }, TimeSpan.FromSeconds(30), 65536, token, new OwnedWorkPhaseBudget(Life))).ConfigureAwait(false); Life.Check();
        var command = result.Command; var facts = command.LifecycleDiagnostics;
        if (result.CaptureBoundExceeded || command.TimedOut || command.Cancelled || !command.OwnedProcessQuiescent || !result.RootExitObserved || !result.StandardOutputDrainCompleted || !result.StandardErrorDrainCompleted || facts?.NativeFailure is not null || facts?.OwnedTree is not { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 } || OperatingSystem.IsLinux() && result.OwnershipKind != BoundedCaptureOwnershipKind.LinuxCreationOwnedScope || command.ExitCode is null) throw new IOException("Actual original fixture Git group/stream closure incomplete.");
        return (command.ExitCode.Value, new UTF8Encoding(false, true).GetString(result.StandardOutputBytes));
    }
    public async Task<AgentControlResult> ControlAsync(AgentRunBinding actual, AgentControlAction action, TimeSpan remaining, CancellationToken token)
    {
        Bound(actual); if (action != AgentControlAction.Stop) return new(actual, action, AgentActivityState.Unknown, false, "", "Fixture has only same-original stop.");
        if (process is null) throw new IOException("Original fixture process missing.");
        var closed = await Keep(process.RetireAsync(true)).ConfigureAwait(false);
        return new(actual, action, closed.KnownClosed ? AgentActivityState.Quiescent : AgentActivityState.Unknown, true, "fixture:original-retirement", "Actual native contract fixture only.");
    }
    internal bool ObserveOriginalSettled(string runId)
    {
        lock (tasks)
        {
            if (request?.RunId != runId || process?.OriginalRetirementTask is not { IsCompletedSuccessfully: true } retired || !retired.Result.KnownClosed || process.HasUnknownIo) return false;
            if (mode == ParentNativeContractMode.Complete) return tasks.All(task => task.IsCompletedSuccessfully);
            // Preserve the exact original interrupted facade-read WORK-post failure.
            // Only returned native EOF+known physical closure permits this expected
            // stopped fixture observation; arbitrary fault/cancel remains nonquiet.
            bool ExpectedPost(Exception? error) => error is TimeoutException && error.Message == "Original tool WORK endpoint expired.";
            if (!ExpectedPost(InterruptedWaitFailure) || ActiveRead is not { IsFaulted: true } read) return false;
            if (tasks.Any(task => !task.IsCompletedSuccessfully && !ReferenceEquals(task, read))) return false;
            var io = process.ActualIo;
            if (!io.Any(row => row.Kind == "stdout-read" && row.ActualResult is int count && count == 0 && ExpectedPost(row.Post))) return false;
            return io.All(row => row.InvocationReturned && row.ActualTask is { IsCompletedSuccessfully: true } && row.Primary is null && (row.Post is null || row.ActualResult is int count && count == 0 && ExpectedPost(row.Post)));
        }
    }
}

internal sealed class ParentNativeContractAdapter(ParentNativeContractPort port) : NamedHostAdapter(ParentNativeContractPort.AdapterId, port);

// Internal friend-test route only. It cannot be selected through production CLI
// configuration parsing, installed-host acceptance or a serialized fixture flag.
internal sealed class ParentNativeContractComposition : IAsyncDisposable
{
    readonly ToolLifetime original; readonly TrustedConfiguration config; readonly ProcessControl processes; readonly ExecutionCoordinator coordinator;
    internal ParentCoordinationHost Parent { get; private set; } = null!;
    internal OwnerHostBridge Bridge { get; }
    internal ProcessCommandExecutor Executor { get; }
    internal ParentNativeContractPort Native { get; }
    internal NamedHostAdapter Adapter { get; }
    readonly WorktreeManager actualGit; readonly ParentNativeContractMode mode;
    internal WorktreeManager Git => actualGit;
    ParentNativeContractComposition(TrustedConfiguration actual, ToolLifetime life, string store, StandaloneHostSelection image, IReadOnlyList<AdmissionFile> closure, LinuxScopeSelection? linux, ParentNativeContractMode selectedMode)
    {
        if (actual.Adapter != ParentNativeContractPort.AdapterId || actual.AdapterVersion != ParentNativeContractPort.Version || actual.StandaloneRoot is not null || actual.Coordination is null || actual.OperationSeconds != 600 || actual.MaximumCeiling != "implement" || actual.ExpectedPlanCeiling != "implement") throw new ToolUsageException("Closed fixture configuration only; no installed host/publication authority.");
        mode = selectedMode; config = actual; original = life; Executor = OperatingSystem.IsLinux() ? new ProcessCommandExecutor(linux ?? throw new IOException("Actual Linux scope required."), life) : new ProcessCommandExecutor();
        var git = new WorktreeManager(actual.GitExecutable, Executor, life); actualGit = git; var port = new ParentNativeContractPort(image, closure, git, Executor, actual.GitExecutable, linux, selectedMode); Native = port; var adapter = new ParentNativeContractAdapter(port); Adapter = adapter;
        processes = new ProcessControl(); coordinator = new(new RunStore(store), git, processes, adapter); Bridge = new(coordinator, adapter, requireCoordination: true);
        Bridge.BindContractFixtureProvisioning(this);
    }
    bool provisioningEntered;
    internal async Task<bool> ProvisionAsync(FrozenWorkerPlan assignment, ParentCoordinationHost actualParent)
    {
        original.Check();
        if (provisioningEntered || !ReferenceEquals(actualParent, Parent)) throw new IOException("The same acquired fixture parent provisions a fresh worktree once.");
        provisioningEntered = true;
        var admitted = actualGit.WithParentAdmission(actualParent, assignment.RunId);
        _ = coordinator.BindOriginalScheduling(original, actualParent, assignment.RunId, admitted);
        await Keep(admitted.PrepareAsync(assignment.ParentCheckout, assignment.Worktree, assignment.BaseCommit, original.Token)).WaitAsync(original.Remaining).ConfigureAwait(false);
        var before = await Keep(admitted.ObserveAsync(assignment.Worktree, original.Token)).WaitAsync(original.Remaining).ConfigureAwait(false);
        Native.BindProvisionedObservation(assignment, before);
        original.Check(); return true;
    }
    static readonly List<ParentNativeContractComposition> Retained = []; bool disposeEntered; readonly List<Task> lifecycle = [];
    internal Task<ToolResult>? InterruptedBridgeCall { get; private set; }
    internal Exception? InterruptedBridgeFailure { get; private set; }
    T Keep<T>(T task) where T : Task { lock (lifecycle) { if (lifecycle.Count >= 16) throw new IOException("Finite fixture composition ledger exhausted."); lifecycle.Add(task); } return task; }
    internal static async Task<ParentNativeContractComposition> CreateAsync(TrustedConfiguration config, ToolLifetime life, string store, StandaloneHostSelection image, IReadOnlyList<AdmissionFile> closure, LinuxScopeSelection? linux, ParentNativeContractMode selectedMode = ParentNativeContractMode.Complete)
    {
        life.Check(); var owner = new ParentNativeContractComposition(config, life, store, image, closure, linux, selectedMode); lock (Retained) { if (Retained.Count >= 16) throw new IOException("Finite retained fixture owners exhausted."); Retained.Add(owner); }
        try { owner.Parent = await owner.Keep(ParentCoordinationHost.CreateAsync(config.Coordination!, life)).WaitAsync(life.Remaining).ConfigureAwait(false); life.Check(); owner.Bridge.BindCoordination(owner.Parent); return owner; } catch { life.RetainUnknown(); throw; }
    }
    internal async Task<(ToolResult Result, FrozenWorkerPlan Assignment)> ImplementAsync(FrozenPlan plan, LocalWorkerPaths paths)
    {
        original.Check(); actualGit.ValidateDestination(config.RepositoryRoot, paths.Worktree);
        if (Directory.Exists(paths.Worktree) || File.Exists(paths.Worktree)) throw new IOException("Fresh absent native fixture destination required before acquiring leases.");
        var assignment = await Keep(new LocalWorkerAssignmentPort(original, paths).SelectAsync(new(plan, config, null, null), original.Remaining, original.Token)).WaitAsync(original.Remaining).ConfigureAwait(false); original.Check();
        Bridge.BindCoordinationSelection(plan, assignment, config, original);
        var actualBridge = Keep(Bridge.ImplementAsync(assignment, HostInstructionContext.FromFrozenPlan(plan, assignment), original, original.Token));
        ToolResult result;
        if (mode == ParentNativeContractMode.HoldUntilSameOriginalStop)
        {
            // The public bridge observer must retain its expired-WORK fault.
            // Only this closed fixture may inspect the original core's nonpass
            // DATA after the same owner's stop has actually settled.
            try { _ = await actualBridge.WaitAsync(original.Remaining).ConfigureAwait(false); throw new IOException("Interrupted bridge unexpectedly returned a result."); }
            catch (TimeoutException error) when (actualBridge.IsFaulted && Bridge.RetainedFrames.Count == 1 &&
                Bridge.RetainedFrames.Single().Worker.Primary is null && ReferenceEquals(Bridge.RetainedFrames.Single().Worker.Post, error))
            { InterruptedBridgeCall = actualBridge; InterruptedBridgeFailure = error; }
            _ = original.CleanupRemaining; var stopped = Parent.ObserveContractFixtureData().StopTask ?? throw new IOException("Interrupted fixture has no original lease-loss Stop task.");
            await Keep(stopped).WaitAsync(original.CleanupRemaining).ConfigureAwait(false); _ = original.CleanupRemaining; var frame = Bridge.RetainedFrames.Single();
            if (!ExpectedStoppedBridgeFailure(actualBridge)) throw new IOException("Expected interrupted original fixture is not genuinely settled.");
            result = frame.Worker.Actual!.Result; // Actual retained core DATA, never a successful bridge return.
        }
        else { result = await actualBridge.WaitAsync(original.Remaining).ConfigureAwait(false); original.Check(); }
        return (result, assignment);
    }
    bool ExpectedStoppedBridgeFailure(Task actual)
    {
        if (mode != ParentNativeContractMode.HoldUntilSameOriginalStop || !ReferenceEquals(actual, InterruptedBridgeCall) ||
            !actual.IsFaulted || InterruptedBridgeFailure is not TimeoutException ||
            actual.Exception!.InnerExceptions.Count != 1 || !ReferenceEquals(actual.Exception.InnerExceptions[0], InterruptedBridgeFailure) ||
            Bridge.RetainedFrames.Count != 1) return false;
        var frame = Bridge.RetainedFrames.Single(); var core = frame.Worker.Actual;
        return frame.Worker.Primary is null && ReferenceEquals(frame.Worker.Post, InterruptedBridgeFailure) &&
            core is { IsCompletedSuccessfully: true } && core.Result.Outcome == "INCOMPLETE" && core.Result.Code == "HOST_OPERATION_RETAINED" &&
            frame.OwnerStopped && !frame.OwnerCompleted && !frame.Unknown && Native.ObserveOriginalSettled(frame.RunId);
    }
    internal Task<WorktreeObservation> ObserveWorkAsync(string root) => Keep(actualGit.ObserveAsync(root, original.Token));
    internal async Task<WorktreeObservation> ObserveStoppedWorkAsync(string root, FileSnapshot before, IReadOnlyList<string> allowed)
    {
        var observed = await Keep(actualGit.ObserveOwnedCleanupAsync(root, CancellationToken.None)).WaitAsync(original.CleanupRemaining).ConfigureAwait(false); _ = original.CleanupRemaining;
        actualGit.EnsureAllowedChanges(before, observed.Snapshot, allowed, Array.Empty<string>()); return observed;
    }
    public async ValueTask DisposeAsync()
    {
        if (disposeEntered) throw new IOException("Original fixture composition disposition already entered."); disposeEntered = true;
        Exception? primary = null, post = null;
        try
        {
            lock (lifecycle) if (lifecycle.Any(task => !task.IsCompletedSuccessfully && !ExpectedStoppedBridgeFailure(task))) throw new IOException("Original fixture composition operation remains failed/unsettled.");
            if (Bridge.RetainedFrames.Any(frame => frame.Unknown)) throw new IOException("Original fixture work unsettled; owners retained.");
            if (Parent is null) throw new IOException("Original fixture parent construction did not return.");
            var frame = Bridge.RetainedFrames.Single();
            bool Quiet() => !frame.Unknown && Bridge.ObserveCoordinationWorkSettled(frame.RunId) && Native.ObserveOriginalSettled(frame.RunId);
            // Final disposition also follows deliberate lease-loss cases. The
            // existing cleanup route checks this exact completed receipt/live
            // writer and first close without requiring renewed forward authority.
            if (frame.OwnerCompleted && frame.Completion is { } completed)
                await Keep(coordinator.ReleaseCompletedWriterAfterFailureAsync(frame.RunId, completed, Quiet)).WaitAsync(original.CleanupRemaining).ConfigureAwait(false);
            else if (frame.OwnerStopped)
                await Keep(coordinator.ReleaseStoppedWriterAsync(frame.RunId, Quiet)).WaitAsync(original.CleanupRemaining).ConfigureAwait(false);
            else throw new IOException("Actual completed or stopped fixture writer required before lease disposition.");
            await Keep(Parent.DisposeAsync().AsTask()).WaitAsync(original.CleanupRemaining).ConfigureAwait(false);
            coordinator.Dispose(); await Keep(processes.DisposeAsync().AsTask()).WaitAsync(original.CleanupRemaining).ConfigureAwait(false);
        }
        catch (Exception error) { primary = error; original.RetainUnknown(); }
        try { _ = original.CleanupRemaining; } catch (Exception error) { post = error; original.RetainUnknown(); }
        if (primary is not null && post is not null) throw new AggregateException("Fixture original disposition and independent same endpoint post.", primary, post);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
        lock (Retained) Retained.Remove(this);
    }
}
