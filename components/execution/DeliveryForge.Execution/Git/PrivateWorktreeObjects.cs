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

        internal void ValidatePrivateObjectsAfterRetirement()
        {
            parent.EnterSynchronous(RunId, () =>
            {
                Check();
                if (!HasPrivateObjects || baseObjectIds is null || privateInventory is not null)
                    throw new WorktreeBoundaryException("Private object validation cannot adopt or replay.");
                RequirePrivateWriterClosed();
                // No caller-supplied quiescence bool or retirement DATA can enter
                // this phase. The actual retained native owner is the boundary.
                privateInventory = LooseGitObjectInventory.Capture(ObjectsPath, baseObjectIds, () => Phase(false), ReadPrivateObject);
                RequirePrivateWriterClosed();
                return true;
            });
        }

        internal void RequireValidatedPrivateObjects()
        {
            if (!HasPrivateObjects) return;
            if (privateObjectWriter is not null) RequirePrivateWriterClosed();
            if (privateInventory is null) throw new WorktreeBoundaryException("Private Git objects have not passed original post-retirement validation.");
            privateInventory.Verify(() => Phase(false));
        }

        byte[] ReadPrivateObject(string path, int maximum)
        {
            FileStream? file = null; byte[]? result = null; Exception? primary = null, close = null;
            try
            {
                Phase(false); RejectSymlinkAncestors(path);
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (WriterNamespaceIdentityReader.Observe(file.SafeFileHandle, path).Outcome != WriterNamespaceIdentityOutcome.Match ||
                    !PrivateObjectLinks.HasOneLink(file.SafeFileHandle))
                    throw new WorktreeBoundaryException("Private Git object is not one original regular file.");
                var size = file.Length;
                if (size is < 1 || size > maximum) throw new WorktreeBoundaryException("Private Git object compressed extent exceeds its finite allowance.");
                result = new byte[checked((int)size)]; var offset = 0;
                while (offset < result.Length)
                {
                    Phase(false); var count = file.Read(result, offset, Math.Min(65536, result.Length - offset)); Phase(false);
                    if (count == 0) throw new WorktreeBoundaryException("Private Git object ended before its captured extent.");
                    offset += count;
                }
                Phase(false);
                if (file.ReadByte() != -1 || file.Length != size ||
                    WriterNamespaceIdentityReader.Observe(file.SafeFileHandle, path).Outcome != WriterNamespaceIdentityOutcome.Match ||
                    !PrivateObjectLinks.HasOneLink(file.SafeFileHandle))
                    throw new WorktreeBoundaryException("Private Git object identity or extent changed during capture.");
                Phase(false);
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
    LooseGitObjectInventory(string root, Dictionary<string, Epoch> epochs) { this.root = root; this.epochs = epochs; }
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

    internal static int ValidateObject(string oid, byte[] encoded, int maximumInflated, Action check)
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
                total += count; hash.AppendData(buffer, 0, count);
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
