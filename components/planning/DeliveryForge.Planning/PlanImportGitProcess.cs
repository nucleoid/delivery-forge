using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace DeliveryForge.Planning;

public sealed partial class GitRepositoryContextReader
{
    // Import-only lifetime retention. Pending original handles/pipes/tasks are not
    // disposed as a substitute for observing completion, and never grant readiness.
    private sealed record ImportGitObservation(Task[] Tasks, IReadOnlyList<Exception> Errors);
    private static readonly ConcurrentDictionary<Process, ImportGitObservation> RetainedImportGit = new();
    private readonly ConcurrentDictionary<Process, byte> _importOriginals = new();
    internal bool HasUnsettledImportProcess => _ownedGitUnknown || !_importOriginals.IsEmpty;
    private async Task<GitResult> RunImportProcess(ProcessStartInfo startInfo, CancellationToken caller, bool allowFailure)
    {
        caller.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(caller);
        timeout.CancelAfter(GitOperationTimeout);
        var original = new Process { StartInfo = startInfo };
        _importOriginals.TryAdd(original, 0);
        Task<byte[]>? stdout = null; Task<byte[]>? stderr = null; Task? exit = null; Task? cleanupExit = null;
        var errors = new List<Exception>();
        var started = false;
        try
        {
            _importLifetime!.Check(); _importView!.Verify();
            if (!original.Start()) throw new PlanningException("Unable to start import Git.");
            started = true;
            stdout = ReadBoundedAsync(original.StandardOutput.BaseStream, timeout.Token);
            stderr = ReadBoundedAsync(original.StandardError.BaseStream, timeout.Token);
            exit = original.WaitForExitAsync(timeout.Token);
            RetainedImportGit.TryAdd(original, new([stdout, stderr, exit], errors));
            await Task.WhenAll(stdout, stderr, exit).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested();
            _importLifetime.Check();
            var result = new GitResult(original.ExitCode, await stdout, await stderr);
            if (!allowFailure && result.ExitCode != 0)
                throw new PlanningException("Git could not resolve the current import source: " + Encoding.UTF8.GetString(result.StandardError).Trim());
            return result;
        }
        catch (Exception error)
        {
            errors.Add(error);
            if (started)
            {
                // Best-effort control is confined to this original Process. The
                // outer original allowance still governs the observer's return.
                try { if (!original.HasExited) original.Kill(entireProcessTree: true); }
                catch (Exception cleanup) when (cleanup is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { errors.Add(cleanup); }
                try
                {
                    cleanupExit = original.WaitForExitAsync(CancellationToken.None);
                    var left = _importLifetime!.Remaining();
                    await cleanupExit.WaitAsync(left < GitTerminationTimeout ? left : GitTerminationTimeout).ConfigureAwait(false);
                }
                catch (Exception cleanup) when (cleanup is TimeoutException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { errors.Add(cleanup); }
            }
            if (caller.IsCancellationRequested) throw new OperationCanceledException(caller);
            if (error is PlanningException) throw;
            if (timeout.IsCancellationRequested) throw new PlanningException("Import Git exceeded its bounded original stage allowance.");
            throw new PlanningException("Import Git observation failed: " + error.Message);
        }
        finally
        {
            var tasks = new Task?[] { stdout, stderr, exit, cleanupExit }.Where(task => task is not null).Cast<Task>().ToArray();
            foreach (var task in tasks)
                _ = task.ContinueWith(done => _ = done.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var closed = !started;
            if (started && stdout is not null && stderr is not null && exit is not null && tasks.All(task => task.Status == TaskStatus.RanToCompletion))
            { try { closed = original.HasExited; } catch (InvalidOperationException) { closed = false; } }
            if (closed) { RetainedImportGit.TryRemove(original, out _); _importOriginals.TryRemove(original, out _); original.Dispose(); }
            else RetainedImportGit[original] = new(tasks, errors.ToArray()); // Pending/error observations remain rooted, not closure proof.
        }
    }
}
