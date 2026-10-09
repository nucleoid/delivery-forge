using System.Diagnostics;

namespace DeliveryForge.Evidence;

public sealed class ProcessCommandExecutor : ICommandExecutor
{
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);
    private readonly Action<Process> _killTree;
    private readonly Func<Process, bool> _hasExited;
    private readonly Func<Task, TimeSpan, Task> _waitForDrain;
    private readonly TimeSpan _waitTimeout;

    public ProcessCommandExecutor()
        : this(
            process => process.Kill(entireProcessTree: true),
            process => process.HasExited,
            waitForDrain: null,
            DefaultWaitTimeout)
    {
    }

    internal ProcessCommandExecutor(
        Action<Process> killTree,
        Func<Process, bool> hasExited,
        Func<Task, TimeSpan, Task>? waitForDrain = null,
        TimeSpan? waitTimeout = null)
    {
        _killTree = killTree;
        _hasExited = hasExited;
        _waitForDrain = waitForDrain ?? ((task, timeout) =>
            task.WaitAsync(timeout, CancellationToken.None));
        _waitTimeout = waitTimeout ?? DefaultWaitTimeout;
    }

    public async Task<CommandResult> ExecuteAsync(
        CommandInvocation invocation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var pair in invocation.Environment ?? new Dictionary<string, string>())
            startInfo.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = startInfo };
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            if (!process.Start())
                return new CommandResult(
                    null, string.Empty, "Process did not start.", false, false, startedAt, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new CommandResult(
                null, string.Empty, $"Process start failed: {exception.Message}",
                false, false, startedAt, DateTimeOffset.UtcNow);
        }
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var timedOut = false;
        var cancelled = false;
        var killSucceeded = true;
        var exitWaitCompleted = true;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = !timedOut;
            killSucceeded = TryKillTree(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(_waitTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                exitWaitCompleted = false;
            }
        }

        string stdout;
        string stderr;
        var drainCompleted = true;
        try
        {
            await _waitForDrain(Task.WhenAll(stdoutTask, stderrTask), _waitTimeout).ConfigureAwait(false);
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            drainCompleted = false;
            stdout = "<stdout drain timed out>";
            stderr = "<stderr drain timed out>";
        }
        var processExited = ObserveHasExited(process);
        return new CommandResult(
            processExited ? process.ExitCode : null,
            stdout,
            stderr,
            timedOut,
            cancelled,
            startedAt,
            DateTimeOffset.UtcNow,
            drainCompleted && processExited &&
            (!timedOut && !cancelled || killSucceeded && exitWaitCompleted));
    }

    private bool TryKillTree(Process process)
    {
        try
        {
            if (_hasExited(process))
                return false;

            _killTree(process);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            return false;
        }
    }

    private bool ObserveHasExited(Process process)
    {
        try
        {
            return _hasExited(process);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
