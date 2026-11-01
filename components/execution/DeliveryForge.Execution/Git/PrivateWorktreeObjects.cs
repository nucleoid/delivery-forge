using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DeliveryForge.Evidence;

namespace DeliveryForge.Execution.Git;

public sealed partial class WorktreeManager
{
    internal sealed partial class OwnedGitBinding
    {
        Task<bool>? privateObjectInitialization;
        OwnedDuplexProcess? privateObjectWriter;
        HashSet<string>? baseObjectIds;
        LooseGitObjectInventory? privateInventory;
        TaskCompletionSource<bool>? privateObjectValidation;
        internal Task? PrivateObjectValidation => Volatile.Read(ref privateObjectValidation)?.Task;
        internal bool HasPrivateObjects { get; private set; }

        // Selected native route only. Ordinary provisioning, Windows adapters and
        // unbound repository utilities keep their existing object namespace.
        internal Task EnablePrivateObjectWritesAsync(CancellationToken token)
        {
            RequireRetained();
            return parent.EnterAsync(RunId, () =>
            {
                Phase(false);
                if (privateObjectInitialization is not null) throw new WorktreeBoundaryException("Private Git objects cannot be initialized again.");
                privateObjectInitialization = EnableCoreAsync(token);
                return privateObjectInitialization;
            });
        }

        async Task<bool> EnableCoreAsync(CancellationToken token)
        {
            await Task.Yield();
            RequireConfinedConformanceConfiguration(); Check(); token.ThrowIfCancellationRequested();
            if (CommonObjectsPath.IndexOfAny([Path.PathSeparator, '"', '\r', '\n', '\0']) >= 0)
                throw new WorktreeBoundaryException("Common object path cannot encode another alternate.");
            if (Path.Exists(Path.Combine(CommonObjectsPath, "info", "alternates")) ||
                Path.Exists(Path.Combine(CommonObjectsPath, "info", "http-alternates")))
                throw new WorktreeBoundaryException("Original common object namespace cannot route to another object authority.");
            // This invocation has the original COMMON-only environment. A private
            // filename must not shadow even an object stored inside a base pack.
            var text = await manager.GitAsync(Worktree, token, "cat-file", "--batch-all-objects", "--batch-check=%(objectname)", "--unordered").ConfigureAwait(false);
            // Reuse the actual native capture's existing16MiB bound. A packed
            // base may contain more OIDs than the review's filesystem-entry cap.
            if (text.Length > 16 * 1024 * 1024) throw new WorktreeBoundaryException("Original base object capture exceeds16MiB.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var oid = row.TrimEnd('\r');
                if (!LooseGitObjectInventory.IsOid(oid) || !ids.Add(oid))
                    throw new WorktreeBoundaryException("Original base object inventory is unsupported or duplicated.");
            }
            if (ids.Count == 0 || ids.Select(value => value.Length).Distinct().Count() != 1)
                throw new WorktreeBoundaryException("One nonempty original Git object format is required.");
            return parent.EnterSynchronous(RunId, () =>
            {
                Phase(false); VerifyMetadata(false); token.ThrowIfCancellationRequested();
                var path = Path.Combine(GitDirectory, "objects"); RejectSymlinkAncestors(path);
                if (Path.Exists(path)) throw new WorktreeBoundaryException("Private object destination already exists; no adoption or replay.");
                Directory.CreateDirectory(path);
                baseObjectIds = ids;
                privateInventory = LooseGitObjectInventory.Capture(path, ids, () => Phase(false), ReadPrivateObject);
                HasPrivateObjects = true;
                return true;
            });
        }

        internal void AttachPrivateObjectWriter(OwnedDuplexProcess actualProcess)
        {
            ArgumentNullException.ThrowIfNull(actualProcess);
            RequireRetained();
            if (!HasPrivateObjects || privateObjectInitialization is not { IsCompletedSuccessfully: true } ||
                privateObjectWriter is not null || !actualProcess.IsOwnedBy(original))
                throw new WorktreeBoundaryException("One actual same-original private-object writer is required.");
            // Retaining a physical failed birth is not forward effect admission.
            // Invalidate before any post-birth check can throw, including startup
            // failure/parent revocation; never leave the old empty view accepted.
            privateObjectWriter = actualProcess;
            privateInventory = null;
            if (actualProcess.StartupFailure is not null) return;
            parent.EnterSynchronous(RunId, () =>
            {
                Check();
                if (!actualProcess.HasRetainedOriginalOwner || actualProcess.OriginalRetirementTask is not null)
                    throw new WorktreeBoundaryException("One actual newly created private-object writer is required.");
                return true;
            });
        }

        void RequirePrivateWriterClosed()
        {
            if (privateObjectWriter is null || privateObjectWriter.OriginalRetirementTask is not { IsCompletedSuccessfully: true } actual ||
                !actual.Result.KnownClosed || actual.Result.ActiveProcesses != 0 || !actual.Result.OutputEof ||
                !actual.Result.ErrorEof || !actual.Result.InputCloseReturned || !actual.Result.DispositionReturned || privateObjectWriter.HasUnknownIo)
                throw new WorktreeBoundaryException("Actual original private-object writer is not known closed.");
        }

        internal Task ValidatePrivateObjectsAfterRetirementAsync(CancellationToken token, bool cleanup = false)
        {
            RequireRetained();
            Phase(cleanup); VerifyMetadata(cleanup); RequirePrivateWriterClosed();
            var prior = Volatile.Read(ref privateObjectValidation);
            if (prior is not null)
            {
                if (cleanup) return prior.Task;
                throw new WorktreeBoundaryException("Private object validation cannot replay its original attempt.");
            }
            if (!HasPrivateObjects || baseObjectIds is null || privateInventory is not null)
                throw new WorktreeBoundaryException("Private object validation cannot adopt or replay.");
            var attempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            prior = Interlocked.CompareExchange(ref privateObjectValidation, attempt, null);
            if (prior is not null)
            {
                if (cleanup) return prior.Task;
                throw new WorktreeBoundaryException("Private object validation cannot replay its original attempt.");
            }
            // Publish the joinable task BEFORE starting its body. There is no
            // started-without-task interval and no private gate/parent-gate order.
            _ = CompleteValidationAttemptAsync(attempt, token, cleanup);
            return attempt.Task;
        }

        async Task CompleteValidationAttemptAsync(TaskCompletionSource<bool> attempt, CancellationToken token, bool cleanup)
        {
            try
            {
                Task<bool> Begin()
                {
                    Phase(cleanup); VerifyMetadata(cleanup); RequirePrivateWriterClosed();
                    return ValidateCoreAsync(token, cleanup);
                }
                // Completion includes the original parent admission postcheck.
                // Cleanup retains the same original endpoint, with no forward grant.
                await (cleanup ? Begin() : parent.EnterAsync(RunId, Begin)).ConfigureAwait(false);
                attempt.TrySetResult(true);
            }
            catch (Exception failure) { attempt.TrySetException(failure); }
        }

        async Task<bool> ValidateCoreAsync(CancellationToken token, bool cleanup)
        {
            // Never execute native Git while the admission factory's gate is held.
            await Task.Yield();
            void CheckPhase() { Phase(cleanup); VerifyMetadata(cleanup); RequirePrivateWriterClosed(); }
            CheckPhase();
            var snapshot = await LooseGitObjectInventory.CaptureAsync(ObjectsPath, baseObjectIds!, CheckPhase,
                (path, maximum) => ReadPrivateObject(path, maximum, cleanup),
                (oid, type, maximum, cancellation) => ReadCommonObjectAsync(oid, type, maximum, cancellation, cleanup),
                token).ConfigureAwait(false);
            CheckPhase(); privateInventory = snapshot;
            return true;
        }

        internal bool IsValidatedBaseDuplicate(string oid)
        {
            RequireValidatedPrivateObjects();
            return privateInventory!.IsBaseDuplicate(oid);
        }

        internal string[] CaptureValidatedBaseDuplicateIds()
        {
            RequireValidatedPrivateObjects();
            return privateInventory!.BaseDuplicateIds();
        }

        async Task<byte[]> ReadCommonObjectAsync(string oid, string type, int maximum, CancellationToken token, bool cleanup)
        {
            if (baseObjectIds is null || !baseObjectIds.Contains(oid) || type is not ("blob" or "tree" or "commit" or "tag") || maximum is < 0 or > 16777216)
                throw new WorktreeBoundaryException("Only a captured base object may be compared with the private duplicate.");
            var executor = manager._originalExecutor ?? throw new WorktreeBoundaryException("Actual original Git executor is absent.");
            var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(row => !((string)row.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => (string)row.Key, row => row.Value?.ToString() ?? "", OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            environment["GIT_DIR"] = CommonDirectory; environment["GIT_COMMON_DIR"] = CommonDirectory;
            environment["GIT_OBJECT_DIRECTORY"] = CommonObjectsPath; environment["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "";
            environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = nullDevice; environment["GIT_CONFIG_SYSTEM"] = nullDevice;
            environment["GIT_NO_REPLACE_OBJECTS"] = "1"; environment["GIT_GRAFT_FILE"] = nullDevice;
            environment["GIT_NO_LAZY_FETCH"] = "1"; environment["GIT_TERMINAL_PROMPT"] = "0"; environment["GIT_OPTIONAL_LOCKS"] = "0";
            environment["LC_ALL"] = "C"; environment["LANG"] = "C";
            async Task<byte[]> Read(string argument, int bound)
            {
                void CheckPhase()
                {
                    Phase(cleanup); VerifyMetadata(cleanup); RequirePrivateWriterClosed(); RejectSymlinkAncestors(CommonObjectsPath);
                    if (Path.Exists(Path.Combine(CommonObjectsPath, "info", "alternates")) || Path.Exists(Path.Combine(CommonObjectsPath, "info", "http-alternates")))
                        throw new WorktreeBoundaryException("Common object comparison cannot route to another namespace.");
                }
                var invocation = new CommandInvocation(manager._gitExecutable,
                    ["--no-optional-locks", "-c", "core.hooksPath=" + nullDevice, "-c", "core.fsmonitor=false", "cat-file", argument, oid],
                    Worktree, environment)
                { EnvironmentIsComplete = true };
                Task<BoundedCommandCaptureResult> Birth()
                {
                    CheckPhase(); token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref manager.actualOwnedGitInvocations.Value);
                    IOwnedDuplexBudget phase = cleanup ? new OwnedCleanupPhaseBudget(original) : new OwnedWorkPhaseBudget(original);
                    return executor.ExecuteBoundedCaptureAsync(invocation, TimeSpan.FromSeconds(30), Math.Max(1, bound), token, phase);
                }
                var capture = await (cleanup ? Birth() : parent.EnterAsync(RunId, Birth)).ConfigureAwait(false);
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                var command = capture.Command; var facts = command.LifecycleDiagnostics;
                if (capture.CaptureBoundExceeded || capture.StandardErrorBytes.Length != 0 || command.ExitCode != 0 || command.TimedOut || command.Cancelled || !command.OwnedProcessQuiescent ||
                    !capture.RootExitObserved || !capture.StandardOutputDrainCompleted || !capture.StandardErrorDrainCompleted || facts?.NativeFailure is not null ||
                    facts?.OwnedTree is not { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 } ||
                    OperatingSystem.IsLinux() && capture.OwnershipKind != BoundedCaptureOwnershipKind.LinuxCreationOwnedScope)
                    throw new WorktreeBoundaryException("Original common-object comparison did not close successfully.");
                CheckPhase();
                if (capture.StandardOutputBytes.Length > bound) throw new WorktreeBoundaryException("Common object exceeds its remaining shared allowance.");
                return capture.StandardOutputBytes;
            }
            // Keep arbitrary object bodies as bytes. The first fixed query proves
            // the real common type; cat-file's typed form does not apply filters.
            var actualType = await Read("-t", 64).ConfigureAwait(false);
            if (!actualType.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(type + "\n")))
                throw new WorktreeBoundaryException("Private and common canonical object types differ.");
            return await Read(type, maximum).ConfigureAwait(false);
        }

        internal void RequireValidatedPrivateObjects() => RequireValidatedPrivateObjects(cleanup: false);

        internal void RequireValidatedPrivateObjects(bool cleanup)
        {
            if (!HasPrivateObjects) return;
            if (privateObjectWriter is not null) RequirePrivateWriterClosed();
            if (privateInventory is null) throw new WorktreeBoundaryException("Private Git objects have not passed original post-retirement validation.");
            privateInventory.Verify(() => Phase(cleanup));
        }

        byte[] ReadPrivateObject(string path, int maximum) => ReadPrivateObject(path, maximum, false);

        byte[] ReadPrivateObject(string path, int maximum, bool cleanup)
        {
            FileStream? file = null; byte[]? result = null; Exception? primary = null, close = null;
            try
            {
                Phase(cleanup); RejectSymlinkAncestors(path);
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (WriterNamespaceIdentityReader.Observe(file.SafeFileHandle, path).Outcome != WriterNamespaceIdentityOutcome.Match ||
                    !PrivateObjectLinks.HasOneLink(file.SafeFileHandle))
                    throw new WorktreeBoundaryException("Private Git object is not one original regular file.");
                var size = file.Length;
                if (size is < 1 || size > maximum) throw new WorktreeBoundaryException("Private Git object compressed extent exceeds its finite allowance.");
                result = new byte[checked((int)size)]; var offset = 0;
                while (offset < result.Length)
                {
                    Phase(cleanup); var count = file.Read(result, offset, Math.Min(65536, result.Length - offset)); Phase(cleanup);
                    if (count == 0) throw new WorktreeBoundaryException("Private Git object ended before its captured extent.");
                    offset += count;
                }
                Phase(cleanup);
                if (file.ReadByte() != -1 || file.Length != size ||
                    WriterNamespaceIdentityReader.Observe(file.SafeFileHandle, path).Outcome != WriterNamespaceIdentityOutcome.Match ||
                    !PrivateObjectLinks.HasOneLink(file.SafeFileHandle))
                    throw new WorktreeBoundaryException("Private Git object identity or extent changed during capture.");
                Phase(cleanup);
            }
            catch (Exception failure) { primary = failure; }
            if (file is not null)
            {
                var returned = false;
                try
                {
                    if (primary is null) Phase(cleanup); else _ = original.CleanupRemaining;
                    file.Dispose(); returned = true;
                    if (primary is null) Phase(cleanup); else _ = original.CleanupRemaining;
                }
                catch (Exception failure)
                {
                    close = failure;
                    if (!returned)
                    {
                        lock (Unsettled) Unsettled.Add((this, file)); Uncertain = true;
                        var failures = new List<Exception> { close };
                        try { if (original is IOwnedBudgetUnknownRetention retained) retained.PreserveUnknown(); } catch (Exception observation) { failures.Add(observation); }
                        try { parent.RetainUncertain(RunId, "Private Git object first disposition did not return."); } catch (Exception observation) { failures.Add(observation); }
                        if (failures.Count > 1) close = new AggregateException("Private Git object disposition/uncertainty observations retained.", failures);
                    }
                }
            }
            if (primary is not null && close is not null) throw new AggregateException("Private Git object capture and first disposition failed.", primary, close);
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
            if (close is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(close).Throw();
            return result ?? throw new IOException("Private Git object capture returned no bytes.");
        }
    }
}

// A finite loose-only Git encoding profile. These snapshots are DATA inside the
// retained binding, never authority to adopt a writer or another namespace.
internal sealed class LooseGitObjectInventory
{
    const int MaximumEntries = 4096, MaximumFileBytes = 16 * 1024 * 1024;
    const long MaximumBytes = 128L * 1024 * 1024;
    readonly string root;
    readonly Dictionary<string, Epoch> epochs;
    readonly HashSet<string> baseDuplicates = new(StringComparer.Ordinal);
    LooseGitObjectInventory(string root, Dictionary<string, Epoch> epochs) { this.root = root; this.epochs = epochs; }
    internal bool IsBaseDuplicate(string oid) => baseDuplicates.Contains(oid);
    internal string[] BaseDuplicateIds() => baseDuplicates.ToArray();
    internal static bool IsOid(string text) => text.Length is 40 or 64 && text.All(char.IsAsciiHexDigitLower);
    static bool Hex(string text) => text.All(char.IsAsciiHexDigitLower);

    internal static LooseGitObjectInventory Capture(string root, HashSet<string> protectedObjects, Action check, Func<string, int, byte[]> read)
    {
        var epochs = Inventory(root, check); long bytes = 0;
        var format = protectedObjects.First().Length;
        foreach (var (path, epoch) in epochs)
        {
            if (epoch.Directory) continue;
            check(); var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var oid = relative.Replace("/", "", StringComparison.Ordinal);
            if (oid.Length != format || protectedObjects.Contains(oid)) throw new WorktreeBoundaryException("Private Git object shadows the original base object namespace or format.");
            var available = Math.Min(MaximumFileBytes, MaximumBytes - bytes);
            if (epoch.Length > available) throw new WorktreeBoundaryException("Private Git objects exceed the existing128MiB allowance.");
            var encoded = read(path, checked((int)available)); bytes += encoded.Length;
            epoch.Verify(path); check();
            var inflated = ValidateObject(oid, encoded, checked((int)Math.Min(MaximumFileBytes, MaximumBytes - bytes)), check);
            bytes += inflated; check();
        }
        var result = new LooseGitObjectInventory(root, epochs); result.Verify(check); return result;
    }

    internal static async Task<LooseGitObjectInventory> CaptureAsync(string root, HashSet<string> protectedObjects,
        Action check, Func<string, int, byte[]> read,
        Func<string, string, int, CancellationToken, Task<byte[]>> readCommon, CancellationToken token)
    {
        var epochs = Inventory(root, check); long bytes = 0;
        var result = new LooseGitObjectInventory(root, epochs); var format = protectedObjects.First().Length;
        foreach (var (path, epoch) in epochs)
        {
            if (epoch.Directory) continue;
            check(); token.ThrowIfCancellationRequested();
            var oid = Path.GetRelativePath(root, path).Replace("/", "", StringComparison.Ordinal).Replace("\\", "", StringComparison.Ordinal);
            if (oid.Length != format) throw new WorktreeBoundaryException("Private object format differs from the original namespace.");
            var available = Math.Min(MaximumFileBytes, MaximumBytes - bytes);
            if (epoch.Length > available) throw new WorktreeBoundaryException("Private Git objects exceed the existing128MiB allowance.");
            var encoded = read(path, checked((int)available));
            if (encoded.Length > available) throw new WorktreeBoundaryException("Private object read exceeds its shared allowance.");
            bytes += encoded.Length; epoch.Verify(path); check();
            using var canonical = protectedObjects.Contains(oid) ? new MemoryStream() : null;
            bytes += ValidateObject(oid, encoded, checked((int)Math.Min(MaximumFileBytes, MaximumBytes - bytes)), check, canonical);
            if (canonical is not null)
            {
                var raw = canonical.ToArray(); var header = Array.IndexOf(raw, (byte)0) + 1;
                var type = Encoding.ASCII.GetString(raw, 0, Array.IndexOf(raw, (byte)' '));
                if (header > MaximumBytes - bytes) throw new WorktreeBoundaryException("Base comparison exceeds the existing shared allowance.");
                var maximum = checked((int)Math.Min(MaximumFileBytes - header, MaximumBytes - bytes - header));
                var common = await readCommon(oid, type, maximum, token).ConfigureAwait(false);
                check(); epoch.Verify(path);
                bytes += checked(header + common.Length);
                if (common.Length > maximum || bytes > MaximumBytes || !raw.AsSpan(header).SequenceEqual(common))
                    throw new WorktreeBoundaryException("Private base duplicate differs from the actual common canonical object.");
                result.baseDuplicates.Add(oid);
            }
            check();
        }
        result.Verify(check); return result;
    }

    internal static int ValidateObject(string oid, byte[] encoded, int maximumInflated, Action check, Stream? canonical = null)
    {
        if (!IsOid(oid) || encoded.Length is < 6 or > MaximumFileBytes || maximumInflated < 1)
            throw new WorktreeBoundaryException("Private loose object encoding exceeds its finite profile.");
        try
        {
            using var source = new MemoryStream(encoded, false);
            using var inflate = new ZLibStream(source, CompressionMode.Decompress);
            using var hash = IncrementalHash.CreateHash(oid.Length == 40 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
            var header = new List<byte>(64); long content = 0; var found = false; var total = 0;
            var buffer = new byte[65536]; uint a = 1, b = 0;
            while (true)
            {
                check(); var count = inflate.Read(buffer, 0, Math.Min(buffer.Length, maximumInflated - total + 1)); check();
                if (count == 0) break;
                if (count > maximumInflated - total) throw new WorktreeBoundaryException("Private Git object inflated extent exceeds its finite allowance.");
                total += count; hash.AppendData(buffer, 0, count); canonical?.Write(buffer, 0, count);
                foreach (var value in buffer.AsSpan(0, count))
                {
                    a = (a + value) % 65521; b = (b + a) % 65521;
                    if (found) { content++; continue; }
                    if (value == 0) { found = true; continue; }
                    if (header.Count >= 64 || value > 127) throw new WorktreeBoundaryException("Private Git object header is unsupported.");
                    header.Add(value);
                }
            }
            var fields = Encoding.ASCII.GetString(header.ToArray()).Split(' ');
            if (!found || fields.Length != 2 || fields[0] is not ("blob" or "tree" or "commit" or "tag") ||
                !long.TryParse(fields[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) ||
                size < 0 || size.ToString(System.Globalization.CultureInfo.InvariantCulture) != fields[1] || size != content ||
                Convert.ToHexStringLower(hash.GetHashAndReset()) != oid ||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(encoded.Length - 4)) != (b << 16 | a))
                throw new WorktreeBoundaryException("Private Git object canonical type, extent, checksum or object identity differs.");
            return total;
        }
        catch (InvalidDataException failure) { throw new WorktreeBoundaryException("Private loose object is not a complete zlib stream.", failure); }
    }

    internal void Verify(Action check)
    {
        var current = Inventory(root, check);
        if (current.Count != epochs.Count || current.Any(row => !epochs.TryGetValue(row.Key, out var prior) || row.Value != prior))
            throw new WorktreeBoundaryException("Private object namespace changed after original validation.");
        check();
    }

    static Dictionary<string, Epoch> Inventory(string root, Action check)
    {
        var result = new Dictionary<string, Epoch>(StringComparer.Ordinal) { [root] = Epoch.Read(root, true) };
        check();
        foreach (var fanout in Directory.EnumerateFileSystemEntries(root))
        {
            check(); var name = Path.GetFileName(fanout);
            if (name.Length != 2 || !Hex(name) || result.Count >= MaximumEntries)
                throw new WorktreeBoundaryException("Private object directory admits only bounded loose fanouts; packs/routing are unsupported.");
            result.Add(fanout, Epoch.Read(fanout, true));
            foreach (var file in Directory.EnumerateFileSystemEntries(fanout))
            {
                check(); var leaf = Path.GetFileName(file);
                if (leaf.Length is not (38 or 62) || !Hex(leaf) || result.Count >= MaximumEntries)
                    throw new WorktreeBoundaryException("Private object filename or entry count is unsupported.");
                result.Add(file, Epoch.Read(file, false));
            }
        }
        check(); return result;
    }

    internal sealed record Epoch(bool Directory, long Length, DateTime Created, DateTime Written, ulong Device, ulong Inode, long ChangedSeconds, uint ChangedNanoseconds)
    {
        internal static Epoch Read(string path, bool directory)
        {
            WorktreeManager.RejectSymlinkAncestors(path);
            FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path); info.Refresh();
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                ((info.Attributes & FileAttributes.Directory) != 0) != directory)
                throw new WorktreeBoundaryException("Private object inventory requires original plain entries.");
            var native = PrivateObjectLinks.Identity(path, directory);
            return new(directory, directory ? 0 : ((FileInfo)info).Length, info.CreationTimeUtc, info.LastWriteTimeUtc,
                native.Device, native.Inode, native.ChangedSeconds, native.ChangedNanoseconds);
        }
        internal void Verify(string path)
        {
            if (Read(path, Directory) != this) throw new WorktreeBoundaryException("Private object source epoch changed during validation.");
        }
    }
}
