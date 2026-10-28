using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text;
namespace DeliveryForge.Evidence;

// Private creation extension shared by ordinary and duplex capture. It never
// creates a second Process, adopts a PID or signals via name/manager lookup.
[SupportedOSPlatform("linux")]
internal sealed class LinuxCommandScope
{
    readonly LinuxScopeImage? exactImage; bool exactImageObserved;
    readonly Process process; readonly IOwnedDuplexBudget original; readonly LinuxScopeSelection selected; readonly object gate = new();
    readonly string unit = "delivery-forge-" + Guid.NewGuid().ToString("N") + ".scope", ticket = Guid.NewGuid().ToString("N");
    readonly List<Operation> operations = []; readonly List<object> retained = []; readonly List<Exception> failures = [];
    LinuxSystemdLibrary? library; LinuxScopeBus? bus; LinuxScopeKernel? kernel; string? unitPath, controlGroup, cpus; byte[]? invocation; ulong procStart; long actualMask;
    DuplexNativeObservation? finalObservation;
    System.Threading.Thread? namespaceThread;
    bool productionGate; bool prepared, creationEntered, creationReturned, created, bound, referenceEntered, referenceReturned, releaseEntered, releaseReturned, stopEntered, stopFailureCleanup, dispositionEntered, disposed;
    internal sealed class Operation { internal required string Kind; internal bool Entered, Returned; internal Task? ActualTask; internal object? Resource; internal bool CloseEntered, Closed; internal Exception? Primary, Post, CloseFailure; internal bool Unknown => Entered && (!Returned || ActualTask is not null && !ActualTask.IsCompleted) || CloseEntered && !Closed; }
    static readonly List<LinuxCommandScope> Retained = [];
    internal LinuxCommandScope(Process actualProcess, IOwnedDuplexBudget actualOriginal, LinuxScopeSelection selection, LinuxScopeImage? exactNativeImage = null) { exactImage = exactNativeImage is null ? null : exactNativeImage with { }; if (selection is null || selection.GateBuildReceipt is null || selection.GateBuildReceipt.Path is null || !Path.IsPathFullyQualified(selection.GateBuildReceipt.Path) || selection.GateBuildReceipt.Bytes is <= 0 or > 65536 || selection.GateBuildReceipt.Sha256 is null || selection.GateBuildReceipt.Sha256.Length != 64 || selection.GateBuildReceipt.Sha256.Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)) || selection.BirthGateClosure is null || selection.BirthGateClosure.Count != 5 || selection.ManagerVersion is null || selection.ManagerVersion.Length > 256 || selection.KernelRelease is null || selection.KernelRelease.Length > 256) throw new IOException("Finite complete scope profile required before snapshot allocation."); process = actualProcess; original = actualOriginal; selected = selection with { BirthGateClosure = Array.AsReadOnly(selection.BirthGateClosure.ToArray()) }; LinuxExactImageBackingRules.Require(selected.ExactImageBacking, exactImage?.Path ?? "/selected-image"); if (exactImage is null && selected.ExactImageBacking != LinuxExactImageBacking.SealedMemfd) throw new IOException("Root-controlled backing requires an exact image selection."); retained.Add(actualProcess); retained.Add(actualOriginal); lock (Retained) Retained.Add(this); }
    void Check(bool cleanup) { if (cleanup) _ = original.CleanupRemaining; else original.CheckWork(); }
    Operation Enroll(string kind, bool cleanup) { Check(cleanup); if (operations.Count >= 4096) throw new IOException("Finite original scope owner ledger exhausted."); var row = new Operation { Kind = kind }; operations.Add(row); return row; }
    T Observe<T>(string kind, Func<T> action, bool cleanup = false) { var row = Enroll(kind, cleanup); T value = default!; try { Check(cleanup); row.Entered = true; value = action(); row.Returned = true; } catch (Exception error) { row.Primary = error; failures.Add(error); } try { Check(cleanup); } catch (Exception error) { row.Post = error; failures.Add(error); } Throw(row.Primary, row.Post); return value; }
    static void Throw(Exception? primary, Exception? post) { if (primary is not null && post is not null) throw new AggregateException("Original Linux scope operation and independent post.", primary, post); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw(); }
    T Io<T>(string kind, Func<Task<T>> action) { var row = Enroll(kind, false); T result = default!; try { Check(false); row.Entered = true; var actual = action(); row.ActualTask = actual; row.Returned = true; if (actual is null) throw new IOException("Original birth operation returned no actual Task."); var left = original.WorkLimit - Stopwatch.GetElapsedTime(original.OriginalStarted); result = actual.WaitAsync(left, original.Token).GetAwaiter().GetResult(); } catch (Exception error) { row.Primary = error; failures.Add(error); } try { Check(false); } catch (Exception error) { row.Post = error; failures.Add(error); } Throw(row.Primary, row.Post); return result; }
    void Io(string kind, Func<Task> action) { var row = Enroll(kind, false); try { Check(false); row.Entered = true; var actual = action(); row.ActualTask = actual; row.Returned = true; if (actual is null) throw new IOException("Original birth operation returned no actual Task."); actual.WaitAsync(original.WorkLimit - Stopwatch.GetElapsedTime(original.OriginalStarted), original.Token).GetAwaiter().GetResult(); } catch (Exception error) { row.Primary = error; failures.Add(error); } try { Check(false); } catch (Exception error) { row.Post = error; failures.Add(error); } Throw(row.Primary, row.Post); }
    string ReadText(string path, int maximum, bool cleanup, int? ownedDescriptor = null)
    {
        var row = Enroll("actual-kernel-text:" + path, cleanup); FileStream? stream = null; byte[] data = new byte[maximum + 1]; retained.Add(data); string result = "";
        try
        {
            Check(cleanup); row.Entered = true;
            if (ownedDescriptor is int descriptor)
            {
                // Borrow only the already journal-owned descriptor. The stream
                // never closes it; its original open row owns the first close.
                var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle((nint)descriptor, ownsHandle: false);
                retained.Add(handle); row.Resource = handle; stream = new FileStream(handle, FileAccess.Read);
            }
            else stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            row.Resource = stream; row.Returned = true; Check(cleanup); var used = 0;
            while (used < data.Length) { var n = Observe("actual-kernel-read", () => stream.Read(data, used, data.Length - used), cleanup); if (n == 0) break; used += n; }
            if (used > maximum) throw new IOException("Complete original kernel text exceeded finite quota."); result = new UTF8Encoding(false, true).GetString(data, 0, used); Check(cleanup);
        }
        catch (Exception error) { row.Primary = error; failures.Add(error); }
        if (stream is not null) { try { Check(cleanup || row.Primary is not null); row.CloseEntered = true; stream.Dispose(); row.Closed = true; Check(cleanup || row.Primary is not null); } catch (Exception error) { row.CloseFailure = error; failures.Add(error); } }
        try { Check(cleanup || row.Primary is not null); } catch (Exception error) { row.Post = error; failures.Add(error); }
        Exception? disposition = row.CloseFailure; if (row.Post is not null) disposition = disposition is null ? row.Post : new AggregateException(disposition, row.Post); Throw(row.Primary, disposition); return result;
    }
    string RequireInitialCgroupNamespace()
    {
        // Linux6.8 source-profile observation, not a caller-issued capability.
        // Keep returned descriptors on the SAME original journal before any post.
        var opened = new List<(int Fd, Operation Row)>(5); Exception? primary = null; string membership = "";
        int Native(string kind, Func<int> invoke, bool cleanup = false)
        {
            var error = 0;
            var result = Observe(kind, () => { var value = invoke(); if (value < 0) error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError(); return value; }, cleanup);
            if (result < 0) throw new System.ComponentModel.Win32Exception(error, "Actual prebirth namespace observation refused: " + kind);
            return result;
        }
        int Open(int parent, string path, int flags)
        {
            var row = Enroll("actual-owner-namespace-open:" + path, false); int value = -1, error = 0; Exception? failure = null, post = null;
            try
            {
                Check(false); row.Entered = true; value = NamespaceOpenAt(parent, path, flags);
                if (value >= 0) { row.Resource = value; opened.Add((value, row)); } else error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                row.Returned = true; if (value < 0) throw new System.ComponentModel.Win32Exception(error, "Actual owner namespace open refused.");
            }
            catch (Exception caught) { failure = caught; row.Primary = caught; failures.Add(caught); }
            try { Check(false); } catch (Exception caught) { post = caught; row.Post = caught; failures.Add(caught); }
            Throw(failure, post); return value;
        }
        NamespaceStatx Identity(int fd, string path, int flags)
        {
            NamespaceStatx value = default; Native("actual-owner-namespace-statx", () => NamespaceStat(fd, path, flags, 0x17ff, out value));
            if ((value.Mask & 0x1103) != 0x1103) throw new IOException("Actual namespace metadata lacks type/mode/inode/mount identity.");
            return value;
        }
        static bool SameMount(NamespaceStatx a, NamespaceStatx b) => a.Major == b.Major && a.Minor == b.Minor && a.Mount == b.Mount;
        static bool SameLink(NamespaceStatx a, NamespaceStatx b) => SameMount(a, b) && a.Inode == b.Inode && a.Mode == b.Mode;
        try
        {
            const int Directory = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000;
            var proc = Open(-100, "/proc", Directory | NoFollow | CloseOnExec);
            NamespaceStatFs fs = default; Native("actual-owner-procfs", () => NamespaceFileSystem(proc, out fs));
            if (fs.Type != 0x9fa0) throw new IOException("Actual own namespace path requires procfs.");
            var root = Identity(proc, "", 0x1000);
            var selfBefore = Identity(proc, "thread-self", 0x100);
            if ((root.Mode & 0xf000) != 0x4000 || (selfBefore.Mode & 0xf000) != 0xa000 || !SameMount(root, selfBefore)) throw new IOException("Actual procfs thread-self magic-link provenance unavailable.");
            // Follow the verified calling-thread magic link, not the TGID leader.
            // The exact managed calling Thread is retained after successful
            // proof/disposition and rechecked immediately before creation.
            var self = Open(proc, "thread-self", Directory | CloseOnExec);
            var selfIdentity = Identity(self, "", 0x1000);
            var selfAfter = Identity(proc, "thread-self", 0x100);
            if ((selfIdentity.Mode & 0xf000) != 0x4000 || !SameMount(root, selfIdentity) || !SameLink(selfBefore, selfAfter)) throw new IOException("Actual own procfs task association changed or was redirected.");
            var directory = Open(self, "ns", Directory | NoFollow | CloseOnExec);
            var directoryIdentity = Identity(directory, "", 0x1000);
            if ((directoryIdentity.Mode & 0xf000) != 0x4000 || !SameMount(root, directoryIdentity)) throw new IOException("Actual own namespace directory was redirected.");
            var linkBefore = Identity(directory, "cgroup", 0x100);
            if ((linkBefore.Mode & 0xf000) != 0xa000 || !SameMount(root, linkBefore)) throw new IOException("Actual own cgroup namespace magic link was redirected.");
            var actual = Open(directory, "cgroup", CloseOnExec);
            Native("actual-owner-namespace-filesystem", () => NamespaceFileSystem(actual, out fs));
            if (fs.Type != 0x6e736673 || Native("actual-owner-namespace-type", () => NamespaceType(actual, 0xb703)) != 0x02000000) throw new IOException("Actual cgroup namespace object/type required.");
            var identity = Identity(actual, "", 0x1000);
            var linkAfter = Identity(directory, "cgroup", 0x100);
            if (!SameLink(linkBefore, linkAfter) || identity.Inode != 0xeffffffbUL) throw new IOException("Selected Linux6.8 policy requires the actual initial cgroup namespace before birth.");
            // Placement must come from this held verified procfs task, not a
            // later pathname lookup through a potentially different mount view.
            var member = Open(self, "cgroup", NoFollow | CloseOnExec);
            Native("actual-owner-membership-procfs", () => NamespaceFileSystem(member, out fs));
            var memberIdentity = Identity(member, "", 0x1000);
            if (fs.Type != 0x9fa0 || (memberIdentity.Mode & 0xf000) != 0x8000 || !SameMount(root, memberIdentity))
                throw new IOException("Actual owner membership must be a regular file on the verified procfs mount.");
            membership = ReadText("verified-owner-cgroup-membership", 8192, false, member);
        }
        catch (Exception caught) { primary = caught; failures.Add(caught); }
        // Every actual returned descriptor gets at most its first close. Mark
        // that ownership before admission so a refused close remains retained.
        for (var index = opened.Count - 1; index >= 0; index--)
        {
            var actual = opened[index]; actual.Row.CloseEntered = true;
            try
            {
                var cleanup = primary is not null;
                if (cleanup && original is IOwnedFailureCleanup failure) failure.BeginFailureCleanup();
                Native("actual-owner-namespace-first-close", () => { var result = NamespaceClose(actual.Fd); if (result == 0) actual.Row.Closed = true; return result; }, cleanup);
            }
            catch (Exception caught) { actual.Row.CloseFailure = caught; failures.Add(caught); primary = primary is null ? caught : new AggregateException("Actual owner namespace observation and independent first disposition.", primary, caught); }
        }
        Throw(primary, null);
        namespaceThread = System.Threading.Thread.CurrentThread;
        return membership;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 256)] struct NamespaceStatFs { [System.Runtime.InteropServices.FieldOffset(0)] public long Type; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 256)]
    struct NamespaceStatx
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public uint Mask;
        [System.Runtime.InteropServices.FieldOffset(28)] public ushort Mode;
        [System.Runtime.InteropServices.FieldOffset(32)] public ulong Inode;
        [System.Runtime.InteropServices.FieldOffset(136)] public uint Major;
        [System.Runtime.InteropServices.FieldOffset(140)] public uint Minor;
        [System.Runtime.InteropServices.FieldOffset(144)] public ulong Mount;
    }
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "openat", SetLastError = true)] static extern int NamespaceOpenAt(int fd, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close", SetLastError = true)] static extern int NamespaceClose(int fd);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)] static extern int NamespaceFileSystem(int fd, out NamespaceStatFs value);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "statx", SetLastError = true)] static extern int NamespaceStat(int fd, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out NamespaceStatx value);
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] static extern int NamespaceType(int fd, nuint request);

    void VerifyGateDirectory(string directory)
    {
        var expected = selected.BirthGateClosure.Select(x => Path.GetFullPath(x.Path)).ToHashSet(StringComparer.Ordinal); var found = new HashSet<string>(StringComparer.Ordinal);
        var row = Enroll("actual-gate-directory-inventory", false); IEnumerator<string>? iterator = null; Exception? primary = null, post = null;
        try
        {
            Check(false); row.Entered = true; iterator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator(); row.Resource = iterator; row.Returned = true; Check(false);
            while (Observe("actual-gate-inventory-move", iterator.MoveNext))
            {
                if (found.Count >= 5) throw new IOException("Actual gate directory has extra entries before inventory allocation.");
                var path = Observe("actual-gate-inventory-current", () => Path.GetFullPath(iterator.Current)); var attrs = Observe("actual-gate-inventory-attrs", () => File.GetAttributes(path));
                if ((attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || !expected.Contains(path) || !found.Add(path)) throw new IOException("Actual complete gate membership has foreign/duplicate/nonplain entries.");
            }
            if (!found.SetEquals(expected)) throw new IOException("Actual complete five-file gate membership differs."); Check(false);
        }
        catch (Exception error) { primary = error; failures.Add(error); }
        if (iterator is not null) { try { Check(primary is not null); row.CloseEntered = true; iterator.Dispose(); row.Closed = true; Check(primary is not null); } catch (Exception error) { post = error; row.CloseFailure = error; failures.Add(error); } }
        try { Check(primary is not null || post is not null); } catch (Exception error) { row.Post = error; failures.Add(error); post = post is null ? error : new AggregateException(post, error); }
        Throw(primary, post);
    }
    internal void Prepare(ProcessStartInfo originalInfo)
    {
        lock (gate)
        {
            Check(false); if (prepared || creationEntered) throw new IOException("Original Linux scope prepare already entered."); if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) throw new PlatformNotSupportedException("Supported scope ABI is Linuxx64.");
            if (!selected.ManagerVersion.StartsWith("255.", StringComparison.Ordinal) && selected.ManagerVersion != "255") throw new PlatformNotSupportedException("Only selected grounded systemdv255 source profile is admitted.");
            var release = ReadText("/proc/sys/kernel/osrelease", 256, false).TrimEnd('\n'); if (release != selected.KernelRelease || !release.StartsWith("6.8.", StringComparison.Ordinal)) throw new PlatformNotSupportedException("Exact independently selected grounded Linux6.8 kernel profile required.");
            foreach (var pair in new[] { ("DOTNET_PROCESSOR_COUNT", "2"), ("DOTNET_GCHeapHardLimit", "0x100000000"), ("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "1"), ("MSBUILDDISABLENODEREUSE", "1") })
                if (Environment.GetEnvironmentVariable(pair.Item1) != pair.Item2 || !originalInfo.Environment.TryGetValue(pair.Item1, out var actual) || actual != pair.Item2)
                    throw new IOException("Actual containing and inherited resource/server environment differs before Linux birth: " + pair.Item1);
            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (Environment.ProcessorCount != 2 || available <= 0 || available > 4294967296L)
                throw new IOException("Actual prebirth managed CPU/heap resource profile is not established.");
            if (selected.RestrictiveChildPolicy is not null)
            {
                // The selected ancestor must contain descendants, not their
                // owner. Observe only this caller before any library/bus/birth.
                if (selected.RestrictiveChildPolicy != "nnp-user1000-ancestor-v3") throw new IOException("Only explicit exact-image unprivileged child policy is supported.");
                LinuxScopeOwnerBoundary.RequireOutsideAncestorMembership(RequireInitialCgroupNamespace());
            }
            // The original caller already owns this process wrapper. It is not
            // an adopted child; observe its real inherited two-bit CPU mask.
            Process? caller = null; var borrowed = Enroll("actual-parent-affinity", false);
            try { borrowed.Entered = true; caller = Process.GetCurrentProcess(); borrowed.Resource = caller; borrowed.Returned = true; Check(false); actualMask = caller.ProcessorAffinity.ToInt64(); Check(false); if (BitOperations.PopCount(unchecked((ulong)actualMask)) != 2) throw new IOException("Actual inherited two-bit affinity required before creation."); } catch (Exception error) { borrowed.Primary = error; }
            if (caller is not null) { try { Check(borrowed.Primary is not null); borrowed.CloseEntered = true; caller.Dispose(); borrowed.Closed = true; Check(borrowed.Primary is not null); } catch (Exception error) { borrowed.CloseFailure = error; } }
            Throw(borrowed.Primary, borrowed.CloseFailure);
            var bits = Enumerable.Range(0, 64).Where(bit => (unchecked((ulong)actualMask) & (1UL << bit)) != 0).ToArray(); cpus = bits[1] == bits[0] + 1 ? bits[0].ToString(CultureInfo.InvariantCulture) + "-" + bits[1].ToString(CultureInfo.InvariantCulture) : string.Join(',', bits);
            productionGate = LinuxGateBuildBinding.Require(ReadText(selected.GateBuildReceipt.Path, 65536, false), selected, original);
            library = new LinuxSystemdLibrary(original, selected.SystemdLibrary); library.Load(); bus = LinuxScopeBus.Create(original, library); bus.Open(); if (bus.ManagerVersion() != selected.ManagerVersion) throw new IOException("Actual user-manager version differs from selected installed source profile.");
            library.VerifyImage(selected.SystemdRun); if (selected.BirthGateClosure.Count != 5 || selected.BirthGateClosure.Select(x => Path.GetFullPath(x.Path)).Distinct(StringComparer.Ordinal).Count() != 5 || !selected.BirthGateClosure.Contains(selected.BirthGate)) throw new IOException("Exact actual package-free five-file birth-gate build closure required.");
            var gateDirectory = Path.GetDirectoryName(Path.GetFullPath(selected.BirthGate.Path))!; VerifyGateDirectory(gateDirectory); foreach (var file in selected.BirthGateClosure) { if (Path.GetDirectoryName(Path.GetFullPath(file.Path)) != gateDirectory) throw new IOException("Actual gate build members must share one directory."); library.VerifyImage(file); }
            foreach (var key in new[] { "LD_PRELOAD", "LD_AUDIT", "LD_LIBRARY_PATH", "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE" }) if (originalInfo.Environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)) throw new PlatformNotSupportedException("Inherited execution hooks are outside the selected supported scope profile: " + key);
            if (selected.RestrictiveChildPolicy is not (null or "nnp-user1000-ancestor-v3")) throw new IOException("Only explicit exact-image unprivileged child policy is supported.");
            var target = originalInfo.FileName;
            LinuxExactImageBackingRules.Require(selected.ExactImageBacking, target);
            if (exactImage is null && selected.ExactImageBacking != LinuxExactImageBacking.SealedMemfd) throw new IOException("Root-controlled backing requires an exact image selection.");
            if (exactImage is not null && (exactImage.Path != target || !Path.IsPathFullyQualified(exactImage.Path) || exactImage.Bytes is < 64 or > 536870912 || exactImage.Sha256 is not { Length: 64 } || exactImage.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))) throw new IOException("Exact finite same-target native ELF selection required before birth.");
            var targetArgs = originalInfo.ArgumentList.ToArray(); if (!Path.IsPathFullyQualified(target) || targetArgs.Length >= 64 || targetArgs.Any(x => x.Length > 8192 || x.Contains('\0'))) throw new IOException("Exact finite target invocation required.");
            originalInfo.FileName = selected.SystemdRun.Path; originalInfo.ArgumentList.Clear(); foreach (var arg in new[] { "--user", "--scope", "--quiet", "--no-ask-password", "--expand-environment=no", "--unit=" + unit, (selected.RestrictiveChildPolicy is null || exactImage is null ? "--property=MemoryMax=4294967296" : "--property=MemoryMax=2147483648"), "--property=MemorySwapMax=0", "--property=CPUQuota=200%", "--property=AllowedCPUs=" + cpus, (selected.RestrictiveChildPolicy is null || exactImage is null ? "--property=TasksMax=128" : "--property=TasksMax=64"), "--property=Delegate=no", "--", selected.BirthGate.Path, "--original", original.OriginalStarted.ToString(CultureInfo.InvariantCulture), "--frequency", Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture), "--work-ticks", original.WorkLimit.Ticks.ToString(CultureInfo.InvariantCulture), "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--ticket", ticket }) originalInfo.ArgumentList.Add(arg); if (exactImage is not null) { originalInfo.ArgumentList.Add(LinuxExactImageBackingRules.GateArgument(selected.ExactImageBacking, selected.RestrictiveChildPolicy is not null)); originalInfo.ArgumentList.Add(exactImage.Bytes.ToString(CultureInfo.InvariantCulture)); originalInfo.ArgumentList.Add(exactImage.Sha256); }
            originalInfo.ArgumentList.Add("--"); originalInfo.ArgumentList.Add(target); foreach (var arg in targetArgs) originalInfo.ArgumentList.Add(arg);
            Check(false); prepared = true;
        }
    }
    internal void BeforeCreation() { lock (gate) { Check(false); if (!prepared || creationEntered) throw new IOException("One privately prepared original creation required."); if (selected.RestrictiveChildPolicy is not null) LinuxScopeOwnerBoundary.RequireCallingThread(namespaceThread); creationEntered = true; } }
    internal void CreationReturned(bool actualCreated) { lock (gate) { if (!creationEntered || creationReturned) throw new IOException("Actual original creation return cannot be synthesized/repeated."); created = actualCreated; creationReturned = true; Check(false); } }
    ulong ProcStart(bool cleanup) { var stat = ReadText("/proc/" + process.Id.ToString(CultureInfo.InvariantCulture) + "/stat", 8192, cleanup); var end = stat.LastIndexOf(')'); if (end < 0) throw new IOException("Complete original proc stat malformed."); var fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries); if (fields.Length < 20 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start == 0) throw new IOException("Actual original kernel process generation unavailable."); return start; }
    internal void BindAndRelease(Stream input, Stream output)
    {
        lock (gate)
        {
            Check(false); if (!creationReturned || !created || bound || releaseEntered) throw new IOException("Actual original creation/bind state differs.");
            if (Observe("actual-root-exit-before-association", () => process.HasExited)) throw new IOException("Original birth root exited before private scope association.");
            procStart = ProcStart(false); string? observedGroup = null;
            // Observe the actual creation-owned PID/generation joining our
            // privately minted scope BEFORE READY parsing. No unit-name/PID
            // adoption: both refer to the same retained Process.Start return.
            for (var attempt = 0; attempt < 128; attempt++)
            {
                Check(false); if (Observe("actual-birth-root-exit", () => process.HasExited)) throw new IOException("Original birth exited before group association.");
                var membership = ReadText("/proc/" + process.Id.ToString(CultureInfo.InvariantCulture) + "/cgroup", 8192, false);
                if (membership.StartsWith("0::/", StringComparison.Ordinal) && membership.EndsWith("/" + unit + "\n", StringComparison.Ordinal) && membership.Count(c => c == '\n') == 1) { observedGroup = membership[3..^1]; break; }
                Io("actual-private-birth-association-wait", () => Task.Delay(100, original.Token));
            }
            if (observedGroup is null) throw new IOException("Actual original private group association did not return within finite attempts.");
            unitPath = bus!.GetUnit(unit); invocation = bus.InvocationId(unitPath, false);
            // systemd255 exposes ControlGroup on Scope; InvocationID stays on Unit.
            // https://www.freedesktop.org/software/systemd/man/255/org.freedesktop.systemd1.html#Scope%20Unit%20Objects
            controlGroup = bus.StringProperty(unitPath, "org.freedesktop.systemd1.Scope", "ControlGroup", false);
            if (controlGroup != observedGroup || ProcStart(false) != procStart) throw new IOException("Actual private group/generation does not join the original creator.");
            referenceEntered = true; bus.RefUnit(unit); referenceReturned = true; kernel = LinuxScopeKernel.Open(controlGroup, unit, original);
            // The returned kernel owner controls this exact group even when a
            // subsequent policy/READY check refuses. Release remains separate.
            bound = true;
            kernel.RequireLimits(cpus!, false, selected.RestrictiveChildPolicy is not null && exactImage is not null); if (selected.RestrictiveChildPolicy is not null) kernel.RequireConformanceAncestor(cpus!);
            if (!invocation.AsSpan().SequenceEqual(bus.InvocationId(unitPath, false)) || ProcStart(false) != procStart || Observe("actual-root-exit-at-binding", () => process.HasExited) || Observe("actual-original-root-affinity", () => process.ProcessorAffinity.ToInt64()) != actualMask) throw new IOException("Actual private unit/root generation/affinity changed.");
            var bytes = new List<byte>(); var one = new byte[1]; while (true) { if (bytes.Count >= 512) throw new IOException("Complete private birth READY exceeded fixed width."); var n = Io("actual-birth-ready-read", () => output.ReadAsync(one, 0, 1, original.Token)); if (n == 0) throw new IOException("Actual birth ended before complete READY."); if (one[0] == 10) break; bytes.Add(one[0]); }
            var ready = Encoding.ASCII.GetString(bytes.ToArray()).Split('|'); if (ready.Length != (exactImage is null ? 5 : 12) || ready[0] != "DF_SCOPE_READY" || ready[1] != process.Id.ToString(CultureInfo.InvariantCulture) || ready[2] != original.OriginalStarted.ToString(CultureInfo.InvariantCulture) || ready[3] != ticket || ready[4] != Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)) throw new IOException("Actual private birth identity/clock differs.");
            // The birth barrier keeps this actual kernel process on the gate
            // image. Hash its opened /proc executable before any release, joined
            // again to the same retained generation. This is not pathname-only
            // acceptance of a replaced deployment entry.
            library!.VerifyImage(selected.BirthGate with { Path = "/proc/" + process.Id.ToString(CultureInfo.InvariantCulture) + "/exe" });
            if (ProcStart(false) != procStart) throw new IOException("Actual gate executable generation changed during image observation.");
            if (exactImage is not null)
            {
                if (ready[5] != LinuxExactImageBackingRules.ReadyMarker(selected.ExactImageBacking) || !int.TryParse(ready[6], NumberStyles.None, CultureInfo.InvariantCulture, out var fd) || fd <= 2 || ready[7] != exactImage.Bytes.ToString(CultureInfo.InvariantCulture) || ready[8] != exactImage.Sha256 || !ulong.TryParse(ready[9], NumberStyles.None, CultureInfo.InvariantCulture, out var inode) || inode == 0 || !uint.TryParse(ready[10], NumberStyles.None, CultureInfo.InvariantCulture, out var major) || !uint.TryParse(ready[11], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) throw new IOException("Actual exact-image READY descriptor identity differs.");
                if (ProcStart(false) != procStart) throw new IOException("Original generation changed before selected image observation.");
                kernel.RequireExactImage(process.Id, fd, exactImage, inode, major, minor, selected.ExactImageBacking);
                if (selected.RestrictiveChildPolicy is not null || selected.ExactImageBacking == LinuxExactImageBacking.RootControlledDisk) kernel.RequireUnprivilegedCreator(process.Id);
                if (ProcStart(false) != procStart || !invocation.AsSpan().SequenceEqual(bus.InvocationId(unitPath, false))) throw new IOException("Original generation/invocation changed after selected image observation.");
                exactImageObserved = true;
            }
            // A genuine negative fixture may exercise the SAME kernel observations,
            // but must NEVER earn release even if a kernel check regresses.
            if (!productionGate) throw new IOException("Negative gate-build profile may never release a target.");
            var release = Encoding.ASCII.GetBytes("DF_SCOPE_RELEASE|" + ticket + "|" + original.OriginalStarted.ToString(CultureInfo.InvariantCulture) + "\n"); retained.Add(release); releaseEntered = true; Io("actual-private-release-write", () => input.WriteAsync(release, 0, release.Length, original.Token)); Io("actual-private-release-flush", () => input.FlushAsync(original.Token)); releaseReturned = true; Check(false);
        }
    }
    internal bool HasCreationOwnedExactImage { get { lock (gate) return exactImage is not null && exactImageObserved && bound && releaseReturned; } }
    internal LinuxExactImageBacking? CreationOwnedExactImageBacking { get { lock (gate) return HasCreationOwnedExactImage ? selected.ExactImageBacking : null; } }
    internal bool HasCreationOwnedScope { get { lock (gate) return bound && releaseReturned; } }
    internal IReadOnlyDictionary<string, ulong?> CaptureLeafCounters()
    {
        lock (gate)
        {
            Check(false);
            if (!bound || !releaseReturned || dispositionEntered || disposed || stopEntered || stopFailureCleanup || kernel is null) throw new IOException("Live bound original scope required for leaf diagnostic.");
            var raw = Observe("actual-original-leaf-counters", kernel.ReadLeafCounterBytes);
            // Native reads and their identity/close checks have returned. A pure
            // parse refusal is diagnostic failure, not an unreturned operation.
            return OwnedDuplexProcess.ParseLeafCounters(raw);
        }
    }
    internal DuplexNativeObservation ObserveNative(bool cleanup = false)
    {
        lock (gate)
        {
            if (disposed) return finalObservation ?? new(false, false, uint.MaxValue, null, "Final original scope proof unavailable.");
            if (!creationEntered || creationReturned && !created) return new(true, true, 0, null, "Actual creator did not create a root; no scope issued."); if (!creationReturned) return new(false, false, uint.MaxValue, null, "Original Process.Start return unknown.");
            cleanup |= stopFailureCleanup; stopFailureCleanup |= cleanup;
            try { Check(cleanup); var exited = Observe("actual-original-root-exit", () => process.HasExited, cleanup); if (!bound || kernel is null) return new(false, exited, uint.MaxValue, exited ? process.ExitCode : null, "Original pre-target scope binding/release incomplete."); var empty = kernel.Empty(cleanup); Check(cleanup); return new(true, exited, empty ? 0u : uint.MaxValue, exited ? process.ExitCode : null, "Actual original scope FD populated/deleted proof; nonempty is not an exact process count."); }
            catch (Exception error) { failures.Add(error); return new(false, false, uint.MaxValue, null, "Original scope observation failed: " + error.GetType().Name); }
        }
    }
    internal void Stop(bool cleanup) { lock (gate) { cleanup |= stopFailureCleanup; Check(cleanup); if (stopEntered) throw new IOException("Original scope stop already entered; no retry."); stopEntered = true; stopFailureCleanup |= cleanup; if (!creationEntered || creationReturned && !created) return; if (!creationReturned || kernel is null) throw new IOException("Original prebinding scope cannot be guessed or adopted for termination."); if (!kernel.Empty(cleanup)) kernel.Kill(cleanup); Check(cleanup); } }
    internal bool DisposeKnown(bool cleanup)
    {
        lock (gate)
        {
            cleanup |= stopFailureCleanup; Check(cleanup); if (dispositionEntered) throw new IOException("Original scope disposition already entered."); var observation = ObserveNative(cleanup); if (!observation.Known || !observation.RootExited || observation.ActiveProcesses != 0 || operations.Any(x => x.Unknown) || referenceEntered && !referenceReturned) return false; dispositionEntered = true;
            var failed = cleanup;
            try { kernel?.CloseEmpty(failed); } catch (Exception error) { failures.Add(error); failed = true; }
            if (kernel is not null && !kernel.KnownDisposed) return false;
            if (referenceReturned) { try { bus!.UnrefUnit(unit, failed); } catch (Exception error) { failures.Add(error); failed = true; } }
            try { bus?.CloseKnown(true, failed); } catch (Exception error) { failures.Add(error); failed = true; }
            if (bus is not null && !bus.KnownClosed) return false;
            try { library?.DisposeKnown(true, failed); } catch (Exception error) { failures.Add(error); failed = true; }
            if (library is not null && !library.KnownDisposed) return false;
            try { Check(failed); } catch (Exception error) { failures.Add(error); return false; }
            finalObservation = observation; disposed = true; lock (Retained) Retained.Remove(this); return true;
        }
    }
    internal bool KnownDisposed => disposed;
    internal IReadOnlyList<Exception> Failures => failures.AsReadOnly();
}

// Pure refusal predicates shared by the actual prebirth path and DATA controls.
// They issue no prepared scope, runtime authority or caller-supplied proof.
internal static class LinuxScopeOwnerBoundary
{
    internal static void RequireCallingThread(System.Threading.Thread? expected)
    {
        if (expected is null || !ReferenceEquals(expected, System.Threading.Thread.CurrentThread))
            throw new IOException("Selected-policy birth must remain on the actual namespace-proved calling thread.");
    }
    internal static void RequireOutsideAncestorMembership(string membership)
    {
        if (!membership.StartsWith("0::/", StringComparison.Ordinal) || !membership.EndsWith('\n') || membership.Count(c => c == '\n') != 1 || membership[..^1].Any(char.IsControl)) throw new IOException("Complete actual owner unified-cgroup membership required before selected-policy birth.");
        var ownerGroup = membership[3..^1];
        if (ownerGroup == "/" || ownerGroup[1..].Split('/').Any(component => component is "" or "." or "..")) throw new IOException("Canonical non-root actual owner unified-cgroup membership required; a namespace-relative root cannot establish outside-ancestor placement.");
        const string ancestor = "/user.slice/user-1000.slice";
        if (ownerGroup == ancestor || ownerGroup.StartsWith(ancestor + "/", StringComparison.Ordinal)) throw new IOException("Selected-policy owner must be outside the constrained user-1000.slice ancestor; an independently selected outside-owner launch is required.");
    }
}
