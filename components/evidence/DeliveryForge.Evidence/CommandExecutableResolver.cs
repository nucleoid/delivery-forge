using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DeliveryForge.Evidence;

// Selects the executable in the actual parent before applying a child-only
// environment. This cooperative lookup is not sealed-image/kernel ownership.
internal static class CommandExecutableResolver
{
    internal static string ResolveWindows(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0')) throw new ArgumentException("Invalid executable name.");
        name = name.Trim();
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"') name = name[1..^1];
        if (Path.IsPathFullyQualified(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(name);
        var buffer = new StringBuilder(32768);
        var length = SearchPathW(null, name, ".exe", (uint)buffer.Capacity, buffer, IntPtr.Zero);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "resolve-executable");
        return buffer.ToString();
    }

    internal static string ResolveLinux(string name) => ResolveLinuxParentSnapshot(name,
        Environment.GetEnvironmentVariable("PATH"), Directory.GetCurrentDirectory());

    // The caller above supplies the actual parent snapshot, never invocation
    // Environment/PATH. Internal filesystem controls can exercise that lookup
    // without changing the test process's global environment.
    internal static string ResolveLinuxParentSnapshot(string name, string? parentPath, string parentDirectory)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux parent executable lookup only.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32768 || name.Contains('\0'))
            throw new ArgumentException("Invalid executable name.");
        if (name.Contains('/'))
        {
            var selected = Path.GetFullPath(name, parentDirectory);
            if (IsExecutable(selected)) return selected;
            throw new FileNotFoundException("Explicit parent-selected executable is unavailable.", selected);
        }
        if (parentPath is null || parentPath.Length > 32768 || parentPath.Contains('\0'))
            throw new ArgumentException("Parent PATH is missing or exceeds the finite lookup profile.");
        if (parentPath.Count(character => character == ':') >= 1024)
            throw new ArgumentException("Parent PATH exceeds1024 lookup entries.");
        var remaining = parentPath.AsSpan();
        while (true)
        {
            var separator = remaining.IndexOf(':');
            var entry = separator < 0 ? remaining : remaining[..separator];
            // Empty/relative entries keep the actual parent's lookup semantics;
            // the child's separate WorkingDirectory never rebases selection.
            var directory = entry.IsEmpty ? parentDirectory : Path.GetFullPath(entry.ToString(), parentDirectory);
            var selected = Path.GetFullPath(Path.Combine(directory, name));
            if (IsExecutable(selected)) return selected;
            if (separator < 0) break;
            remaining = remaining[(separator + 1)..];
        }
        throw new FileNotFoundException("Executable was not found in the actual parent PATH.", name);
    }

    private static bool IsExecutable(string path) => File.Exists(path) && access(path, 1) == 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SearchPathW(string? path, string name, string? extension, uint length, StringBuilder result, IntPtr filePart);
    [DllImport("libc", SetLastError = true)]
    private static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
