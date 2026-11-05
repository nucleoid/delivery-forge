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
public sealed record ProcessControlResult(ProcessControlOutcome Outcome, string Reason)
{
    public OwnedProcessImageObservation? ImageObservation { get; init; }
}
// Diagnostic observation data only; no native handle, adoption or signaling authority.
public sealed record OwnedProcessImageObservation(ProcessControlOutcome Outcome, string RequestedLaunch,
    string ObservedImagePath, long PlatformStartIdentity, string Stage, string Reason)
{
    public bool RequestedLaunchComplete { get; init; } = true;
}

public sealed class ProcessControl : IAsyncDisposable
{
    // This controller owns exact roots; it does not own a kernel descendant group.
    public bool OwnsDescendantGroups => false;

    private readonly ConcurrentDictionary<int, OwnedProcess> _owned = new();
    private readonly ConcurrentDictionary<int, WindowsOwnedProcessImage> _windows = new();
    private readonly ConcurrentDictionary<int, string> _requestedLaunches = new();
    private readonly object _registry = new();
    private int _disposed;

    public OwnedProcess StartOwned(ProcessLaunch launch, string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Delivery Forge process control supports Linux and Windows only; macOS is unclaimed.");
        var start = new ProcessStartInfo(launch.Executable) { UseShellExecute = false };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The child process did not start.");
        WindowsOwnedProcessImage? original = null;
        try
        {
            if (OperatingSystem.IsWindows()) original = new(process, launch.Executable);
            var observed = original is null ? ReadLiveIdentity(process) : ReadOriginalWindowsIdentity(original);
            if (observed.State != IdentityReadState.Live)
                throw new InvalidOperationException("The child process identity could not be established while it was live.");
            var identity = new ProcessIdentity(process.Id, observed.PlatformStartIdentity, observed.ExecutablePath,
                Digest(observed.ExecutablePath, launch.Arguments));
            var owned = new OwnedProcess(process, identity, runId);
            lock (_registry)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (!_owned.TryAdd(process.Id, owned))
                    throw new InvalidOperationException("A process with the same PID is already registered in this controller.");
                if (original is not null) _windows[process.Id] = original;
                _requestedLaunches[process.Id] = launch.Executable;
            }
            return owned;
        }
        catch
        {
            TryTerminateExactHandle(process);
            process.Dispose();
            original?.Dispose();
            throw;
        }
    }

    public IReadOnlyList<ProcessControlResult> ObserveOwned(string runId) =>
        _owned.Values.Where(owned => owned.RunId == runId)
            .Select(owned => Observe(owned.Identity)).ToArray();

    public OwnedProcessImageObservation ObserveOwnedImage(ProcessIdentity expectedRegisteredIdentity) =>
        ObserveOwnedImageCore(expectedRegisteredIdentity, allowDisposedController: false);

    private OwnedProcessImageObservation ObserveOwnedImageCore(ProcessIdentity expected, bool allowDisposedController)
    {
        OwnedProcess owned;
        WindowsOwnedProcessImage? original;
        string requested;
        lock (_registry)
        {
            if ((!allowDisposedController && Volatile.Read(ref _disposed) != 0) ||
                !_owned.TryGetValue(expected.ProcessId, out owned!) || owned.Identity != expected)
                return new(ProcessControlOutcome.IdentityUnknown, "", "", 0, "registry-refusal",
                    "PID is not registered with the exact start/executable/argv identity in this live executor lifetime.")
                { RequestedLaunchComplete = false };
            _windows.TryGetValue(expected.ProcessId, out original);
            requested = _requestedLaunches[expected.ProcessId];
        }
        OwnedProcessImageObservation observed;
        if (OperatingSystem.IsWindows())
        {
            observed = original?.Read() ?? new(ProcessControlOutcome.IdentityUnknown, requested.Length <= 4096 ? requested : requested[..4096], "", 0,
                "windows-owned-handle-image", "Original privately captured process handle is unavailable.")
            { RequestedLaunchComplete = requested.Length <= 4096 };
        }
        else
        {
            var read = ReadLiveIdentity(owned.Process); // Existing Linux image/start behavior is preserved.
            observed = new(read.State == IdentityReadState.Live ? ProcessControlOutcome.LiveOwned :
                read.State == IdentityReadState.Exited ? ProcessControlOutcome.AlreadyExited : ProcessControlOutcome.IdentityUnknown,
                requested, read.State == IdentityReadState.Live ? read.ExecutablePath : "",
                read.State == IdentityReadState.Live ? read.PlatformStartIdentity : 0, "linux-owned-image",
                read.State == IdentityReadState.Exited ? "Owned child already exited." :
                read.State == IdentityReadState.Unknown ? "Process identity could not be observed safely." : "Owned process image/start observation sampled.")
            { RequestedLaunch = requested.Length <= 4096 ? requested : requested[..4096], RequestedLaunchComplete = requested.Length <= 4096 };
        }
        if (observed.Outcome != ProcessControlOutcome.LiveOwned) return observed;
        if (observed.PlatformStartIdentity != expected.PlatformStartIdentity)
            return observed with { Outcome = ProcessControlOutcome.IdentityUnknown, Reason = "Live PID no longer matches its platform start identity." };
        if (!string.Equals(observed.ObservedImagePath, expected.ExecutablePath, PlatformPathComparison()))
            return observed with { Outcome = ProcessControlOutcome.IdentityUnknown, Reason = "Live PID no longer matches its executable identity." };
        return observed with { Reason = "Identity positively matched and the registered process remains live." };
    }

    public IReadOnlyList<ProcessIdentity> GetOwnedIdentities(string runId) =>
        _owned.Values.Where(owned => owned.RunId == runId)
            .Select(owned => owned.Identity).ToArray();

    // Observation only: the snapshot cannot register/adopt a process or mint control authority.
    public IReadOnlyList<ProcessIdentity> GetAllOwnedIdentities()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var snapshot = _owned.Values.Select(owned => owned.Identity).ToArray();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return snapshot;
    }

    public ProcessControlResult Observe(ProcessIdentity expected)
    {
        var observed = ObserveOwnedImage(expected);
        return new(observed.Outcome, observed.Reason) { ImageObservation = observed };
    }

    public async Task<ProcessControlResult> QuiesceAsync(ProcessIdentity expected, TimeSpan gracefulDeadline, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            return new ProcessControlResult(ProcessControlOutcome.UnsupportedPlatform, "Linux and Windows are supported; macOS is unclaimed.");
        var observation = Observe(expected);
        if (observation.Outcome is ProcessControlOutcome.AlreadyExited or ProcessControlOutcome.IdentityUnknown) return observation;
        var process = _owned[expected.ProcessId].Process;
        _windows.TryGetValue(expected.ProcessId, out var original);

        if (Observe(expected).Outcome != ProcessControlOutcome.LiveOwned)
            return new ProcessControlResult(ProcessControlOutcome.IdentityUnknown, "Identity changed before graceful signaling; no signal sent.");
        if (OperatingSystem.IsLinux()) _ = kill(process.Id, 15);
        else if (original is null) return new(ProcessControlOutcome.IdentityUnknown, "Original Windows control handle is unavailable; no signal sent.");
        else _ = original.TryCloseMainWindow(expected);
        if (OperatingSystem.IsWindows())
        {
            var afterGraceful = Observe(expected);
            if (afterGraceful.Outcome == ProcessControlOutcome.IdentityUnknown) return afterGraceful;
            if (afterGraceful.Outcome == ProcessControlOutcome.AlreadyExited)
                return new(ProcessControlOutcome.Quiesced, "Original owned child exited after graceful control.") { ImageObservation = afterGraceful.ImageObservation };
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(gracefulDeadline);
        try
        {
            if (original is null) await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            else await original.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new ProcessControlResult(ProcessControlOutcome.Quiesced, "Owned child exited within the bounded graceful deadline.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (Observe(expected).Outcome != ProcessControlOutcome.LiveOwned)
                return new ProcessControlResult(ProcessControlOutcome.IdentityUnknown, "Identity changed before forced termination; no signal sent.");
            if (original is null) process.Kill(entireProcessTree: false);
            else if (!original.TryTerminate(expected)) return new(ProcessControlOutcome.IdentityUnknown, "Original Windows identity changed before forced termination; no signal sent.");
            using var forced = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (original is null) await process.WaitForExitAsync(forced.Token).ConfigureAwait(false);
                else await original.WaitForExitAsync(forced.Token).ConfigureAwait(false);
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
        Interlocked.Exchange(ref _disposed, 1);
        foreach (var owned in _owned.Values)
        {
            try
            {
                var observation = ObserveOwnedImageCore(owned.Identity, allowDisposedController: true).Outcome;
                _windows.TryGetValue(owned.Identity.ProcessId, out var original);
                if (OperatingSystem.IsWindows() && original is null)
                    throw new InvalidOperationException("Original Windows owner handle is unavailable; no caller-object signal attempted.");
                if (observation == ProcessControlOutcome.LiveOwned)
                {
                    if (original is null) owned.Process.Kill(entireProcessTree: false);
                    else if (!original.TryTerminate(owned.Identity))
                        throw new InvalidOperationException("Original Windows identity changed before disposal control; no signal sent.");
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    if (original is null) await owned.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    else await original.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                }
                else if (observation == ProcessControlOutcome.AlreadyExited)
                {
                    if (original is null) await owned.Process.WaitForExitAsync().ConfigureAwait(false);
                    else
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await original.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException) { }
            lock (_registry)
            {
                _owned.TryRemove(owned.Identity.ProcessId, out _);
                _requestedLaunches.TryRemove(owned.Identity.ProcessId, out _);
                if (_windows.TryRemove(owned.Identity.ProcessId, out var captured)) captured.Dispose();
                owned.Process.Dispose();
            }
        }
        _owned.Clear();
    }

    private static IdentityRead ReadOriginalWindowsIdentity(WindowsOwnedProcessImage original)
    {
        var observed = original.Read();
        return new(observed.Outcome == ProcessControlOutcome.LiveOwned ? IdentityReadState.Live :
            observed.Outcome == ProcessControlOutcome.AlreadyExited ? IdentityReadState.Exited : IdentityReadState.Unknown,
            observed.PlatformStartIdentity, observed.ObservedImagePath);
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
