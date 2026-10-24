using System.Text.Json;
using DeliveryForge.Planning;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;

public sealed class ProgramFailureTests
{
    [Fact]
    public void First_caught_capture_does_not_enter_cleanup_or_output_and_is_nonsecret_once_only()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        var actual = new IOException("do-not-export-this-secret", new InvalidOperationException("nor-this-secret"));
        life.RecordFirstCaught("review-source-preparation", actual); attention.EmitFirstCaughtOnce();
        life.Check(); Assert.Equal("", errors.ToString()); // Capture did not latch cleanup; Check would throw if it had.
        life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        life.RecordFirstCaught("standalone", new TimeoutException("later endpoint must not replace first catch"));
        attention.EmitFirstCaughtOnce();
        var lines = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => row.TrimEnd('\r')).ToArray();
        Assert.Equal(2, lines.Length); Assert.StartsWith("DFCLI_FAILURE|", lines[0], StringComparison.Ordinal);
        const string prefix = "DFCLI_FIRST_CAUGHT|"; Assert.StartsWith(prefix, lines[1], StringComparison.Ordinal);
        using var json = JsonDocument.Parse(lines[1][prefix.Length..]); var diagnostic = json.RootElement;
        Assert.Equal("review-source-preparation", diagnostic.GetProperty("Stage").GetString());
        Assert.Equal(typeof(IOException).FullName, diagnostic.GetProperty("Type").GetString());
        Assert.Equal(actual.HResult, diagnostic.GetProperty("HResult").GetInt32());
        Assert.Equal(typeof(InvalidOperationException).FullName, diagnostic.GetProperty("InnerType").GetString());
        Assert.False(diagnostic.GetProperty("CleanupAlreadyBegun").GetBoolean()); Assert.False(diagnostic.GetProperty("MarkerAlreadyEntered").GetBoolean());
        Assert.DoesNotContain("secret", errors.ToString(), StringComparison.Ordinal); Assert.False(diagnostic.TryGetProperty("Message", out _));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(lines[1]) <= 1024); Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
    }
    [Fact]
    public void Marker_before_capture_records_cleanup_consequence_flags_at_later_root_emission()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new StringWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.BeginFailureCleanup(); var consequence = Assert.Throws<TimeoutException>(() => life.Check());
        life.RecordFirstCaught("review-source-preparation", consequence);
        life.BeginFailureCleanup(); Assert.DoesNotContain("DFCLI_FIRST_CAUGHT|", errors.ToString(), StringComparison.Ordinal);
        attention.EmitFirstCaughtOnce(); attention.EmitFirstCaughtOnce();
        var line = Assert.Single(errors.ToString().Split('\n'), row => row.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(line["DFCLI_FIRST_CAUGHT|".Length..]); var diagnostic = json.RootElement;
        Assert.True(diagnostic.GetProperty("CleanupAlreadyBegun").GetBoolean()); Assert.True(diagnostic.GetProperty("MarkerAlreadyEntered").GetBoolean());
        Assert.Equal(typeof(TimeoutException).FullName, diagnostic.GetProperty("Type").GetString()); Assert.False(life.HasExplicitUnknown);
        Assert.Throws<TimeoutException>(() => life.Check()); // Diagnostic cannot reopen WORK or refund a cleanup latch.
    }
    sealed class BlockingDiagnosticWriter : StringWriter
    {
        readonly object writeGate = new();
        internal readonly TaskCompletionSource DiagnosticStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PermitDiagnostic = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DiagnosticWrites;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            var text = buffer.ToString(); lock (writeGate) base.WriteLine(text);
            if (!text.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal)) return Task.CompletedTask;
            Interlocked.Increment(ref DiagnosticWrites); DiagnosticStarted.TrySetResult(); return PermitDiagnostic.Task;
        }
    }
    [Fact(Timeout = 10000)]
    public async Task Concurrent_marker_and_root_diagnostic_do_not_turn_a_settled_marker_into_unknown()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new BlockingDiagnosticWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.RecordFirstCaught("host-implement", new IOException("actual caught host failure"));
        using var start = new Barrier(3); var token = TestContext.Current.CancellationToken;
        Task Invoke() => Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5), token)); life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce(); }, token);
        var a = Invoke(); var b = Invoke(); Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5), token));
        try
        {
            await errors.DiagnosticStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True(attention.Unknown); Assert.False(life.HasExplicitUnknown);
            life.BeginFailureCleanup(); Assert.False(life.HasExplicitUnknown); // Marker-only callback while the distinct diagnostic is pending.
        }
        finally { errors.PermitDiagnostic.TrySetResult(); }
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(1, errors.DiagnosticWrites); Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
        Assert.Equal(2, errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
    sealed class FailedDiagnosticWriter : StringWriter
    {
        internal int DiagnosticWrites;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Span.StartsWith("DFCLI_FIRST_CAUGHT|", StringComparison.Ordinal))
            { DiagnosticWrites++; return Task.FromException(new IOException("actual diagnostic writer failed")); }
            return base.WriteLineAsync(buffer, cancellationToken);
        }
    }
    [Fact]
    public void Failed_root_diagnostic_retains_actual_task_uncertainty_without_retry_or_fallback()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120)); using var errors = new FailedDiagnosticWriter();
        var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.RecordFirstCaught("host-implement", new IOException("actual host failure"));
        life.BeginFailureCleanup(); Assert.Equal(0, errors.DiagnosticWrites);
        attention.EmitFirstCaughtOnce(); attention.EmitFirstCaughtOnce(); life.BeginFailureCleanup();
        Assert.Equal(1, errors.DiagnosticWrites); Assert.True(attention.Unknown); Assert.True(life.HasExplicitUnknown);
        Assert.Single(errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
    [Fact(Timeout = 10000)]
    public async Task Actual_standalone_catch_emits_nonsecret_diagnostic_before_root_failure_delivery()
    {
        using var output = new StringWriter(); using var errors = new StringWriter();
        var exit = await StandaloneEntrypoint.RunAsync(["not-a-tool-mode"], output, errors).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, exit); Assert.Equal("", output.ToString());
        var lines = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => row.TrimEnd('\r')).ToArray();
        Assert.StartsWith("DFCLI_FAILURE|", lines[0], StringComparison.Ordinal);
        const string prefix = "DFCLI_FIRST_CAUGHT|"; Assert.StartsWith(prefix, lines[1], StringComparison.Ordinal);
        using var json = JsonDocument.Parse(lines[1][prefix.Length..]); var diagnostic = json.RootElement;
        Assert.Equal("standalone", diagnostic.GetProperty("Stage").GetString()); Assert.Equal(typeof(ToolUsageException).FullName, diagnostic.GetProperty("Type").GetString());
        Assert.False(diagnostic.GetProperty("CleanupAlreadyBegun").GetBoolean()); Assert.False(diagnostic.GetProperty("MarkerAlreadyEntered").GetBoolean());
        using var report = JsonDocument.Parse(string.Join('\n', lines.Skip(2))); Assert.Equal("ACTUAL_OPERATION_FAILED", report.RootElement.GetProperty("Code").GetString());
    }
    sealed class CancellingIssue(CancellationTokenSource caller) : IIssueContentPort
    {
        public Task<PlanIntent>? Actual { get; private set; }
        public Task<PlanIntent> ReadPublicIssueAsync(string issue, TimeSpan remaining, CancellationToken token) { caller.Cancel(); return Actual = Task.FromException<PlanIntent>(new IOException("actual issue-read primary")); }
    }
    [Fact]
    public async Task Genuine_tool_Observe_dual_primary_and_endpoint_failure_returns_bounded_structured_ERROR()
    {
        var directory = Path.Combine(Path.GetTempPath(), "delivery-forge-error-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var config = Path.Combine(directory, "config.json"); File.WriteAllBytes(config, JsonSerializer.SerializeToUtf8Bytes(new TrustedConfiguration("fixture/repo", directory, "refs/heads/main", Path.Combine(directory, "explicit-git-never-invoked")), BoundedFiles.Json)); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var issue = new CancellingIssue(cancel); var application = new ToolApplication(issues: issue); using var output = new StringWriter(); using var errors = new StringWriter();
        var exit = await Program.ExecuteAsync(["plan", "--repo", directory, "--issue", "fixture/repo#1", "--config", config, "--json"], application, output, errors, cancel.Token); Assert.Equal(1, exit); Assert.Empty(output.ToString()); using var report = JsonDocument.Parse(errors.ToString()); Assert.Equal("ERROR", report.RootElement.GetProperty("Outcome").GetString()); var root = report.RootElement.GetProperty("Failure"); Assert.Equal(typeof(AggregateException).FullName, root.GetProperty("Type").GetString()); var causes = root.GetProperty("Causes"); Assert.Equal(2, causes.GetArrayLength()); Assert.Equal("actual issue-read primary", causes[0].GetProperty("Message").GetString()); Assert.Contains("Canceled", causes[1].GetProperty("Type").GetString(), StringComparison.Ordinal); Assert.Single(application.RetainedExternalCalls); Assert.NotNull(application.RetainedExternalCalls[0].Primary); Assert.NotNull(application.RetainedExternalCalls[0].Post); Assert.NotNull(issue.Actual); Assert.True(issue.Actual.IsFaulted); Assert.Same(issue.Actual, application.RetainedExternalCalls[0].ActualTask); Assert.IsType<IOException>(application.RetainedExternalCalls[0].Primary);
    }
    [Fact]
    public void Huge_actual_error_message_and_deep_aggregate_are_explicitly_unavailable_never_silently_truncated()
    {
        var actual = new AggregateException(new IOException(new string('a', 8192)), new TimeoutException("independent post")); var report = ToolFailureReport.Create(actual); Assert.True(report.DetailsUnavailable); Assert.True(report.Failure.Causes[0].MessageUnavailable); Assert.Null(report.Failure.Causes[0].Message); Assert.Equal("independent post", report.Failure.Causes[1].Message); Assert.True(JsonSerializer.SerializeToUtf8Bytes(report).Length < 65536);
    }
    [Fact]
    public void Aggregate_collection_admission_and_complete_output_node_bound_are_explicit()
    {
        var many = new AggregateException(Enumerable.Range(0, 17).Select(x => new IOException("cause-" + x))); var refused = ToolFailureReport.Create(many); Assert.True(refused.DetailsUnavailable); Assert.Empty(refused.Failure.Causes); Assert.Equal(17, many.InnerExceptions.Count);
        var nested = new AggregateException(new AggregateException(Enumerable.Range(0, 14).Select(x => new IOException("nested-" + x))), new IOException("later-sibling")); var bounded = ToolFailureReport.Create(nested); Assert.True(bounded.DetailsUnavailable); Assert.Equal(16, Count(bounded.Failure)); Assert.Equal(2, nested.InnerExceptions.Count);
        static int Count(ToolFailureNode node) => 1 + node.Causes.Sum(Count);
    }

    sealed class CancellingAssignment(CancellationTokenSource caller) : IWorkerAssignmentPort
    {
        public Task<DeliveryForge.Execution.FrozenWorkerPlan>? Actual { get; private set; }
        public ParentAssignmentRequest? Request { get; private set; }
        public readonly IOException Failure = new("actual assignment primary");
        public Task<DeliveryForge.Execution.FrozenWorkerPlan> SelectAsync(ParentAssignmentRequest request, TimeSpan remaining, CancellationToken cancellationToken)
        {
            Request = request; caller.Cancel(); return Actual = Task.FromException<DeliveryForge.Execution.FrozenWorkerPlan>(Failure);
        }
    }
    sealed class UncalledHost : IHostToolSessionPort
    {
        public DeliveryForge.Execution.Host.AgentAdapterProfile Profile => DeliveryForge.Execution.Host.KnownAgentProfiles.CodexCli("assignment-fixture");
        public HostObservationRealm Realm => HostObservationRealm.ContractFixture;
        public int Calls { get; private set; }
        public Task<DeliveryForge.Execution.Host.AgentAcceptedReceipt> StartAsync(DeliveryForge.Execution.Host.AgentRequest request, TimeSpan remaining, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("Host must not start after assignment failure."); }
        public Task<DeliveryForge.Execution.Host.AgentCompletionReceipt> WaitAsync(DeliveryForge.Execution.Host.AgentRunBinding binding, TimeSpan remaining, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("No actual host was started."); }
        public Task<DeliveryForge.Execution.Host.AgentControlResult> ControlAsync(DeliveryForge.Execution.Host.AgentRunBinding binding, DeliveryForge.Execution.Host.AgentControlAction action, TimeSpan remaining, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("No actual host was started."); }
    }
    [Fact]
    public async Task Genuine_public_implement_import_preserves_already_faulted_assignment_after_caller_cancel()
    {
        var token = TestContext.Current.CancellationToken;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120), cancel.Token);
        var root = Path.Combine(Path.GetTempPath(), "forge-assignment-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repository"); life.Check(); Directory.CreateDirectory(repo); life.Check();
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "git.exe" : "git")).First(File.Exists);
        var executor = new DeliveryForge.Evidence.ProcessCommandExecutor();
        async Task Git(params string[] arguments)
        {
            life.Check(); var nullPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var argv = new[] { "--no-replace-objects", "-c", "core.hooksPath=" + nullPath, "-c", "core.fsmonitor=false", "-c", "user.name=AssignmentFixture", "-c", "user.email=fixture@example.invalid" }.Concat(arguments).ToArray();
            var env = new Dictionary<string, string> { { "GIT_CONFIG_NOSYSTEM", "1" }, { "GIT_CONFIG_GLOBAL", nullPath }, { "GIT_CONFIG_SYSTEM", nullPath }, { "GIT_NO_REPLACE_OBJECTS", "1" }, { "GIT_TERMINAL_PROMPT", "0" }, { "GIT_OPTIONAL_LOCKS", "0" } };
            var result = await executor.ExecuteAsync(new(git, argv, repo, env), life.Remaining, life.Token); life.Check();
            Assert.Equal(0, result.ExitCode); Assert.False(result.TimedOut); Assert.False(result.Cancelled); Assert.True(result.OwnedProcessQuiescent);
        }
        BoundedFiles.WriteNew(Path.Combine(repo, "tracked.txt"), System.Text.Encoding.UTF8.GetBytes("actual committed assignment input\n"), life);
        await Git("init", "-q", "-b", "main"); await Git("add", "--", "tracked.txt"); await Git("commit", "-q", "-m", "real assignment baseline");
        var reader = new GitRepositoryContextReader(git);
        var context = await reader.ReadAsync(repo, "refs/heads/main", life.Token); life.Check();
        var file = await reader.ReadFileAsync(context, "tracked.txt", life.Token); life.Check();
        var now = DateTimeOffset.UtcNow;
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, now, [], EvidenceRequirement.Required)];
        var request = new PlanningRequest("fixture/assignment", "#1", "implement", "Preserve actual assignment failure after cancellation", ["tracked.txt"], ["production"], ["Actual assignment fault is retained and host never starts"], "implement");
        var frozen = PlanFreezer.Freeze(new(request, context, evidence, [new("tracked.txt", "content", "update")], [new("base", [], "current source")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", ["inspect diagnostics"]), [], IntakePlanner.Assess(request, evidence)), "assignment-primary", now);
        Assert.True(frozen.DownstreamReady); life.Check();
        var plan = Path.Combine(root, "plan.json"); BoundedFiles.WriteNew(plan, frozen.CanonicalBytes, life);
        var config = Path.Combine(root, "config.json"); BoundedFiles.WriteNew(config, JsonSerializer.SerializeToUtf8Bytes(new TrustedConfiguration("fixture/assignment", repo, "refs/heads/main", git,
            AdapterVersion: "assignment-fixture", MaximumCeiling: "implement", OperationSeconds: 120, ExpectedBundleIdentity: frozen.Identity, ExpectedContractIdentity: frozen.ContractIdentity, ExpectedWorkItem: "#1", ExpectedPlanCeiling: "implement"), BoundedFiles.Json), life);
        await using var processes = new DeliveryForge.Execution.ProcessControl();
        using var coordinator = new DeliveryForge.Execution.ExecutionCoordinator(new DeliveryForge.Execution.RunStore(Path.Combine(root, "store")), new DeliveryForge.Execution.Git.WorktreeManager(git), processes);
        var host = new UncalledHost(); var bridge = new OwnerHostBridge(coordinator, new CodexHostAdapter(host)); var assignment = new CancellingAssignment(cancel);
        var application = new ToolApplication(host: bridge, assignments: assignment, actualOriginal: life);
        var command = CommandLine.Parse(["implement", "--plan", plan, "--config", config]);
        var failure = await Assert.ThrowsAsync<IOException>(() => application.ExecuteAsync(command, token));
        Assert.Same(assignment.Failure, failure); Assert.NotNull(assignment.Actual); Assert.True(assignment.Actual.IsFaulted);
        var observation = Assert.Single(application.RetainedAssignments); Assert.True(observation.Entered); Assert.True(observation.InvocationReturned); Assert.False(observation.Unknown);
        Assert.Same(assignment.Actual, observation.ActualTask); Assert.Same(failure, observation.Primary); Assert.Null(observation.Result);
        Assert.NotNull(assignment.Request); Assert.Equal(frozen.Identity, assignment.Request.Plan.Identity); Assert.True(assignment.Request.Plan.DownstreamReady);
        Assert.Equal(0, host.Calls); Assert.Empty(bridge.RetainedFrames);
        Assert.Contains(application.RetainedExternalCalls, row => row.Kind == "ActualPlanImport" && row.Primary is null && row.Post is null);
        // Preserve the genuine fixture and its plan/import/cancellation evidence;
        // no fresh work/disposition is attempted through the cancelled original.
    }
}
