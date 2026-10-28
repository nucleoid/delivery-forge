using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
namespace DeliveryForge.Evidence;

// Internal kernel half of the creation-owned scope. Not constructible from public
// DTOs/PIDs. The systemd birth owner must first bind its actual process, unit,
// invocation and ControlGroup before calling Open. No namespace/config mutation.
[SupportedOSPlatform("linux")]
internal sealed class LinuxScopeKernel
{
    const int DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000;
    const long Cgroup2Magic = 0x63677270;
    // Ordinary scopes keep their original ceilings. Exact-image admission selects
    // the approved finite ceiling on this SAME counter; no prior debit is reset.
    const long WorkReadByteLimit = 14L * 1024 * 1024, TotalReadByteLimit = 16L * 1024 * 1024;
    const long ExactWorkReadByteLimit = 300031689L, ExactTotalReadByteLimit = 302128841L;
    const int WorkCallLimit = 8192, TotalCallLimit = 16384;
    readonly IOwnedDuplexBudget original; readonly object gate = new(); readonly List<Call> calls = []; readonly List<OwnedFd> descriptors = [];
    bool exactImageEntered; long exactWorkReadByteLimit, exactTotalReadByteLimit;
    OwnedFd? group, procDescriptors; Identity identity; string? originalPath; long readBytes; bool bound, stopEntered, closeEntered;
    internal sealed class Call { internal required string Kind; internal bool Entered, Returned; internal long Result, ByteLimit, BytesRead; internal int Error, NativeReads, NativeReadLimit; internal byte[]? OriginalBuffer; internal Exception? Primary, Post; }
    sealed class OwnedFd(int actual) { internal readonly int Actual = actual; internal bool CloseEntered, CloseInvoked, CloseReturned, Closed; internal Exception? Failure; }
    readonly record struct Identity(ulong Inode, uint Major, uint Minor, ulong Mount);
    internal IReadOnlyList<Call> ActualCalls { get { lock (gate) return calls.ToArray(); } }
    internal bool Unknown { get { lock (gate) return calls.Any(x => x.Entered && !x.Returned) || descriptors.Any(x => !x.Closed && (x.CloseEntered || (!ReferenceEquals(x, group) && !ReferenceEquals(x, procDescriptors)))); } }
    LinuxScopeKernel(IOwnedDuplexBudget original) { this.original = original; }
    void Check(bool cleanup) { if (cleanup) _ = original.CleanupRemaining; else original.CheckWork(); }
    long Native(string kind, Func<long> invoke, bool cleanup, byte[]? buffer = null)
    {
        Check(cleanup); Call row; lock (gate) { if (calls.Count >= (cleanup || !exactImageEntered ? TotalCallLimit : WorkCallLimit)) throw new IOException("Finite original Linux scope syscall ledger exhausted before invocation."); row = new() { Kind = kind, OriginalBuffer = buffer }; calls.Add(row); }
        row.Entered = true; long result = 0; Exception? primary = null, post = null;
        try { result = invoke(); row.Result = result; row.Error = result < 0 ? Marshal.GetLastPInvokeError() : 0; row.Returned = true; if (result < 0) throw new Win32Exception(row.Error, "Actual original scope syscall refused: " + kind); }
        catch (Exception error) { primary = error; row.Primary = error; }
        try { Check(cleanup); } catch (Exception error) { post = error; row.Post = error; }
        if (primary is not null && post is not null) throw new AggregateException("Original scope syscall and independent endpoint post.", primary, post); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw(); return result;
    }
    // One logical content-read frame, with actual return facts for every bounded
    // syscall. Managed/precheck refusal after a known return is not native UNKNOWN.
    long ExactImageRead(Call row, OwnedFd fd, byte[] buffer, int requested)
    {
        Check(false);
        if (requested <= 0 || requested > buffer.Length || row.NativeReads >= row.NativeReadLimit ||
            requested > row.ByteLimit - row.BytesRead || row.Entered && !row.Returned)
            throw new IOException("Finite original sealed-image read plan exhausted before invocation.");
        row.NativeReads++; row.Entered = true; row.Returned = false;
        long result = 0; Exception? primary = null, post = null;
        try
        {
            result = read(fd.Actual, buffer, (nuint)requested);
            row.Result = result; row.Error = result < 0 ? Marshal.GetLastPInvokeError() : 0; row.Returned = true;
            if (result < 0) throw new Win32Exception(row.Error, "Actual original sealed-image read refused.");
            if (result > requested) throw new IOException("Actual original sealed-image read exceeded its request.");
            row.BytesRead += result;
        }
        catch (Exception error) { primary = error; row.Primary ??= error; }
        try { Check(false); } catch (Exception error) { post = error; row.Post ??= error; }
        if (primary is not null && post is not null) throw new AggregateException("Original sealed-image read and independent endpoint post.", primary, post);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
        return result;
    }
    OwnedFd OpenFd(string path, int flags, bool cleanup, OwnedFd? parent = null)
    {
        OwnedFd? actual = null;
        // Save the actual returned descriptor BEFORE the independent endpoint post.
        Native("open:" + path, () => { var value = parent is null ? open(path, flags) : openat(parent.Actual, path, flags); if (value >= 0) { actual = new(value); lock (gate) descriptors.Add(actual); } return value; }, cleanup);
        return actual ?? throw new IOException("Original open returned no retained descriptor.");
    }
    void Close(OwnedFd actual, bool cleanup, bool continueUninvoked = false)
    {
        Check(cleanup); lock (gate) { if (actual.CloseEntered && (!continueUninvoked || actual.CloseInvoked)) throw new IOException("Original scope descriptor first-close already entered; no retry."); actual.CloseEntered = true; }
        try { Native("close-original-descriptor", () => { actual.CloseInvoked = true; var result = close(actual.Actual); actual.CloseReturned = true; actual.Closed = result == 0; return result; }, cleanup); } catch (Exception failure) { actual.Failure = failure; throw; }
    }
    // Same original private first-close sequence; no descriptor or scope adoption.
    Exception? CloseExactImage(OwnedFd fd, bool cleanupClose, Exception? post)
    {
        void CloseImage(bool cleanup, bool continueUninvoked = false)
        {
            // Notify the same actual enclosing owner before its cleanup
            // view: a successful subordinate disposal otherwise stays WORK.
            if (cleanup && original is IOwnedFailureCleanup failure) failure.BeginFailureCleanup();
            Close(fd, cleanup, continueUninvoked);
        }
        try { CloseImage(cleanupClose); }
        catch (Exception failure)
        {
            post = post is null ? failure : new AggregateException("Original image close admission and first disposition.", post, failure);
            // CloseEntered also records an attempt whose Native precheck or
            // row admission refused. Only an uninvoked syscall may continue
            // its SAME first close in cleanup; never retry a native effect.
            if (!cleanupClose && !fd.CloseInvoked)
            {
                try { CloseImage(true, continueUninvoked: true); }
                catch (Exception cleanup) { post = new AggregateException("Original WORK close admission and SAME cleanup first-close.", post, cleanup); }
            }
        }
        return post;
    }
    static void Relative(string path) { if (path.Length is 0 or > 4096 || path.Contains('\0') || path.Contains('\\') || path.Split('/').Any(x => x is "" or "." or ".." || x.Any(char.IsControl))) throw new IOException("Closed original cgroup path required."); }
    internal static LinuxScopeKernel Open(string actualControlGroup, string actualUnit, IOwnedDuplexBudget original)
    {
        original.CheckWork(); if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("The source-owned scope ABI profile is Linux x64 only.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(actualUnit, "^delivery-forge-[a-f0-9]{32}\\.scope$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw new IOException("Private fresh fixed unit syntax required.");
        if (!actualControlGroup.StartsWith('/') || actualControlGroup == "/" || !actualControlGroup.EndsWith('/' + actualUnit, StringComparison.Ordinal) || !actualUnit.StartsWith("delivery-forge-", StringComparison.Ordinal) || !actualUnit.EndsWith(".scope", StringComparison.Ordinal)) throw new IOException("Actual privately created scope path mismatch.");
        var relative = actualControlGroup[1..]; Relative(relative); var owner = new LinuxScopeKernel(original);
        try
        {
            var current = owner.OpenFd("/sys/fs/cgroup", DirectoryFlag | NoFollow | CloseOnExec, false);
            foreach (var component in relative.Split('/')) { original.CheckWork(); var next = owner.OpenFd(component, DirectoryFlag | NoFollow | CloseOnExec, false, current); owner.Close(current, false); current = next; }
            owner.group = current; owner.originalPath = "/sys/fs/cgroup" + actualControlGroup; owner.identity = owner.ObserveIdentity(false); owner.bound = true;
            owner.procDescriptors = owner.OpenFd("/proc/self/fd", DirectoryFlag | NoFollow | CloseOnExec, false);
            StatFs procFs = default; owner.Native("fstatfs-original-proc-fd-directory", () => fstatfs(owner.procDescriptors.Actual, out procFs), false); if (procFs.Type != 0x9fa0) throw new IOException("Actual original fd observation requires procfs.");
            if (owner.DescriptorPath(false) != owner.originalPath) throw new IOException("Original cgroup fd link differs before populated binding.");
            var events = owner.Read("cgroup.events", false, 1024); if (!ParsePopulated(events)) throw new IOException("Actual original process was not observed in a populated created scope.");
            return owner;
        }
        catch
        { // Retain partially entered descriptors; do not re-open/retry/adopt.
            lock (FailedCreations) FailedCreations.Add(owner); throw;
        }
    }
    static readonly List<LinuxScopeKernel> FailedCreations = [];
    Identity ObserveIdentity(bool cleanup)
    {
        if (group is null || group.CloseEntered) throw new IOException("Original cgroup descriptor unavailable or closing.");
        StatFs fs = default; Native("fstatfs-original-cgroup", () => fstatfs(group.Actual, out fs), cleanup); if (fs.Type != Cgroup2Magic) throw new IOException("Actual original filesystem is not cgroup-v2.");
        Statx observed = default; Native("statx-original-cgroup", () => statx(group.Actual, "", 0x1800, 0x17ff, out observed), cleanup);
        if ((observed.Mask & 0x1103) != 0x1103 || (observed.Mode & 0xf000) != 0x4000) throw new IOException("Actual original cgroup directory identity unavailable."); return new(observed.Inode, observed.Major, observed.Minor, observed.Mount);
    }
    void Same(bool cleanup) { if (!bound || ObserveIdentity(cleanup) != identity) throw new IOException("Original cgroup kernel identity differs; no path adoption."); }
    byte[] Read(string name, bool cleanup, int maximum)
    {
        Check(cleanup); if (name is not ("cgroup.events" or "memory.max" or "memory.swap.max" or "cpuset.cpus.effective" or "cpu.max" or "pids.max" or "pids.current" or "pids.events" or "memory.current" or "memory.events")) throw new IOException("Unlisted original cgroup observation.");
        if (readBytes > (exactImageEntered ? (cleanup ? exactTotalReadByteLimit : exactWorkReadByteLimit) : (cleanup ? TotalReadByteLimit : WorkReadByteLimit)) - (maximum + 1)) throw new IOException("Complete cgroup observation byte quota exceeded before allocation."); readBytes += maximum + 1; var data = new byte[maximum + 1]; var fd = OpenFd(name, NoFollow | CloseOnExec, cleanup, group); var count = 0; Exception? primary = null, post = null;
        try { while (count < data.Length) { Check(cleanup); var chunk = new byte[data.Length - count]; var n = (int)Native("read:" + name, () => read(fd.Actual, chunk, (nuint)chunk.Length), cleanup, chunk); if (n == 0) break; Buffer.BlockCopy(chunk, 0, data, count, n); count += n; } if (count > maximum) throw new IOException("Complete cgroup file exceeded its finite profile."); }
        catch (Exception error) { primary = error; }
        try { Close(fd, cleanup); Check(cleanup); } catch (Exception error) { post = error; }
        if (primary is not null && post is not null) throw new AggregateException("Actual original cgroup read/first-close.", primary, post); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw(); return data.AsSpan(0, count).ToArray();
    }
    // Diagnostic DATA from this already-owned leaf only. Each read reserves its
    // complete maximum+EOF on the SAME original counter; no ancestor/path adoption.
    internal IReadOnlyDictionary<string, byte[]> ReadLeafCounterBytes()
    {
        lock (gate)
        {
            Check(false); Same(false);
            var raw = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var name in OwnedDuplexProcess.LeafCounterNames) raw.Add(name, Read(name, false, name.EndsWith(".events", StringComparison.Ordinal) ? 1024 : 256));
            Same(false); Check(false); return raw;
        }
    }
    // Invoked only by the private owner for its already-bound creator, while the
    // verified gate is waiting for RELEASE. This method cannot bind/adopt a PID.
    internal void RequireUnprivilegedCreator(int actualPid)
    {
        lock (gate)
        {
            Check(false); Same(false);
            if (!exactImageEntered || actualPid <= 0) throw new IOException("Actual exact-image creator required before privilege observation.");
            const int maximum = 8192, maximumReads = 32;
            if (readBytes > exactWorkReadByteLimit - (maximum + 1)) throw new IOException("Original privilege-status byte credit exhausted before open.");
            readBytes += maximum + 1; var bytes = new byte[maximum + 1]; var used = 0; var eof = false;
            var fd = OpenFd("/proc/" + actualPid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/status", NoFollow | CloseOnExec, false);
            Exception? primary = null, post = null;
            try
            {
                StatFs fs = default; Native("fstatfs-original-creator-status", () => fstatfs(fd.Actual, out fs), false);
                if (fs.Type != 0x9fa0) throw new IOException("Actual creator status requires procfs.");
                for (var attempt = 0; attempt < maximumReads && used < bytes.Length; attempt++)
                {
                    var chunk = new byte[bytes.Length - used];
                    var n = checked((int)Native("read-original-creator-status", () => read(fd.Actual, chunk, (nuint)chunk.Length), false, chunk));
                    if (n == 0) { eof = true; break; }
                    if (n > chunk.Length) throw new IOException("Actual creator status exceeded requested extent.");
                    Buffer.BlockCopy(chunk, 0, bytes, used, n); used += n;
                }
                if (!eof || used > maximum) throw new IOException("Complete creator status exceeded finite read plan.");
            }
            catch (Exception failure) { primary = failure; }
            post = CloseExactImage(fd, primary is not null, post);
            if (primary is not null && post is not null) throw new AggregateException("Original creator status and first-close.", primary, post);
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
            var required = new HashSet<string>(StringComparer.Ordinal) { "Pid", "Tgid", "Uid", "Gid", "NoNewPrivs", "CapInh", "CapPrm", "CapEff", "CapAmb", "Groups" };
            var found = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var line in Utf8.GetString(bytes, 0, used).Split('\n'))
            {
                var colon = line.IndexOf(':'); if (colon < 0) continue; var key = line[..colon];
                if (!required.Contains(key)) continue;
                if (!found.TryAdd(key, line[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))) throw new IOException("Duplicate creator privilege field.");
            }
            bool One(string key, string expected) => found.TryGetValue(key, out var values) && values.Length == 1 && values[0] == expected;
            bool Identity(string key) => found.TryGetValue(key, out var values) && values.Length == 4 && values.All(value => value == "1000");
            if (found.Count != required.Count || !One("Pid", actualPid.ToString(System.Globalization.CultureInfo.InvariantCulture)) || !One("Tgid", actualPid.ToString(System.Globalization.CultureInfo.InvariantCulture)) ||
                !One("NoNewPrivs", "1") || !One("Groups", "1000") || !Identity("Uid") || !Identity("Gid") ||
                new[] { "CapInh", "CapPrm", "CapEff", "CapAmb" }.Any(key => !One(key, "0000000000000000")))
                throw new IOException("Actual creator must have NNP1, UID/GID1000 and zero inherited/permitted/effective/ambient capabilities before release.");
            Same(false); Check(false);
        }
    }
    // Selected conformance ancestor is observed only, never signalled/adopted.
    // The actual original cgroup path must be below this root-controlled boundary.
    internal void RequireConformanceAncestor(string expectedCpus)
    {
        lock (gate)
        {
            Check(false); Same(false);
            const string path = "/sys/fs/cgroup/user.slice/user-1000.slice";
            if (originalPath is null || !originalPath.StartsWith(path + "/", StringComparison.Ordinal)) throw new IOException("Original scope is outside selected forge ancestor.");
            var held = new List<OwnedFd>(); Exception? primary = null, post = null;
            void RootControlled(OwnedFd fd, bool directory)
            {
                StatFs fs = default; Native("fstatfs-selected-ancestor", () => fstatfs(fd.Actual, out fs), false);
                Statx row = default; Native("statx-selected-ancestor", () => statx(fd.Actual, "", 0x1800, 0x17ff, out row), false);
                if (fs.Type != Cgroup2Magic || (row.Mask & 0x111b) != 0x111b || row.Uid != 0 || row.Gid != 0 || (row.Mode & 0x12) != 0 || (row.Mode & 0xf000) != (directory ? 0x4000 : 0x8000)) throw new IOException("Actual selected ancestor/control is not root-controlled cgroup2.");
            }
            try
            {
                var ancestor = OpenFd("/sys/fs/cgroup", DirectoryFlag | NoFollow | CloseOnExec, false); held.Add(ancestor); RootControlled(ancestor, true);
                foreach (var name in new[] { "user.slice", "user-1000.slice" })
                { var next = OpenFd(name, DirectoryFlag | NoFollow | CloseOnExec, false, ancestor); held.Add(next); RootControlled(next, true); ancestor = next; }
                Statx identity = default; Native("statx-held-conformance-ancestor", () => statx(ancestor.Actual, "", 0x1800, 0x17ff, out identity), false);
                string Text(string name)
                {
                    const int maximum = 256; var limit = exactImageEntered ? exactWorkReadByteLimit : WorkReadByteLimit;
                    if (readBytes > limit - (maximum + 1)) throw new IOException("Original ancestor read credit exhausted.");
                    readBytes += maximum + 1; var bytes = new byte[maximum + 1]; var used = 0; var eof = false;
                    var fd = OpenFd(name, NoFollow | CloseOnExec, false, ancestor); Exception? failure = null, closeFailure = null;
                    try
                    {
                        RootControlled(fd, false);
                        for (var attempt = 0; attempt < 4 && used < bytes.Length; attempt++)
                        {
                            var chunk = new byte[bytes.Length - used]; var n = checked((int)Native("read-selected-ancestor:" + name, () => read(fd.Actual, chunk, (nuint)chunk.Length), false, chunk));
                            if (n == 0) { eof = true; break; }
                            if (n > chunk.Length) throw new IOException("Actual ancestor read exceeded request.");
                            Buffer.BlockCopy(chunk, 0, bytes, used, n); used += n;
                        }
                        if (!eof || used > maximum) throw new IOException("Complete selected ancestor metadata exceeded finite plan.");
                    }
                    catch (Exception error) { failure = error; }
                    closeFailure = CloseExactImage(fd, failure is not null, closeFailure);
                    if (failure is not null && closeFailure is not null) throw new AggregateException(failure, closeFailure);
                    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    if (closeFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeFailure).Throw();
                    return Utf8.GetString(bytes, 0, used).TrimEnd('\n');
                }
                if (Text("memory.max") != "4294967296" || Text("memory.swap.max") != "0" || Text("cpuset.cpus.effective") != expectedCpus || Text("pids.max") != "128") throw new IOException("Selected ancestor resource limits differ.");
                var cpu = Text("cpu.max").Split(' ');
                if (cpu.Length != 2 || !ulong.TryParse(cpu[0], out var quota) || !ulong.TryParse(cpu[1], out var period) || period == 0 || quota / period != 2 || quota % period != 0) throw new IOException("Selected ancestor CPU quota differs.");
                if (!uint.TryParse(Text("pids.current"), out var current) || current is < 1 or > 128) throw new IOException("Actual ancestor task population exceeds profile.");
                Statx after = default; Native("statx-conformance-ancestor-post", () => statx(ancestor.Actual, "", 0x1800, 0x17ff, out after), false);
                if (after.Inode != identity.Inode || after.Mount != identity.Mount || after.Major != identity.Major || after.Minor != identity.Minor) throw new IOException("Original conformance ancestor identity changed.");
                RootControlled(ancestor, true); Same(false); Check(false);
            }
            catch (Exception error) { primary = error; }
            foreach (var fd in held.AsEnumerable().Reverse()) post = CloseExactImage(fd, primary is not null || post is not null, post);
            if (primary is not null && post is not null) throw new AggregateException("Selected ancestor observation and first-close.", primary, post);
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
        }
    }
    static readonly UTF8Encoding Utf8 = new(false, true);
    internal static bool ParsePopulated(byte[] complete)
    {
        var text = Utf8.GetString(complete); var seen = new HashSet<string>(StringComparer.Ordinal); bool? populated = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { var row = line.Split(' '); if (row.Length != 2 || row[0] is not ("populated" or "frozen") || row[1] is not ("0" or "1") || !seen.Add(row[0])) throw new IOException("Malformed complete cgroup.events observation."); if (row[0] == "populated") populated = row[1] == "1"; }
        return populated ?? throw new IOException("Actual populated value missing.");
    }
    string DescriptorPath(bool cleanup)
    {
        Check(cleanup); if (group is null || group.CloseEntered || procDescriptors is null || procDescriptors.CloseEntered) throw new IOException("Actual retained cgroup/proc descriptors required.");
        var bytes = new byte[8193]; var count = (int)Native("readlinkat-original-cgroup-descriptor", () => readlinkat(procDescriptors.Actual, group.Actual.ToString(System.Globalization.CultureInfo.InvariantCulture), bytes, (nuint)bytes.Length), cleanup, bytes);
        if (count <= 0 || count > 8192) throw new IOException("Complete original descriptor link exceeded finite profile."); var text = Utf8.GetString(bytes, 0, count); Check(cleanup); return text;
    }
    bool DeletedOriginal(bool cleanup)
    {
        Same(cleanup); var link = DescriptorPath(cleanup); Same(cleanup);
        if (link == originalPath) return false;
        // This is the actual retained fd's procfs link, never a caller path or
        // ENOENT/adopted replacement. Names cannot themselves end in this suffix:
        // the privately minted unit is strictly delivery-forge-<hex>.scope.
        if (link == originalPath + " (deleted)") return true;
        throw new IOException("Original cgroup descriptor was renamed or substituted; retain UNKNOWN.");
    }
    internal bool Empty(bool cleanup)
    {
        lock (gate)
        {
            if (DeletedOriginal(cleanup)) return true;
            var raw = Read("cgroup.events", cleanup, 1024); var populated = ParsePopulated(raw); Same(cleanup); return !populated;
        }
        // A missing NAME, manager inactive state or root EOF never proves zero.
        // Exact retained-kernel-fd unlink is a distinct supported-kernel proof:
        // cgroup_destroy_locked rejects populated/live children before disabling
        // migration and removing kernfs (Linuxv6.8 cgroup.c5536..5568).
    }
    // Called only after the private original creator/generation/group binding.
    // Following THIS retained process's numbered proc descriptor is deliberate;
    // no caller-supplied PID/path or resource name can issue ownership here.
    internal void RequireExactImage(int actualPid, int actualFd, LinuxScopeImage selected, ulong inode, uint major, uint minor)
    {
        lock (gate)
        {
            Check(false); Same(false); if (exactImageEntered || actualPid <= 0 || actualFd <= 2 || selected.Bytes < 64 || selected.Bytes > OwnedDuplexProcess.MaximumExactLinuxImageBytes) throw new IOException("One finite original exact-image descriptor observation required.");
            // Admit complete image plus EOF before descriptor open. The SAME
            // counter keeps its prior metadata debits; a small image cannot
            // inherit the maximum image's unused extent as later read credit.
            var byteLimit = selected.Bytes + 1;
            if (byteLimit > ExactWorkReadByteLimit - readBytes) throw new IOException("Complete sealed-image observation exceeds remaining original kernel byte quota before open.");
            exactWorkReadByteLimit = Math.Min(ExactWorkReadByteLimit, checked(readBytes + byteLimit + 3L * 1024 * 1024));
            exactTotalReadByteLimit = Math.Min(ExactTotalReadByteLimit, checked(exactWorkReadByteLimit + 2L * 1024 * 1024));
            readBytes += byteLimit; exactImageEntered = true; Check(false);
            var fd = OpenFd("/proc/" + actualPid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/fd/" + actualFd.ToString(System.Globalization.CultureInfo.InvariantCulture), CloseOnExec, false);
            Exception? primary = null, post = null;
            try
            {
                const int sealsRequired = 0x2f;
                void Identity()
                {
                    Statx actual = default; Native("statx-original-sealed-native-image", () => statx(fd.Actual, "", 0x1000, 0x7ff, out actual), false);
                    if ((actual.Mask & 0x303) != 0x303 || (actual.Mode & 0xf000) != 0x8000 || (actual.Mode & 0x1ff) != 0x140 || actual.Size != (ulong)selected.Bytes || actual.Inode != inode || actual.Major != major || actual.Minor != minor) throw new IOException("Actual sealed image identity/extent/mode differs.");
                    var seals = Native("fcntl-original-image-seals", () => fcntl(fd.Actual, 1034, 0), false);
                    if ((seals & sealsRequired) != sealsRequired) throw new IOException("Complete actual immutable content/extent/exec/seal-set required.");
                }
                Identity(); using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                Check(false); if (calls.Count >= WorkCallLimit) throw new IOException("Finite original Linux scope syscall ledger exhausted before invocation.");
                var buffer = new byte[65536]; long consumed = 0;
                var imageReads = new Call
                {
                    Kind = "bounded-read-original-sealed-native-image",
                    OriginalBuffer = buffer,
                    ByteLimit = byteLimit,
                    NativeReadLimit = checked((int)((selected.Bytes + 65535) / 65536 + 1))
                };
                calls.Add(imageReads);
                while (consumed < selected.Bytes)
                {
                    var requested = (int)Math.Min(buffer.Length, selected.Bytes - consumed);
                    var n = ExactImageRead(imageReads, fd, buffer, requested);
                    if (n <= 0 || n > requested) throw new IOException("Actual complete sealed image ended early.");
                    hash.AppendData(buffer, 0, (int)n); consumed += n; Check(false);
                }
                if (ExactImageRead(imageReads, fd, buffer, 1) != 0 || Convert.ToHexStringLower(hash.GetHashAndReset()) != selected.Sha256) throw new IOException("Actual complete sealed image hash/EOF differs.");
                Identity(); Same(false); Check(false);
            }
            catch (Exception failure) { primary = failure; }
            var cleanupClose = primary is not null;
            if (!cleanupClose)
            {
                try { Check(false); }
                catch (Exception admission) { post = admission; cleanupClose = true; }
            }
            post = CloseExactImage(fd, cleanupClose, post);
            if (primary is not null && post is not null) throw new AggregateException("Actual exact image and original first-close.", primary, post);
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
        }
    }
    internal void RequireLimits(string expectedCpus, bool cleanup, bool restrictedChild = false) { lock (gate) RequireLimitsCore(expectedCpus, cleanup, restrictedChild); }
    void RequireLimitsCore(string expectedCpus, bool cleanup, bool restrictedChild)
    {
        Same(cleanup);
        string Text(string name) => Utf8.GetString(Read(name, cleanup, 256)).TrimEnd('\n');
        if (Text("memory.max") != (restrictedChild ? "2147483648" : "4294967296") || Text("memory.swap.max") != "0" || Text("cpuset.cpus.effective") != expectedCpus || Text("pids.max") != (restrictedChild ? "48" : "128")) throw new IOException("Actual scope limits differ from selected closed ordinary or stricter child profile.");
        var cpu = Text("cpu.max").Split(' '); if (cpu.Length != 2 || !ulong.TryParse(cpu[0], out var quota) || !ulong.TryParse(cpu[1], out var period) || period == 0 || quota / period > 2 || quota % period != 0 || quota / period != 2) throw new IOException("Actual cgroup CPU quota is not exactly two CPUs."); Same(cleanup);
    }
    internal void Kill(bool cleanup) { lock (gate) KillCore(cleanup); }
    void KillCore(bool cleanup)
    {
        Same(cleanup); lock (gate) { if (stopEntered) throw new IOException("Actual original cgroup kill already entered; no retry."); stopEntered = true; }
        var fd = OpenFd("cgroup.kill", 1 | NoFollow | CloseOnExec, cleanup, group); var data = new byte[] { (byte)'1' }; Exception? primary = null, post = null;
        try { if (Native("write-original-cgroup.kill", () => write(fd.Actual, data, 1), cleanup, data) != 1) throw new IOException("Original cgroup kill did not accept exactly one byte."); } catch (Exception failure) { primary = failure; }
        try { Close(fd, cleanup); Same(cleanup); } catch (Exception failure) { post = failure; }
        if (primary is not null && post is not null) throw new AggregateException("Actual scope kill/independent post.", primary, post); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
    }
    internal void CloseEmpty(bool cleanup) { lock (gate) CloseEmptyCore(cleanup); }
    internal bool KnownDisposed { get { lock (gate) return group is { Closed: true } && procDescriptors is { Closed: true } && descriptors.All(x => x.Closed) && calls.All(x => !x.Entered || x.Returned); } }
    void CloseEmptyCore(bool cleanup)
    {
        if (!Empty(cleanup) || Unknown) throw new IOException("Actual original cgroup is not known empty/settled; retain."); if (closeEntered) throw new IOException("Scope first disposition already entered."); closeEntered = true;
        Exception? primary = null, post = null; try { Close(group!, cleanup); } catch (Exception error) { primary = error; }
        // Close proc observer independently only after the actual dependent group
        // fd returned closed; a failed post does not erase that physical fact.
        if (group is { Closed: true } && procDescriptors is not null) { try { Close(procDescriptors, cleanup || primary is not null); } catch (Exception error) { post = error; } }
        if (primary is not null && post is not null) throw new AggregateException(primary, post); if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw(); if (post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(post).Throw();
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)] struct StatFs { [FieldOffset(0)] public long Type; }
    [StructLayout(LayoutKind.Explicit, Size = 256)] struct Statx { [FieldOffset(0)] public uint Mask; [FieldOffset(20)] public uint Uid; [FieldOffset(24)] public uint Gid; [FieldOffset(28)] public ushort Mode; [FieldOffset(32)] public ulong Inode; [FieldOffset(40)] public ulong Size; [FieldOffset(136)] public uint Major; [FieldOffset(140)] public uint Minor; [FieldOffset(144)] public ulong Mount; }
    [DllImport("libc", SetLastError = true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] static extern int openat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] static extern int fcntl(int fd, int command, int value);
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, [Out] byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] static extern nint write(int fd, byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] static extern nint readlinkat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [Out] byte[] buffer, nuint maximum);
    [DllImport("libc", SetLastError = true)] static extern int fstatfs(int fd, out StatFs value);
    [DllImport("libc", SetLastError = true)] static extern int statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx value);
}
