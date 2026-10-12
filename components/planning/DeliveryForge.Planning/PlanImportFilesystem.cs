using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeliveryForge.Planning;

// Private original handles bind source namespace; observations never grant filesystem control.
internal static class PlanImportFilesystem
{
    internal sealed class DirectoryBinding : IDisposable
    {
        private readonly string _path;
        private readonly SafeFileHandle _handle;
        private readonly Identity _identity;
        internal DirectoryBinding(string path)
        { _path = path; Plain(path); _handle = Open(path, directory: true);
            try { _identity = Held(_handle, true); Verify(); } catch { _handle.Dispose(); throw; } }
        internal void Verify()
        { Plain(_path); using var current = Open(_path, true); if (Held(current, true) != _identity || Held(_handle, true) != _identity) throw new PlanningException("Original import root namespace changed."); }
        public void Dispose() => _handle.Dispose();
    }
    internal static byte[] Read(string path, int maximum, PlanImportLifetime life)
    {
        life.Check(); Plain(path);
        using var handle = Open(path, false); var held = Held(handle, false);
        using var stream = new FileStream(handle, FileAccess.Read);
        var originalLength = stream.Length;
        if (originalLength > maximum) throw new PlanningException("Import metadata byte bound exceeded.");
        using var bytes = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        { life.Check(); var read = stream.Read(buffer); life.Check(); if (read == 0) break; if (bytes.Length + read > maximum) throw new PlanningException("Import metadata grew beyond bound."); bytes.Write(buffer, 0, read); }
        Plain(path); using var current = Open(path, false);
        if (bytes.Length != originalLength || stream.Length != originalLength || Held(current, false) != held || Held(handle, false) != held)
            throw new PlanningException("Original import metadata namespace/length changed.");
        life.Check(); return bytes.ToArray();
    }
    internal static void Plain(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new PlanningException("Import requires absolute source paths.");
        for (FileSystemInfo? entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path); entry is not null;
            entry = entry is DirectoryInfo directory ? directory.Parent : ((FileInfo)entry).Directory)
            if (entry.LinkTarget is not null || entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new PlanningException("Import source path is redirected.");
    }
    private static SafeFileHandle Open(string path, bool directory)
    {
        SafeFileHandle result;
        if (OperatingSystem.IsLinux())
        {
            _ = Linux(-100, path, 0x100, directory);
            var fd = LinuxOpen(path, 0x800 | 0x20000 | 0x80000 | (directory ? 0x10000 : 0));
            if (fd < 0) throw new PlanningException("Original import no-follow open unavailable: " + Marshal.GetLastPInvokeError());
            result = new SafeFileHandle((IntPtr)fd, true);
        }
        else if (OperatingSystem.IsWindows())
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\??\", StringComparison.Ordinal)) throw new PlanningException("Import device namespace unsupported.");
            result = WindowsOpen(path, directory ? 0 : 0x80000000, FileShare.Read, IntPtr.Zero, FileMode.Open, 0x00200000 | 0x02000000, IntPtr.Zero);
            if (result.IsInvalid) { var error = Marshal.GetLastPInvokeError(); result.Dispose(); throw new PlanningException("Original import disk handle unavailable: " + error); }
        }
        else throw new PlanningException("Native import namespace observation is unavailable on this platform.");
        try { _ = Held(result, directory); return result; } catch { result.Dispose(); throw; }
    }
    private static Identity Held(SafeFileHandle original, bool directory)
    {
        if (OperatingSystem.IsLinux())
        {
            var pinned = false;
            try { original.DangerousAddRef(ref pinned); var fd = original.DangerousGetHandle().ToInt64();
                if (fd is < 0 or > int.MaxValue) throw new PlanningException("Original import descriptor unavailable."); return Linux((int)fd, "", 0x1000, directory); }
            finally { if (pinned) original.DangerousRelease(); }
        }
        if (GetFileType(original) != 1 || !Attributes(original, 9, out var attributes, 8) ||
            (attributes.Attributes & 0x400) != 0 || ((attributes.Attributes & 0x10) != 0) != directory ||
            !FileId(original, 18, out var id, 24) || (id.Low | id.High) == 0) throw new PlanningException("Original import disk kind/identity unavailable.");
        return new(id.Volume, id.Low, id.High);
    }
    private static Identity Linux(int fd, string path, int flags, bool directory)
    {
        if (Statx(fd, path, flags, 0x101, out var info) != 0 || (info.Mask & 0x101) != 0x101 ||
            (info.Mode & 0xF000) != (directory ? 0x4000 : 0x8000) || info.Inode == 0) throw new PlanningException("Original import regular/directory kind or inode unavailable.");
        return new(((ulong)info.Major << 32) | info.Minor, info.Inode, 0);
    }
    private sealed record Identity(ulong Device, ulong Low, ulong High);
    [StructLayout(LayoutKind.Explicit, Size = 256)] private struct LinuxInfo
    { [FieldOffset(0)] public uint Mask; [FieldOffset(28)] public ushort Mode; [FieldOffset(32)] public ulong Inode; [FieldOffset(136)] public uint Major; [FieldOffset(140)] public uint Minor; }
    [StructLayout(LayoutKind.Sequential, Size = 24)] private struct WindowsInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential, Size = 8)] private struct WindowsAttributes { public uint Attributes, Tag; }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxInfo info);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle WindowsOpen(string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Attributes(SafeFileHandle handle, int kind, out WindowsAttributes info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FileId(SafeFileHandle handle, int kind, out WindowsInfo info, uint size);
}
