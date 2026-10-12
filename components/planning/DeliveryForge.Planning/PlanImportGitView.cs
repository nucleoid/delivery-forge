using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeliveryForge.Planning;

// Owner-private immutable effective metadata. Never discover workspace config at dispatch.
// Linux carriers are kernel sealed, not advisory-lock protected. Self PID addresses
// only this creator's held file descriptors; it is never imported process control.
internal sealed class PlanImportGitView : IDisposable
{
    private static readonly ConcurrentDictionary<PlanImportGitView, byte> Retained = new();
    private readonly PlanImportLifetime _life;
    private readonly List<IDisposable> _leases = [];
    private readonly List<Carrier> _carriers = [];
    private readonly string _directory;
    private readonly string _objects;
    private readonly string _sourceObjects;
    private GitRepositoryContextReader? _reader;
    internal PlanImportGitMetadata Metadata { get; }
    internal PlanImportGitView(string root, string trustedGit, PlanImportLifetime life)
    {
        _life = life; life.Check(); Metadata = PlanImportGitMetadata.Read(root, life);
        _sourceObjects = Path.Combine(Metadata.Common, "objects");
        _directory = Path.Combine(Path.GetTempPath(), "delivery-forge-plan-import-" + Guid.NewGuid().ToString("N"));
        _objects = Path.Combine(_directory, "objects");
        try
        {
            _leases.Add(new PlanImportFilesystem.FileBinding(trustedGit));
            _leases.Add(new PlanImportFilesystem.DirectoryBinding(Metadata.Metadata));
            if (Metadata.Common != Metadata.Metadata) _leases.Add(new PlanImportFilesystem.DirectoryBinding(Metadata.Common));
            _leases.Add(new PlanImportFilesystem.DirectoryBinding(_sourceObjects));
            PlanImportFilesystem.Plain(_directory); life.Check();
            if (Path.Exists(_directory)) throw new PlanningException("Private import metadata collision.");
            Directory.CreateDirectory(_directory); Directory.CreateDirectory(Path.Combine(_directory, "refs")); Directory.CreateDirectory(Path.Combine(_directory, "objects"));
            _leases.Add(new PlanImportFilesystem.DirectoryBinding(_directory));
            _leases.Add(new PlanImportFilesystem.DirectoryBinding(_objects));
            FreezeObjects();
            foreach (var file in Metadata.Files)
            {
                life.Check(); var path = Path.Combine(_directory, file.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string? symbolicHead = null;
                if (OperatingSystem.IsLinux() && file.Key == "HEAD")
                {
                    // Git validates HEAD symlinks as legacy refs/ symbolic references,
                    // not as ordinary file links. A /proc FD link is not a repository HEAD.
                    var text = new UTF8Encoding(false, true).GetString(file.Value);
                    if (!text.StartsWith("ref: refs/", StringComparison.Ordinal))
                        throw new PlanningException("Detached or unsupported HEAD cannot be reconciled as a fresh branch import.");
                    symbolicHead = text[5..].TrimEnd('\r', '\n');
                    if (!symbolicHead.StartsWith("refs/", StringComparison.Ordinal) || symbolicHead.Length > 240 ||
                        symbolicHead.Any(char.IsWhiteSpace) || symbolicHead.Contains('\\') || symbolicHead.Contains('\0') ||
                        symbolicHead.Split('/').Any(part => part is "" or "." or ".."))
                        throw new PlanningException("Unsupported original HEAD symbolic reference.");
                }
                _carriers.Add(new Carrier(path, file.Value, symbolicHead)); life.Check();
            }
            Verify();
        }
        catch { foreach (var carrier in _carriers) carrier.Dispose(); foreach (var lease in _leases) lease.Dispose(); throw; }
    }
    // No Git process is started until this own immutable object namespace is complete.
    // Full loose objects and complete pack+index pairs preserve Git's actual encodings.
    // Acceleration/routing formats outside this finite profile are explicitly refused.
    private void FreezeObjects()
    {
        const int maximumFiles = 4096, maximumDirectories = 259, maximumEntries = 8192;
        const int maximumFileBytes = 64 * 1024 * 1024;
        const long maximumTotalBytes = 256L * 1024 * 1024;
        var files = 0; var directories = 1; var entries = 0; long bytes = 0;
        var packs = new HashSet<string>(StringComparer.Ordinal);
        var indexes = new HashSet<string>(StringComparer.Ordinal);
        var packAuxiliaries = new HashSet<string>(StringComparer.Ordinal);
        bool Hex(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        void Entry(string path)
        {
            _life.Check();
            if (++entries > maximumEntries) throw new PlanningException("Immutable object entry bound exceeded.");
            PlanImportFilesystem.Plain(path);
        }
        void Capture(string source, string relative)
        {
            _life.Check();
            if (++files > maximumFiles) throw new PlanningException("Immutable object file bound exceeded.");
            var content = PlanImportFilesystem.Read(source, maximumFileBytes, _life);
            if (content.LongLength > maximumTotalBytes - bytes) throw new PlanningException("Immutable object aggregate byte bound exceeded.");
            bytes += content.LongLength;
            var target = Path.Combine(_objects, relative.Replace('/', Path.DirectorySeparatorChar));
            _carriers.Add(new Carrier(target, content, null)); _life.Check();
        }
        foreach (var directory in Directory.EnumerateFileSystemEntries(_sourceObjects))
        {
            Entry(directory);
            var name = Path.GetFileName(directory);
            if (!Directory.Exists(directory) || !(name is "pack" or "info" || name.Length == 2 && Hex(name)))
                throw new PlanningException("Unsupported immutable object root shape.");
            if (++directories > maximumDirectories) throw new PlanningException("Immutable object directory bound exceeded.");
            _leases.Add(new PlanImportFilesystem.DirectoryBinding(directory));
            var ownDirectory = Path.Combine(_objects, name);
            Directory.CreateDirectory(ownDirectory); _life.Check();
            _leases.Add(new PlanImportFilesystem.DirectoryBinding(ownDirectory));
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                Entry(path);
                var leaf = Path.GetFileName(path);
                if (Directory.Exists(path)) throw new PlanningException("Nested object routing/acceleration directories are unsupported.");
                if (name == "info")
                {
                    // Server-info lists are not local object authority. Do not copy them.
                    // Alternates, HTTP alternates, grafts and commit-graph formats cannot route this view.
                    if (leaf != "packs") throw new PlanningException("Unsupported external object routing or acceleration metadata.");
                    _ = PlanImportFilesystem.Read(path, 1024 * 1024, _life); continue;
                }
                if (name == "pack")
                {
                    var extension = Path.GetExtension(leaf);
                    var stem = Path.GetFileNameWithoutExtension(leaf);
                    if (!stem.StartsWith("pack-", StringComparison.Ordinal) || stem.Length is not (45 or 69) || !Hex(stem[5..]) ||
                        extension is not (".pack" or ".idx" or ".rev" or ".bitmap" or ".keep"))
                        throw new PlanningException("Unsupported packed object encoding/routing; complete ordinary packs are required.");
                    if (extension == ".pack") packs.Add(stem);
                    if (extension == ".idx") indexes.Add(stem);
                    if (extension is ".rev" or ".bitmap" or ".keep") packAuxiliaries.Add(stem);
                }
                else if (leaf.Length is not (38 or 62) || !Hex(leaf))
                    throw new PlanningException("Unsupported loose object encoding or in-progress object file.");
                Capture(path, name + "/" + leaf);
            }
        }
        if (!packs.SetEquals(indexes)) throw new PlanningException("Immutable packed objects require matching pack/index pairs.");
        if (!packAuxiliaries.IsSubsetOf(packs)) throw new PlanningException("Immutable pack auxiliary has no matching ordinary pack.");
        _life.Check();
    }
    internal void Attach(GitRepositoryContextReader reader) => _reader = reader;
    internal void Apply(IDictionary<string, string?> environment)
    {
        Verify(); environment["GIT_DIR"] = _directory; environment["GIT_COMMON_DIR"] = _directory;
        environment["GIT_WORK_TREE"] = Metadata.Root; environment["GIT_INDEX_FILE"] = Path.Combine(_directory, "index");
        environment["GIT_OBJECT_DIRECTORY"] = _objects;
    }
    internal void Verify()
    {
        _life.Check();
        foreach (var lease in _leases)
        { if (lease is PlanImportFilesystem.FileBinding file) file.Verify(); else ((PlanImportFilesystem.DirectoryBinding)lease).Verify(); }
        foreach (var carrier in _carriers) carrier.Verify(); _life.Check();
    }
    public void Dispose()
    {
        if (_life.HasPendingTasks || _reader?.HasUnsettledImportProcess == true) { Retained.TryAdd(this, 0); return; }
        foreach (var carrier in _carriers) carrier.Dispose(); foreach (var lease in _leases) lease.Dispose();
        // Private files are retained; no recursive deletion or closure claim.
    }
    private sealed class Carrier : IDisposable
    {
        private readonly string _path;
        private readonly string? _target;
        private readonly SafeFileHandle? _handle;
        private readonly FileStream? _windows;
        private readonly object? _windowsIdentity;
        private readonly bool _symbolicHead;
        internal Carrier(string path, byte[] bytes, string? symbolicHead)
        {
            _path = path;
            if (OperatingSystem.IsLinux())
            {
                var descriptor = Memfd("delivery-forge-plan-metadata", 3); // MFD_CLOEXEC | MFD_ALLOW_SEALING; child reads parent-owned address, not inherited FD.
                if (descriptor < 0) throw new PlanningException("Kernel-sealed import metadata unavailable: " + Marshal.GetLastPInvokeError());
                _handle = new SafeFileHandle((IntPtr)descriptor, true);
                try
                {
                    using (var output = new FileStream(new SafeFileHandle((IntPtr)descriptor, false), FileAccess.ReadWrite))
                    { output.Write(bytes); output.Flush(); }
                    if (Fcntl(descriptor, 1033, 15) != 0 || Fcntl(descriptor, 1034, 0) != 15) // F_ADD/GET_SEALS; SEAL/SHRINK/GROW/WRITE
                        throw new PlanningException("Original import metadata sealing failed.");
                    _symbolicHead = symbolicHead is not null;
                    _target = symbolicHead ?? "/proc/" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/fd/" + descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    File.CreateSymbolicLink(path, _target); Verify();
                }
                catch { _handle.Dispose(); throw; }
            }
            else if (OperatingSystem.IsWindows())
            {
                _windows = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                try { _windows.Write(bytes); _windows.Flush(true); _windowsIdentity = PlanImportFilesystem.CaptureFileIdentity(_windows.SafeFileHandle); Verify(); }
                catch { _windows.Dispose(); throw; }
            }
            else throw new PlanningException("Immutable effective Git metadata unavailable on this platform.");
        }
        internal void Verify()
        {
            if (_handle is not null)
            {
                var pinned = false;
                try
                {
                    _handle.DangerousAddRef(ref pinned); var descriptor = checked((int)_handle.DangerousGetHandle().ToInt64());
                    if (_handle.IsClosed || Fcntl(descriptor, 1034, 0) != 15 || new FileInfo(_path).LinkTarget != _target)
                        throw new PlanningException("Original sealed import metadata carrier changed.");
                    if (_symbolicHead && (!_target!.StartsWith("refs/", StringComparison.Ordinal) ||
                        !Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_path)!, _target)).StartsWith(Path.GetDirectoryName(_path)! + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                        throw new PlanningException("Original symbolic HEAD escaped its private reference namespace.");
                }
                finally { if (pinned) _handle.DangerousRelease(); }
            }
            else if (_windows is null || _windows.SafeFileHandle.IsClosed || _windowsIdentity is null) throw new PlanningException("Original immutable Windows metadata handle unavailable.");
            else PlanImportFilesystem.VerifyFileIdentity(_path, _windows.SafeFileHandle, _windowsIdentity);
        }
        public void Dispose() { _handle?.Dispose(); _windows?.Dispose(); }
        [DllImport("libc", EntryPoint = "memfd_create", SetLastError = true)] private static extern int Memfd([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int Fcntl(int descriptor, int command, int flags);
    }
}
