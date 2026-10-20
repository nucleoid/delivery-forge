using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Evidence;

public sealed class GitRepositoryIdentityReader(
    ICommandExecutor executor,
    string repositoryRoot,
    string protectedBaseCommit) : IDeadlineRepositoryIdentityReader, IRepositoryEvaluationScopeReader
{
    internal const int MaximumGitListingBytes = 64 * 1024 * 1024;
    internal const int MaximumSuppressionScanBytes = JavaScriptSuppressionScanner.MaximumInputBytes;
    internal const int MaximumFrozenInputCount = 100_000;
    internal const long MaximumFrozenInputBytes = 512L * 1024 * 1024;
    internal const int MaximumFrozenObjectBytes = 64 * 1024 * 1024;
    internal const int MaximumBatchPayloadBytes = 16 * 1024 * 1024;
    private readonly string _repositoryRoot = Path.GetFullPath(repositoryRoot);
    internal int ObjectBatchInvocationCount { get; private set; }
    internal bool SupportsCreationOwnedBoundedRead =>
        executor is ProcessCommandExecutor { SupportsCreationOwnedBoundedCapture: true };

    public async Task<RepositoryIdentity> ReadAsync(CancellationToken cancellationToken)
        => await ReadIdentityAsync(null, cancellationToken).ConfigureAwait(false);

    public async Task<RepositoryIdentity> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new OperationCanceledException("The finite repository identity deadline expired.");
        return await ReadIdentityAsync(DateTimeOffset.UtcNow + timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RepositoryIdentity> ReadIdentityAsync(
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(_repositoryRoot) ||
            !IsGitObject(protectedBaseCommit))
            throw new EvidenceRepositoryException("Repository root and a full protected base commit are required.");

        var head = await GitAsync(["rev-parse", "HEAD"], cancellationToken, deadline).ConfigureAwait(false);
        var resolvedBase = await GitAsync(
            ["rev-parse", "--verify", $"{protectedBaseCommit}^{{commit}}"], cancellationToken, deadline).ConfigureAwait(false);
        var headId = head.StandardOutput.Trim();
        var baseId = resolvedBase.StandardOutput.Trim();
        if (!string.Equals(baseId, protectedBaseCommit, StringComparison.Ordinal) || !IsGitObject(headId))
            throw new EvidenceRepositoryException("Git did not resolve the exact protected base/head identities.");
        await GitAsync(
            ["merge-base", "--is-ancestor", baseId, headId], cancellationToken, deadline).ConfigureAwait(false);
        var tree = await GitAsync(["rev-parse", "HEAD^{tree}"], cancellationToken, deadline).ConfigureAwait(false);
        var status = await GitAsync(
            ["status", "--porcelain=v1", "--untracked-files=all"], cancellationToken, deadline).ConfigureAwait(false);
        var treeId = tree.StandardOutput.Trim();
        if (!IsGitObject(treeId))
            throw new EvidenceRepositoryException("Git returned a malformed head or tree identity.");

        return new RepositoryIdentity(
            baseId,
            headId,
            treeId,
            string.IsNullOrEmpty(status.StandardOutput));
    }

    public async Task<RepositoryEvaluationScope> ReadEvaluationScopeAsync(
        string expectedHeadCommit,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new EvidenceRepositoryException("Evaluation scope requires a positive finite deadline.");
        var deadline = DateTimeOffset.UtcNow + timeout;
        if (!IsGitObject(expectedHeadCommit))
            throw new EvidenceRepositoryException("A full frozen head commit is required for evaluation scope.");
        var head = await GitAsync(["rev-parse", "HEAD"], cancellationToken, deadline).ConfigureAwait(false);
        if (!string.Equals(head.StandardOutput.Trim(), expectedHeadCommit, StringComparison.Ordinal))
            throw new EvidenceRepositoryException("Evaluation scope head does not match the frozen repository head.");

        var tree = await GitAsync(
            ["ls-tree", "-r", "-z", expectedHeadCommit],
            cancellationToken, deadline)
            .ConfigureAwait(false);
        var entries = tree.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseTreeEntry)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var files = entries.Select(entry => entry.Path).ToArray();
        var javascriptPaths = entries
            .Where(entry => Adapters.AntiSlopEvaluationPlugin.IsSupportedInput(entry.Path))
            .ToArray();
        var ignoreSelection = await ReadProtectedIgnoresAsync(entries, cancellationToken, deadline).ConfigureAwait(false);
        var ignores = ignoreSelection.Active;
        var invalidInputs = javascriptPaths
            .Where(entry => entry.Mode is not ("100644" or "100755") && !ignores.Contains(entry.Path))
            .Select(entry => entry.Path)
            .ToArray();
        var javascript = javascriptPaths
            .Where(entry => entry.Mode is "100644" or "100755")
            .ToArray();
        var changed = await GitAsync(
            ["diff", "--name-only", "-z", protectedBaseCommit, expectedHeadCommit, "--"],
            cancellationToken, deadline)
            .ConfigureAwait(false);
        var changedPaths = changed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> violations;
        string? materializedRoot;
        if (SupportsCreationOwnedBoundedRead)
        {
            var baseTree = await GitAsync(
                ["ls-tree", "-r", "-z", protectedBaseCommit],
                cancellationToken, deadline).ConfigureAwait(false);
            var baseEntries = baseTree.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseTreeEntry)
                .ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            (violations, materializedRoot) = await ReadAndMaterializeBatchedAsync(
                javascript.Where(entry => !ignores.Contains(entry.Path)).ToArray(),
                baseEntries, changedPaths, cancellationToken, deadline).ConfigureAwait(false);
        }
        else
        {
            violations = await ReadSuppressionFixtureObjectsAsync(
                javascript.Where(entry => changedPaths.Contains(entry.Path) && !ignores.Contains(entry.Path)).ToArray(),
                cancellationToken, deadline).ConfigureAwait(false);
            materializedRoot = null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in javascriptPaths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Path));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(entry.ObjectId));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Mode));
            hash.AppendData([0]);
            hash.AppendData([ignores.Contains(entry.Path) ? (byte)1 : (byte)0]);
        }
        foreach (var retired in ignoreSelection.Retired)
        {
            hash.AppendData(Encoding.UTF8.GetBytes("retired-ignore"));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(retired));
            hash.AppendData([0]);
        }
        return new RepositoryEvaluationScope(
            files,
            ignores,
            $"sha256:{Convert.ToHexStringLower(hash.GetHashAndReset())}",
            violations,
            invalidInputs,
            ignoreSelection.Retired,
            materializedRoot);
    }

    private static TreeEntry ParseTreeEntry(string value)
    {
        var first = value.IndexOf('\t');
        var second = first < 0 ? -1 : value.IndexOf('\t', first + 1);
        string mode;
        string objectId;
        string path;
        if (first > 0 && !value.AsSpan(0, first).Contains(' '))
        {
            // Deterministic fixture form retained for isolated executor tests.
            if (second <= first + 1 || second == value.Length - 1)
                throw new EvidenceRepositoryException("Git returned a malformed frozen tree entry.");
            mode = value[..first];
            objectId = value[(first + 1)..second];
            path = value[(second + 1)..];
        }
        else
        {
            if (first <= 0 || first == value.Length - 1)
                throw new EvidenceRepositoryException("Git returned a malformed frozen tree entry.");
            var metadata = value[..first].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 3 || metadata[1] is not ("blob" or "commit"))
                throw new EvidenceRepositoryException("Git returned a malformed frozen tree entry.");
            mode = metadata[0];
            objectId = metadata[2];
            path = value[(first + 1)..];
        }
        if (!IsGitObject(objectId) || string.IsNullOrWhiteSpace(path))
            throw new EvidenceRepositoryException("Git returned a malformed frozen tree object identity.");
        return new TreeEntry(mode, path, objectId);
    }

    internal static bool ContainsSuppressionDirective(string content) =>
        JavaScriptSuppressionScanner.Scan(Encoding.UTF8.GetBytes(content)).DirectiveCount > 0;

    private async Task<IReadOnlyList<string>> ReadSuppressionFixtureObjectsAsync(
        IReadOnlyList<TreeEntry> changedInputs,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        var violations = new List<string>();
        foreach (var entry in changedInputs)
        {
            var headBlob = await GitAsync(
                ["cat-file", "blob", "--end-of-options", entry.ObjectId], cancellationToken, deadline)
                .ConfigureAwait(false);
            var baseObject = await TryGitAsync(
                ["rev-parse", "--verify", $"{protectedBaseCommit}:{entry.Path}"], cancellationToken, deadline)
                .ConfigureAwait(false);
            var baseBlob = baseObject is null ? null : await GitAsync(
                ["cat-file", "blob", "--end-of-options", baseObject.StandardOutput.Trim()], cancellationToken, deadline)
                .ConfigureAwait(false);
            var headProfile = JavaScriptSuppressionScanner.Scan(
                Encoding.UTF8.GetBytes(headBlob.StandardOutput), cancellationToken, deadline);
            var baseProfile = baseBlob is null
                ? null
                : JavaScriptSuppressionScanner.Scan(
                    Encoding.UTF8.GetBytes(baseBlob.StandardOutput), cancellationToken, deadline);
            if (JavaScriptSuppressionScanner.IntroducesDangerousCoverage(headProfile, baseProfile))
                violations.Add(entry.Path);
        }
        return violations;
    }

    private async Task<(IReadOnlyList<string> Violations, string MaterializedRoot)> ReadAndMaterializeBatchedAsync(
        IReadOnlyList<TreeEntry> inputs,
        IReadOnlyDictionary<string, TreeEntry> baseEntries,
        IReadOnlySet<string> changedPaths,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        ValidateFrozenInputLimits(inputs.Count, 0);

        var consumers = new Dictionary<string, BatchObjectConsumers>(StringComparer.Ordinal);
        for (var inputIndex = 0; inputIndex < inputs.Count; inputIndex++)
        {
            CheckBatchBudget(inputIndex, cancellationToken, deadline);
            var entry = inputs[inputIndex];
            if (!consumers.TryGetValue(entry.ObjectId, out var objectConsumers))
                consumers.Add(entry.ObjectId, objectConsumers = new());
            objectConsumers.Head.Add(entry);
            if (changedPaths.Contains(entry.Path) && baseEntries.TryGetValue(entry.Path, out var baseEntry) &&
                baseEntry.Mode is "100644" or "100755")
            {
                if (!consumers.TryGetValue(baseEntry.ObjectId, out var baseConsumers))
                    consumers.Add(baseEntry.ObjectId, baseConsumers = new());
                baseConsumers.BasePaths.Add(entry.Path);
            }
        }

        var sizes = await ReadBatchObjectSizesAsync(
            consumers.Keys.Order(StringComparer.Ordinal).ToArray(), cancellationToken, deadline).ConfigureAwait(false);
        long materializedBytes = 0;
        for (var inputIndex = 0; inputIndex < inputs.Count; inputIndex++)
        {
            CheckBatchBudget(inputIndex, cancellationToken, deadline);
            materializedBytes = checked(materializedBytes + sizes[inputs[inputIndex].ObjectId]);
        }
        ValidateFrozenInputLimits(inputs.Count, materializedBytes);

        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-frozen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var headProfiles = new Dictionary<string, SuppressionProfile>(StringComparer.Ordinal);
        var baseProfiles = new Dictionary<string, SuppressionProfile>(StringComparer.Ordinal);
        var materializedPaths = new HashSet<string>(PathComparer);
        try
        {
            foreach (var batch in CreateObjectBatches(sizes, cancellationToken, deadline))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new OperationCanceledException("The finite frozen-object batch deadline expired.");
                var objects = await ReadBatchObjectsAsync(batch, sizes, cancellationToken, deadline).ConfigureAwait(false);
                var objectIndex = 0;
                foreach (var (objectId, bytes) in objects)
                {
                    CheckBatchBudget(objectIndex++, cancellationToken, deadline);
                    VerifyGitBlobIdentity(objectId, bytes);
                    var objectConsumers = consumers[objectId];
                    foreach (var entry in objectConsumers.Head)
                    {
                        if (changedPaths.Contains(entry.Path))
                            headProfiles[entry.Path] = JavaScriptSuppressionScanner.Scan(
                                bytes, cancellationToken, deadline);
                        await MaterializeFrozenObjectAsync(
                            root, entry, bytes, materializedPaths, cancellationToken).ConfigureAwait(false);
                    }
                    foreach (var path in objectConsumers.BasePaths)
                        baseProfiles[path] = JavaScriptSuppressionScanner.Scan(bytes, cancellationToken, deadline);
                }
            }

            var violations = inputs
                .Where(entry => changedPaths.Contains(entry.Path) &&
                    JavaScriptSuppressionScanner.IntroducesDangerousCoverage(
                        headProfiles[entry.Path], baseProfiles.GetValueOrDefault(entry.Path)))
                .Select(entry => entry.Path)
                .ToArray();
            return (violations, root);
        }
        catch
        {
            DeleteMaterialization(root);
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, int>> ReadBatchObjectSizesAsync(
        IReadOnlyList<string> objectIds,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        if (objectIds.Count == 0)
            return new Dictionary<string, int>(StringComparer.Ordinal);
        var result = await ExecuteGitBytesAsync(
            ["cat-file", "--batch-check"], BatchInput(objectIds), MaximumGitListingBytes,
            cancellationToken, deadline).ConfigureAwait(false);
        ObjectBatchInvocationCount++;
        var lines = Encoding.ASCII.GetString(result).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != objectIds.Count)
            throw new EvidenceRepositoryException("Git batch-check returned an incomplete object listing.");
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            CheckBatchBudget(index, cancellationToken, deadline);
            var fields = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || !string.Equals(fields[0], objectIds[index], StringComparison.Ordinal) ||
                fields[1] != "blob" || !int.TryParse(fields[2], out var size) ||
                size < 0 || size > MaximumFrozenObjectBytes)
                throw new EvidenceRepositoryException("Git batch-check returned a missing, malformed, non-blob, or oversized object.");
            sizes.Add(fields[0], size);
        }
        return sizes;
    }

    private static IReadOnlyList<IReadOnlyList<string>> CreateObjectBatches(
        IReadOnlyDictionary<string, int> sizes,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        var batches = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        long bytes = 0;
        var objectIndex = 0;
        foreach (var (objectId, size) in sizes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            CheckBatchBudget(objectIndex++, cancellationToken, deadline);
            var framed = (long)size + objectId.Length + 32;
            if (current.Count > 0 && bytes + framed > MaximumBatchPayloadBytes)
            {
                batches.Add(current.ToArray());
                current.Clear();
                bytes = 0;
            }
            current.Add(objectId);
            bytes += framed;
        }
        if (current.Count > 0)
            batches.Add(current.ToArray());
        return batches;
    }

    private async Task<IReadOnlyDictionary<string, byte[]>> ReadBatchObjectsAsync(
        IReadOnlyList<string> objectIds,
        IReadOnlyDictionary<string, int> expectedSizes,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        var maximumBytes = checked((int)Math.Min(
            128L * 1024 * 1024,
            objectIds.Sum(id => (long)expectedSizes[id] + id.Length + 32) + 1));
        var output = await ExecuteGitBytesAsync(
            ["cat-file", "--batch"], BatchInput(objectIds), maximumBytes,
            cancellationToken, deadline).ConfigureAwait(false);
        ObjectBatchInvocationCount++;
        return ParseBatchObjects(output, objectIds, expectedSizes, cancellationToken, deadline);
    }

    internal static IReadOnlyDictionary<string, byte[]> ParseBatchObjects(
        byte[] output,
        IReadOnlyList<string> objectIds,
        IReadOnlyDictionary<string, int> expectedSizes,
        CancellationToken cancellationToken = default,
        DateTimeOffset? deadline = null)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var cursor = 0;
        for (var objectIndex = 0; objectIndex < objectIds.Count; objectIndex++)
        {
            CheckBatchBudget(objectIndex, cancellationToken, deadline);
            var requested = objectIds[objectIndex];
            var newline = Array.IndexOf(output, (byte)'\n', cursor);
            if (newline < 0 || newline - cursor > 256)
                throw new EvidenceRepositoryException("Git object batch returned a malformed header.");
            var header = Encoding.ASCII.GetString(output, cursor, newline - cursor)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length != 3 || header[0] != requested || header[1] != "blob" ||
                !int.TryParse(header[2], out var size) || size != expectedSizes[requested])
                throw new EvidenceRepositoryException("Git object batch returned a missing or mismatched object.");
            cursor = newline + 1;
            if (size < 0 || cursor > output.Length - size || cursor + size >= output.Length ||
                output[cursor + size] != (byte)'\n')
                throw new EvidenceRepositoryException("Git object batch returned a truncated object body.");
            var bytes = new byte[size];
            Buffer.BlockCopy(output, cursor, bytes, 0, size);
            result.Add(requested, bytes);
            cursor += size + 1;
        }
        if (cursor != output.Length)
            throw new EvidenceRepositoryException("Git object batch returned unexpected trailing bytes.");
        return result;
    }

    private static void CheckBatchBudget(
        int iteration,
        CancellationToken cancellationToken,
        DateTimeOffset? deadline)
    {
        if ((iteration & 1023) != 0)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline is not null && DateTimeOffset.UtcNow >= deadline)
            throw new OperationCanceledException("The finite frozen-object batch deadline expired.");
    }

    internal static void ValidateFrozenInputLimits(int count, long bytes)
    {
        if (count < 0 || count > MaximumFrozenInputCount)
            throw new EvidenceRepositoryException(
                $"Frozen anti-slop scope exceeds the {MaximumFrozenInputCount}-input ceiling.");
        if (bytes < 0 || bytes > MaximumFrozenInputBytes)
            throw new EvidenceRepositoryException(
                $"Frozen anti-slop objects exceed the {MaximumFrozenInputBytes}-byte aggregate ceiling.");
    }

    private async Task<byte[]> ExecuteGitBytesAsync(
        IReadOnlyList<string> arguments,
        string standardInput,
        int maximumBytes,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = deadline - DateTimeOffset.UtcNow;
        if (budget <= TimeSpan.Zero)
            throw new OperationCanceledException("The finite frozen-object batch deadline expired.");
        if (budget > TimeSpan.FromSeconds(30))
            budget = TimeSpan.FromSeconds(30);
        if (executor is not ProcessCommandExecutor { SupportsCreationOwnedBoundedCapture: true } owned)
            throw new EvidenceRepositoryException("Production Git batching requires the code-owned bounded executor.");
        var capture = await owned.ExecuteBoundedCaptureAsync(
            new CommandInvocation(
                "git", arguments, _repositoryRoot,
                new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" },
                StandardInput: standardInput),
            budget, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (capture.CaptureBoundExceeded || capture.Command.ExitCode != 0 ||
            capture.Command.TimedOut || capture.Command.Cancelled || !capture.Command.OwnedProcessQuiescent ||
            !capture.RootExitObserved || !capture.StandardOutputDrainCompleted || !capture.StandardErrorDrainCompleted)
            throw new EvidenceRepositoryException(
                $"Git object batch failed, exceeded bounds, or did not settle: git {string.Join(" ", arguments)}");
        return capture.StandardOutputBytes;
    }

    private static async Task MaterializeFrozenObjectAsync(
        string root,
        TreeEntry entry,
        byte[] bytes,
        ISet<string> materializedPaths,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(Path.Combine(
            root, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) ||
            !materializedPaths.Add(destination))
            throw new EvidenceRepositoryException("Frozen input escaped or collided in the private materialization root.");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);
        File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
    }

    private static void VerifyGitBlobIdentity(string objectId, byte[] bytes)
    {
        using HashAlgorithm algorithm = objectId.Length == 64 ? SHA256.Create() : SHA1.Create();
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        algorithm.TransformBlock(header, 0, header.Length, null, 0);
        algorithm.TransformFinalBlock(bytes, 0, bytes.Length);
        if (!string.Equals(Convert.ToHexStringLower(algorithm.Hash!), objectId, StringComparison.Ordinal))
            throw new EvidenceRepositoryException("Git object batch bytes do not match the requested frozen object identity.");
    }

    private static string BatchInput(IEnumerable<string> objectIds) =>
        string.Join('\n', objectIds) + "\n";

    internal static void DeleteMaterialization(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private async Task<ProtectedIgnoreSelection> ReadProtectedIgnoresAsync(
        IReadOnlyList<TreeEntry> entries,
        CancellationToken cancellationToken,
        DateTimeOffset deadline)
    {
        const string path = ".delivery-forge/anti-slop-ignore";
        var result = await TryGitAsync(["show", $"{protectedBaseCommit}:{path}"], cancellationToken, deadline)
            .ConfigureAwait(false);
        if (result is null)
            return new(new HashSet<string>(StringComparer.Ordinal), []);
        var trackedJavascript = entries
            .Where(entry => Adapters.AntiSlopEvaluationPlugin.IsSupportedInput(entry.Path))
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.Ordinal);
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        var retired = new List<string>();
        foreach (var raw in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = raw.Trim().Replace('\\', '/');
            if (value.Length == 0 || value.StartsWith('#'))
                continue;
            var normalized = DeterministicEvaluationPluginRegistry.NormalizeInputs([value]).Single();
            if (trackedJavascript.Contains(normalized))
                ignored.Add(normalized);
            else
                retired.Add(normalized);
        }
        return new(ignored, retired.Order(StringComparer.Ordinal).ToArray());
    }

    private async Task<CommandResult> GitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? deadline = null)
    {
        var result = await ExecuteGitAsync(arguments, cancellationToken, deadline).ConfigureAwait(false);
        if (result.TimedOut || result.Cancelled || result.ExitCode != 0)
            throw new EvidenceRepositoryException(
                $"Git identity command failed: git {string.Join(" ", arguments)}");
        return result;
    }

    private async Task<CommandResult?> TryGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? deadline = null)
    {
        var result = await ExecuteGitAsync(arguments, cancellationToken, deadline).ConfigureAwait(false);
        if (result.TimedOut || result.Cancelled)
            throw new EvidenceRepositoryException($"Frozen Git blob read did not settle: git {string.Join(" ", arguments)}");
        return result.ExitCode == 0 ? result : null;
    }

    private async Task<CommandResult> ExecuteGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? deadline = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = deadline is null
            ? TimeSpan.FromSeconds(30)
            : deadline.Value - DateTimeOffset.UtcNow;
        if (budget <= TimeSpan.Zero)
            throw new EvidenceRepositoryException("The finite evaluation-scope deadline expired.");
        if (budget > TimeSpan.FromSeconds(30))
            budget = TimeSpan.FromSeconds(30);
        var invocation = new CommandInvocation(
            "git", arguments, _repositoryRoot,
            new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" });
        if (executor is ProcessCommandExecutor { SupportsCreationOwnedBoundedCapture: true } owned)
        {
            var capture = await owned.ExecuteBoundedCaptureAsync(
                invocation, budget, MaximumGitListingBytes, cancellationToken).ConfigureAwait(false);
            if (capture.CaptureBoundExceeded)
                throw new EvidenceRepositoryException("Frozen Git evidence exceeded the bounded capture limit.");
            return capture.Command;
        }
        if (executor is IBoundedGateFixtureExecutor { SupportsCreationOwnedBoundedCapture: true } fixture)
        {
            var capture = await fixture.ExecuteBoundedCaptureAsync(
                invocation, budget, MaximumGitListingBytes, cancellationToken).ConfigureAwait(false);
            if (capture.CaptureBoundExceeded)
                throw new EvidenceRepositoryException("Frozen Git evidence exceeded the bounded capture limit.");
            return capture.Command;
        }
        return await executor.ExecuteAsync(invocation, budget, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsGitObject(string value) =>
        value.Length is 40 or 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed record TreeEntry(string Mode, string Path, string ObjectId);
internal sealed record ProtectedIgnoreSelection(HashSet<string> Active, IReadOnlyList<string> Retired);
internal sealed class BatchObjectConsumers
{
    internal List<TreeEntry> Head { get; } = [];
    internal List<string> BasePaths { get; } = [];
}

public sealed class EvidenceRepositoryException(string message) : Exception(message);
