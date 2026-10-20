namespace DeliveryForge.Evidence.Tests;

using DeliveryForge.Contracts.Validation;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;

public sealed class GateRunnerTests
{
    [Fact]
    public async Task Default_writer_preserves_v1_for_readers_first_rollout()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path);
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository()),
            TestEvidence.WorkingDirectory(temp.Path),
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(
            request with { Plugin = new EvaluationPluginSelection("delivery-forge.dotnet", false) },
            TestContext.Current.CancellationToken);

        using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(
            result.ReceiptPath, TestContext.Current.CancellationToken));
        Assert.Equal("1.0.0", receipt.RootElement.GetProperty("schemaVersion").GetString());
    }

    [Fact]
    public void V2_flag_requires_host_owned_store_before_any_gate_can_run()
    {
        using var temp = new TempDirectory();

        var error = Assert.Throws<ArgumentException>(() => new GateRunner(
            new QueueCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository()),
            TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'),
            emitAuthoritativeV2: true));

        Assert.Contains("host-owned", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Authoritative_host_composition_emits_v2_and_reopens_durable_provenance()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path) with
        {
            Plugin = new EvaluationPluginSelection("delivery-forge.dotnet", Enabled: false)
        };
        var receiptRoot = System.IO.Path.Combine(temp.Path, "host-receipts");
        var host = new AuthoritativeEvaluationHost(
            new QueueCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository()),
            request.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'),
            receiptRoot,
            new byte[32]);

        var result = await host.RunAsync(request, TestContext.Current.CancellationToken);
        var receipt = ContractValidator.ParseAndValidate(await File.ReadAllBytesAsync(
            result.ReceiptPath, TestContext.Current.CancellationToken));
        var reopened = new HostOwnedReceiptStore(receiptRoot, new byte[32]);

        Assert.Equal("2.0.0", receipt.SchemaVersion);
        Assert.True(host.ReceiptProvenance.Contains(receipt.Identity));
        Assert.True(reopened.Provenance.Contains(receipt.Identity));
        Assert.Equal(receipt.Identity, host.ReadReceipt(receipt.Identity).Identity);
    }

    [Fact]
    public void Supported_capability_has_no_public_minting_or_record_copy_surface()
    {
        Assert.Empty(typeof(ToolCapability).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(typeof(ToolCapability).GetMethod(
            "DetectedFixture", BindingFlags.Public | BindingFlags.Static));
        Assert.Null(typeof(ToolCapability).GetMethod(
            "<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void Temp_cleanup_does_not_change_a_reparse_point_target()
    {
        using var target = new TempDirectory();
        var targetFile = System.IO.Path.Combine(target.Path, "target.txt");
        File.WriteAllText(targetFile, "outside");
        File.SetAttributes(targetFile, FileAttributes.ReadOnly);
        var attributes = File.GetAttributes(targetFile);

        using (var cleanupRoot = new TempDirectory())
        {
            try
            {
                File.CreateSymbolicLink(System.IO.Path.Combine(cleanupRoot.Path, "link.txt"), targetFile);
            }
            catch (Exception exception) when (OperatingSystem.IsWindows() &&
                                              exception is UnauthorizedAccessException or IOException)
            {
                Assert.Skip($"Windows host cannot create the required file symlink: {exception.Message}");
            }
        }

        Assert.True(File.Exists(targetFile));
        Assert.Equal(attributes, File.GetAttributes(targetFile));
    }

    [Fact]
    public async Task Captures_immutable_argv_artifacts_and_source_identity()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.False(result.ProductionCapable);
        Assert.Equal(["test", "DeliveryForge.Tests.csproj", "--no-restore"], result.Invocation.Arguments);
        Assert.Equal(2, result.ArtifactHashes.Count);
        Assert.All(result.ArtifactHashes.Values, value => Assert.StartsWith("sha256:", value));
    }

    [Fact]
    public async Task Readers_first_rollout_switch_writes_canonical_v1_without_v2_authority()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'),
            emitAuthoritativeV2: false);

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(
            result.ReceiptPath, TestContext.Current.CancellationToken));
        Assert.Equal("1.0.0", receipt.RootElement.GetProperty("schemaVersion").GetString());
        Assert.False(receipt.RootElement.TryGetProperty("authorityBinding", out _));
    }

    [Fact]
    public async Task Identity_deadline_persists_incomplete_and_releases_claim_for_retry()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path) with { Timeout = TimeSpan.FromMilliseconds(25) };
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new CancellingRepositoryIdentityReader(),
            TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var first = await runner.RunAsync(request, TestContext.Current.CancellationToken);
        var retry = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, first.Outcome);
        Assert.Equal(GateOutcome.Incomplete, retry.Outcome);
        Assert.True(File.Exists(first.ReceiptPath));
        Assert.True(File.Exists(retry.ReceiptPath));
        Assert.Contains(".attempt-", retry.ReceiptPath, StringComparison.Ordinal);
        Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, request.GateId + ".claim")));
    }

    [Fact]
    public async Task Killed_execution_owner_persists_interruption_and_allows_immutable_retry()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path) with
        {
            Plugin = new EvaluationPluginSelection("delivery-forge.dotnet", Enabled: false)
        };
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec")! : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                OperatingSystem.IsWindows() ? "/d" : "-c",
                OperatingSystem.IsWindows() ? "/s" : "sleep 1",
                OperatingSystem.IsWindows() ? "/c" : string.Empty,
                OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 >NUL" : "sleep 30"
            }
        }) ?? throw new InvalidOperationException("Child claim fixture did not start.");
        var claimPath = System.IO.Path.Combine(temp.Path, request.GateId + ".claim");
        await File.WriteAllBytesAsync(
            claimPath, GateClaimLease.CreatePayloadForProcess(child.Id), TestContext.Current.CancellationToken);
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository()),
            request.Invocation.WorkingDirectory, TestEvidence.Commit('a'));

        var live = await runner.RunAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GateOutcome.Incomplete, live.Outcome);
        Assert.Contains("live process", live.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(claimPath));

        var interruptedAttempt = System.IO.Path.Combine(
            temp.Path, request.GateId + ".attempt-killed-fixture");
        Directory.CreateDirectory(interruptedAttempt);
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        var recovered = await runner.RunAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GateOutcome.Incomplete, recovered.Outcome);
        Assert.Contains("interrupted", recovered.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(recovered.ReceiptPath));
        Assert.True(Directory.Exists(interruptedAttempt));
        Assert.False(File.Exists(claimPath));

        var retry = await runner.RunAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(GateOutcome.Incomplete, retry.Outcome);
        Assert.Contains("disabled", retry.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(retry.ReceiptPath));
    }

    [Fact]
    public async Task Ambiguous_oversized_claim_persists_incomplete_without_takeover()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path);
        var claimPath = System.IO.Path.Combine(temp.Path, request.GateId + ".claim");
        await File.WriteAllBytesAsync(
            claimPath, new byte[GateClaimLease.MaximumClaimBytes + 1], TestContext.Current.CancellationToken);
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository()),
            request.Invocation.WorkingDirectory, TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("ambiguous", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GateClaimLease.MaximumClaimBytes + 1, new FileInfo(claimPath).Length);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Source_drift_is_error_and_append_only_output_is_not_overwritten()
    {
        using var temp = new TempDirectory();
        var changed = TestEvidence.Repository() with { TreeId = TestEvidence.Commit('d') };
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), changed), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.True(result.SourceChanged);
        var retry = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);
        Assert.Equal(GateOutcome.Error, retry.Outcome);
        Assert.Contains(".attempt-", retry.ReceiptPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_never_passes_even_with_exit_zero()
    {
        using var temp = new TempDirectory();
        var execution = TestEvidence.CommandResult() with { TimedOut = true };
        var runner = new GateRunner(
            new StubCommandExecutor(execution),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
    }

    [Fact]
    public async Task Missing_executable_is_recorded_as_error_receipt()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path);
        var runner = new GateRunner(
            new DeletingProcessCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Caller_cancellation_result_is_recorded_incomplete()
    {
        using var temp = new TempDirectory();
        var execution = TestEvidence.CommandResult() with { Cancelled = true };
        var runner = new GateRunner(
            new StubCommandExecutor(execution),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), cancellation.Token);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Executor_thrown_cancellation_is_nonquiescent_incomplete()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new CancellingCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.False(result.Execution.OwnedProcessQuiescent);
        Assert.Contains("quiescent", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Repository_preflight_failure_is_recorded_as_incomplete_receipt()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new ThrowingRepositoryIdentityReader(), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.True(File.Exists(result.ReceiptPath));
        Assert.Contains("could not be read", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unexpected_executor_failure_is_recorded_as_incomplete_receipt()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.True(File.Exists(result.ReceiptPath));
        Assert.Null(result.Execution.ExitCode);
    }

    [Fact]
    public async Task Repository_postflight_failure_is_recorded_as_incomplete_receipt()
    {
        using var temp = new TempDirectory();
        var runner = new GateRunner(
            new StubCommandExecutor(TestEvidence.CommandResult()),
            new FirstThenThrowRepositoryIdentityReader(TestEvidence.Repository()), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.True(result.SourceChanged);
        Assert.True(File.Exists(result.ReceiptPath));
        Assert.Contains("after evaluation", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adapter_tool_mismatch_is_rejected_before_execution()
    {
        using var temp = new TempDirectory();
        var original = TestEvidence.GateRequest(temp.Path);
        var request = original with
        {
            Capability = new ToolCapability(
                "crap4csharp", original.Capability.Version, original.Capability.FormatVersion,
                original.Capability.Supported, original.Capability.Fixture,
                original.Capability.Limitations, original.Capability.ExecutablePath,
                original.Capability.ExecutableIdentity, original.Capability.Operations,
                detectionVerified: true,
                probeWorkingDirectory: original.Capability.ProbeWorkingDirectory)
        };
        var runner = new GateRunner(new QueueCommandExecutor(),
            new ThrowingRepositoryIdentityReader(), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.Contains("adapter", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Capability_probe_root_mismatch_is_rejected_before_execution()
    {
        using var temp = new TempDirectory();
        using var other = new TempDirectory();
        var original = TestEvidence.GateRequest(temp.Path);
        var request = original with
        {
            Capability = new ToolCapability(
                original.Capability.Tool, original.Capability.Version,
                original.Capability.FormatVersion, original.Capability.Supported,
                original.Capability.Fixture, original.Capability.Limitations,
                original.Capability.ExecutablePath, original.Capability.ExecutableIdentity,
                original.Capability.Operations, detectionVerified: true,
                probeWorkingDirectory: other.Path)
        };
        var runner = new GateRunner(
            new QueueCommandExecutor(), new ThrowingRepositoryIdentityReader(),
            TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.Contains("probe root", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Executable_swap_during_run_is_an_orchestration_error()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path);
        var runner = new GateRunner(
            new MutatingCommandExecutor(request.Invocation.FileName, TestEvidence.CommandResult()),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.Contains("executable bytes changed", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Preexisting_unchanged_trx_cannot_be_reused()
    {
        using var temp = new TempDirectory();
        var trx = System.IO.Path.Combine(TestEvidence.WorkingDirectory(temp.Path), "stale.trx");
        Directory.CreateDirectory(TestEvidence.WorkingDirectory(temp.Path));
        await File.WriteAllTextAsync(
            trx,
            "<TestRun><ResultSummary><Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>",
            TestContext.Current.CancellationToken);
        var request = TestEvidence.GateRequest(temp.Path);
        request = request with
        {
            Invocation = request.Invocation with
            {
                Arguments = [.. request.Invocation.Arguments, "--logger", $"trx;LogFileName={trx}"]
            }
        };
        var execution = TestEvidence.CommandResult() with { StandardOutput = "Subject -> Subject.dll" };
        var runner = new GateRunner(
            new StubCommandExecutor(execution),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()), TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.Contains("stale", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evidence.trx", result.ArtifactHashes.Keys);
    }

    [Fact]
    public async Task Preexisting_legacy_gate_directory_cannot_block_a_nonce_attempt()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path);
        Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, request.GateId));
        var runner = new GateRunner(
            new QueueCommandExecutor(),
            new ThrowingRepositoryIdentityReader(),
            TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("could not be read", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".attempt-", result.ReceiptPath, StringComparison.Ordinal);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public async Task Nonquiescent_owned_process_is_recorded_as_incomplete()
    {
        using var temp = new TempDirectory();
        var execution = TestEvidence.CommandResult() with
        {
            TimedOut = true,
            OwnedProcessQuiescent = false
        };
        var runner = new GateRunner(
            new StubCommandExecutor(execution),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            TestEvidence.WorkingDirectory(temp.Path), TestEvidence.Commit('a'));

        var result = await runner.RunAsync(
            TestEvidence.GateRequest(temp.Path), TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("quiescent", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ReceiptPath));
    }
}
