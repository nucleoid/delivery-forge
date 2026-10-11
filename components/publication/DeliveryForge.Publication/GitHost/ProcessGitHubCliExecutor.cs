using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Publication.GitHost;

public sealed class ProcessGitHubCliExecutor(GitHubReadConfiguration configuration) : IGitHubCliExecutor
{
    private const int MaximumOutputBytes = 1024 * 1024;

    public async Task<GitHubCliResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateConfiguration(configuration);
        if (!PinMatches(configuration)) throw new PublicationUnavailableException("Pinned GitHub CLI executable bytes changed.");
        var mutation = arguments.Count > 1 && arguments[0] == "pr" && arguments[1] is "create" or "edit" or "merge";
        var budget = mutation ? configuration.ActionTimeout : configuration.ReadTimeout;
        var start = new ProcessStartInfo(configuration.ExecutablePath)
        {
            WorkingDirectory = configuration.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["GH_HOST"] = "github.com";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        start.Environment["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment.Remove("GH_REPO");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        try { if (!process.Start()) return new(null, "", "CLI did not start."); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { return new(null, "", "CLI startup unavailable."); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(budget);
        var stdout = ReadBounded(process.StandardOutput.BaseStream, cancellation.Token);
        var stderr = ReadBounded(process.StandardError.BaseStream, cancellation.Token);
        var exit = process.WaitForExitAsync(cancellation.Token);
        var aggregate = Task.WhenAll(stdout, stderr, exit);
        var pending = new HashSet<Task> { stdout, stderr, exit };
        var deadline = Task.Delay(budget, CancellationToken.None);
        var cancelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), cancelSignal);
        var complete = false;
        try
        {
            process.StandardInput.Close();
            while (pending.Count > 0)
            {
                var winner = await Task.WhenAny(pending.Append(deadline).Append(cancelSignal.Task)).ConfigureAwait(false);
                if (winner == deadline || winner == cancelSignal.Task) throw new OperationCanceledException();
                pending.Remove(winner);
                await winner.ConfigureAwait(false);
            }
            await aggregate.ConfigureAwait(false);
            complete = true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var timedOut = !cancellationToken.IsCancellationRequested && (deadline.IsCompleted || cancellation.IsCancellationRequested);
            cancellation.Cancel();
            var killFailed = false;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception failure) when (failure is Win32Exception or InvalidOperationException or AggregateException or NotSupportedException)
            { killFailed = true; }
            var closeFailed = !Close(process.StandardOutput) | !Close(process.StandardError);
            var drained = await Task.WhenAny(aggregate, Task.Delay(configuration.CleanupTimeout)).ConfigureAwait(false) == aggregate;
            if (drained) { try { await aggregate.ConfigureAwait(false); } catch (Exception) { } }
            else ObserveLate(aggregate);
            var exited = HasExited(process);
            return new(null, "", "CLI capture incomplete; action outcome requires readback.",
                timedOut,
                killFailed || closeFailed || !drained || !exited);
        }
        return complete ? new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false)) :
            new(null, "", "CLI capture incomplete.", ResidualProcessUncertain: true);
    }

    internal static void ValidateConfiguration(GitHubReadConfiguration configuration)
    {
        if (!Path.IsPathFullyQualified(configuration.ExecutablePath) || !File.Exists(configuration.ExecutablePath) ||
            !Path.IsPathFullyQualified(configuration.WorkingDirectory) || !Directory.Exists(configuration.WorkingDirectory) ||
            !Version.TryParse(configuration.ExpectedVersion, out _) ||
            new[] { configuration.ReadTimeout, configuration.ActionTimeout, configuration.CleanupTimeout }
                .Any(value => value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5)))
            throw new ArgumentException("CLI requires pinned absolute executable/working directory/version and finite operation budgets.");
    }
    internal static bool PinMatches(GitHubReadConfiguration c) => c.ExecutableIdentity ==
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(c.ExecutablePath)));
    private static async Task<string> ReadBounded(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read == 0) return Encoding.UTF8.GetString(buffer.ToArray());
            if (buffer.Length + read > MaximumOutputBytes) throw new IOException("CLI output exceeded its frozen bound.");
            buffer.Write(bytes, 0, read);
        }
    }
    private static bool Close(IDisposable stream) { try { stream.Dispose(); return true; } catch (Exception) { return false; } }
    private static bool HasExited(Process process) { try { return process.HasExited; } catch (Exception) { return false; } }
    internal static void ObserveLate(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
