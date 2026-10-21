using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Evidence.Tests;

public sealed class ProcessCommandExecutorTests
{
    [Fact]
    public async Task Creation_owned_capture_writes_bounded_standard_input()
    {
        if (!OperatingSystem.IsLinux())
            return;
        const string input = "first\nsecond\n";

        var result = await new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(
            new CommandInvocation(
                "/bin/sh", ["-c", "cat"], System.IO.Path.GetTempPath(), StandardInput: input),
            TimeSpan.FromSeconds(10), 1024, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Command.ExitCode);
        Assert.Equal(input, result.Command.StandardOutput);
        Assert.True(result.Command.OwnedProcessQuiescent);
    }

    [Fact]
    public async Task Linux_timeout_kills_exited_roots_remaining_process_group_children()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var temp = new TempDirectory();
        var childPath = Path.Combine(temp.Path, "child.pid");
        var script = Path.Combine(temp.Path, "descendant.sh");
        await File.WriteAllTextAsync(
            script,
            $"sleep 30 &\nprintf '%s' \"$!\" > '{childPath.Replace("'", "'\\''", StringComparison.Ordinal)}'\nexit 0\n",
            TestContext.Current.CancellationToken);

        var result = await new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(
            new CommandInvocation("/bin/sh", [script], temp.Path),
            TimeSpan.FromSeconds(2), 1024, TestContext.Current.CancellationToken);

        Assert.True(result.Command.TimedOut);
        Assert.True(result.Command.OwnedProcessQuiescent);
        Assert.True(result.Command.LifecycleDiagnostics?.KillAttempted);
        Assert.True(result.Command.LifecycleDiagnostics?.KillSucceeded);
        var childPid = int.Parse(await File.ReadAllTextAsync(childPath, TestContext.Current.CancellationToken));
        Assert.True(SpinWait.SpinUntil(
            () => !Directory.Exists($"/proc/{childPid}"), TimeSpan.FromSeconds(2)),
            $"Owned descendant {childPid} survived process-group cleanup.");
    }

    [Fact]
    public async Task Bounded_capture_accepts_existing_gate_budgets_over_fifteen_minutes()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var executor = new ProcessCommandExecutor();
        var result = await executor.ExecuteBoundedCaptureAsync(
            new CommandInvocation("/usr/bin/true", [], System.IO.Path.GetTempPath()),
            TimeSpan.FromMinutes(16),
            1024,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Command.ExitCode);
        Assert.True(result.Command.OwnedProcessQuiescent);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_owned_capture_bounds_large_stdout_and_stderr(bool standardError)
    {
        using var temp = new TempDirectory();
        var invocation = await LargeOutputInvocationAsync(temp.Path, standardError);
        const int quota = 4096;

        var capture = await new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(
            invocation, TimeSpan.FromSeconds(10), quota, TestContext.Current.CancellationToken);

        Assert.True(capture.CaptureBoundExceeded);
        Assert.True(capture.StandardOutputBytes.Length <= quota);
        Assert.True(capture.StandardErrorBytes.Length <= quota);
        Assert.True(capture.Command.OwnedProcessQuiescent);
        Assert.True(capture.RootExitObserved);
        Assert.True(capture.StandardOutputDrainCompleted);
        Assert.True(capture.StandardErrorDrainCompleted);
        Assert.Equal(standardError ? 0 : quota, capture.StandardOutputBytes.Length);
        Assert.Equal(standardError ? quota : 0, capture.StandardErrorBytes.Length);
    }

    [Fact]
    public async Task Existing_gate_output_over_one_megabyte_is_not_silently_truncated()
    {
        using var temp = new TempDirectory();
        var request = TestEvidence.GateRequest(temp.Path);
        var invocation = await LargeOutputInvocationAsync(request.Invocation.WorkingDirectory, standardError: false);
        var capability = ToolCapability.FromDetection(
            "dotnet", "10.0.401", "trx-v1", true, [], invocation.FileName,
            $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(invocation.FileName)))}",
            ["test"], request.Invocation.WorkingDirectory);
        request = request with { Invocation = invocation, Capability = capability, Timeout = TimeSpan.FromSeconds(10) };
        var runner = new GateRunner(
            new ProcessCommandExecutor(),
            new SequenceRepositoryIdentityReader(TestEvidence.Repository(), TestEvidence.Repository()),
            request.Invocation.WorkingDirectory,
            TestEvidence.Commit('a'));

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.DoesNotContain("capture limit", result.Reason, StringComparison.Ordinal);
        Assert.True(result.Execution.OwnedProcessQuiescent);
        Assert.True(Encoding.UTF8.GetByteCount(result.Execution.StandardOutput) > 1024 * 1024);
        Assert.True(Encoding.UTF8.GetByteCount(result.Execution.StandardOutput) <= GateRunner.ExistingGateMaximumCapturedBytesPerStream);
    }

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
        var streamsWritten = Path.Combine(temp.Path, "streams-written");
        var script = Path.Combine(temp.Path, OperatingSystem.IsWindows() ? "streams.cmd" : "streams.sh");
        var scriptText = OperatingSystem.IsWindows()
            ? $"@echo off\r\necho actual-stdout\r\necho actual-stderr 1>&2\r\necho ready>\"{streamsWritten}\"\r\nfor /L %%i in (1,1,2147483647) do @rem\r\n"
            : $"printf actual-stdout\nprintf actual-stderr >&2\nprintf ready > '{streamsWritten.Replace("'", "'\\''", StringComparison.Ordinal)}'\nexec sleep 30\n";
        await File.WriteAllTextAsync(script, scriptText, TestContext.Current.CancellationToken);
        var invocation = request.Invocation with
        {
            Arguments = OperatingSystem.IsWindows()
                ? ["/d", "/s", "/c", script]
                : [script]
        };
        var entered = false;
        var executor = new ProcessCommandExecutor(
            killTree: process =>
            {
                // This injected diagnostic fixture must actually write both streams before its
                // deliberate kill; the 100ms command timeout is not a process-startup guarantee.
                entered = SpinWait.SpinUntil(() => File.Exists(streamsWritten), TimeSpan.FromSeconds(2));
                process.Kill(entireProcessTree: false);
            },
            hasExited: process => process.HasExited,
            waitForDrain: async (task, _) =>
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                throw new TimeoutException("owner-test injected drain wait timeout after actual stream completion");
            },
            waitTimeout: TimeSpan.FromMilliseconds(250));
        var result = await executor.ExecuteAsync(invocation, request.Timeout, TestContext.Current.CancellationToken);
        Assert.True(entered, "Actual command did not write its stream-entry barrier before fixture kill.");
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
    public async Task Partial_tree_kill_failure_flows_through_real_executor_to_gate_incomplete()
    {
        using var temp = new TempDirectory();
        var request = LongRunningRequest(temp.Path) with { Timeout = TimeSpan.FromSeconds(2) };
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

        Assert.True(result.Execution.TimedOut,
            $"Expected the real command to reach timeout cleanup: outcome={result.Outcome}; " +
            $"reason={result.Reason}; exit={result.Execution.ExitCode}; " +
            $"cancelled={result.Execution.Cancelled}; native={result.Execution.LifecycleDiagnostics?.NativeFailure}");
        Assert.False(result.Execution.Cancelled);
        Assert.Same(killedProcess, observedProcess);
        Assert.False(result.Execution.OwnedProcessQuiescent);
        Assert.Equal(GateOutcome.Incomplete, result.Outcome);
        Assert.Contains("identity could not be read after evaluation", result.Reason, StringComparison.OrdinalIgnoreCase);
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
            ? (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"),
                new[] { "-n", "30", "127.0.0.1" })
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

    private static async Task<CommandInvocation> LargeOutputInvocationAsync(string directory, bool standardError)
    {
        var line = new string('x', 255);
        var path = Path.Combine(directory, OperatingSystem.IsWindows() ? "large-output.cmd" : "large-output.sh");
        var script = OperatingSystem.IsWindows()
            ? $"@echo off\r\nfor /L %%i in (1,1,10000) do @echo {line}{(standardError ? " 1>&2" : "")}\r\n"
            : $"i=0\nwhile [ \"$i\" -lt 10000 ]; do printf '{line}\\n'{(standardError ? " >&2" : "")}; i=$((i+1)); done\n";
        await File.WriteAllTextAsync(path, script, TestContext.Current.CancellationToken);
        return OperatingSystem.IsWindows()
            ? new CommandInvocation(Environment.GetEnvironmentVariable("ComSpec")!, ["/d", "/s", "/c", path], directory)
            : new CommandInvocation("/bin/sh", [path], directory);
    }
}
