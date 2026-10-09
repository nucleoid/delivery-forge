using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Execution;

public sealed record ProcessLaunch(string Executable, IReadOnlyList<string> Arguments);
public sealed record ProcessIdentity(int ProcessId, long PlatformStartIdentity, string ExecutablePath, string ArgumentDigest);
public sealed record OwnedProcess(Process Process, ProcessIdentity Identity, string? RunId = null);
public enum ProcessControlOutcome { LiveOwned, Quiesced, AlreadyExited, IdentityUnknown, TimedOut, UnsupportedPlatform, ControlFailed }
public sealed record ProcessControlResult(ProcessControlOutcome Outcome, string Reason);

public sealed class ProcessControl : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, OwnedProcess> _owned = new();

    public OwnedProcess StartOwned(ProcessLaunch launch, string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Delivery Forge process control supports Linux and Windows only; macOS is unclaimed.");
        var start = new ProcessStartInfo(launch.Executable) { UseShellExecute = false };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The child process did not start.");
        try
        {
            var observed = ReadLiveIdentity(process);
            if (observed.State != IdentityReadState.Live)
                throw new InvalidOperationException("The child process identity could not be established while it was live.");
            var identity = new ProcessIdentity(process.Id, observed.PlatformStartIdentity, observed.ExecutablePath,
                Digest(observed.ExecutablePath, launch.Arguments));
            var owned = new OwnedProcess(process, identity, runId);
            if (!_owned.TryAdd(process.Id, owned))
                throw new InvalidOperationException("A process with the same PID is already registered in this controller.");
            return owned;
        }
        catch
        {
            TryTerminateExactHandle(process);
            process.Dispose();
            throw;
        }
    }

    public IReadOnlyList<ProcessControlResult> ObserveOwned(string runId) =>
        _owned.Values.Where(owned => owned.RunId == runId)
            .Select(owned => Observe(owned.Identity)).ToArray();

    public IReadOnlyList<ProcessIdentity> GetOwnedIdentities(string runId) =>
        _owned.Values.Where(owned => owned.RunId == runId)
            .Select(owned => owned.Identity).ToArray();

    public ProcessControlResult Observe(ProcessIdentity expected)
    {
        if (!_owned.TryGetValue(expected.ProcessId, out var owned) || owned.Identity != expected)
            return new ProcessControlResult(ProcessControlOutcome.IdentityUnknown, "PID is not registered with the exact start/executable/argv identity in this executor lifetime.");
        var observed = ReadLiveIdentity(owned.Process);
        if (observed.State == IdentityReadState.Exited)
            return new ProcessControlResult(ProcessControlOutcome.AlreadyExited, "Owned child already exited.");
        if (observed.State == IdentityReadState.Unknown)
            return new ProcessControlResult(ProcessControlOutcome.IdentityUnknown, "Process identity could not be observed safely.");
        if (observed.PlatformStartIdentity != expected.PlatformStartIdentity)
            return new ProcessControlResult(ProcessControlOutcome.IdentityUnknown, "Live PID no longer matches its platform start identity.");
        if (!string.Equals(observed.ExecutablePath, expected.ExecutablePath, PlatformPathComparison()))
            return new ProcessControlResult(ProcessControlOutcome.IdentityUnknown, "Live PID no longer matches its executable identity.");
        return new ProcessControlResult(ProcessControlOutcome.LiveOwned, "Identity positively matched and the registered process remains live.");
    }

    public async Task<ProcessControlResult> QuiesceAsync(ProcessIdentity expected, TimeSpan gracefulDeadline, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            return new ProcessControlResult(ProcessControlOutcome.UnsupportedPlatform, "Linux and Windows are supported; macOS is unclaimed.");
        var observation = Observe(expected);
        if (observation.Outcome is ProcessControlOutcome.AlreadyExited or ProcessControlOutcome.IdentityUnknown) return observation;
        var process = _owned[expected.ProcessId].Process;

        if (Observe(expected).Outcome != ProcessControlOutcome.LiveOwned)
            return new ProcessControlResult(ProcessControlOutcome.IdentityUnknown, "Identity changed before graceful signaling; no signal sent.");
        if (OperatingSystem.IsLinux()) _ = kill(process.Id, 15);
        else _ = process.CloseMainWindow();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(gracefulDeadline);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new ProcessControlResult(ProcessControlOutcome.Quiesced, "Owned child exited within the bounded graceful deadline.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (Observe(expected).Outcome != ProcessControlOutcome.LiveOwned)
                return new ProcessControlResult(ProcessControlOutcome.IdentityUnknown, "Identity changed before forced termination; no signal sent.");
            process.Kill(entireProcessTree: false);
            using var forced = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(forced.Token).ConfigureAwait(false);
                return new ProcessControlResult(ProcessControlOutcome.Quiesced, "Exact owned child required bounded forced termination.");
            }
            catch (OperationCanceledException)
            {
                return new ProcessControlResult(ProcessControlOutcome.TimedOut, "Owned child did not quiesce within bounded deadlines.");
            }
        }
    }

    public static string Digest(string executable, IEnumerable<string> arguments)
    {
        var value = string.Join('\0', new[] { Path.GetFullPath(executable) }.Concat(arguments));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned.Values)
        {
            try
            {
                var observation = Observe(owned.Identity).Outcome;
                if (observation == ProcessControlOutcome.LiveOwned)
                {
                    owned.Process.Kill(entireProcessTree: false);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await owned.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                }
                else if (observation == ProcessControlOutcome.AlreadyExited)
                {
                    await owned.Process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException) { }
            owned.Process.Dispose();
        }
        _owned.Clear();
    }

    private static void TryTerminateExactHandle(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
    }

    private static IdentityRead ReadLiveIdentity(Process process)
    {
        const int attempts = 3;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                process.Refresh();
                if (process.HasExited) return new IdentityRead(IdentityReadState.Exited, 0, "");
                var executable = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executable))
                    return new IdentityRead(IdentityReadState.Live, process.StartTime.ToUniversalTime().Ticks,
                        Path.GetFullPath(executable));
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or ArgumentException)
            {
                if (attempt == attempts - 1) break;
            }

            if (attempt < attempts - 1) Thread.Sleep(10);
        }

        return new IdentityRead(IdentityReadState.Unknown, 0, "");
    }

    private static StringComparison PlatformPathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);

    private enum IdentityReadState { Live, Exited, Unknown }
    private sealed record IdentityRead(IdentityReadState State, long PlatformStartIdentity, string ExecutablePath);
}
