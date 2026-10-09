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
        if (!process.Start())
            return new CommandResult(null, string.Empty, "Process did not start.", false, false, startedAt, DateTimeOffset.UtcNow);
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var timedOut = false;
        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = !timedOut;
            TryKillTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return new CommandResult(
            process.HasExited ? process.ExitCode : null,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            timedOut,
            cancelled,
            startedAt,
            DateTimeOffset.UtcNow);
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
