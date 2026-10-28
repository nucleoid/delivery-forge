using System.Text;
using DeliveryForge.Execution.Host;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Execution.Git;

public sealed partial class WorktreeManager
{
    System.Collections.Concurrent.ConcurrentDictionary<string, OwnedGitBinding> ownedGit = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    bool cleanupObservation;

    // Only PrepareAsync installs this object after its actual parent-owned add.
    // Even an internal lookalike cannot be used unless it is this retained entry.
    internal sealed class OwnedGitBinding
    {
        static readonly List<(OwnedGitBinding Owner, FileStream Resource)> Unsettled = [];
        readonly WorktreeManager manager;
        readonly IOwnedDuplexBudget original;
        readonly ICoordinatedSynchronousEffectAdmission parent;
        readonly byte[] markerBytes, gitdirBytes, commonBytes;
        WriterLease? writer;
        internal bool Uncertain { get; private set; }
        internal string RunId { get; }
        internal string Worktree { get; }
        internal string GitDirectory { get; }
        internal string CommonDirectory { get; }
        internal string IndexPath => Path.Combine(GitDirectory, "index");
        internal string ObjectsPath => Path.Combine(CommonDirectory, "objects");
        internal string WriterPath => Path.Combine(GitDirectory, "delivery-forge-writer.lock");

        internal OwnedGitBinding(WorktreeManager manager, string worktree, string expectedCommon,
            string gitDirectory, byte[] marker, byte[] gitdir, byte[] common)
        {
            this.manager = manager; original = manager._originalBudget!;
            parent = (ICoordinatedSynchronousEffectAdmission)manager._effectAdmission!;
            RunId = manager._effectRunId!; Worktree = worktree;
            CommonDirectory = expectedCommon; GitDirectory = gitDirectory;
            markerBytes = marker; gitdirBytes = gitdir; commonBytes = common;
        }
        internal void RequireOriginal(IOwnedDuplexBudget owner, ICoordinatedEffectAdmission admission, string runId)
        {
            if (!ReferenceEquals(owner, original) || !ReferenceEquals(parent, admission) || runId != RunId)
                throw new WorktreeBoundaryException("Provisioned Git binding belongs to another original parent/run.");
            Check();
        }
        void Phase(bool cleanup)
        {
            if (Uncertain) throw new WorktreeBoundaryException("Provisioned Git metadata has an unresolved original file.");
            if (cleanup) _ = original.CleanupRemaining;
            else { original.CheckWork(); parent.CheckCurrent(RunId); }
        }
        internal byte[] Read(string path, bool cleanup = false)
        {
            FileStream? file = null; byte[]? result = null; Exception? primary = null, close = null;
            try
            {
                Phase(cleanup); RejectSymlinkAncestors(path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new WorktreeBoundaryException("Provisioned metadata must be plain.");
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > 4096) throw new WorktreeBoundaryException("Provisioned metadata exceeds4096 bytes.");
                using var bytes = new MemoryStream(); var buffer = new byte[4097];
                while (true)
                {
                    Phase(cleanup); var count = file.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    if (bytes.Length + count > 4096) throw new WorktreeBoundaryException("Provisioned metadata grew past4096 bytes.");
                    bytes.Write(buffer, 0, count);
                }
                result = bytes.ToArray();
            }
            catch (Exception failure) { primary = failure; }
            if (file is not null)
            {
                if (primary is null) { try { Phase(cleanup); } catch (Exception failure) { primary = failure; } }
                var returned = false;
                try
                {
                    if (primary is not null) _ = original.CleanupRemaining;
                    file.Dispose(); returned = true;
                    if (primary is not null) _ = original.CleanupRemaining; else Phase(cleanup);
                }
                catch (Exception failure)
                {
                    close = failure;
                    if (!returned)
                    {
                        lock (Unsettled) Unsettled.Add((this, file)); Uncertain = true;
                        try { if (original is IOwnedBudgetUnknownRetention retained) retained.PreserveUnknown(); }
                        catch (Exception observation) { close = new AggregateException("Metadata disposition and original uncertainty observation failed.", close, observation); }
                        try { parent.RetainUncertain(RunId, "Original provisioned Git metadata first disposition did not return."); }
                        catch (Exception observation) { close = new AggregateException("Metadata disposition and parent uncertainty observation failed.", close, observation); }
                    }
                }
            }
            if (primary is not null && close is not null) throw new AggregateException("Metadata read and first disposition failed.", primary, close);
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (close is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(close).Throw();
            return result ?? throw new IOException("Provisioned metadata returned no bytes.");
        }
        internal static string Text(byte[] bytes)
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\r', '\n');
            if (text.Length == 0 || text.Contains('\r') || text.Contains('\n') || text.Contains('\0'))
                throw new WorktreeBoundaryException("Provisioned metadata is not one exact bounded line.");
            return text;
        }
        void RequireRetained()
        {
            if (!manager.ownedGit.TryGetValue(Worktree, out var retained) || !ReferenceEquals(retained, this))
                throw new WorktreeBoundaryException("No original parent provisioning owns this Git binding.");
        }
        void VerifyMetadata(bool cleanup)
        {
            Phase(cleanup);
            if (!Read(Path.Combine(Worktree, ".git"), cleanup).AsSpan().SequenceEqual(markerBytes) ||
                !Read(Path.Combine(GitDirectory, "gitdir"), cleanup).AsSpan().SequenceEqual(gitdirBytes) ||
                !Read(Path.Combine(GitDirectory, "commondir"), cleanup).AsSpan().SequenceEqual(commonBytes))
                throw new WorktreeBoundaryException("Original provisioned Git metadata association changed.");
            RejectSymlinkAncestors(GitDirectory); RejectSymlinkAncestors(CommonDirectory);
            if (writer is not null && !(cleanup && writer.FirstCloseReturned) &&
                writer.ObserveCurrentNamespaceIdentity().Outcome != WriterNamespaceIdentityOutcome.Match)
                throw new WorktreeBoundaryException("Original provisioned writer namespace changed or is unknown.");
            Phase(cleanup);
        }
        internal void Check(bool cleanup = false)
        {
            RequireRetained();
            if (cleanup) VerifyMetadata(true);
            else parent.EnterSynchronous(RunId, () => { VerifyMetadata(false); return true; });
        }
        // One code-owned synchronous review verification, not an admitted scope
        // returned to a caller or authority that may flow across an await.
        internal void CheckAndVerifyReviewImage(IOwnedDuplexBudget owner, ICoordinatedEffectAdmission admission,
            string runId, Action verifyImage)
        {
            ArgumentNullException.ThrowIfNull(verifyImage);
            if (!ReferenceEquals(owner, original) || !ReferenceEquals(parent, admission) || runId != RunId)
                throw new WorktreeBoundaryException("Provisioned Git binding belongs to another original parent/run.");
            RequireRetained();
            parent.EnterSynchronous(RunId, () =>
            {
                VerifyMetadata(false);
                verifyImage();
                return true;
            });
        }
        void RequireNativeInvocation(CommandInvocation invocation)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            if (!string.Equals(Path.GetFullPath(invocation.FileName), manager._gitExecutable, PathComparison()) ||
                !string.Equals(Path.GetFullPath(invocation.WorkingDirectory), Worktree, PathComparison()))
                throw new WorktreeBoundaryException("Native Git invocation differs from its retained original image/worktree.");
            RequireRetained();
        }
        internal Task<BoundedCommandCaptureResult> ExecuteWorktreeGitAsync(CommandInvocation invocation, CancellationToken token)
        {
            RequireNativeInvocation(invocation);
            var executor = manager._originalExecutor ?? throw new WorktreeBoundaryException("Actual original Git executor is absent.");
            return parent.EnterAsync(RunId, () =>
            {
                original.CheckWork();
                VerifyMetadata(false);
                Interlocked.Increment(ref manager.actualOwnedGitInvocations.Value);
                return executor.ExecuteBoundedCaptureAsync(invocation, TimeSpan.FromSeconds(30), 16 * 1024 * 1024, token, new OwnedWorkPhaseBudget(original));
            });
        }
        internal Task<BoundedCommandCaptureResult> ExecuteReviewGitAsync(ProductionReviewSourceOwner actualOwner,
            CommandInvocation invocation, int maximumBytes, CancellationToken token, out bool dispatchEntered)
        {
            var entered = false;
            try
            {
                RequireNativeInvocation(invocation);
                var executor = manager._originalExecutor ?? throw new WorktreeBoundaryException("Actual original Git executor is absent.");
                actualOwner.RequireNativeGitBinding(this, original, parent, RunId, executor);
                return parent.EnterAsync(RunId, async () =>
                {
                    actualOwner.Check();
                    VerifyMetadata(false);
                    var allowance = actualOwner.Remaining();
                    entered = true;
                    actualOwner.NativeGitCaptureStarting();
                    var capture = await executor.ExecuteBoundedCaptureAsync(invocation, allowance, maximumBytes, token, new OwnedWorkPhaseBudget(original)).ConfigureAwait(false);
                    // Even an already-completed native Task must leave the
                    // admission factory's monitor before file/hash postchecks.
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    var facts = capture.Command.LifecycleDiagnostics;
                    if (!capture.Command.OwnedProcessQuiescent || !capture.RootExitObserved || !capture.StandardOutputDrainCompleted || !capture.StandardErrorDrainCompleted ||
                        facts?.NativeFailure is not null || facts?.OwnedTree is not { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 } ||
                        OperatingSystem.IsLinux() && capture.OwnershipKind != BoundedCaptureOwnershipKind.LinuxCreationOwnedScope ||
                        capture.Command.Cancelled || capture.Command.TimedOut || capture.CaptureBoundExceeded || capture.Command.ExitCode != 0)
                        return capture; // Preserve the outer original failure classification before any post-I/O.
                    try
                    {
                        actualOwner.Check();
                        RequireNativeInvocation(invocation);
                        VerifyMetadata(false);
                        actualOwner.VerifyCompletedNativeGitImage(this, original, parent, RunId, executor);
                    }
                    catch (Exception failure)
                    {
                        failure.Data["ReviewGitPreparationCapture"] = ReviewGitPreparationCaptureData.FromCapture(capture);
                        throw;
                    }
                    return capture;
                });
            }
            finally { dispatchEntered = entered; }
        }
        internal bool OwnsWriter(WriterLease actual) => ReferenceEquals(writer, actual);
        internal Task<T> EnterAsync<T>(Func<Task<T>> invoke) => parent.EnterAsync(RunId, invoke);
        internal void Attach(WriterLease actual)
        {
            Check();
            if (writer is not null || actual.RunId != RunId || !string.Equals(actual.DiagnosticLockPath, WriterPath, PathComparison()) ||
                actual.ObserveCurrentNamespaceIdentity().Outcome != WriterNamespaceIdentityOutcome.Match)
                throw new WorktreeBoundaryException("Provisioning requires the exact first original writer handle.");
            writer = actual;
        }
    }

    internal OwnedGitBinding RequireOriginalBinding(string worktree, bool cleanup = false)
    {
        if (_originalBudget is null) throw new WorktreeBoundaryException("Actual original parent provisioning binding is missing.");
        if (cleanup || cleanupObservation) _ = _originalBudget.CleanupRemaining; else _originalBudget.CheckWork();
        if (!ownedGit.TryGetValue(Path.GetFullPath(worktree), out var binding))
            throw new WorktreeBoundaryException("Actual original parent provisioning binding is missing.");
        binding.Check(cleanup || cleanupObservation); return binding;
    }
    internal OwnedGitBinding RequireOriginalBinding(string runId, IOwnedDuplexBudget original, ICoordinatedEffectAdmission parent)
    {
        var matches = ownedGit.Values.Where(value => value.RunId == runId).Take(2).ToArray();
        if (matches.Length != 1) throw new WorktreeBoundaryException("No unique actual original provisioned worktree.");
        matches[0].RequireOriginal(original, parent, runId); return matches[0];
    }
    // Same existing retained provisioning; this overload cannot adopt metadata.
    internal OwnedGitBinding RequireOriginalBinding(string worktree, IOwnedDuplexBudget original, string runId)
    {
        if (!ReferenceEquals(_originalBudget, original))
            throw new WorktreeBoundaryException("Confined Git roots belong to another original.");
        var binding = RequireOriginalBinding(worktree);
        if (binding.RunId != runId) throw new WorktreeBoundaryException("Confined Git roots belong to another run.");
        return binding;
    }

    OwnedGitBinding CaptureProvisionedBinding(string worktree, string common)
    {
        if (_originalBudget is null || _effectAdmission is not ICoordinatedSynchronousEffectAdmission parent || _effectRunId is null)
            throw new WorktreeBoundaryException("Provisioned binding requires the actual original synchronous parent.");
        var root = Path.GetFullPath(worktree); common = CanonicalPath(common);
        return parent.EnterSynchronous(_effectRunId, () =>
        {
            // Temporary construction is private to this completed provisioning
            // call. The object is not usable until retained in ownedGit below.
            var candidate = new OwnedGitBinding(this, root, common, common, [], [], []);
            var marker = candidate.Read(Path.Combine(root, ".git")); var link = OwnedGitBinding.Text(marker);
            if (!link.StartsWith("gitdir: ", StringComparison.Ordinal)) throw new WorktreeBoundaryException("Provisioned linked-worktree marker required.");
            var admin = CanonicalPath(Path.GetFullPath(link[8..], root));
            if (!IsWithin(admin, Path.Combine(common, "worktrees"))) throw new WorktreeBoundaryException("Provisioned worker admin is outside the original parent common directory.");
            var gitdir = candidate.Read(Path.Combine(admin, "gitdir")); var commonBytes = candidate.Read(Path.Combine(admin, "commondir"));
            if (!string.Equals(Path.GetFullPath(OwnedGitBinding.Text(gitdir), admin), Path.Combine(root, ".git"), PathComparison()) ||
                !string.Equals(CanonicalPath(Path.GetFullPath(OwnedGitBinding.Text(commonBytes), admin)), common, PathComparison()))
                throw new WorktreeBoundaryException("Provisioned metadata does not join the original parent and destination.");
            var binding = new OwnedGitBinding(this, root, common, admin, marker, gitdir, commonBytes);
            if (!ownedGit.TryAdd(root, binding)) throw new WorktreeBoundaryException("Original worktree provisioning cannot replay.");
            return binding;
        });
    }
}
