using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeliveryForge.Execution.Git;

// FileShare.Delete is a shared advisory flock in .NET on Linux. Upgrade the SAME
// opened descriptor before reading identity; no path reopen or deletion authority.
internal static class LinuxCleanupWriterFence
{
    internal static void RequireExclusive(SafeFileHandle original)
    {
        if (!OperatingSystem.IsLinux()) return; // Windows keeps its native delete-sharing exclusion.
        try
        {
            const int exclusive = 2, nonblocking = 4;
            // SafeHandle marshalling pins the original descriptor for this call. The
            // owning FileStream retains it; closing that descriptor releases the lock.
            if (Flock(original, exclusive | nonblocking) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new WorktreeBoundaryException("Original Linux cleanup writer could not acquire an exclusive nonblocking fence.", new Win32Exception(error));
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            throw new WorktreeBoundaryException("Original Linux cleanup writer exclusive fencing is unavailable.", error);
        }
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle original, int operation);
}
