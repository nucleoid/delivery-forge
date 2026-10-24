using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// Lifecycle and refusal controls only. No gate, native worker, Continuum lease,
// production profile or baseline approval is supplied by these DATA tests.
public sealed class HistoricalQualityTests
{
    [Theory]
    [InlineData(false, false, GateOutcome.Error, true)]
    [InlineData(true, false, GateOutcome.Error, false)]
    [InlineData(false, true, GateOutcome.Error, false)]
    [InlineData(false, false, GateOutcome.Pass, false)]
    public void Error_receipt_requires_noncontinuable_failure_DATA(bool complete, bool mayContinue, GateOutcome raw, bool accepted)
    {
        var tuple = AdvisoryTuple(raw);
        var receipt = tuple.Receipt with { Outcome = GateOutcome.Error };
        var capture = new InitialBaselineCaptureData("crap4csharp", raw, complete, mayContinue,
            tuple.Capture.ReportIdentity, [], [], null, null, ["PRODUCER_BINDING_FAILED"]);
        if (accepted) HistoricalQualityHost.RequireInitialCaptureTuple("crap4csharp", receipt, capture);
        else Assert.Throws<IOException>(() => HistoricalQualityHost.RequireInitialCaptureTuple("crap4csharp", receipt, capture));
    }

    [Fact]
    public void Failed_capture_summary_retains_actual_binding_failure_reference_without_requiring_a_capture_sidecar()
    {
        var tuple = AdvisoryTuple(GateOutcome.Error);
        var root = Path.Combine(Path.GetTempPath(), "capture-failure-data");
        var receipt = tuple.Receipt with
        {
            Outcome = GateOutcome.Error,
            ReceiptPath = Path.Combine(root, "receipts", "gate.json"),
            ArtifactHashes = new Dictionary<string, string> { ["producer-binding-failure.json"] = "sha256:" + new string('f', 64) }
        };
        var capture = new InitialBaselineCaptureData("crap4csharp", GateOutcome.Error, false, false,
            tuple.Capture.ReportIdentity, [], [], null, null, ["PRODUCER_BINDING_FAILED"]);
        HistoricalQualityHost.RequireInitialCaptureTuple("crap4csharp", receipt, capture);
        var summary = JsonSerializer.SerializeToElement(HistoricalQualityHost.SummarizeInitialCapture(receipt, capture));
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("CaptureArtifact").ValueKind);
        Assert.Equal(Path.Combine(root, "producer-binding-failure.json"), summary.GetProperty("BindingFailureArtifact").GetProperty("Path").GetString());
        Assert.Equal(receipt.ArtifactHashes["producer-binding-failure.json"], summary.GetProperty("BindingFailureArtifact").GetProperty("Identity").GetString());
        Assert.Throws<IOException>(() => HistoricalQualityHost.SummarizeInitialCapture(receipt with { ArtifactHashes = new Dictionary<string, string>() }, capture));
    }

    static TrustedConfiguration Configuration()
    {
        var root = Path.Combine(Path.GetTempPath(), "historical-quality-data-" + Guid.NewGuid().ToString("N"));
        var profile = new AdmissionFile(Path.Combine(root, "external-profile.json"), 1, new string('a', 64));
        var quality = new InstalledQualitySelection("quality/policy.json", "sha256:" + new string('b', 64), Path.Combine(root, "artifacts"),
            [new("mutation", "mutate4csharp", profile, "base", null, 10), new("callables", "crap4csharp", profile, "all", "tests/Execution.Tests.csproj", 10)]);
        return new("nucleoid/delivery-forge", Path.Combine(root, "parent"), "refs/heads/main", Environment.ProcessPath!,
            MaximumCeiling: "implement", HistoricalQuality: new(new string('1', 40), new string('2', 40), quality));
    }

    [Theory]
    [InlineData("head")]
    [InlineData("tree")]
    [InlineData("registry")]
    [InlineData("mixed-current-quality")]
    [InlineData("gate-count")]
    public void Historical_selection_refuses_ambiguous_or_authoritative_inputs(string changed)
    {
        var config = Configuration(); var historical = config.HistoricalQuality!;
        HistoricalQualityHost.ValidateSelection(config, historical);
        if (changed == "head") historical = historical with { HeadCommit = "HEAD" };
        if (changed == "tree") historical = historical with { TreeId = new string('G', 40) };
        if (changed == "registry") historical = historical with
        {
            Quality = historical.Quality with
            {
                Registry = new(new string('3', 40), new string('4', 40), "refs/heads/main", "registry.json", "sha256:" + new string('c', 64),
                Path.Combine(Path.GetTempPath(), "registry-artifacts"), new("unused", 1, new string('d', 64)))
            }
        };
        if (changed == "mixed-current-quality") config = config with { InstalledQuality = historical.Quality };
        if (changed == "gate-count") historical = historical with { Quality = historical.Quality with { Gates = historical.Quality.Gates.Take(1).ToArray() } };
        Assert.Throws<ToolUsageException>(() => HistoricalQualityHost.ValidateSelection(config, historical));
    }

    [Fact]
    public async Task Explicit_mode_and_independent_plan_expectations_refuse_before_any_external_effect()
    {
        var path = Path.Combine(Path.GetTempPath(), "historical-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(Configuration(), BoundedFiles.Json));
        try
        {
            var application = new ToolApplication();
            await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(CommandLine.Parse(["implement", "--plan", "/must-not-read", "--config", path]), TestContext.Current.CancellationToken));
            var result = await application.ExecuteAsync(CommandLine.Parse(["quality-baseline", "--plan", "/must-not-read", "--config", path]), TestContext.Current.CancellationToken);
            Assert.Equal("PLAN_EXPECTATIONS_REQUIRED", result.Code); Assert.False(result.RuntimeCertified);
            Assert.Empty(application.RetainedExternalCalls); Assert.Empty(application.RetainedAssignments);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Plan_only_immutable_ceiling_refuses_before_plan_read_or_assignment_without_optional_scheduler()
    {
        var root = Path.Combine(Path.GetTempPath(), "historical-plan-only-" + Guid.NewGuid().ToString("N"));
        var path = root + ".json"; var missingPlan = Path.Combine(root, "must-not-read.json");
        var config = Configuration() with
        {
            ExpectedBundleIdentity = "sha256:" + new string('a', 64),
            ExpectedContractIdentity = "sha256:" + new string('b', 64),
            ExpectedWorkItem = "#17",
            ExpectedPlanCeiling = "plan"
        };
        Assert.Equal("implement", config.MaximumCeiling); Assert.Null(config.LocalSingle);
        Assert.False(File.Exists(missingPlan));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config, BoundedFiles.Json));
        try
        {
            var application = new ToolApplication();
            var refusal = await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(
                CommandLine.Parse(["quality-baseline", "--plan", missingPlan, "--config", path]), TestContext.Current.CancellationToken));
            Assert.Contains("immutable ceiling", refusal.Message);
            Assert.Empty(application.RetainedExternalCalls); Assert.Empty(application.RetainedAssignments);
            // A plan read would throw IOException instead. Even a completely
            // valid plan-only bundle cannot grant the missing execution ceiling.
            Assert.False(Directory.Exists(root));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Actual_quality_task_retains_original_until_settled_without_worker_completion()
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "historical-store-" + Guid.NewGuid().ToString("N"))), new WorktreeManager(), processes);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("historical-task-control", original, coordinator, () => false, false);
        var pending = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        frame.EnterRoute();
        frame.Quality.Start(original, () => frame.Enter(() => pending.Task));
        frame.LeaveRoute();
        HistoricalQualityHost.RequireNoWorker(frame);
        Assert.False(frame.Worker.Started); Assert.True(frame.Quality.Unknown); Assert.False(frame.CloseWhenSettled());
        Assert.Same(pending.Task, Assert.Single(frame.EnteredTasks)); Assert.True(original.HasRetainedUnknown);
        pending.SetResult(ToolResult.Incomplete("DATA_ONLY", "No real gates executed by this control."));
        var result = await frame.Quality.ObserveAsync(original);
        Assert.Equal("INCOMPLETE", result.Outcome); Assert.False(result.RuntimeCertified);
        HistoricalQualityHost.RequireNoWorker(frame);
        Assert.True(frame.CloseWhenSettled()); Assert.False(original.HasRetainedUnknown);
        Assert.Null(frame.Request); Assert.Null(frame.Accepted); Assert.Null(frame.Completion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_phase_or_owner_completion_cannot_be_relabelled_historical(bool completed)
    {
        await using var processes = new ProcessControl();
        using var coordinator = new ExecutionCoordinator(new RunStore(Path.Combine(Path.GetTempPath(), "historical-refusal-" + Guid.NewGuid().ToString("N"))), new WorktreeManager(), processes);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var frame = new HostBridgeFrame("historical-refusal", original, coordinator, () => false, false);
        if (completed) frame.OwnerCompleted = true;
        else frame.Worker.Start(original, () => Task.FromResult(ToolResult.Incomplete("DATA_ONLY", "No native worker.")));
        Assert.Throws<DeliveryForge.Execution.WorkerEnvelopeException>(() => HistoricalQualityHost.RequireNoWorker(frame));
    }
    [Theory]
    [InlineData("")]
    [InlineData("initial_baseline_capture")]
    [InlineData("ordinary")]
    public async Task Unknown_capture_purpose_refuses_at_actual_configuration_parse_before_plan_or_external_effect(string purpose)
    {
        var config = Configuration();
        config = config with { HistoricalQuality = config.HistoricalQuality! with { Purpose = purpose } };
        var path = Path.Combine(Path.GetTempPath(), "historical-purpose-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config, BoundedFiles.Json));
        var application = new ToolApplication();
        var error = await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(
            CommandLine.Parse(["quality-baseline", "--plan", "/must-not-read", "--config", path]), TestContext.Current.CancellationToken));
        Assert.Contains("Unknown historical quality purpose", error.Message);
        Assert.Empty(application.RetainedExternalCalls); Assert.Empty(application.RetainedAssignments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_capture_selection_never_supplies_missing_plan_expectations_or_execution_ceiling(bool planOnly)
    {
        var config = Configuration();
        config = config with { HistoricalQuality = config.HistoricalQuality! with { Purpose = HistoricalQualityHost.InitialBaselineCapture } };
        if (planOnly) config = config with
        {
            ExpectedBundleIdentity = "sha256:" + new string('a', 64),
            ExpectedContractIdentity = "sha256:" + new string('b', 64),
            ExpectedWorkItem = "#17",
            ExpectedPlanCeiling = "plan"
        };
        var path = Path.Combine(Path.GetTempPath(), "historical-capture-expectations-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config, BoundedFiles.Json));
        var application = new ToolApplication();
        if (planOnly)
        {
            var error = await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(
                CommandLine.Parse(["quality-baseline", "--plan", "/must-not-read", "--config", path]), TestContext.Current.CancellationToken));
            Assert.Contains("immutable ceiling", error.Message);
        }
        else
        {
            var result = await application.ExecuteAsync(CommandLine.Parse(["quality-baseline", "--plan", "/must-not-read", "--config", path]), TestContext.Current.CancellationToken);
            Assert.Equal("PLAN_EXPECTATIONS_REQUIRED", result.Code); Assert.False(result.RuntimeCertified);
        }
        Assert.Empty(application.RetainedExternalCalls); Assert.Empty(application.RetainedAssignments);
    }
    [Fact]
    public async Task Explicit_initial_capture_still_refuses_worker_mode_before_plan_or_assignment()
    {
        var config = Configuration();
        config = config with { HistoricalQuality = config.HistoricalQuality! with { Purpose = HistoricalQualityHost.InitialBaselineCapture } };
        var path = Path.Combine(Path.GetTempPath(), "historical-capture-mode-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config, BoundedFiles.Json));
        var application = new ToolApplication();
        var error = await Assert.ThrowsAsync<ToolUsageException>(() => application.ExecuteAsync(
            CommandLine.Parse(["implement", "--plan", "/must-not-read", "--config", path]), TestContext.Current.CancellationToken));
        Assert.Contains("explicit quality-baseline mode", error.Message);
        Assert.Empty(application.RetainedExternalCalls); Assert.Empty(application.RetainedAssignments);
    }
    // DATA guard regression only: no HostScope, original, producer or native ownership is issued.
    static (GateRunResult Receipt, InitialBaselineCaptureData Capture) AdvisoryTuple(GateOutcome rawOutcome)
    {
        var repository = new RepositoryIdentity(new string('1', 40), new string('2', 40), new string('3', 40), true);
        var receipt = new GateRunResult(GateOutcome.Incomplete, "advisory capture", false, false, [], false,
            new("unused", [], Path.GetTempPath()), new(0, "", "", false, false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            repository, repository, "sha256:" + new string('a', 64), "sha256:" + new string('b', 64), "sha256:" + new string('c', 64),
            "all", new Dictionary<string, string>(), "unused", null);
        var capture = new InitialBaselineCaptureData("crap4csharp", rawOutcome, true, true,
            "sha256:" + new string('d', 64), [], [], null, null, []);
        return (receipt, capture);
    }

    [Theory]
    [InlineData(GateOutcome.Pass)]
    [InlineData(GateOutcome.Fail)]
    public void Raw_producer_pass_or_debt_fail_does_not_have_to_equal_advisory_receipt(GateOutcome rawOutcome)
    {
        var tuple = AdvisoryTuple(rawOutcome);
        HistoricalQualityHost.RequireInitialCaptureTuple("crap4csharp", tuple.Receipt, tuple.Capture);
        Assert.Equal(GateOutcome.Incomplete, tuple.Receipt.Outcome);
        Assert.Equal(rawOutcome, tuple.Capture.RawOutcome);
        Assert.False(tuple.Capture.ProductionAuthority);
    }

    [Theory]
    [InlineData("receipt-pass")]
    [InlineData("fixture")]
    [InlineData("production")]
    public void Initial_capture_data_guard_refuses_gate_pass_or_capability_promotion(string changed)
    {
        var tuple = AdvisoryTuple(GateOutcome.Pass);
        var receipt = changed switch
        {
            "receipt-pass" => tuple.Receipt with { Outcome = GateOutcome.Pass },
            "fixture" => tuple.Receipt with { Fixture = true },
            _ => tuple.Receipt with { ProductionCapable = true }
        };
        Assert.Throws<IOException>(() => HistoricalQualityHost.RequireInitialCaptureTuple("crap4csharp", receipt, tuple.Capture));
    }
    [Fact]
    public void Initial_capture_summary_references_full_sidecars_without_serializing_large_measurement_vectors()
    {
        // Pure summary DATA, never supplied to an actual scope or native authority issuer.
        const int count = 100000;
        var tuple = AdvisoryTuple(GateOutcome.Fail);
        var gateRoot = Path.Combine(Path.GetTempPath(), "capture-summary-data");
        var captureHash = "sha256:" + new string('e', 64);
        var failureHash = "sha256:" + new string('f', 64);
        var receipt = tuple.Receipt with
        {
            ReceiptPath = Path.Combine(gateRoot, "receipts", "sha256-data.json"),
            ArtifactHashes = new Dictionary<string, string>
            {
                ["producer-initial-baseline-capture.json"] = captureHash,
                ["producer-binding-failure.json"] = failureHash
            }
        };
        var source = new InitialBaselineSource("context", "source.cs", new string('a', 64), false);
        var callable = new InitialBaselineCallable("context", "source.cs", "method", new string('a', 64), 0.25m, 99m);
        var capture = new InitialBaselineCaptureData("crap4csharp", GateOutcome.Fail, true, true,
            "sha256:" + new string('d', 64), Enumerable.Repeat(source, count), Enumerable.Repeat(callable, count),
            new(12, 3, 25m, 0, true, "sha256:" + new string('c', 64), new string('x', 100000)), null,
            Enumerable.Repeat(new string('z', 1024), count));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(HistoricalQualityHost.SummarizeInitialCapture(receipt, capture), BoundedFiles.Json);
        Assert.True(bytes.Length < 8192, $"Summary unexpectedly expanded to {bytes.Length} bytes.");
        using var document = JsonDocument.Parse(bytes);
        var summary = document.RootElement;
        Assert.Equal(count, summary.GetProperty("SourceCount").GetInt32());
        Assert.Equal(count, summary.GetProperty("CallableCount").GetInt32());
        Assert.Equal(count, summary.GetProperty("DiagnosticCount").GetInt32());
        Assert.False(summary.TryGetProperty("Sources", out _)); Assert.False(summary.TryGetProperty("Callables", out _));
        Assert.Equal("Fail", summary.GetProperty("RawOutcome").GetString());
        Assert.Equal(12, summary.GetProperty("Lines").GetProperty("ExecutableLines").GetInt32());
        Assert.False(summary.GetProperty("Lines").TryGetProperty("Reason", out _));
        var excerpts = summary.GetProperty("DiagnosticExcerpts").EnumerateArray().ToArray();
        Assert.Equal(8, excerpts.Length); Assert.All(excerpts, value => Assert.Equal(256, value.GetString()!.Length));
        Assert.Equal(Path.Combine(gateRoot, "producer-initial-baseline-capture.json"), summary.GetProperty("CaptureArtifact").GetProperty("Path").GetString());
        Assert.Equal(captureHash, summary.GetProperty("CaptureArtifact").GetProperty("Identity").GetString());
        Assert.Equal(Path.Combine(gateRoot, "producer-binding-failure.json"), summary.GetProperty("BindingFailureArtifact").GetProperty("Path").GetString());
        Assert.Equal(failureHash, summary.GetProperty("BindingFailureArtifact").GetProperty("Identity").GetString());
        Assert.False(summary.GetProperty("ProductionAuthority").GetBoolean());
        Assert.Equal(count, capture.Sources.Count); Assert.Equal(count, capture.Callables.Count);
    }
}
