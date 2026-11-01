using System.IO.Compression;
using System.Text;
using DeliveryForge.Evidence;

namespace DeliveryForge.Execution.Git;

public sealed partial class WorktreeManager
{
    internal sealed partial class OwnedGitBinding
    {
        const int ImportFileBytes = 16 * 1024 * 1024;
        const long ImportBytes = 128L * 1024 * 1024;
        sealed record PreparedImport(string Oid, string Kind, ReadOnlyMemory<byte> Canonical,
            int BodyOffset, byte[] Encoded, byte[] CommonEncoding, bool Existing)
        {
            internal ReadOnlyMemory<byte> Body => Canonical[BodyOffset..];
        }
        TaskCompletionSource<bool>? privateObjectReconciliation;
        bool reconciledObjects;
        byte[]? reconciledHead;
        internal bool HasReconciledObjects => Volatile.Read(ref reconciledObjects);
        internal Task? PrivateObjectReconciliation => Volatile.Read(ref privateObjectReconciliation)?.Task;
        internal string ReadObjectsPath => HasReconciledObjects ? CommonObjectsPath : ObjectsPath;
        // This reserved name is DATA until the successful CAS and readback. It
        // cannot authorize adoption of an existing ref or another binding.
        internal string? CandidateRef => BaseAnchorRef is { } anchor ? anchor[..^4] + "candidate" : null;

        internal Task ReconcilePrivateObjectsAfterRetirementAsync(CancellationToken token)
        {
            RequireRetained(); Phase(false); VerifyMetadata(false); RequirePrivateWriterClosed();
            if (!HasPrivateAdministration || !HasPrivateObjects ||
                Volatile.Read(ref privateObjectValidation)?.Task.IsCompletedSuccessfully != true)
                throw new WorktreeBoundaryException("Reconciliation requires this original private administration and successful post-retirement validation.");
            RequireValidatedPrivateObjects();
            var attempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref privateObjectReconciliation, attempt, null) is not null)
                throw new WorktreeBoundaryException("Private object reconciliation cannot adopt or replay its original attempt.");
            _ = CompleteReconciliationAsync(attempt, token);
            return attempt.Task;
        }

        async Task CompleteReconciliationAsync(TaskCompletionSource<bool> attempt, CancellationToken token)
        {
            try
            {
                await parent.EnterAsync(RunId, () => ReconcileCoreAsync(token)).ConfigureAwait(false);
                attempt.TrySetResult(true);
            }
            catch (Exception failure) { attempt.TrySetException(failure); }
        }

        void CheckReconciliation(CancellationToken token)
        {
            RequireRetained(); Phase(false); token.ThrowIfCancellationRequested();
            VerifyMetadata(false); RequirePrivateWriterClosed();
            RejectSymlinkAncestors(CommonObjectsPath);
            if (Path.Exists(Path.Combine(CommonObjectsPath, "info", "alternates")) || Path.Exists(Path.Combine(CommonObjectsPath, "info", "http-alternates")))
                throw new WorktreeBoundaryException("COMMON reconciliation cannot route to another object namespace.");
        }

        async Task<bool> ReconcileCoreAsync(CancellationToken token)
        {
            // The admission factory must return before nested native admission.
            await Task.Yield();
            CheckReconciliation(token); RequireValidatedPrivateObjects();
            await VerifyBaseAnchorAsync(token).ConfigureAwait(false);
            var head = Read(Path.Combine(GitDirectory, "HEAD"));
            var candidate = Text(head);
            if (!LooseGitObjectInventory.IsOid(candidate) || FrozenBaseCommit is not { } baseline || candidate.Length != baseline.Length)
                throw new WorktreeBoundaryException("Private candidate must be one detached commit in the frozen base object format.");
            var candidateRef = CandidateRef ?? throw new WorktreeBoundaryException("Original candidate ref is absent.");
            var closure = await manager.GitAsync(Worktree, token, "rev-list", "--objects", "--no-object-names",
                "--missing=error", candidate, "^" + baseline).ConfigureAwait(false);
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in closure.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var oid = line.TrimEnd('\r');
                if (!LooseGitObjectInventory.IsOid(oid) || oid.Length != baseline.Length ||
                    reachable.Count >= 4096 || !reachable.Add(oid))
                    throw new WorktreeBoundaryException("Candidate object closure exceeds the finite unique object profile.");
            }
            CheckReconciliation(token); RequireValidatedPrivateObjects(); CheckSameHead(head);
            var objects = privateInventory!.ReachableObjectIds(reachable, () => CheckReconciliation(token));
            if (objects.Length != reachable.Count)
                throw new WorktreeBoundaryException("Candidate delta requires a validated private copy of every object outside the frozen base closure.");

            // ALL object-deleting maintenance, including expiry-based gc, must
            // be serialized outside promotion through the post-ref proof.
            // Parent admission does not exclude arbitrary external Git.
            long bytes = 0;
            var staged = new List<PreparedImport>();
            var collisions = 0;
            var bodyRoot = Path.Combine(GitDirectory, "forge-reconciliation-bodies-" + Guid.NewGuid().ToString("N"));
            // Reserve every known read/encoding/proof cost BEFORE any COMMON
            // write. Canonical validation is not an import-budget reservation.
            // Retain these exact outputs; do not recompress during promotion.
            parent.EnterSynchronous(RunId, () =>
            {
                foreach (var oid in objects)
                {
                    CheckReconciliation(token);
                    var encoded = privateInventory.ReadForImport(oid,
                        checked((int)Math.Min(ImportFileBytes, ImportBytes - bytes)),
                        () => CheckReconciliation(token), ReadPrivateObject);
                    bytes += encoded.Length;
                    using var canonical = new MemoryStream();
                    bytes += LooseGitObjectInventory.ValidateObject(oid, encoded,
                        checked((int)Math.Min(ImportFileBytes, ImportBytes - bytes)), () => CheckReconciliation(token), canonical);
                    var expected = canonical.GetBuffer().AsMemory(0, checked((int)canonical.Length));
                    // A valid canonical body need not imply that the worker's
                    // original compressed stream has no ignored trailing bytes.
                    // Only Forge's fresh complete encoding may enter COMMON.
                    var trustedEncoding = EncodeCanonical(expected,
                        checked((int)Math.Min(ImportFileBytes, ImportBytes - bytes)), token);
                    bytes += trustedEncoding.Length;
                    var separator = expected.Span.IndexOf((byte)0);
                    var space = expected.Span[..separator].IndexOf((byte)' ');
                    var kind = Encoding.ASCII.GetString(expected.Span[..space]);
                    var target = Path.Combine(CommonObjectsPath, oid[..2], oid[2..]);
                    RejectSymlinkAncestors(target);
                    var existing = Path.Exists(target);
                    if (existing && ++collisions > 32)
                        throw new WorktreeBoundaryException("Canonical import preflight exceeds its finite 32 COMMON loose-collision comparison profile.");
                    var commonEncoding = existing ? CompareExistingLooseObject(oid, target, expected, ref bytes, token) : trustedEncoding;
                    var prepared = new PreparedImport(oid, kind, expected, separator + 1, trustedEncoding, commonEncoding, existing);
                    ReserveImportBytes(ref bytes, commonEncoding.Length); // exact encoded readback after promotion.
                    if (existing) ReserveImportBytes(ref bytes, prepared.Body.Length); // actual Git raw-body comparison.
                    if (kind != "blob") ReserveImportBytes(ref bytes, prepared.Body.Length * 2L); // body file write + Git read.
                    staged.Add(prepared);
                }
                if (staged.Any(item => item.Kind != "blob"))
                {
                    RejectSymlinkAncestors(bodyRoot);
                    if (Path.Exists(bodyRoot)) throw new WorktreeBoundaryException("Fresh canonical body staging path already exists.");
                    Directory.CreateDirectory(bodyRoot); RejectSymlinkAncestors(bodyRoot);
                    foreach (var item in staged.Where(item => item.Kind != "blob"))
                        WriteImportFile(Path.Combine(bodyRoot, item.Oid), item.Body, token);
                }
                CheckReconciliation(token); return true;
            });
            foreach (var kind in new[] { "tree", "commit", "tag" })
            {
                foreach (var group in staged.Where(item => item.Kind == kind).Chunk(48))
                {
                    var expected = string.Concat(group.Select(item => item.Oid + "\n"));
                    // Controlled relative OID arguments avoid supplied stdin,
                    // which the selected Linux capture intentionally refuses.
                    // 16 fixed arguments + at most 48 leaves fit the existing
                    // Linux 64-argument guard; at most 88 structural captures.
                    var checkedBodies = await ReconciliationCaptureAsync(ObjectsPath, CommonObjectsPath, bodyRoot,
                        "Private candidate structural preflight", token,
                        ["hash-object", "--no-filters", "-t", kind, "--", .. group.Select(item => item.Oid)]).ConfigureAwait(false);
                    if (!checkedBodies.StandardOutputBytes.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(expected)))
                        throw new WorktreeBoundaryException("Private candidate structural preflight returned different Git object hashes.");
                }
            }
            foreach (var item in staged.Where(item => item.Existing))
            {
                var capture = await ReconciliationCaptureAsync(CommonObjectsPath, null, Worktree,
                    "COMMON collision canonical preflight", token, "cat-file", item.Kind, item.Oid).ConfigureAwait(false);
                if (!capture.StandardOutputBytes.AsSpan().SequenceEqual(item.Body.Span))
                    throw new WorktreeBoundaryException("COMMON collision canonical preflight differs from the validated private body.");
            }
            RequireValidatedPrivateObjects(); CheckSameHead(head);
            await VerifyBaseAnchorAsync(token).ConfigureAwait(false);
            parent.EnterSynchronous(RunId, () =>
            {
                foreach (var item in staged)
                {
                    CheckReconciliation(token);
                    var target = Path.Combine(CommonObjectsPath, item.Oid[..2], item.Oid[2..]);
                    RejectSymlinkAncestors(target);
                    if (item.Existing)
                    {
                        if (!File.Exists(target)) throw new WorktreeBoundaryException("Preflight COMMON object disappeared before promotion.");
                    }
                    else ImportLooseObject(item.Oid, item.Encoded, token);
                }
                // Fresh Forge encodings are byte-for-byte checked. Existing
                // encodings were additionally read by actual Git above.
                foreach (var item in staged)
                {
                    var target = Path.Combine(CommonObjectsPath, item.Oid[..2], item.Oid[2..]);
                    var actual = ReadPrivateObject(target, item.CommonEncoding.Length);
                    if (!actual.AsSpan().SequenceEqual(item.CommonEncoding))
                        throw new WorktreeBoundaryException("COMMON encoded readback differs from its canonical preflight.");
                    CheckReconciliation(token);
                }
                CheckReconciliation(token); return true;
            });
            // Every graph proof deliberately uses COMMON only, never the worker
            // alternate. No successful overlay read can certify this transport.
            _ = await CommonGitAsync(token, "cat-file", "-e", candidate + "^{commit}").ConfigureAwait(false);
            _ = await CommonGitAsync(token, "merge-base", "--is-ancestor", baseline, candidate).ConfigureAwait(false);
            await VerifyCommonDeltaAsync(candidate, baseline, reachable, token).ConfigureAwait(false);
            RequireValidatedPrivateObjects(); CheckSameHead(head);
            await VerifyBaseAnchorAsync(token).ConfigureAwait(false);
            await manager.CreateFreshCommonRefAsync(CommonDirectory, candidateRef, candidate, token).ConfigureAwait(false);
            var retained = await CommonGitAsync(token, "rev-parse", "--verify", candidateRef + "^{commit}").ConfigureAwait(false);
            if (retained.TrimEnd('\r', '\n') != candidate)
                throw new WorktreeBoundaryException("Original COMMON candidate ref readback differs after publication.");
            await VerifyCommonDeltaAsync(candidateRef, baseline, reachable, token).ConfigureAwait(false);
            parent.EnterSynchronous(RunId, () =>
            {
                CheckReconciliation(token); RequireValidatedPrivateObjects(); CheckSameHead(head);
                reconciledHead = head;
                Volatile.Write(ref reconciledObjects, true);
                return true;
            });
            return true;
        }

        static void ReserveImportBytes(ref long bytes, long additional)
        {
            if (additional < 0 || additional > ImportBytes - bytes)
                throw new WorktreeBoundaryException("Canonical import preflight exceeds its unchanged shared byte allowance.");
            bytes += additional;
        }

        async Task VerifyCommonDeltaAsync(string candidate, string baseline, HashSet<string> expected, CancellationToken token)
        {
            var output = await CommonGitAsync(token, "rev-list", "--objects", "--no-object-names", "--missing=error",
                candidate, "^" + baseline).ConfigureAwait(false);
            var actual = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
            if (actual.Length != expected.Count || !expected.SetEquals(actual))
                throw new WorktreeBoundaryException("COMMON candidate delta differs from the validated private closure.");
        }

        void CheckSameHead(byte[] expected, bool cleanup = false)
        {
            if (!Read(Path.Combine(GitDirectory, "HEAD"), cleanup).AsSpan().SequenceEqual(expected))
                throw new WorktreeBoundaryException("Private candidate HEAD changed during or after reconciliation.");
        }

        byte[] EncodeCanonical(ReadOnlyMemory<byte> canonical, int maximum, CancellationToken token)
        {
            if (maximum < 6) throw new WorktreeBoundaryException("Canonical recompression exceeds its remaining shared allowance.");
            using var encoded = new BoundedCompressionBuffer(maximum);
            using (var compressor = new ZLibStream(encoded, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (var offset = 0; offset < canonical.Length;)
                {
                    CheckReconciliation(token); var count = Math.Min(65536, canonical.Length - offset);
                    compressor.Write(canonical.Span.Slice(offset, count)); offset += count;
                }
                CheckReconciliation(token);
            }
            CheckReconciliation(token); return encoded.ToArray();
        }

        sealed class BoundedCompressionBuffer(int maximum) : MemoryStream
        {
            void Bound(int count)
            {
                if (count < 0 || count > maximum - Length)
                    throw new WorktreeBoundaryException("Canonical compressed extent exceeds its finite allowance.");
            }
            public override void Write(byte[] buffer, int offset, int count) { Bound(count); base.Write(buffer, offset, count); }
            public override void Write(ReadOnlySpan<byte> buffer) { Bound(buffer.Length); base.Write(buffer); }
            public override void WriteByte(byte value) { Bound(1); base.WriteByte(value); }
        }

        void ImportLooseObject(string oid, byte[] encoded, CancellationToken token)
        {
            var fanout = Path.Combine(CommonObjectsPath, oid[..2]);
            var target = Path.Combine(fanout, oid[2..]);
            RejectSymlinkAncestors(fanout); CheckReconciliation(token);
            if (!Directory.Exists(fanout)) Directory.CreateDirectory(fanout);
            RejectSymlinkAncestors(target); CheckReconciliation(token);
            if (Path.Exists(target)) throw new WorktreeBoundaryException("COMMON target appeared after canonical preflight.");
            var temporary = Path.Combine(fanout, "tmp_forge_" + Guid.NewGuid().ToString("N"));
            WriteImportFile(temporary, encoded, token);
            CheckReconciliation(token); RejectSymlinkAncestors(target);
            // No overwrite, link, rollback, deletion or retry. If a concurrent
            // ordinary writer wins, this attempt fails and retains its evidence.
            File.Move(temporary, target, overwrite: false);
            CheckReconciliation(token);
        }

        byte[] CompareExistingLooseObject(string oid, string target, ReadOnlyMemory<byte> expected, ref long bytes, CancellationToken token)
        {
            var available = checked((int)Math.Min(ImportFileBytes, ImportBytes - bytes));
            var existing = ReadPrivateObject(target, available);
            bytes += existing.Length;
            using var actual = new MemoryStream();
            bytes += LooseGitObjectInventory.ValidateObject(oid, existing,
                checked((int)Math.Min(ImportFileBytes, ImportBytes - bytes)), () => CheckReconciliation(token), actual);
            if (!actual.GetBuffer().AsSpan(0, checked((int)actual.Length)).SequenceEqual(expected.Span))
                throw new WorktreeBoundaryException("Existing COMMON loose object differs from the validated candidate object.");
            return existing;
        }

        void WriteImportFile(string path, ReadOnlyMemory<byte> encoded, CancellationToken token)
        {
            FileStream? file = null; Exception? primary = null, close = null;
            try
            {
                CheckReconciliation(token); RejectSymlinkAncestors(path);
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                for (var offset = 0; offset < encoded.Length;)
                {
                    CheckReconciliation(token); var count = Math.Min(65536, encoded.Length - offset);
                    file.Write(encoded.Span.Slice(offset, count)); offset += count; CheckReconciliation(token);
                }
                file.Flush(flushToDisk: true); CheckReconciliation(token);
            }
            catch (Exception failure) { primary = failure; }
            if (file is not null)
            {
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
                        try { parent.RetainUncertain(RunId, "COMMON object import first disposition did not return."); }
                        catch (Exception observation) { close = new AggregateException(close, observation); }
                    }
                }
            }
            if (primary is not null && close is not null) throw new AggregateException("Object import write and first disposition failed.", primary, close);
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (close is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(close).Throw();
        }

        async Task<string> CommonGitAsync(CancellationToken token, params string[] arguments)
        {
            var capture = await ReconciliationCaptureAsync(CommonObjectsPath, null, Worktree,
                "COMMON reconciliation Git", token, arguments).ConfigureAwait(false);
            return new UTF8Encoding(false, true).GetString(capture.StandardOutputBytes);
        }

        async Task<BoundedCommandCaptureResult> ReconciliationCaptureAsync(string objectRoot, string? alternateRoot,
            string workingDirectory, string operation, CancellationToken token, params string[] arguments)
        {
            var executor = manager._originalExecutor ?? throw new WorktreeBoundaryException("Original Git executor is absent.");
            var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(row => !((string)row.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => (string)row.Key, row => row.Value?.ToString() ?? "", OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            environment["GIT_DIR"] = CommonDirectory; environment["GIT_COMMON_DIR"] = CommonDirectory;
            environment["GIT_OBJECT_DIRECTORY"] = objectRoot; environment["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = alternateRoot ?? "";
            environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = nullDevice; environment["GIT_CONFIG_SYSTEM"] = nullDevice;
            environment["GIT_NO_REPLACE_OBJECTS"] = "1"; environment["GIT_GRAFT_FILE"] = nullDevice;
            environment["GIT_NO_LAZY_FETCH"] = "1"; environment["GIT_TERMINAL_PROMPT"] = "0"; environment["GIT_OPTIONAL_LOCKS"] = "0";
            environment["LC_ALL"] = "C"; environment["LANG"] = "C";
            var invocation = new CommandInvocation(manager._gitExecutable,
                ["--no-optional-locks", "-c", "core.hooksPath=" + nullDevice, "-c", "core.fsmonitor=false", "-c", "core.commitGraph=false", "-c", "gc.auto=0", "-c", "maintenance.auto=false", .. arguments], workingDirectory, environment)
            { EnvironmentIsComplete = true };
            var capture = await parent.EnterAsync(RunId, () =>
            {
                CheckReconciliation(token);
                Interlocked.Increment(ref manager.actualOwnedGitInvocations.Value);
                return executor.ExecuteBoundedCaptureAsync(invocation, TimeSpan.FromSeconds(30), ImportFileBytes, token, new OwnedWorkPhaseBudget(original));
            }).ConfigureAwait(false);
            var command = capture.Command; var facts = command.LifecycleDiagnostics;
            if (capture.CaptureBoundExceeded || capture.StandardErrorBytes.Length != 0 || command.ExitCode != 0 || command.TimedOut || command.Cancelled || !command.OwnedProcessQuiescent ||
                !capture.RootExitObserved || !capture.StandardOutputDrainCompleted || !capture.StandardErrorDrainCompleted || facts?.NativeFailure is not null ||
                facts?.OwnedTree is not { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 } ||
                OperatingSystem.IsLinux() && capture.OwnershipKind != BoundedCaptureOwnershipKind.LinuxCreationOwnedScope)
                throw new WorktreeBoundaryException(operation + " did not close successfully.");
            CheckReconciliation(token);
            return capture;
        }
    }
}
