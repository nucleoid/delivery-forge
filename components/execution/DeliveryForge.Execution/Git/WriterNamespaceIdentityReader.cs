using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeliveryForge.Execution.Git;

// Consumes only a live owner-held handle and its private bound path. Results are data,
// never replacement/adoption/deletion authority or a filesystem transaction guarantee.
internal static class WriterNamespaceIdentityReader
{
    public static WriterNamespaceIdentityObservation Observe(SafeFileHandle held, string path)
    {
        if (held.IsClosed || held.IsInvalid) return Unknown("Owner-held file handle is unavailable.");
        var pinned = false;
        try
        {
            held.DangerousAddRef(ref pinned);
            if (OperatingSystem.IsWindows()) return ObserveWindows(held, path);
            if (OperatingSystem.IsLinux()) return ObserveLinux(held, path);
            return Unsupported("Native writer namespace identity is implemented only for Windows and Linux.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return Unsupported("Required native identity API is unavailable: " + exception.GetType().Name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return Unknown("Native identity observation failed: " + exception.GetType().Name);
        }
        finally { if (pinned) held.DangerousRelease(); }
    }

    private static WriterNamespaceIdentityObservation ObserveWindows(SafeFileHandle held, string path)
    {
        const uint openReparsePoint = 0x00200000;
        const uint backupSemantics = 0x02000000;
        using var current = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, openReparsePoint | backupSemantics, IntPtr.Zero);
        if (current.IsInvalid) return Unknown("Current namespace entry could not be opened without following its leaf; Win32=" + Marshal.GetLastPInvokeError());
        if (GetFileType(held) != 1 || GetFileType(current) != 1)
            return Changed("Held or current entry is not an ordinary disk file.");
        if (!GetAttributeInfo(held, 9, out var heldAttributes, 8))
            return Unknown("Held file attribute query unavailable; Win32=" + Marshal.GetLastPInvokeError());
        if (!GetAttributeInfo(current, 9, out var currentAttributes, 8))
            return Unknown("Current file attribute query unavailable; Win32=" + Marshal.GetLastPInvokeError());
        const uint unacceptable = 0x10 | 0x400; // Directory or reparse point.
        if (((heldAttributes.Attributes | currentAttributes.Attributes) & unacceptable) != 0)
            return Changed("Held or current entry is a directory or reparse point.");
        if (!GetIdInfo(held, 18, out var heldId, 24))
            return Unknown("Held 128-bit file identity unavailable; Win32=" + Marshal.GetLastPInvokeError());
        if (!GetIdInfo(current, 18, out var currentId, 24))
            return Unknown("Current 128-bit file identity unavailable; Win32=" + Marshal.GetLastPInvokeError());
        if ((heldId.Low | heldId.High) == 0 || (currentId.Low | currentId.High) == 0)
            return Unknown("Filesystem did not supply a usable 128-bit file identity.");
        return heldId.Volume == currentId.Volume && heldId.Low == currentId.Low && heldId.High == currentId.High
            ? Match("Fresh no-follow namespace entry matches the live held volume and full 128-bit file identity.")
            : Changed("Fresh namespace entry has a different native file identity from the live held handle.");
    }

    private static WriterNamespaceIdentityObservation ObserveLinux(SafeFileHandle held, string path)
    {
        var descriptor = held.DangerousGetHandle().ToInt64();
        if (descriptor is < 0 or > int.MaxValue) return Unknown("Owner-held descriptor cannot be represented safely.");
        const uint required = 0x101; // STATX_TYPE | STATX_INO.
        if (Statx((int)descriptor, "", 0x1000, required, out var heldIdentity) != 0)
            return LinuxFailure("Held descriptor statx", Marshal.GetLastPInvokeError());
        if (Statx(-100, path, 0x100, required, out var currentIdentity) != 0)
            return LinuxFailure("Current no-follow namespace statx", Marshal.GetLastPInvokeError());
        if ((heldIdentity.Mask & required) != required || (currentIdentity.Mask & required) != required)
            return Unknown("statx did not provide all required type and inode fields.");
        if ((heldIdentity.Mode & 0xF000) != 0x8000 || (currentIdentity.Mode & 0xF000) != 0x8000)
            return Changed("Held or current no-follow entry is not a regular file.");
        if (heldIdentity.Inode == 0 || currentIdentity.Inode == 0)
            return Unknown("statx did not provide a usable inode identity.");
        return heldIdentity.DeviceMajor == currentIdentity.DeviceMajor && heldIdentity.DeviceMinor == currentIdentity.DeviceMinor && heldIdentity.Inode == currentIdentity.Inode
            ? Match("Fresh no-follow namespace entry matches the live held containing device and inode.")
            : Changed("Fresh namespace entry has a different native device/inode identity from the live held handle.");
    }

    private static WriterNamespaceIdentityObservation LinuxFailure(string operation, int error) => error is 38 or 22 or 95
        ? Unsupported(operation + " is unavailable; errno=" + error)
        : Unknown(operation + " failed; errno=" + error);
    private static WriterNamespaceIdentityObservation Match(string reason) => new(WriterNamespaceIdentityOutcome.Match, reason);
    private static WriterNamespaceIdentityObservation Changed(string reason) => new(WriterNamespaceIdentityOutcome.Changed, reason);
    private static WriterNamespaceIdentityObservation Unknown(string reason) => new(WriterNamespaceIdentityOutcome.Unknown, reason);
    private static WriterNamespaceIdentityObservation Unsupported(string reason) => new(WriterNamespaceIdentityOutcome.Unsupported, reason);

    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct WindowsFileId { public ulong Volume; public ulong Low; public ulong High; }
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    private struct WindowsAttributeTag { public uint Attributes; public uint Tag; }
    // Full Linux UAPI statx buffer, not the platform-dependent legacy struct stat.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, FileMode disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdInfo(SafeFileHandle handle, int informationClass, out WindowsFileId information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributeInfo(SafeFileHandle handle, int informationClass, out WindowsAttributeTag information, uint size);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx information);
}
