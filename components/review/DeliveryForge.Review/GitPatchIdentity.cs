using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DeliveryForge.Contracts.State;

namespace DeliveryForge.Review;

internal delegate Task<GitProcessResult> OwnedReviewGit(string gitDirectory, IReadOnlyList<string> arguments,
    CancellationToken token, byte[]? input = null, IReadOnlyDictionary<string, string>? environment = null);

internal sealed record OwnedSourceTreeEntry(string Mode, string ObjectId);
internal delegate Task<byte[]> OwnedReviewSourceSnapshot(string headCommit, string headTree,
    IReadOnlyDictionary<string, OwnedSourceTreeEntry> tree, CancellationToken token);

public sealed class ControlledGitEvidenceContext : IDisposable
{
    private readonly Action? _dispose;
    private readonly Action? _disposeOwnedWork;
    private readonly Action<bool>? _checkDisposition;
    // 0 open, 1 WORK entered, 2 WORK returned, 3 cleanup claimed, 4 cleanup returned.
    private int _workDisposition;
    internal bool DispositionReturned => Volatile.Read(ref _workDisposition) is 2 or 4;
    private readonly HashSet<string> retainedScratch = new(StringComparer.Ordinal);
    public IReadOnlyList<string> RetainedScratchPaths { get { lock (retainedScratch) return retainedScratch.ToArray(); } }
    internal void RegisterScratch(params string[] paths)
    {
        lock (retainedScratch)
        {
            if (retainedScratch.Count + paths.Length > 128)
                throw new ReviewPreflightException("Finite original patch scratch profile exceeded; existing paths retained.");
            foreach (var path in paths) retainedScratch.Add(path);
        }
    }
    internal void ScratchRetired(string path) { lock (retainedScratch) retainedScratch.Remove(path); }

    internal ControlledGitEvidenceContext(
        string sourceRoot,
        string workerGitDirectory,
        string controlledGitDirectory,
        string trustedGitExecutable,
        string expectedGitVersion,
        long maximumEvidenceBytes,
        string sourceAuthorityIdentity,
        string frozenHeadCommit,
        string protectedObjectViewIdentity,
        ReviewAuthorityRealm sourceRealm,
        byte[] workerIndexHash,
        object quiescenceAuthority,
        Action? dispose = null,
        Action? beforeFinalRecheck = null,
        OwnedReviewGit? ownedGit = null,
        Func<bool>? mayCleanOwnedFiles = null,
        Action? checkOwnedWork = null,
        Action<string>? deleteOwnedScratch = null,
        Func<Func<object?>, object?>? ownedSynchronous = null,
        Func<string, int, CancellationToken, Task<byte[]>>? readOwnedFile = null,
        OwnedReviewSourceSnapshot? ownedSourceSnapshot = null,
        Action? disposeOwnedWork = null,
        Action<bool>? checkDisposition = null,
        Action<ControlledGitEvidenceContext, IReadOnlyList<string>>? retireOwnedIndexScratch = null)
    {
        SourceRoot = Path.GetFullPath(sourceRoot);
        WorkerGitDirectory = Path.GetFullPath(workerGitDirectory);
        ControlledGitDirectory = Path.GetFullPath(controlledGitDirectory);
        TrustedGitExecutable = Path.GetFullPath(trustedGitExecutable);
        ExpectedGitVersion = expectedGitVersion;
        MaximumEvidenceBytes = maximumEvidenceBytes;
        SourceAuthorityIdentity = sourceAuthorityIdentity;
        FrozenHeadCommit = frozenHeadCommit;
        ProtectedObjectViewIdentity = protectedObjectViewIdentity;
        SourceRealm = sourceRealm;
        WorkerIndexHash = workerIndexHash.ToArray();
        QuiescenceAuthority = quiescenceAuthority;
        _dispose = dispose;
        _disposeOwnedWork = disposeOwnedWork;
        _checkDisposition = checkDisposition;
        BeforeFinalRecheck = beforeFinalRecheck;
        OwnedGit = ownedGit;
        MayCleanOwnedFiles = mayCleanOwnedFiles;
        CheckOwnedWork = checkOwnedWork;
        DeleteOwnedScratch = deleteOwnedScratch;
        RetireOwnedIndexScratch = retireOwnedIndexScratch;
        OwnedSynchronous = ownedSynchronous;
        ReadOwnedFile = readOwnedFile;
        OwnedSourceSnapshot = ownedSourceSnapshot;
    }

    public string SourceRoot { get; }
    internal string WorkerGitDirectory { get; }
    public string ControlledGitDirectory { get; }
    public string TrustedGitExecutable { get; }
    public string ExpectedGitVersion { get; }
    public long MaximumEvidenceBytes { get; }
    public string SourceAuthorityIdentity { get; }
    public string FrozenHeadCommit { get; }
    public string ProtectedObjectViewIdentity { get; }
    public ReviewAuthorityRealm SourceRealm { get; }
    internal byte[] WorkerIndexHash { get; }
    internal object QuiescenceAuthority { get; }
    internal Action? BeforeFinalRecheck { get; }
    internal OwnedReviewGit? OwnedGit { get; }
    internal Func<bool>? MayCleanOwnedFiles { get; }
    internal Action? CheckOwnedWork { get; }
    internal Action<string>? DeleteOwnedScratch { get; }
    internal Action<ControlledGitEvidenceContext, IReadOnlyList<string>>? RetireOwnedIndexScratch { get; }
    internal Func<Func<object?>, object?>? OwnedSynchronous { get; }
    internal Func<string, int, CancellationToken, Task<byte[]>>? ReadOwnedFile { get; }
    internal OwnedReviewSourceSnapshot? OwnedSourceSnapshot { get; }
    internal T EnterOwnedSynchronous<T>(Func<T> invoke)
    {
        if (OwnedSynchronous is null) throw new ReviewPreflightException("Original source file admission is absent.");
        return (T)OwnedSynchronous(() => invoke())!;
    }

    internal void CheckDisposition(bool cleanup)
    {
        if (Volatile.Read(ref _workDisposition) != 0) return;
        if (SourceRealm == ReviewAuthorityRealm.Production && _checkDisposition is null)
            throw new ReviewPreflightException("Actual original view disposition preflight is absent.");
        _checkDisposition?.Invoke(cleanup);
    }

    public void Dispose()
    {
        // A failed reconstruction/retirement retains the actual controlled view
        // and exact scratch paths. Do not erase evidence through a using-finally.
        if (RetainedScratchPaths.Count != 0 || Volatile.Read(ref _workDisposition) != 0) return;
        CheckDisposition(cleanup: true);
        if (Interlocked.CompareExchange(ref _workDisposition, 3, 0) == 0)
        {
            if (_dispose is null) return;
            _dispose();
            Volatile.Write(ref _workDisposition, 4);
        }
    }
    internal void DisposeDuringWork()
    {
        if (Volatile.Read(ref _workDisposition) == 2) return;
        if (RetainedScratchPaths.Count != 0 || _disposeOwnedWork is null)
            throw new ReviewPreflightException("Original WORK view retirement is unavailable; exact view and scratch retained.");
        CheckDisposition(cleanup: false);
        var prior = Interlocked.CompareExchange(ref _workDisposition, 1, 0);
        if (prior == 2) return;
        if (prior != 0)
            throw new ReviewPreflightException("WORK cannot follow an already entered view retirement.");
        _disposeOwnedWork();
        Volatile.Write(ref _workDisposition, 2);
    }
}

public static partial class GitPatchIdentity
{
    private static readonly string[] DiffArguments =
    [
        "-c", "core.quotepath=false",
        "-c", "core.fsmonitor=false",
        "-c", $"core.hooksPath={NullDevice}",
        "-c", "diff.noprefix=false",
        "-c", "diff.mnemonicPrefix=false",
        "-c", "diff.relative=false",
        "-c", "diff.renames=true",
        "-c", "diff.algorithm=myers",
        "-c", $"diff.orderFile={NullDevice}",
        "diff", "--binary", "--full-index", "--no-color", "--no-ext-diff", "--no-textconv",
        "--ignore-submodules=none"
    ];

    private static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    public static Task<PatchIdentity> ComputeAsync(
        string repositoryRoot,
        string baseCommit,
        string headCommit,
        string planIdentity,
        IReadOnlyDictionary<string, string> artifactIdentities,
        CancellationToken cancellationToken = default) =>
        throw new ReviewPreflightException(
            "Controlled parent Git evidence is required; worker repository metadata is never an authority fallback.");

    public static async Task<byte[]> ExportVerifiedPatchAsync(ControlledGitEvidenceContext context, PatchIdentity expected, CancellationToken cancellationToken = default)
    {
        var current = await ComputeCoreAsync(context, expected.BaseCommit, expected.HeadCommit, expected.PlanIdentity, expected.ArtifactIdentities, cancellationToken).ConfigureAwait(false);
        if (!ReviewNormalizer.SameIdentity(current.Identity, expected)) throw new ReviewPreflightException("Current owned patch differs from the exact review request.");
        if ("sha256:" + Convert.ToHexStringLower(SHA256.HashData(current.Patch)) != expected.PatchSha256)
            throw new ReviewPreflightException("Complete exported patch differs from its bound identity.");
        cancellationToken.ThrowIfCancellationRequested();
        if (context.SourceRealm == ReviewAuthorityRealm.Production) context.CheckOwnedWork?.Invoke();
        return current.Patch;
    }

    public static async Task<PatchIdentity> ComputeAsync(
        ControlledGitEvidenceContext context,
        string baseCommit,
        string headCommit,
        string planIdentity,
        IReadOnlyDictionary<string, string> artifactIdentities,
        CancellationToken cancellationToken = default)
        => (await ComputeCoreAsync(context, baseCommit, headCommit, planIdentity, artifactIdentities, cancellationToken).ConfigureAwait(false)).Identity;

    // Return bytes from this invocation's actual diff only after reconstruction
    // and both full source checks. No cross-invocation snapshot is retained.
    private static async Task<(PatchIdentity Identity, byte[] Patch)> ComputeCoreAsync(
        ControlledGitEvidenceContext context, string baseCommit, string headCommit, string planIdentity,
        IReadOnlyDictionary<string, string> artifactIdentities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(artifactIdentities);
        if (!Directory.Exists(context.SourceRoot) ||
            !Directory.Exists(context.WorkerGitDirectory) ||
            !Directory.Exists(context.ControlledGitDirectory) ||
            !File.Exists(context.TrustedGitExecutable) ||
            context.MaximumEvidenceBytes <= 0 ||
            string.IsNullOrWhiteSpace(context.SourceAuthorityIdentity) ||
            string.IsNullOrWhiteSpace(context.ProtectedObjectViewIdentity) ||
            string.IsNullOrWhiteSpace(context.FrozenHeadCommit) ||
            context.QuiescenceAuthority is null ||
            !GitObjectPattern().IsMatch(baseCommit) ||
            !GitObjectPattern().IsMatch(headCommit) ||
            !IdentityPattern().IsMatch(planIdentity) ||
            artifactIdentities.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !IdentityPattern().IsMatch(pair.Value)))
            throw new ReviewPreflightException("Controlled Git evidence and supplied identities must be explicit and well formed.");

        await ValidateControlledMetadataAsync(context, cancellationToken).ConfigureAwait(false);
        var version = Text(await ControlledGit(context, ["--version"], cancellationToken));
        if (!string.Equals(version, context.ExpectedGitVersion, StringComparison.Ordinal))
            throw new ReviewPreflightException("Trusted Git version does not match the frozen host context.");

        // The shallow scalar and four fixed full-OID expressions share one query.
        // --verify accepts only one operand; require the complete ordered output
        // instead, including refusal of missing, extra or malformed scalar rows.
        var resolved = Text(await ControlledGit(context, ["rev-parse", "--is-shallow-repository",
            $"{baseCommit}^{{commit}}", $"{headCommit}^{{commit}}", $"{baseCommit}^{{tree}}", $"{headCommit}^{{tree}}"], cancellationToken))
            .Split('\n').Select(row => row.TrimEnd('\r')).ToArray();
        if (resolved.Length != 5 || resolved[0] != "false" || resolved.Skip(1).Any(row => !GitObjectPattern().IsMatch(row)))
            throw new ReviewPreflightException("Exact ordered controlled base/head commit and tree identities required.");
        var exactBase = resolved[1]; var exactHead = resolved[2];
        if (!string.Equals(exactBase, baseCommit, StringComparison.Ordinal) ||
            !string.Equals(exactHead, headCommit, StringComparison.Ordinal))
            throw new ReviewPreflightException("Base or head did not resolve to the exact supplied commit in controlled objects.");
        if (!string.Equals(headCommit, context.FrozenHeadCommit, StringComparison.Ordinal))
            throw new ReviewPreflightException("Requested head is not the parent-owned immutable source snapshot.");

        await ControlledGit(context, ["merge-base", "--is-ancestor", baseCommit, headCommit], cancellationToken);
        var baseTree = resolved[3]; var headTree = resolved[4];
        var before = await VerifySourceSnapshotAsync(context, headCommit, headTree, cancellationToken);

        var args = DiffArguments.Concat([baseCommit, headCommit, "--"]).ToArray();
        var patch = await ControlledGit(context, args, cancellationToken);
        await VerifyPatchReconstructsBothDirectionsAsync(
            context, patch.StandardOutput, baseCommit, headCommit, baseTree, headTree, cancellationToken);
        var patchHash = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(patch.StandardOutput))}";

        context.BeforeFinalRecheck?.Invoke();
        var after = await VerifySourceSnapshotAsync(context, headCommit, headTree, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(before, after))
            throw new ReviewPreflightException("Source changed while controlled review evidence was captured.");
        if (context.SourceRealm == ReviewAuthorityRealm.Production) context.CheckOwnedWork?.Invoke();

        string[] recorded =
        [
            context.TrustedGitExecutable,
            "--no-replace-objects",
            $"--git-dir={context.ControlledGitDirectory}",
            $"--work-tree={context.SourceRoot}",
            .. args
        ];
        return (ReviewNormalizer.Freeze(new PatchIdentity(
            baseCommit,
            headCommit,
            headTree,
            patchHash,
            planIdentity,
            artifactIdentities.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            version,
            recorded)), patch.StandardOutput);
    }

    private static async Task ValidateControlledMetadataAsync(ControlledGitEvidenceContext context, CancellationToken token)
    {
        var objects = Path.Combine(context.ControlledGitDirectory, "objects");
        bool InvalidObjects() => !Directory.Exists(objects) ||
            File.Exists(Path.Combine(objects, "info", "alternates")) ||
            File.Exists(Path.Combine(context.ControlledGitDirectory, "info", "grafts")) ||
            File.Exists(Path.Combine(context.ControlledGitDirectory, "shallow")) ||
            Directory.Exists(Path.Combine(context.ControlledGitDirectory, "refs", "replace"));
        if (context.SourceRealm == ReviewAuthorityRealm.Production ? context.EnterOwnedSynchronous(InvalidObjects) : InvalidObjects())
            throw new ReviewPreflightException("Controlled Git metadata contains incomplete or external object authority.");

        var configPath = Path.Combine(context.ControlledGitDirectory, "config");
        var config = context.SourceRealm == ReviewAuthorityRealm.Production
            ? new UTF8Encoding(false, true).GetString(await ReadOwnedBoundedAsync(context, configPath, 1048576, token).ConfigureAwait(false))
            : File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var forbidden = new[] { "promisor", "partialclone", "fsmonitor", "textconv", "[filter", "[diff ", "alternates" };
        if (forbidden.Any(value => config.Contains(value, StringComparison.OrdinalIgnoreCase)))
            throw new ReviewPreflightException("Controlled Git metadata contains executable or remote configuration.");
    }

    private static async Task<byte[]> VerifySourceSnapshotAsync(
        ControlledGitEvidenceContext context,
        string headCommit,
        string headTree,
        CancellationToken cancellationToken)
    {
        await VerifyWorkerHeadAndIndexAsync(context, cancellationToken);
        var listing = await ControlledGit(
            context,
            ["ls-tree", "-r", "-z", "--full-tree", headCommit],
            cancellationToken);
        var tracked = ParseTree(listing.StandardOutput, context.SourceRealm == ReviewAuthorityRealm.Production);
        if (context.SourceRealm == ReviewAuthorityRealm.Production)
        {
            if (tracked.Count > 4096 || context.OwnedSourceSnapshot is null)
                throw new ReviewPreflightException("Production source snapshot requires its finite original owner.");
            if (tracked.Values.Any(entry => entry.Type != "blob"))
                throw new ReviewPreflightException("Submodule working-state evidence is unsupported.");
            // Only the private original owner compares complete native archive
            // bytes with one bounded actual source census. Public DATA cannot
            // supply a snapshot digest or skip native HEAD/index verification.
            return await context.OwnedSourceSnapshot(headCommit, headTree,
                tracked.ToDictionary(pair => pair.Key, pair => new OwnedSourceTreeEntry(pair.Value.Mode, pair.Value.ObjectId), StringComparer.Ordinal),
                cancellationToken).ConfigureAwait(false);
        }
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateSourceEntries(context, cancellationToken))
        {
            var relative = Path.GetRelativePath(context.SourceRoot, file).Replace('\\', '/');
            if (!tracked.TryGetValue(relative, out var entry))
                throw new ReviewPreflightException($"Untracked source entry exists: {relative}.");
            seen.Add(relative);
            if (entry.Type == "commit")
                throw new ReviewPreflightException("Submodule working-state evidence is unsupported.");

            byte[] bytes;
            string actualMode;
            var info = new FileInfo(file);
            var linkTarget = context.SourceRealm == ReviewAuthorityRealm.Production ? context.EnterOwnedSynchronous(() => info.LinkTarget) : info.LinkTarget;
            if (!string.IsNullOrEmpty(linkTarget))
            {
                bytes = Encoding.UTF8.GetBytes(linkTarget);
                actualMode = "120000";
            }
            else
            {
                bytes = context.SourceRealm == ReviewAuthorityRealm.Production
                    ? await ReadOwnedBoundedAsync(context, file, context.MaximumEvidenceBytes, cancellationToken).ConfigureAwait(false)
                    : await File.ReadAllBytesAsync(file, cancellationToken);
                actualMode = OperatingSystem.IsWindows()
                    ? entry.Mode
                    : !(context.SourceRealm == ReviewAuthorityRealm.Production
                        ? context.EnterOwnedSynchronous(() => OperatingSystem.IsWindows()
                            ? throw new PlatformNotSupportedException("Unix file modes require a non-Windows source.")
                            : File.GetUnixFileMode(file))
                        : File.GetUnixFileMode(file)).HasFlag(UnixFileMode.UserExecute)
                        ? "100644"
                        : "100755";
            }
            if (bytes.LongLength > context.MaximumEvidenceBytes || !string.Equals(actualMode, entry.Mode, StringComparison.Ordinal))
                throw new ReviewPreflightException($"Source mode or size differs from controlled tree: {relative}.");
            var expected = await ControlledGit(context, ["cat-file", "blob", entry.ObjectId], cancellationToken);
            if (!bytes.AsSpan().SequenceEqual(expected.StandardOutput))
                throw new ReviewPreflightException($"Source bytes differ from controlled tree: {relative}.");
            AppendFingerprint(fingerprint, relative, actualMode, bytes);
        }

        if (tracked.Keys.Any(path => !seen.Contains(path)))
            throw new ReviewPreflightException("Tracked source entries are missing from the assigned source root.");
        return fingerprint.GetHashAndReset();
    }

    private static async Task VerifyWorkerHeadAndIndexAsync(
        ControlledGitEvidenceContext context,
        CancellationToken cancellationToken)
    {
        var currentHead = Text(await Git(
            context,
            context.WorkerGitDirectory,
            ["rev-parse", "--verify", "HEAD^{commit}"],
            cancellationToken));
        if (!string.Equals(currentHead, context.FrozenHeadCommit, StringComparison.Ordinal))
            throw new ReviewPreflightException("Worker HEAD changed after the parent-owned source snapshot was frozen.");

        var indexPath = Path.Combine(context.WorkerGitDirectory, "index");
        // The actual bounded open below refuses a missing index. A separate
        // positive File.Exists observation grants no authority to that open.
        byte[] indexBytes;
        try
        {
            indexBytes = context.SourceRealm == ReviewAuthorityRealm.Production
                ? await ReadOwnedBoundedAsync(context, indexPath, 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false)
                : await File.ReadAllBytesAsync(indexPath, cancellationToken);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ReviewPreflightException("Worker index is unavailable for source snapshot verification.", error);
        }
        var currentIndexHash = SHA256.HashData(indexBytes);
        if (!CryptographicOperations.FixedTimeEquals(currentIndexHash, context.WorkerIndexHash))
            throw new ReviewPreflightException("Worker index changed after the parent-owned source snapshot was frozen.");
    }

    private static async Task<byte[]> ReadOwnedBoundedAsync(ControlledGitEvidenceContext context, string path, long maximum, CancellationToken token)
    {
        if (context.CheckOwnedWork is null || context.ReadOwnedFile is null || maximum is < 1 or > 16777216)
            throw new ReviewPreflightException("Production source read requires its original finite owner.");
        context.CheckOwnedWork();
        var bytes = await context.ReadOwnedFile(path, checked((int)maximum), token).ConfigureAwait(false);
        context.CheckOwnedWork(); return bytes;
    }

    private static IEnumerable<string> EnumerateSourceEntries(ControlledGitEvidenceContext context, CancellationToken token)
    {
        var root = context.SourceRoot;
        var production = context.SourceRealm == ReviewAuthorityRealm.Production;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        var entries = 0;
        void Check()
        {
            if (!production) return;
            token.ThrowIfCancellationRequested();
            if (context.CheckOwnedWork is null) throw new ReviewPreflightException("Original source traversal owner is absent.");
            context.CheckOwnedWork();
        }
        while (stack.Count > 0)
        {
            Check();
            var (directory, depth) = stack.Pop();
            using var iterator = production
                ? context.EnterOwnedSynchronous(() => Directory.EnumerateFileSystemEntries(directory).GetEnumerator())
                : Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            while (production ? context.EnterOwnedSynchronous(iterator.MoveNext) : iterator.MoveNext())
            {
                Check();
                if (production && ++entries > 100000) throw new ReviewPreflightException("Owned source traversal exceeds100000 entries.");
                var entry = iterator.Current;
                if (string.Equals(entry, Path.Combine(root, ".git"), StringComparison.Ordinal))
                    continue;
                var attributes = production ? context.EnterOwnedSynchronous(() => File.GetAttributes(entry)) : File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    yield return entry;
                else if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (production && depth >= 64) throw new ReviewPreflightException("Owned source traversal exceeds64 directory levels.");
                    stack.Push((entry, depth + 1));
                }
                else
                    yield return entry;
            }
        }
    }

    private static Dictionary<string, TreeEntry> ParseTree(byte[] bytes, bool strictUtf8 = false)
    {
        var result = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)0, offset);
            if (end < 0) throw new ReviewPreflightException("Controlled Git tree listing is malformed.");
            var record = (strictUtf8 ? new UTF8Encoding(false, true) : Encoding.UTF8).GetString(bytes, offset, end - offset);
            var tab = record.IndexOf('\t');
            var metadata = record[..tab].Split(' ');
            result.Add(record[(tab + 1)..], new TreeEntry(metadata[0], metadata[1], metadata[2]));
            offset = end + 1;
        }
        return result;
    }

    private static void AppendFingerprint(IncrementalHash hash, string path, string mode, byte[] bytes)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(path));
        hash.AppendData([0]);
        hash.AppendData(Encoding.ASCII.GetBytes(mode));
        hash.AppendData([0]);
        hash.AppendData(SHA256.HashData(bytes));
    }

    internal static async Task VerifyPatchReconstructsBothDirectionsAsync(
        ControlledGitEvidenceContext context,
        byte[] patch,
        string baseCommit,
        string headCommit,
        string expectedBaseTree,
        string expectedHeadTree,
        CancellationToken cancellationToken)
    {
        var scratchRoot = context.SourceRealm == ReviewAuthorityRealm.Production ? context.ControlledGitDirectory : Path.GetTempPath();
        var reverseIndex = Path.Combine(scratchRoot, $"delivery-forge-review-index-{Guid.NewGuid():N}");
        var forwardIndex = Path.Combine(scratchRoot, $"delivery-forge-review-index-{Guid.NewGuid():N}");
        var paths = new[] { reverseIndex, reverseIndex + ".lock", forwardIndex, forwardIndex + ".lock" };
        if (context.SourceRealm == ReviewAuthorityRealm.Production) context.RegisterScratch(paths);
        var verified = false;
        Exception? primary = null;
        try
        {
            var reverseEnvironment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = reverseIndex };
            await ControlledGit(context, ["read-tree", headCommit], cancellationToken, environment: reverseEnvironment);
            if (patch.Length > 0)
                await ControlledGit(
                    context,
                    ["apply", "--cached", "--reverse", "--binary", "--whitespace=nowarn", "-"],
                    cancellationToken,
                    patch,
                    reverseEnvironment);
            var reconstructed = Text(await ControlledGit(
                context, ["write-tree"], cancellationToken, environment: reverseEnvironment));
            if (!string.Equals(reconstructed, expectedBaseTree, StringComparison.Ordinal))
                throw new ReviewPreflightException("Binary patch does not reconstruct the exact controlled base tree.");

            var forwardEnvironment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = forwardIndex };
            await ControlledGit(context, ["read-tree", baseCommit], cancellationToken, environment: forwardEnvironment);
            if (patch.Length > 0)
                await ControlledGit(
                    context,
                    ["apply", "--cached", "--binary", "--whitespace=nowarn", "-"],
                    cancellationToken,
                    patch,
                    forwardEnvironment);
            var forward = Text(await ControlledGit(
                context, ["write-tree"], cancellationToken, environment: forwardEnvironment));
            if (!string.Equals(forward, expectedHeadTree, StringComparison.Ordinal))
                throw new ReviewPreflightException("Binary patch does not reconstruct the exact controlled head tree.");
            verified = true;
        }
        catch (Exception failure)
        {
            primary = failure;
            if (context.SourceRealm == ReviewAuthorityRealm.Production)
                failure.Data["RetainedProductionReviewScratch"] = context.RetainedScratchPaths.ToArray();
            throw;
        }
        finally
        {
            try
            {
                if (context.SourceRealm == ReviewAuthorityRealm.Production)
                {
                    // Failed verification deliberately keeps its bounded evidence.
                    if (verified)
                    {
                        if (context.MayCleanOwnedFiles?.Invoke() != true ||
                            context.RetireOwnedIndexScratch is null && context.DeleteOwnedScratch is null)
                            throw new ReviewPreflightException("Original patch scratch remains retained; retirement is not authorized.");
                        if (context.RetireOwnedIndexScratch is { } retire)
                            retire(context, paths);
                        else
                            foreach (var path in paths)
                            {
                                context.DeleteOwnedScratch!(path);
                                context.ScratchRetired(path);
                            }
                    }
                }
                else
                    foreach (var path in paths) if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception retirement)
            {
                retirement.Data["RetainedProductionReviewScratch"] = context.RetainedScratchPaths.ToArray();
                if (primary is null) throw;
                primary.Data["PatchScratchRetirementFailure"] = retirement;
            }
        }
    }

    private static Task<GitProcessResult> ControlledGit(
        ControlledGitEvidenceContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        byte[]? input = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        Git(
            context,
            context.ControlledGitDirectory,
            args,
            cancellationToken,
            input,
            environment);

    private static async Task<GitProcessResult> Git(
        ControlledGitEvidenceContext context,
        string gitDirectory,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        byte[]? input = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (context.SourceRealm == ReviewAuthorityRealm.Production)
        {
            if (context.OwnedGit is null)
                throw new ReviewPreflightException("Production review requires its original owned Git backend.");
            return await context.OwnedGit(gitDirectory, args, cancellationToken, input, environment).ConfigureAwait(false);
        }
        var prefix = new[]
        {
            "--no-replace-objects",
            $"--git-dir={gitDirectory}",
            $"--work-tree={context.SourceRoot}",
            "-c", "core.fsmonitor=false",
            "-c", $"core.hooksPath={NullDevice}"
        };
        var full = prefix.Concat(args).ToArray();
        var result = await GitProcess.RunAsync(
            context.SourceRoot,
            full,
            cancellationToken,
            context.TrustedGitExecutable,
            context.MaximumEvidenceBytes,
            input,
            environment);
        if (result.ExitCode != 0)
            throw new ReviewPreflightException(
                $"Controlled Git evidence failed for '{string.Join(" ", full)}': {Encoding.UTF8.GetString(result.StandardError).Trim()}");
        return result;
    }

    private static string Text(GitProcessResult result) => Encoding.UTF8.GetString(result.StandardOutput).Trim();

    private sealed record TreeEntry(string Mode, string Type, string ObjectId);

    [GeneratedRegex("^[0-9a-f]{40}(?:[0-9a-f]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitObjectPattern();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();
}

internal sealed record GitProcessResult(int ExitCode, byte[] StandardOutput, byte[] StandardError);

internal interface IGitProcess : IDisposable
{
    Stream? StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill(bool entireProcessTree);
}

internal sealed record GitProcessRuntime(
    Func<ProcessStartInfo, IGitProcess> Start,
    TimeSpan CaptureTimeout,
    TimeSpan CleanupTimeout)
{
    public static GitProcessRuntime Production { get; } = new(
        start => new SystemGitProcess(
            Process.Start(start) ?? throw new ReviewPreflightException("Git process could not be started.")),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(2));
}

internal sealed class SystemGitProcess(Process process) : IGitProcess
{
    public Stream? StandardInput => process.StartInfo.RedirectStandardInput
        ? process.StandardInput.BaseStream
        : null;
    public Stream StandardOutput => process.StandardOutput.BaseStream;
    public Stream StandardError => process.StandardError.BaseStream;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);
    public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
    public void Dispose() => process.Dispose();
}

internal static class GitProcess
{
    public static async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string gitExecutable = "git",
        long maximumOutputBytes = long.MaxValue,
        byte[]? standardInput = null,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        GitProcessRuntime? runtime = null)
    {
        runtime ??= GitProcessRuntime.Production;
        var start = new ProcessStartInfo(gitExecutable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        if (extraEnvironment is not null)
            foreach (var pair in extraEnvironment) start.Environment[pair.Key] = pair.Value;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        IGitProcess process;
        try
        {
            process = runtime.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            throw new ReviewPreflightException("Git process could not be started.", error);
        }

        using (process)
        await using (var output = new MemoryStream())
        await using (var error = new MemoryStream())
        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            bounded.CancelAfter(runtime.CaptureTimeout);
            var outputTask = CopyBoundedAsync(process.StandardOutput, output, maximumOutputBytes, bounded.Token);
            var errorTask = CopyBoundedAsync(process.StandardError, error, maximumOutputBytes, bounded.Token);
            var inputTask = standardInput is null
                ? Task.CompletedTask
                : WriteInputAsync(process, standardInput, bounded.Token);
            var exitTask = process.WaitForExitAsync(bounded.Token);
            var captureTasks = new[] { exitTask, outputTask, errorTask, inputTask };
            var aggregate = Task.WhenAll(captureTasks);
            var deadline = Task.Delay(runtime.CaptureTimeout);
            var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                cancellationSignal);
            var pending = captureTasks.ToHashSet();
            Exception? cause = null;
            var deadlineTriggered = false;
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending.Append(deadline).Append(cancellationSignal.Task));
                if (ReferenceEquals(completed, deadline))
                {
                    deadlineTriggered = true;
                    cause = new OperationCanceledException("Git capture deadline elapsed.");
                    break;
                }
                if (ReferenceEquals(completed, cancellationSignal.Task))
                {
                    cause = new OperationCanceledException(cancellationToken);
                    break;
                }

                pending.Remove(completed);
                if (completed.IsFaulted)
                {
                    cause = completed.Exception?.GetBaseException() ??
                            new InvalidOperationException("Git capture task faulted without an exception.");
                    break;
                }
                if (completed.IsCanceled)
                {
                    cause = new OperationCanceledException(bounded.Token);
                    deadlineTriggered = !cancellationToken.IsCancellationRequested;
                    break;
                }
            }

            if (cause is null)
            {
                await aggregate;
                return new GitProcessResult(process.ExitCode, output.ToArray(), error.ToArray());
            }

            bounded.Cancel();
            Exception? killFailure = null;
            var exited = TryHasExited(process);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception killError) when (killError is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    killFailure = killError;
                }
            }
            var streamCloseFailed = !TryClose(process.StandardInput)
                | !TryClose(process.StandardOutput)
                | !TryClose(process.StandardError);
            var drainDeadline = Task.Delay(runtime.CleanupTimeout);
            var drainWinner = await Task.WhenAny(aggregate, drainDeadline);
            var drained = ReferenceEquals(drainWinner, aggregate);
            if (drained)
            {
                try
                {
                    await aggregate;
                }
                catch (Exception) { }
            }
            else
            {
                ObserveLateFault(aggregate);
            }
            exited = TryHasExited(process);
            var reason = cancellationToken.IsCancellationRequested
                ? "Git evidence capture was cancelled."
                : cause is IOException
                    ? "Git evidence exceeded the frozen output bound."
                    : deadlineTriggered
                        ? $"Git evidence exceeded the {runtime.CaptureTimeout.TotalSeconds:0}-second timeout."
                        : "Git evidence capture failed.";
            var residual = !exited || killFailure is not null || streamCloseFailed || !drained;
            if (residual) reason += " Residual process termination is uncertain.";
            throw new ReviewPreflightException(reason, cause, residual);
        }
    }

    private static void ObserveLateFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private static bool TryHasExited(IGitProcess process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    private static bool TryClose(Stream? stream)
    {
        if (stream is null) return true;
        try
        {
            stream.Close();
            return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task WriteInputAsync(IGitProcess process, byte[] input, CancellationToken cancellationToken)
    {
        var standardInput = process.StandardInput
            ?? throw new InvalidOperationException("Redirected standard input is unavailable.");
        await standardInput.WriteAsync(input, cancellationToken);
        await standardInput.FlushAsync(cancellationToken);
        standardInput.Close();
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            total += read;
            if (total > maximumBytes) throw new IOException("Output bound exceeded.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}

public sealed class ReviewPreflightException : Exception
{
    public ReviewPreflightException(
        string message,
        Exception? inner = null,
        bool residualProcessUncertain = false) : base(message, inner) =>
        ResidualProcessUncertain = residualProcessUncertain;

    public bool ResidualProcessUncertain { get; }
}
