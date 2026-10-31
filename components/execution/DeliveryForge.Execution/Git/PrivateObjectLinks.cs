using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeliveryForge.Execution.Git;

// Native DATA checks for the selected original object namespace, not file or
// process adoption. Linux ctime also catches restored-mtime mutations.
internal static class PrivateObjectLinks
{
    internal static (ulong Device, ulong Inode, long ChangedSeconds, uint ChangedNanoseconds) Identity(string path, bool directory)
    {
        if (!OperatingSystem.IsLinux()) return (0, 0, 0, 0); // Windows held files also use exclusive write sharing and native file IDs.
        var value = Read(-100, path, 0x100);
        if ((value.Mode & 0xF000) != (directory ? 0x4000 : 0x8000) || !directory && value.Links != 1)
            throw new WorktreeBoundaryException("Private object namespace contains a redirected type or hard-linked file.");
        return ((ulong)value.DeviceMajor << 32 | value.DeviceMinor, value.Inode, value.ChangedSeconds, value.ChangedNanoseconds);
    }

    internal static bool HasOneLink(SafeFileHandle held)
    {
        if (OperatingSystem.IsLinux())
        {
            var value = Read(checked((int)held.DangerousGetHandle()), "", 0x1000);
            return (value.Mode & 0xF000) == 0x8000 && value.Links == 1;
        }
        if (OperatingSystem.IsWindows())
        {
            if (!GetStandard(held, 1, out var value, 24)) throw new WorktreeBoundaryException("Private object link count is unavailable.");
            return value.Links == 1 && value.Directory == 0 && value.DeletePending == 0;
        }
        throw new PlatformNotSupportedException("Private object identity supports Linux/Windows only.");
    }

    static LinuxStatx Read(int directory, string path, int flags)
    {
        const uint required = 0x187; // TYPE, MODE, NLINK, INO, CTIME.
        if (Statx(directory, path, flags, required, out var value) != 0 || (value.Mask & required) != required || value.Inode == 0)
            throw new WorktreeBoundaryException("Private object original native identity is unavailable.");
        return value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(96)] public long ChangedSeconds;
        [FieldOffset(104)] public uint ChangedNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct WindowsStandard
    {
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public byte DeletePending;
        [FieldOffset(21)] public byte Directory;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx information);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetStandard(SafeFileHandle file, int informationClass, out WindowsStandard information, uint size);
}
