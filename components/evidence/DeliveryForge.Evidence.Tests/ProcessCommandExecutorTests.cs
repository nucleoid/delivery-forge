using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace DeliveryForge.Evidence.Tests;

public sealed class ProcessCommandExecutorTests
{
    [Fact]
    public async Task Lifecycle_diagnostics_preserve_partial_kill_exception_without_promoting_root_exit()
    {
        using var temp = new TempDirectory();
        var request = LongRunningRequest(temp.Path);
        var executor = new ProcessCommandExecutor(
            killTree: process =>
            {
                process.Kill(entireProcessTree: false);
                Assert.True(process.WaitForExit(2000));
                throw new AggregateException(new Win32Exception(5, "owner-test injected descendant refusal"));
            },
            hasExited: process => process.HasExited,
            waitTimeout: TimeSpan.FromMilliseconds(250));
        var result = await executor.ExecuteAsync(request.Invocation, request.Timeout, TestContext.Current.CancellationToken);
        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.OwnedProcessQuiescent);
        var diagnostic = Assert.IsType<CommandLifecycleDiagnostics>(result.LifecycleDiagnostics);
        Assert.True(diagnostic.KillAttempted);
        Assert.False(diagnostic.KillSucceeded);
        Assert.Equal(typeof(AggregateException).FullName, diagnostic.KillExceptionType);
        Assert.Contains("owner-test injected descendant refusal", diagnostic.KillExceptionDetail!, StringComparison.Ordinal);
        Assert.False(diagnostic.KillExceptionDetailTruncated);
        Assert.True(diagnostic.RootExited);
        Assert.True(diagnostic.ExitWaitCompleted);
        Assert.Equal(250, diagnostic.CleanupWaitMilliseconds);
    }

    [Fact]
    public async Task Lifecycle_diagnostics_preserve_completed_raw_streams_when_owner_drain_wait_reports_timeout()
    {
        using var temp = new TempDirectory();
        var request = LongRunningRequest(temp.Path);
        var invocation = request.Invocation with
        {
            Arguments = OperatingSystem.IsWindows()
                ? ["/d", "/s", "/c", "echo actual-stdout & echo actual-stderr 1>&2 & for /L %i in (1,1,2147483647) do @rem"]
                : ["-c", "printf actual-stdout; printf actual-stderr >&2; exec sleep 30"]
        };
        var executor = new ProcessCommandExecutor(
            killTree: process => process.Kill(entireProcessTree: false),
            hasExited: process => process.HasExited,
            waitForDrain: async (task, _) =>
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                throw new TimeoutException("owner-test injected drain wait timeout after actual stream completion");
            },
            waitTimeout: TimeSpan.FromMilliseconds(250));
        var result = await executor.ExecuteAsync(invocation, request.Timeout, TestContext.Current.CancellationToken);
        Assert.True(result.TimedOut);
        Assert.False(result.OwnedProcessQuiescent);
        Assert.Equal("<stdout drain timed out>", result.StandardOutput);
        Assert.Equal("<stderr drain timed out>", result.StandardError);
        var diagnostic = Assert.IsType<CommandLifecycleDiagnostics>(result.LifecycleDiagnostics);
        Assert.True(diagnostic.KillAttempted);
        Assert.True(diagnostic.KillSucceeded);
        Assert.Null(diagnostic.KillExceptionType);
        Assert.True(diagnostic.RootExited);
        Assert.True(diagnostic.ExitWaitCompleted);
        Assert.False(diagnostic.DrainWaitCompleted);
        Assert.True(diagnostic.StandardOutput.CompletedSuccessfully);
        Assert.True(diagnostic.StandardError.CompletedSuccessfully);
        Assert.False(diagnostic.StandardOutput.Faulted);
        Assert.False(diagnostic.StandardError.Faulted);
        Assert.False(diagnostic.StandardOutput.Cancelled);
        Assert.False(diagnostic.StandardError.Cancelled);
        Assert.Contains("actual-stdout", diagnostic.StandardOutput.CompletedText!, StringComparison.Ordinal);
        Assert.Contains("actual-stderr", diagnostic.StandardError.CompletedText!, StringComparison.Ordinal);
        Assert.Equal(250, diagnostic.CleanupWaitMilliseconds);
    }

    [Fact]
    public async Task Partial_tree_kill_failure_flows_through_real_executor_to_gate_error()
    {
        using var temp = new TempDirectory();
        var request = LongRunningRequest(temp.Path);
        Process? killedProcess = null;
        Process? observedProcess = null;
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
                observedProcess ??= process;
                Assert.Same(observedProcess, process);
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
        Assert.Same(killedProcess, observedProcess);
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
        Task? drainTask = null;
        var executor = new ProcessCommandExecutor(
            killTree: process => process.Kill(entireProcessTree: false),
            hasExited: process => process.HasExited,
            waitForDrain: (task, _) =>
            {
                drainTask = task;
                throw new TimeoutException("simulated pipe drain hang");
            },
            waitTimeout: TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await executor.ExecuteAsync(
                request.Invocation, request.Timeout, TestContext.Current.CancellationToken);

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Execution took {stopwatch.Elapsed}.");
            Assert.True(result.TimedOut);
            Assert.False(result.OwnedProcessQuiescent);
            Assert.Equal("<stdout drain timed out>", result.StandardOutput);
            Assert.Equal("<stderr drain timed out>", result.StandardError);
        }
        finally
        {
            Assert.NotNull(drainTask);
            await drainTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
    }

    private static GateRequest LongRunningRequest(string output)
    {
        var request = TestEvidence.GateRequest(output);
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("ComSpec")!, new[] { "/d", "/s", "/c", "for /L %i in (1,1,2147483647) do @rem" })
            : ("/bin/sh", new[] { "-c", "exec sleep 30" });
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
