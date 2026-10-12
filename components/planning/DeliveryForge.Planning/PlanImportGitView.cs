using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
    private GitRepositoryContextReader? _reader;
    internal PlanImportGitMetadata Metadata { get; }
    internal PlanImportGitView(string root, string trustedGit, PlanImportLifetime life)
    {
        _life = life; life.Check(); Metadata = PlanImportGitMetadata.Read(root, life);
        _objects = Path.Combine(Metadata.Common, "objects");
        _directory = Path.Combine(Path.GetTempPath(), "delivery-forge-plan-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            _leases.Add(new PlanImportFilesystem.FileBinding(trustedGit));
            _leases.Add(new PlanImportFilesystem.DirectoryBinding(Metadata.Metadata));
            if (Metadata.Common != Metadata.Metadata) _leases.Add(new PlanImportFilesystem.DirectoryBinding(Metadata.Common));
            _leases.Add(new PlanImportFilesystem.DirectoryBinding(_objects));
            PlanImportFilesystem.Plain(_directory); life.Check();
            if (Path.Exists(_directory)) throw new PlanningException("Private import metadata collision.");
            Directory.CreateDirectory(_directory); Directory.CreateDirectory(Path.Combine(_directory, "refs")); Directory.CreateDirectory(Path.Combine(_directory, "objects"));
            _leases.Add(new PlanImportFilesystem.DirectoryBinding(_directory));
            foreach (var file in Metadata.Files)
            {
                life.Check(); var path = Path.Combine(_directory, file.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _carriers.Add(new Carrier(path, file.Value)); life.Check();
            }
            Verify();
        }
        catch { foreach (var carrier in _carriers) carrier.Dispose(); foreach (var lease in _leases) lease.Dispose(); throw; }
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
        internal Carrier(string path, byte[] bytes)
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
                    _target = "/proc/" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/fd/" + descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
