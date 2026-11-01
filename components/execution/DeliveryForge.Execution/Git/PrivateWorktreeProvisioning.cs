using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Execution.Git;

public sealed partial class WorktreeManager
{
    bool privateAdministrationSelected;
    ConcurrentDictionary<string, PrivateAdministrationProvisioning> privateAdministrations = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal sealed class PrivateAdministrationProvisioning
    {
        internal required WorktreeManager Manager;
        internal required string Worktree, CommonDirectory, PrivateDirectory, BaseAnchorRef, FrozenBaseCommit;
        internal string? RegisteredDirectory;
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
            privateAdministrationSelected = true;
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
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_effectRunId + "\0" + root)));
        var state = new PrivateAdministrationProvisioning
        {
            Manager = this, Worktree = root, CommonDirectory = common,
            PrivateDirectory = Path.Combine(directory, ".delivery-forge-private-admin-" + id),
            BaseAnchorRef = "refs/delivery-forge/private/" + id + "/base", FrozenBaseCommit = baseCommit
        };
        if (IsWithin(state.PrivateDirectory, root) || IsWithin(state.PrivateDirectory, common))
            throw new WorktreeBoundaryException("Private administration must be outside both worker and COMMON.");
        RequireAbsentPrivatePath(state.PrivateDirectory);
        if (!privateAdministrations.TryAdd(root, state))
            throw new WorktreeBoundaryException("Private administration provisioning cannot adopt or replay.");
        var actual = (await GitAsync(parentCheckout, token, "rev-parse", "--verify", baseCommit + "^{commit}").ConfigureAwait(false)).TrimEnd('\r', '\n');
        if (actual != baseCommit) throw new WorktreeBoundaryException("Private administration base is not the exact frozen commit.");
        // Exclusive ref creation precedes unregistering. This common reachable
        // root protects H0 even when its original branch/reflog/index disappear.
        _ = await GitAsync(parentCheckout, token, "update-ref", state.BaseAnchorRef, baseCommit, new string('0', baseCommit.Length)).ConfigureAwait(false);
        state.AnchorCreated = true;
        var anchored = (await GitAsync(parentCheckout, token, "rev-parse", "--verify", state.BaseAnchorRef + "^{commit}").ConfigureAwait(false)).TrimEnd('\r', '\n');
        _originalBudget.CheckWork(); parent.CheckCurrent(_effectRunId);
        if (anchored != baseCommit) throw new WorktreeBoundaryException("Fresh original base anchor readback differs.");
        return state;
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
            var siblingParent = Path.GetDirectoryName(state.PrivateDirectory)!;
            var target = PrivateObjectLinks.Identity(siblingParent, directory: true);
            if (OperatingSystem.IsLinux() ? before.Device != target.Device :
                !string.Equals(Path.GetPathRoot(registered), Path.GetPathRoot(state.PrivateDirectory), PathComparison()))
                throw new WorktreeBoundaryException("Private administration move requires the same filesystem.");
            state.RegisteredDirectory = registered;
            _originalBudget.CheckWork(); parent.CheckCurrent(_effectRunId!);
            state.MoveEntered = true;
            try { Directory.Move(registered, state.PrivateDirectory); state.MoveReturned = true; }
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
            RequireAbsentPrivatePath(registered);
            var after = PrivateObjectLinks.Identity(state.PrivateDirectory, directory: true);
            if (OperatingSystem.IsLinux() && (before.Device != after.Device || before.Inode != after.Inode))
                throw new WorktreeBoundaryException("Private admin identity changed across its no-copy move.");
            metadata.RewriteProvisioningMetadata(Path.Combine(state.PrivateDirectory, "commondir"), commondir,
                Encoding.UTF8.GetBytes(state.CommonDirectory + "\n"));
            metadata.RewriteProvisioningMetadata(Path.Combine(state.Worktree, ".git"), marker,
                Encoding.UTF8.GetBytes("gitdir: " + state.PrivateDirectory + "\n"));
            if (!metadata.Read(Path.Combine(state.PrivateDirectory, "gitdir")).AsSpan().SequenceEqual(gitdir) ||
                OwnedGitBinding.Text(metadata.Read(Path.Combine(state.PrivateDirectory, "HEAD"))) != state.FrozenBaseCommit)
                throw new WorktreeBoundaryException("Moved private admin no longer joins the original worker/base.");
            _originalBudget.CheckWork(); parent.CheckCurrent(_effectRunId!);
            state.Complete = true;
            return true;
        });
    }

    void RequirePrivateAdministrationCapture(PrivateAdministrationProvisioning state, string root, string common, string admin)
    {
        if (!ReferenceEquals(state.Manager, this) || !privateAdministrations.TryGetValue(root, out var retained) ||
            !ReferenceEquals(retained, state) || !state.AnchorCreated || !state.MoveReturned || !state.Complete ||
            !string.Equals(root, state.Worktree, PathComparison()) || !string.Equals(common, state.CommonDirectory, PathComparison()) ||
            !string.Equals(admin, state.PrivateDirectory, PathComparison()) || state.RegisteredDirectory is null ||
            IsWithin(admin, root) || IsWithin(admin, common))
            throw new WorktreeBoundaryException("Private admin capture is not this completed original relocation.");
        RejectSymlinkAncestors(state.RegisteredDirectory);
        RequireAbsentPrivatePath(state.RegisteredDirectory);
        RejectSymlinkAncestors(admin);
    }

    internal sealed partial class OwnedGitBinding
    {
        void VerifyPrivateAdministrationMetadata(bool cleanup)
        {
            if (privateAdministration is not { } state) return;
            Phase(cleanup);
            manager.RequirePrivateAdministrationCapture(state, Worktree, CommonDirectory, GitDirectory);
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

        internal void RewriteProvisioningMetadata(string path, byte[] expected, byte[] replacement)
        {
            FileStream? file = null; Exception? primary = null, close = null;
            try
            {
                Phase(false); RejectSymlinkAncestors(path);
                if (expected.Length > 4096 || replacement.Length > 4096 ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new WorktreeBoundaryException("Private admin metadata requires bounded plain existing files.");
                // Open preserves the existing Windows Hidden attribute. Never
                // recreate the worker marker or adopt a replacement namespace.
                file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                if (!PrivateObjectLinks.HasOneLink(file.SafeFileHandle) ||
                    WriterNamespaceIdentityReader.Observe(file.SafeFileHandle, path).Outcome != WriterNamespaceIdentityOutcome.Match || file.Length != expected.Length)
                    throw new WorktreeBoundaryException("Original metadata handle identity or extent differs.");
                var actual = new byte[expected.Length]; var offset = 0;
                while (offset < actual.Length)
                {
                    Phase(false); var count = file.Read(actual, offset, actual.Length - offset);
                    if (count == 0) throw new WorktreeBoundaryException("Original metadata ended before its exact expected bytes.");
                    offset += count;
                }
                if (!actual.AsSpan().SequenceEqual(expected) || file.ReadByte() != -1)
                    throw new WorktreeBoundaryException("Original metadata bytes changed before relocation rewrite.");
                Phase(false); file.Position = 0; file.SetLength(0); file.Write(replacement); file.Flush(flushToDisk: true); Phase(false);
                if (!PrivateObjectLinks.HasOneLink(file.SafeFileHandle) ||
                    WriterNamespaceIdentityReader.Observe(file.SafeFileHandle, path).Outcome != WriterNamespaceIdentityOutcome.Match || file.Length != replacement.Length)
                    throw new WorktreeBoundaryException("Original metadata namespace changed during its rewrite.");
            }
            catch (Exception failure) { primary = failure; }
            if (file is not null)
            {
                var returned = false;
                try
                {
                    if (primary is null) Phase(false); else _ = original.CleanupRemaining;
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
