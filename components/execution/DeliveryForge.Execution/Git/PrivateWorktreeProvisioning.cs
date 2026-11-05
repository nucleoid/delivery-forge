using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Execution.Git;

public sealed partial class WorktreeManager
{
    ConcurrentDictionary<string, PrivateAdministrationProvisioning> privateAdministrations = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal sealed class PrivateAdministrationProvisioning
    {
        internal required WorktreeManager Manager;
        internal required string Worktree, CommonDirectory, PrivateDirectory, JournalPath, BaseAnchorRef, FrozenBaseCommit;
        internal string? RegisteredDirectory;
        internal (ulong Volume, ulong Low, ulong High) OriginalDirectoryIdentity;
        internal bool AnchorCreated, MoveEntered, MoveReturned, Complete;
    }

    // Explicit selection is permitted in real-Git controls on either OS. The
    // production caller selects only the confined Linux worker route.
    internal void SelectPrivateAdministrationForConfinedWorker()
    {
        if (_originalBudget is null || _originalExecutor is null || cleanupObservation)
            throw new WorktreeBoundaryException("Private administration requires the actual forward original.");
        _originalBudget.CheckWork();
        if (_effectAdmission is ICoordinatedSynchronousEffectAdmission parent) parent.CheckCurrent(_effectRunId!);
        else if (_effectAdmission is not null) throw new WorktreeBoundaryException("Private administration requires synchronous parent admission.");
        lock (actualOwnedGitInvocations)
        {
            if (actualOwnedGitInvocations.ProvisioningStarted || !ownedGit.IsEmpty || !privateAdministrations.IsEmpty)
                throw new WorktreeBoundaryException("Private administration must be selected before provisioning or writer birth.");
            actualOwnedGitInvocations.PrivateAdministrationSelected = true;
        }
    }

    async Task<PrivateAdministrationProvisioning> ReservePrivateAdministrationAsync(string parentCheckout,
        string destination, string baseCommit, string? common, CancellationToken token)
    {
        if (_originalBudget is null || _effectAdmission is not ICoordinatedSynchronousEffectAdmission parent ||
            _effectRunId is null || common is null || !LooseGitObjectInventory.IsOid(baseCommit))
            throw new WorktreeBoundaryException("Private administration requires one actual original parent and frozen base OID.");
        _originalBudget.CheckWork(); parent.CheckCurrent(_effectRunId);
        var root = Path.GetFullPath(destination); common = CanonicalPath(common);
        var directory = Path.GetDirectoryName(root) ?? throw new WorktreeBoundaryException("Private admin sibling parent is absent.");
        RejectSymlinkAncestors(directory);
        if (!Directory.Exists(directory)) throw new WorktreeBoundaryException("Private admin sibling parent must already exist.");
        var id = PrivateAdministrationId(_effectRunId, root);
        var privateDirectory = Path.Combine(directory, ".delivery-forge-private-admin-" + id);
        var state = new PrivateAdministrationProvisioning
        {
            Manager = this,
            Worktree = root,
            CommonDirectory = common,
            PrivateDirectory = privateDirectory,
            JournalPath = privateDirectory + ".provisioning.json",
            BaseAnchorRef = "refs/delivery-forge/private/" + id + "/base",
            FrozenBaseCommit = baseCommit
        };
        if (IsWithin(state.PrivateDirectory, root) || IsWithin(state.PrivateDirectory, common))
            throw new WorktreeBoundaryException("Private administration must be outside both worker and COMMON.");
        RequireAbsentPrivatePath(state.PrivateDirectory);
        RequireAbsentPrivatePath(state.JournalPath);
        if (!privateAdministrations.TryAdd(root, state))
            throw new WorktreeBoundaryException("Private administration provisioning cannot adopt or replay.");
        var actual = (await GitAsync(parentCheckout, token, "rev-parse", "--verify", baseCommit + "^{commit}").ConfigureAwait(false)).TrimEnd('\r', '\n');
        if (actual != baseCommit) throw new WorktreeBoundaryException("Private administration base is not the exact frozen commit.");
        // Exclusive ref creation precedes unregistering. This common reachable
        // root protects H0 even when its original branch/reflog/index disappear.
        await CreateFreshCommonRefAsync(common, state.BaseAnchorRef, baseCommit, token).ConfigureAwait(false);
        state.AnchorCreated = true;
        var anchored = (await GitAsync(parentCheckout, token, "rev-parse", "--verify", state.BaseAnchorRef + "^{commit}").ConfigureAwait(false)).TrimEnd('\r', '\n');
        _originalBudget.CheckWork(); parent.CheckCurrent(_effectRunId);
        if (anchored != baseCommit) throw new WorktreeBoundaryException("Fresh original base anchor readback differs.");
        return state;
    }

    static string PrivateAdministrationId(string runId, string worktree) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runId + "\0" + Path.GetFullPath(worktree))));

    async Task RequireFilesRefStorageAsync(string common, CancellationToken token)
    {
        var storage = await RunGitAsync(common, token, "config", "--local", "--get", "extensions.refStorage").ConfigureAwait(false);
        if (!(storage.ExitCode == 1 && storage.Stdout.Length == 0) &&
            !(storage.ExitCode == 0 && storage.Stdout.TrimEnd('\r', '\n') == "files"))
            throw new WorktreeBoundaryException("Fresh COMMON refs require the existing Git files storage profile.");
    }

    async Task RequireNoPrivateProvisioningAsync(string parentCheckout, string worktree, CancellationToken token)
    {
        const string refusal = "Unstarted cleanup refuses retained or durable private provisioning effects.";
        var root = Path.GetFullPath(worktree);
        if (privateAdministrations.ContainsKey(root)) throw new WorktreeBoundaryException(refusal);
        if (_originalBudget is null) return; // Existing unbound legacy utility route.
        if (_effectAdmission is not ICoordinatedSynchronousEffectAdmission parent || _effectRunId is null)
            throw new WorktreeBoundaryException("Unstarted private provisioning observation requires the actual parent/run.");
        void Check() { _originalBudget.CheckWork(); parent.CheckCurrent(_effectRunId); token.ThrowIfCancellationRequested(); }
        var id = PrivateAdministrationId(_effectRunId, root);
        var privateDirectory = Path.Combine(Path.GetDirectoryName(root)!, ".delivery-forge-private-admin-" + id);
        Check();
        var common = CanonicalPath((await GitAsync(parentCheckout, token, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false)).TrimEnd('\r', '\n'));
        Check(); RejectSymlinkAncestors(common);
        void CheckPaths()
        {
            Check();
            if (privateAdministrations.ContainsKey(root)) throw new WorktreeBoundaryException(refusal);
            foreach (var path in new[] { privateDirectory, privateDirectory + ".provisioning.json",
                Path.Combine(common, "refs", "delivery-forge", "private", id),
                Path.Combine(common, "refs", "delivery-forge", "private", id, "base"),
                Path.Combine(common, "refs", "delivery-forge", "private", id, "base.lock"),
                Path.Combine(common, "refs", "delivery-forge", "private", id, "candidate"),
                Path.Combine(common, "refs", "delivery-forge", "private", id, "candidate.lock") })
            {
                RejectSymlinkAncestors(path);
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                throw new WorktreeBoundaryException(refusal);
            }
        }
        CheckPaths();
        foreach (var leaf in new[] { "base", "candidate" })
        {
            var absent = await GitExitCodeAsync(common, token, "show-ref", "--verify", "--quiet", "refs/delivery-forge/private/" + id + "/" + leaf).ConfigureAwait(false);
            CheckPaths();
            if (absent != 1) throw new WorktreeBoundaryException(refusal);
        }
    }

    // Git's zero-old-OID update-ref can dereference a dangling symref; even
    // --no-deref is not a portable fresh-name test on the selected files backend.
    // Hold its standard physical lock while checking both loose and packed refs.
    internal async Task CreateFreshCommonRefAsync(string common, string refName, string oid, CancellationToken token)
    {
        const string prefix = "refs/delivery-forge/private/";
        if (_originalBudget is null || _originalExecutor is null || cleanupObservation ||
            _effectAdmission is not ICoordinatedSynchronousEffectAdmission parent || _effectRunId is null ||
            !LooseGitObjectInventory.IsOid(oid) || !refName.StartsWith(prefix, StringComparison.Ordinal))
            throw new WorktreeBoundaryException("Fresh COMMON ref requires the actual original parent and code-owned name/OID.");
        var suffix = refName[prefix.Length..];
        if (suffix.Length is not (69 or 74) || suffix[64] != '/' ||
            !suffix[..64].All(char.IsAsciiHexDigitLower) || suffix[65..] is not ("base" or "candidate"))
            throw new WorktreeBoundaryException("Only an exact code-owned base or candidate ref may be created.");
        common = CanonicalPath(common);
        void Check()
        {
            _originalBudget.CheckWork(); parent.CheckCurrent(_effectRunId); token.ThrowIfCancellationRequested();
            RejectSymlinkAncestors(common);
        }
        Check();
        await RequireFilesRefStorageAsync(common, token).ConfigureAwait(false);
        Check();
        var path = Path.Combine(common, refName.Replace('/', Path.DirectorySeparatorChar));
        var lockPath = path + ".lock";
        var bytes = Encoding.ASCII.GetBytes(oid + "\n");
        var metadata = new OwnedGitBinding(this, common, common, common, [], [], []);
        parent.EnterSynchronous(_effectRunId, () =>
        {
            Check(); RejectSymlinkAncestors(path); RejectSymlinkAncestors(lockPath);
            RequireAbsentPrivatePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Check(); RejectSymlinkAncestors(path); RejectSymlinkAncestors(lockPath);
            metadata.CreateFreshRefLock(lockPath, bytes);
            Check(); return true;
        });
        // The closed but retained .lock continues to exclude cooperating Git
        // writers. Errors preserve it; no failure path deletes or replays it.
        var absent = await GitExitCodeAsync(common, token, "show-ref", "--verify", "--quiet", refName).ConfigureAwait(false);
        Check();
        if (absent != 1) throw new WorktreeBoundaryException("Fresh COMMON ref is present, packed, or could not be proved absent.");
        parent.EnterSynchronous(_effectRunId, () =>
        {
            Check(); RejectSymlinkAncestors(path); RejectSymlinkAncestors(lockPath);
            RequireAbsentPrivatePath(path);
            if (!metadata.Read(lockPath).AsSpan().SequenceEqual(bytes))
                throw new WorktreeBoundaryException("Original fresh COMMON ref lock bytes changed.");
            Check(); File.Move(lockPath, path, overwrite: false); Check();
            return true;
        });
        var actual = (await GitAsync(common, token, "rev-parse", "--verify", refName).ConfigureAwait(false)).TrimEnd('\r', '\n');
        Check();
        if (actual != oid) throw new WorktreeBoundaryException("Fresh COMMON ref actual readback differs.");
    }

    static void RequireAbsentPrivatePath(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new WorktreeBoundaryException("Private administration path already exists or remains registered.");
    }

    void RelocatePrivateAdministration(PrivateAdministrationProvisioning state)
    {
        var parent = (ICoordinatedSynchronousEffectAdmission)_effectAdmission!;
        parent.EnterSynchronous(_effectRunId!, () =>
        {
            _originalBudget!.CheckWork(); parent.CheckCurrent(_effectRunId!);
            if (!ReferenceEquals(state.Manager, this) || !state.AnchorCreated || state.MoveEntered || state.Complete ||
                !privateAdministrations.TryGetValue(state.Worktree, out var retained) || !ReferenceEquals(retained, state))
                throw new WorktreeBoundaryException("Private administration relocation cannot adopt or replay.");
            var metadata = new OwnedGitBinding(this, state.Worktree, state.CommonDirectory, state.CommonDirectory, [], [], []);
            var marker = metadata.Read(Path.Combine(state.Worktree, ".git"));
            var text = OwnedGitBinding.Text(marker);
            if (!text.StartsWith("gitdir: ", StringComparison.Ordinal)) throw new WorktreeBoundaryException("Linked worker marker required.");
            var registered = CanonicalPath(Path.GetFullPath(text[8..], state.Worktree));
            if (!IsWithin(registered, Path.Combine(state.CommonDirectory, "worktrees")))
                throw new WorktreeBoundaryException("Only the newly registered original admin may be relocated.");
            RejectSymlinkAncestors(registered); RejectSymlinkAncestors(state.PrivateDirectory);
            var gitdir = metadata.Read(Path.Combine(registered, "gitdir"));
            var commondir = metadata.Read(Path.Combine(registered, "commondir"));
            if (!string.Equals(Path.GetFullPath(OwnedGitBinding.Text(gitdir), registered), Path.Combine(state.Worktree, ".git"), PathComparison()) ||
                !string.Equals(Path.GetFullPath(OwnedGitBinding.Text(gitdir), state.PrivateDirectory), Path.Combine(state.Worktree, ".git"), PathComparison()) ||
                !string.Equals(CanonicalPath(Path.GetFullPath(OwnedGitBinding.Text(commondir), registered)), state.CommonDirectory, PathComparison()) ||
                OwnedGitBinding.Text(metadata.Read(Path.Combine(registered, "HEAD"))) != state.FrozenBaseCommit)
                throw new WorktreeBoundaryException("Original registered admin/base/backpointer association differs.");
            RequireAbsentPrivatePath(state.PrivateDirectory);
            var before = PrivateObjectLinks.Identity(registered, directory: true);
            state.OriginalDirectoryIdentity = metadata.ReadPrivateDirectoryIdentity(registered);
            var siblingParent = Path.GetDirectoryName(state.PrivateDirectory)!;
            var target = PrivateObjectLinks.Identity(siblingParent, directory: true);
            if (OperatingSystem.IsLinux() ? before.Device != target.Device :
                !string.Equals(Path.GetPathRoot(registered), Path.GetPathRoot(state.PrivateDirectory), PathComparison()))
                throw new WorktreeBoundaryException("Private administration move requires the same filesystem.");
            state.RegisteredDirectory = registered;
            // Flushed DATA is diagnostic evidence, not crash durability or
            // ownership. Keep the association before changing routing files.
            var journal = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                Kind = "PrivateAdministrationProvisioning-DATA-v1",
                RunId = _effectRunId,
                state.Worktree,
                state.CommonDirectory,
                RegisteredDirectory = registered,
                state.PrivateDirectory,
                state.BaseAnchorRef,
                state.FrozenBaseCommit,
                OriginalCommonDirectoryBytesBase64 = Convert.ToBase64String(commondir),
                OriginalMarkerBytesBase64 = Convert.ToBase64String(marker),
                Authority = false
            });
            metadata.CreateFreshProvisioningData(state.JournalPath, journal);
            // Before freeing the registry slot, stop .git from resolving to it.
            // Failure before the move leaves the old admin retained and the
            // private target absent; no rollback/replay or foreign-slot adoption.
            metadata.RewriteProvisioningMetadata(Path.Combine(registered, "commondir"), commondir,
                Encoding.UTF8.GetBytes(state.CommonDirectory + "\n"));
            metadata.RewriteProvisioningMetadata(Path.Combine(state.Worktree, ".git"), marker,
                Encoding.UTF8.GetBytes("gitdir: " + state.PrivateDirectory + "\n"));
            _originalBudget.CheckWork(); parent.CheckCurrent(_effectRunId!);
            state.MoveEntered = true;
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    if (RenamePrivateAdministrationNoReplace(-100, registered, -100, state.PrivateDirectory, 1) != 0)
                        throw new WorktreeBoundaryException("Linux private admin no-replace rename failed; errno=" + Marshal.GetLastPInvokeError());
                }
                else Directory.Move(registered, state.PrivateDirectory);
                state.MoveReturned = true;
            }
            catch (Exception primary)
            {
                var failures = new List<Exception> { primary };
                try { if (_originalBudget is IOwnedBudgetUnknownRetention unknown) unknown.PreserveUnknown(); }
                catch (Exception failure) { failures.Add(failure); }
                try { parent.RetainUncertain(_effectRunId!, "Private admin relocation did not return a known move."); }
                catch (Exception failure) { failures.Add(failure); }
                if (failures.Count != 1) throw new AggregateException("Private admin move and uncertainty observations failed.", failures);
                throw;
            }
            _originalBudget.CheckWork(); parent.CheckCurrent(_effectRunId!);
            RequireForeignOrAbsentRegistration(state, metadata, cleanup: false);
            var after = PrivateObjectLinks.Identity(state.PrivateDirectory, directory: true);
            if (OperatingSystem.IsLinux() && (before.Device != after.Device || before.Inode != after.Inode))
                throw new WorktreeBoundaryException("Private admin identity changed across its no-copy move.");
            if (metadata.ReadPrivateDirectoryIdentity(state.PrivateDirectory) != state.OriginalDirectoryIdentity)
                throw new WorktreeBoundaryException("Original private admin directory identity changed across relocation.");
            if (!metadata.Read(Path.Combine(state.PrivateDirectory, "gitdir")).AsSpan().SequenceEqual(gitdir) ||
                OwnedGitBinding.Text(metadata.Read(Path.Combine(state.PrivateDirectory, "HEAD"))) != state.FrozenBaseCommit)
                throw new WorktreeBoundaryException("Moved private admin no longer joins the original worker/base.");
            _originalBudget.CheckWork(); parent.CheckCurrent(_effectRunId!);
            state.Complete = true;
            return true;
        });
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    static extern int RenamePrivateAdministrationNoReplace(int oldDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

    static void RequireForeignOrAbsentRegistration(PrivateAdministrationProvisioning state, OwnedGitBinding metadata, bool cleanup)
    {
        var registered = state.RegisteredDirectory!;
        // Git may legitimately reuse a freed basename, even while that foreign
        // admin is still being populated. Its different identity grants no
        // authority here; never inspect or require its mutable backpointer.
        if (metadata.ReadPrivateDirectoryIdentity(registered, cleanup, foreignSlot: true) == state.OriginalDirectoryIdentity)
            throw new WorktreeBoundaryException("Original private worker admin was registered again.");
    }

    void RequirePrivateAdministrationCapture(PrivateAdministrationProvisioning state, string root, string common, string admin, bool cleanup = false)
    {
        if (!ReferenceEquals(state.Manager, this) || !privateAdministrations.TryGetValue(root, out var retained) ||
            !ReferenceEquals(retained, state) || !state.AnchorCreated || !state.MoveReturned || !state.Complete ||
            !string.Equals(root, state.Worktree, PathComparison()) || !string.Equals(common, state.CommonDirectory, PathComparison()) ||
            !string.Equals(admin, state.PrivateDirectory, PathComparison()) || state.RegisteredDirectory is null ||
            IsWithin(admin, root) || IsWithin(admin, common))
            throw new WorktreeBoundaryException("Private admin capture is not this completed original relocation.");
        RejectSymlinkAncestors(admin);
        var metadata = new OwnedGitBinding(this, root, common, admin, [], [], []);
        if (metadata.ReadPrivateDirectoryIdentity(admin, cleanup) != state.OriginalDirectoryIdentity)
            throw new WorktreeBoundaryException("Original private admin directory identity changed.");
        RequireForeignOrAbsentRegistration(state, metadata, cleanup);
    }

    internal sealed partial class OwnedGitBinding
    {
        static readonly List<(OwnedGitBinding Owner, SafeFileHandle Resource)> UnsettledPrivateDirectories = [];

        bool ForeignSlotAbsentOrNonPlain(string path, bool cleanup)
        {
            Phase(cleanup);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { Phase(cleanup); return true; }
            catch (DirectoryNotFoundException) { Phase(cleanup); return true; }
            Phase(cleanup);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new WorktreeBoundaryException("Private admin directory cannot be a reparse point or symbolic link.");
            return (attributes & FileAttributes.Directory) == 0;
        }

        internal (ulong Volume, ulong Low, ulong High) ReadPrivateDirectoryIdentity(string path, bool cleanup = false, bool foreignSlot = false)
        {
            Phase(cleanup);
            RejectSymlinkAncestors(foreignSlot ? Path.GetDirectoryName(path)! : path);
            // Only this old, freed registry slot may yield the zero sentinel.
            // The original private directory still requires its exact identity.
            if (foreignSlot && ForeignSlotAbsentOrNonPlain(path, cleanup)) return default;
            if (OperatingSystem.IsLinux())
            {
                (ulong Device, ulong Inode, long ChangedSeconds, uint ChangedNanoseconds) value;
                try { value = PrivateObjectLinks.Identity(path, directory: true); }
                catch (WorktreeBoundaryException) when (foreignSlot)
                {
                    // statx's bounded helper has no errno result. Do not turn
                    // arbitrary failures into absence: inspect the namespace
                    // again under the same phase, and retain unreadable entries.
                    if (ForeignSlotAbsentOrNonPlain(path, cleanup)) return default;
                    throw;
                }
                Phase(cleanup); return (value.Device, value.Inode, 0);
            }
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Private admin directory identity supports Linux/Windows only.");
            SafeFileHandle? held = null; Exception? primary = null, close = null;
            (ulong Volume, ulong Low, ulong High)? result = null;
            try
            {
                Phase(cleanup);
                held = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
                    FileMode.Open, 0x02200000, IntPtr.Zero); // BACKUP_SEMANTICS | OPEN_REPARSE_POINT.
                if (held.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (foreignSlot && error is 2 or 3 or 303) result = default((ulong, ulong, ulong));
                    else throw new WorktreeBoundaryException("Original plain private admin directory identity is unavailable.");
                }
                else if (!GetPrivateDirectoryAttributes(held, 9, out var attributes, 8))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (foreignSlot && error is 2 or 3 or 303) result = default((ulong, ulong, ulong));
                    else throw new WorktreeBoundaryException("Original plain private admin directory attributes are unavailable.");
                }
                else if ((attributes.Attributes & 0x410) != 0x10)
                {
                    if ((attributes.Attributes & 0x400) != 0)
                        throw new WorktreeBoundaryException("Private admin directory cannot be a reparse point or symbolic link.");
                    if (foreignSlot) result = default((ulong, ulong, ulong));
                    else throw new WorktreeBoundaryException("Original private admin directory is not plain.");
                }
                else
                {
                    if (!GetPrivateDirectoryId(held, 18, out var id, 24))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (foreignSlot && error is 2 or 3 or 303) result = default((ulong, ulong, ulong));
                        else throw new WorktreeBoundaryException("Original plain private admin directory identity is unavailable.");
                    }
                    else
                    {
                        if ((id.Low | id.High) == 0)
                            throw new WorktreeBoundaryException("Original plain private admin directory identity is unavailable.");
                        result = (id.Volume, id.Low, id.High);
                    }
                }
                Phase(cleanup);
            }
            catch (Exception failure) { primary = failure; }
            if (held is not null)
            {
                if (primary is null) { try { Phase(cleanup); } catch (Exception failure) { primary = failure; } }
                var returned = false;
                try
                {
                    if (primary is not null) _ = original.CleanupRemaining;
                    held.Dispose(); returned = true;
                    if (primary is null) Phase(cleanup); else _ = original.CleanupRemaining;
                }
                catch (Exception failure)
                {
                    close = failure;
                    if (!returned)
                    {
                        lock (UnsettledPrivateDirectories) UnsettledPrivateDirectories.Add((this, held)); Uncertain = true;
                        try { if (original is IOwnedBudgetUnknownRetention retained) retained.PreserveUnknown(); }
                        catch (Exception observation) { close = new AggregateException(close, observation); }
                        try { parent.RetainUncertain(RunId, "Private admin directory identity first disposition did not return."); }
                        catch (Exception observation) { close = new AggregateException(close, observation); }
                    }
                }
            }
            if (primary is not null && close is not null) throw new AggregateException("Private admin directory identity and first disposition failed.", primary, close);
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (close is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(close).Throw();
            return result ?? throw new WorktreeBoundaryException("Original private admin directory identity returned no DATA.");
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PrivateDirectoryId { internal ulong Volume, Low, High; }
        [StructLayout(LayoutKind.Sequential)]
        struct PrivateDirectoryAttributes { internal uint Attributes, ReparseTag; }
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetPrivateDirectoryId(SafeFileHandle handle, int informationClass, out PrivateDirectoryId information, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetPrivateDirectoryAttributes(SafeFileHandle handle, int informationClass, out PrivateDirectoryAttributes information, uint size);

        void VerifyPrivateAdministrationMetadata(bool cleanup)
        {
            if (privateAdministration is not { } state) return;
            Phase(cleanup);
            manager.RequirePrivateAdministrationCapture(state, Worktree, CommonDirectory, GitDirectory, cleanup);
            Phase(cleanup);
        }

        internal async Task VerifyBaseAnchorAsync(CancellationToken token)
        {
            RequireRetained(); Check();
            if (privateAdministration is null) return;
            // Git resolves packed refs as well as loose refs. COMMON is not the
            // worker's bound cwd and cannot select its private object overlay.
            var actual = (await manager.GitAsync(CommonDirectory, token, "rev-parse", "--verify", BaseAnchorRef + "^{commit}").ConfigureAwait(false)).TrimEnd('\r', '\n');
            Check();
            if (actual != FrozenBaseCommit) throw new WorktreeBoundaryException("Original common base anchor changed or disappeared.");
        }

        internal void RewriteProvisioningMetadata(string path, byte[] expected, byte[] replacement) =>
            WriteProvisioningMetadata(path, expected, replacement);

        internal void CreateFreshRefLock(string path, byte[] bytes) => WriteProvisioningMetadata(path, null, bytes);

        internal void CreateFreshProvisioningData(string path, byte[] bytes) => WriteProvisioningMetadata(path, null, bytes);

        void WriteProvisioningMetadata(string path, byte[]? expected, byte[] replacement)
        {
            FileStream? file = null; Exception? primary = null, close = null;
            try
            {
                Phase(false); RejectSymlinkAncestors(path);
                if ((expected?.Length ?? 0) > 4096 || replacement.Length > 4096 ||
                    expected is not null && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new WorktreeBoundaryException("Private admin metadata requires bounded plain existing files.");
                // Open preserves the existing Windows Hidden attribute. Never
                // recreate the worker marker or adopt a replacement namespace.
                file = new FileStream(path, expected is null ? FileMode.CreateNew : FileMode.Open,
                    FileAccess.ReadWrite, expected is null ? FileShare.None : FileShare.Read);
                if (!PrivateObjectLinks.HasOneLink(file.SafeFileHandle) ||
                    WriterNamespaceIdentityReader.Observe(file.SafeFileHandle, path).Outcome != WriterNamespaceIdentityOutcome.Match || file.Length != (expected?.Length ?? 0))
                    throw new WorktreeBoundaryException("Original metadata handle identity or extent differs.");
                var actual = new byte[expected?.Length ?? 0]; var offset = 0;
                while (offset < actual.Length)
                {
                    Phase(false); var count = file.Read(actual, offset, actual.Length - offset);
                    if (count == 0) throw new WorktreeBoundaryException("Original metadata ended before its exact expected bytes.");
                    offset += count;
                }
                if ((expected is not null && !actual.AsSpan().SequenceEqual(expected)) || file.ReadByte() != -1)
                    throw new WorktreeBoundaryException("Original metadata bytes changed before relocation rewrite.");
                Phase(false); file.Position = 0; file.SetLength(0); file.Write(replacement); file.Flush(flushToDisk: true); Phase(false);
                if (!PrivateObjectLinks.HasOneLink(file.SafeFileHandle) ||
                    WriterNamespaceIdentityReader.Observe(file.SafeFileHandle, path).Outcome != WriterNamespaceIdentityOutcome.Match || file.Length != replacement.Length)
                    throw new WorktreeBoundaryException("Original metadata namespace changed during its rewrite.");
            }
            catch (Exception failure) { primary = failure; }
            if (file is not null)
            {
                // If forward currentness expires after the write, latch that
                // failure but still attempt first close under the SAME original
                // cleanup allowance. Joining/closing never grants forward work.
                if (primary is null) { try { Phase(false); } catch (Exception failure) { primary = failure; } }
                var returned = false;
                try
                {
                    if (primary is not null) _ = original.CleanupRemaining;
                    file.Dispose(); returned = true;
                    if (primary is null) Phase(false); else _ = original.CleanupRemaining;
                }
                catch (Exception failure)
                {
                    close = failure;
                    if (!returned)
                    {
                        lock (Unsettled) Unsettled.Add((this, file)); Uncertain = true;
                        try { if (original is IOwnedBudgetUnknownRetention retained) retained.PreserveUnknown(); }
                        catch (Exception observation) { close = new AggregateException(close, observation); }
                        try { parent.RetainUncertain(RunId, "Private admin metadata first disposition did not return."); }
                        catch (Exception observation) { close = new AggregateException(close, observation); }
                    }
                }
            }
            if (primary is not null && close is not null) throw new AggregateException("Private admin metadata rewrite and first disposition failed.", primary, close);
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (close is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(close).Throw();
        }
    }
}
