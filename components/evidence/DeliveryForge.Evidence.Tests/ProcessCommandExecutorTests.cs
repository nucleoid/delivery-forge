using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace DeliveryForge.Evidence.Tests;

public sealed class ProcessCommandExecutorTests
{
    [Fact]
    public async Task Partial_tree_kill_failure_flows_through_real_executor_to_gate_error()
    {
        using var temp = new TempDirectory();
        var request = LongRunningRequest(temp.Path);
        Process? killedProcess = null;
        var executor = new ProcessCommandExecutor(
            killTree: process =>
            {
                killedProcess = process;
                process.Kill(entireProcessTree: false);
                process.WaitForExit();
                throw new AggregateException(new Win32Exception("simulated descendant kill failure"));
            },
            hasExited: process =>
            {
                Assert.Same(killedProcess, process);
                return process.HasExited;
            },
            waitTimeout: TimeSpan.FromMilliseconds(250));
        var runner = new GateRunner(
            executor,
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            request.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.Execution.TimedOut);
        Assert.False(result.Execution.Cancelled);
        Assert.False(result.Execution.OwnedProcessQuiescent);
        Assert.Equal(GateOutcome.Error, result.Outcome);
        Assert.Contains("quiescent", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Successful_tree_kill_and_drain_are_quiescent()
    {
        using var temp = new TempDirectory();
        var request = LongRunningRequest(temp.Path);
        var executor = new ProcessCommandExecutor(
            killTree: process => process.Kill(entireProcessTree: false),
            hasExited: process => process.HasExited,
            waitTimeout: TimeSpan.FromMilliseconds(250));

        var result = await executor.ExecuteAsync(
            request.Invocation, request.Timeout, TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.True(result.OwnedProcessQuiescent);
    }

    [Fact]
    public async Task Already_exited_invalid_operation_remains_conservatively_nonquiescent()
    {
        using var temp = new TempDirectory();
        var request = LongRunningRequest(temp.Path);
        var executor = new ProcessCommandExecutor(
            killTree: process =>
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit();
                throw new InvalidOperationException("simulated root already exited during tree kill");
            },
            hasExited: process => process.HasExited,
            waitTimeout: TimeSpan.FromMilliseconds(250));

        var result = await executor.ExecuteAsync(
            request.Invocation, request.Timeout, TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.False(result.OwnedProcessQuiescent);
    }

    [Fact]
    public async Task Drain_timeout_is_bounded_and_nonquiescent()
    {
        using var temp = new TempDirectory();
        var request = LongRunningRequest(temp.Path);
        var executor = new ProcessCommandExecutor(
            killTree: process => process.Kill(entireProcessTree: false),
            hasExited: process => process.HasExited,
            waitForDrain: (_, _) => throw new TimeoutException("simulated pipe drain hang"),
            waitTimeout: TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        var result = await executor.ExecuteAsync(
            request.Invocation, request.Timeout, TestContext.Current.CancellationToken);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Execution took {stopwatch.Elapsed}.");
        Assert.True(result.TimedOut);
        Assert.False(result.OwnedProcessQuiescent);
        Assert.Equal("<stdout drain timed out>", result.StandardOutput);
        Assert.Equal("<stderr drain timed out>", result.StandardError);
    }

    private static GateRequest LongRunningRequest(string output)
    {
        var request = TestEvidence.GateRequest(output);
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("ComSpec")!, new[] { "/d", "/s", "/c", "ping -n 31 127.0.0.1 > nul" })
            : ("/bin/sh", new[] { "-c", "sleep 30" });
        var executableIdentity = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fileName)))}";
        var capability = ToolCapability.FromDetection(
            request.Capability.Tool,
            request.Capability.Version,
            request.Capability.FormatVersion,
            fixture: true,
            request.Capability.Limitations ?? [],
            fileName,
            executableIdentity,
            request.Capability.Operations ?? [],
            request.Invocation.WorkingDirectory);

        return request with
        {
            Capability = capability,
            Invocation = new CommandInvocation(fileName, arguments, request.Invocation.WorkingDirectory),
            Timeout = TimeSpan.FromMilliseconds(100)
        };
    }
}
