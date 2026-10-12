using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeliveryForge.Execution;

// Captured only immediately after this controller's own Process.Start, before publication.
// The original handle is never replaced by a later caller-visible Process association.
internal sealed class WindowsOwnedProcessImage : IDisposable
{
    private readonly Process _process;
    private readonly int _pid;
    private readonly SafeProcessHandle _handle;
    private readonly string _requested;
    private readonly bool _requestedComplete;
    private readonly object _lifetime = new();
    private bool _pin;
    private bool _disposed;

    public WindowsOwnedProcessImage(Process process, string requested)
    {
        _process = process;
        _pid = process.Id;
        _requested = requested.Length <= 4096 ? requested : requested[..4096];
        _requestedComplete = requested.Length <= 4096;
        _handle = process.SafeHandle; // Exactly once, before publishing the owned process.
        try { _handle.DangerousAddRef(ref _pin); }
        catch { if (_pin) _handle.DangerousRelease(); throw; }
    }

    public OwnedProcessImageObservation Read()
    {
        lock (_lifetime)
        {
            if (!IsOriginalAssociation()) return Unknown("Caller-visible Process is disposed/reassociated or the original owner handle is unavailable.");
            var pinned = false;
            try
            {
                _handle.DangerousAddRef(ref pinned);
                var initial = WaitForSingleObject(_handle, 0);
                if (initial == 0) return Exited();
                if (initial != 258) return Unknown("Original handle liveness is unknown; Win32=" + Marshal.GetLastPInvokeError());
                if (!GetProcessTimes(_handle, out var created, out _, out _, out _))
                    return MetadataUnavailable("Original handle creation time unavailable; Win32=" + Marshal.GetLastPInvokeError());
                var fileTime = ((ulong)created.High << 32) | created.Low;
                if (fileTime > long.MaxValue) return MetadataUnavailable("Original creation FILETIME is outside the supported UTC range.");
                var ticks = DateTime.FromFileTimeUtc((long)fileTime).Ticks;
                string? image = null;
                for (var capacity = 512; capacity <= 32768; capacity *= 2)
                {
                    var buffer = new StringBuilder(capacity);
                    var length = (uint)capacity;
                    if (QueryFullProcessImageName(_handle, 0, buffer, ref length))
                    {
                        if (length == 0 || length >= capacity || buffer.Length != length)
                            return MetadataUnavailable("Original image query returned empty or truncated data.");
                        image = buffer.ToString(0, checked((int)length));
                        if (!Path.IsPathFullyQualified(image)) return MetadataUnavailable("Original kernel image is not an absolute Win32 path.");
                        image = Path.GetFullPath(image);
                        break;
                    }
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 122 || capacity == 32768) return MetadataUnavailable("Original image query unavailable; Win32=" + error);
                }
                var final = WaitForSingleObject(_handle, 0);
                if (final == 0) return Exited();
                if (final != 258 || !IsOriginalAssociation()) return Unknown("Original process liveness/association changed during observation.");
                return image is null ? MetadataUnavailable("Original image exceeded its finite character bound.") :
                    new(ProcessControlOutcome.LiveOwned, _requested, image, ticks, "windows-owned-handle-image",
                        "Executable image and UTC creation identity sampled through the original privately pinned process handle.")
                    { RequestedLaunchComplete = _requestedComplete };
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
            {
                return MetadataUnavailable("Original process image observation unavailable: " + exception.GetType().Name);
            }
            finally { if (pinned) _handle.DangerousRelease(); }
        }
    }

    // Called only under Read's original lifetime lock/pin. Metadata failure is never
    // an exit condition: only this fresh same-handle signal can establish exit.
    private OwnedProcessImageObservation MetadataUnavailable(string preservedFailure)
    {
        if (!IsOriginalAssociation()) return Unknown(preservedFailure + "; original association unavailable.");
        try
        {
            var waited = WaitForSingleObject(_handle, 0);
            var waitError = waited == uint.MaxValue ? Marshal.GetLastPInvokeError() : 0;
            if (!IsOriginalAssociation()) return Unknown(preservedFailure + "; original association changed after exit sample.");
            if (waited == 0)
                return new(ProcessControlOutcome.AlreadyExited, _requested, "", 0, "windows-owned-handle-image",
                    preservedFailure + "; fresh original privately pinned handle is signaled.")
                { RequestedLaunchComplete = _requestedComplete };
            if (waited == 258) return Unknown(preservedFailure + "; fresh original handle remains nonsignaled.");
            return Unknown(preservedFailure + "; fresh exit sample unavailable, wait=" + waited + ", Win32=" + waitError);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return Unknown(preservedFailure + "; fresh original exit sample unavailable: " + exception.GetType().Name);
        }
    }

    public bool TryCloseMainWindow(ProcessIdentity expected)
    {
        lock (_lifetime)
        {
            if (expected.ProcessId != _pid || !Matches(Read(), expected)) return false;
            // Preserve graceful-window behavior only while this caller-visible object
            // still denotes the captured original. Forced control and waits use the handle.
            return IsOriginalAssociation() && _process.CloseMainWindow();
        }
    }

    public bool TryTerminate(ProcessIdentity expected)
    {
        lock (_lifetime)
        {
            if (expected.ProcessId != _pid || !Matches(Read(), expected)) return false;
            if (!TerminateProcess(_handle, 1))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            return true;
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lifetime)
            {
                if (!IsOriginalAssociation()) throw new InvalidOperationException("Original process association is unavailable; no caller-PID wait attempted.");
                var waited = WaitForSingleObject(_handle, 0);
                if (waited == 0) return;
                if (waited != 258) throw new InvalidOperationException("Original handle exit observation is unknown.");
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsOriginalAssociation()
    {
        if (_disposed || _handle.IsClosed || _handle.IsInvalid) return false;
        try { return _process.Id == _pid; }
        catch (InvalidOperationException) { return false; }
    }
    private static bool Matches(OwnedProcessImageObservation observed, ProcessIdentity expected) =>
        observed.Outcome == ProcessControlOutcome.LiveOwned && observed.PlatformStartIdentity == expected.PlatformStartIdentity &&
        string.Equals(observed.ObservedImagePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    private OwnedProcessImageObservation Unknown(string reason) => new(ProcessControlOutcome.IdentityUnknown, _requested, "", 0, "windows-owned-handle-image", reason) { RequestedLaunchComplete = _requestedComplete };
    private OwnedProcessImageObservation Exited() => new(ProcessControlOutcome.AlreadyExited, _requested, "", 0, "windows-owned-handle-image", "Original privately held process handle is signaled; no cached image reported as live.") { RequestedLaunchComplete = _requestedComplete };
    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            if (_pin) { _handle.DangerousRelease(); _pin = false; }
        }
    }
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    private struct NativeFileTime { public uint Low; public uint High; }
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out NativeFileTime created, out NativeFileTime exited, out NativeFileTime kernel, out NativeFileTime user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
