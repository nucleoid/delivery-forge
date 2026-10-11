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
        var killAttempted = false;
        string? killExceptionType = null;
        string? killExceptionDetail = null;
        var killExceptionDetailTruncated = false;
        var exitWaitCompleted = true;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = !timedOut;
            killSucceeded = TryKillTree(process, out killAttempted, out killExceptionType,
                out killExceptionDetail, out killExceptionDetailTruncated);
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
            (!timedOut && !cancelled || killSucceeded && exitWaitCompleted))
        {
            LifecycleDiagnostics = new(killAttempted, killAttempted && killSucceeded, killExceptionType, killExceptionDetail,
                killExceptionDetailTruncated, exitWaitCompleted, processExited, drainCompleted,
                (int)Math.Clamp(_waitTimeout.TotalMilliseconds, 0, int.MaxValue),
                ObserveStream(stdoutTask), ObserveStream(stderrTask))
        };
    }

    private bool TryKillTree(Process process, out bool attempted, out string? exceptionType,
        out string? exceptionDetail, out bool detailTruncated)
    {
        attempted = false; exceptionType = null; exceptionDetail = null; detailTruncated = false;
        try
        {
            if (_hasExited(process))
                return false;

            attempted = true;
            _killTree(process);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            exceptionType = exception.GetType().FullName;
            (exceptionDetail, detailTruncated) = BoundedExceptionDetail(exception);
            return false;
        }
    }

    private static CommandStreamObservation ObserveStream(Task<string> task)
    {
        var complete = task.IsCompletedSuccessfully;
        var faulted = task.IsFaulted;
        var cancelled = task.IsCanceled;
        if (faulted) _ = task.Exception;
        if (!task.IsCompleted)
            _ = task.ContinueWith(static late => { _ = late.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new(complete, faulted, cancelled, complete ? task.Result : null);
    }

    private static (string Detail, bool Truncated) BoundedExceptionDetail(Exception exception)
    {
        const int limit = 2048;
        var builder = new System.Text.StringBuilder();
        var pending = new Stack<Exception>(); pending.Push(exception);
        var count = 0; var truncated = false;
        while (pending.TryPop(out var current))
        {
            if (++count > 8 || builder.Length >= limit) { truncated = true; break; }
            var type = current.GetType().FullName ?? current.GetType().Name;
            var message = current is AggregateException ? "aggregate kill failure" : current.Message;
            var text = type + ": " + message +
                (current is System.ComponentModel.Win32Exception native ? " (Win32=" + native.NativeErrorCode + ")" : "") + "\n";
            var length = Math.Min(limit - builder.Length, text.Length);
            builder.Append(text.AsSpan(0, length));
            truncated |= length != text.Length;
            if (current is AggregateException aggregate)
            {
                var take = Math.Min(8, aggregate.InnerExceptions.Count);
                truncated |= take != aggregate.InnerExceptions.Count;
                for (var index = take - 1; index >= 0; index--) pending.Push(aggregate.InnerExceptions[index]);
            }
            else if (current.InnerException is not null) pending.Push(current.InnerException);
        }
        return (builder.ToString(), truncated);
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
