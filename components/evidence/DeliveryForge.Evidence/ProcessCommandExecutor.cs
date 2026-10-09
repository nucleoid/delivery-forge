using System.Diagnostics;

namespace DeliveryForge.Evidence;

public sealed class ProcessCommandExecutor : ICommandExecutor
{
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
                    .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        string stdout;
        string stderr;
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stdout = "<stdout drain timed out>";
            stderr = "<stderr drain timed out>";
        }
        return new CommandResult(
            process.HasExited ? process.ExitCode : null,
            stdout,
            stderr,
            timedOut,
            cancelled,
            startedAt,
            DateTimeOffset.UtcNow,
            killSucceeded && process.HasExited);
    }

    private static bool TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            return process.HasExited;
        }
    }
}
