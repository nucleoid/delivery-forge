using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeliveryForge.Review;

public sealed class ControlledGitEvidenceContext : IDisposable
{
    private readonly Action? _dispose;

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
        object quiescenceAuthority,
        Action? dispose = null,
        Action? beforeFinalRecheck = null)
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
        QuiescenceAuthority = quiescenceAuthority;
        _dispose = dispose;
        BeforeFinalRecheck = beforeFinalRecheck;
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
    internal object QuiescenceAuthority { get; }
    internal Action? BeforeFinalRecheck { get; }

    public void Dispose() => _dispose?.Invoke();
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

    public static async Task<PatchIdentity> ComputeAsync(
        ControlledGitEvidenceContext context,
        string baseCommit,
        string headCommit,
        string planIdentity,
        IReadOnlyDictionary<string, string> artifactIdentities,
        CancellationToken cancellationToken = default)
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

        ValidateControlledMetadata(context);
        var version = Text(await ControlledGit(context, ["--version"], cancellationToken));
        if (!string.Equals(version, context.ExpectedGitVersion, StringComparison.Ordinal))
            throw new ReviewPreflightException("Trusted Git version does not match the frozen host context.");

        var shallow = Text(await ControlledGit(context, ["rev-parse", "--is-shallow-repository"], cancellationToken));
        if (!string.Equals(shallow, "false", StringComparison.Ordinal))
            throw new ReviewPreflightException("Shallow controlled object evidence is incomplete.");

        var exactBase = Text(await ControlledGit(context, ["rev-parse", "--verify", $"{baseCommit}^{{commit}}"], cancellationToken));
        var exactHead = Text(await ControlledGit(context, ["rev-parse", "--verify", $"{headCommit}^{{commit}}"], cancellationToken));
        if (!string.Equals(exactBase, baseCommit, StringComparison.Ordinal) ||
            !string.Equals(exactHead, headCommit, StringComparison.Ordinal))
            throw new ReviewPreflightException("Base or head did not resolve to the exact supplied commit in controlled objects.");
        if (!string.Equals(headCommit, context.FrozenHeadCommit, StringComparison.Ordinal))
            throw new ReviewPreflightException("Requested head is not the parent-owned immutable source snapshot.");

        await ControlledGit(context, ["merge-base", "--is-ancestor", baseCommit, headCommit], cancellationToken);
        var baseTree = Text(await ControlledGit(context, ["rev-parse", $"{baseCommit}^{{tree}}"], cancellationToken));
        var headTree = Text(await ControlledGit(context, ["rev-parse", $"{headCommit}^{{tree}}"], cancellationToken));
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

        string[] recorded =
        [
            context.TrustedGitExecutable,
            "--no-replace-objects",
            $"--git-dir={context.ControlledGitDirectory}",
            $"--work-tree={context.SourceRoot}",
            .. args
        ];
        return new PatchIdentity(
            baseCommit,
            headCommit,
            headTree,
            patchHash,
            planIdentity,
            artifactIdentities.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            version,
            recorded);
    }

    private static void ValidateControlledMetadata(ControlledGitEvidenceContext context)
    {
        var objects = Path.Combine(context.ControlledGitDirectory, "objects");
        if (!Directory.Exists(objects) ||
            File.Exists(Path.Combine(objects, "info", "alternates")) ||
            File.Exists(Path.Combine(context.ControlledGitDirectory, "info", "grafts")) ||
            File.Exists(Path.Combine(context.ControlledGitDirectory, "shallow")) ||
            Directory.Exists(Path.Combine(context.ControlledGitDirectory, "refs", "replace")))
            throw new ReviewPreflightException("Controlled Git metadata contains incomplete or external object authority.");

        var configPath = Path.Combine(context.ControlledGitDirectory, "config");
        var config = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
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
        var listing = await ControlledGit(
            context,
            ["ls-tree", "-r", "-z", "--full-tree", headCommit],
            cancellationToken);
        var tracked = ParseTree(listing.StandardOutput);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateSourceEntries(context.SourceRoot))
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
            if (!string.IsNullOrEmpty(info.LinkTarget))
            {
                bytes = Encoding.UTF8.GetBytes(info.LinkTarget);
                actualMode = "120000";
            }
            else
            {
                bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                actualMode = OperatingSystem.IsWindows()
                    ? entry.Mode
                    : !File.GetUnixFileMode(file).HasFlag(UnixFileMode.UserExecute)
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

    private static IEnumerable<string> EnumerateSourceEntries(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (string.Equals(entry, Path.Combine(root, ".git"), StringComparison.Ordinal))
                    continue;
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    yield return entry;
                else if (attributes.HasFlag(FileAttributes.Directory))
                    stack.Push(entry);
                else
                    yield return entry;
            }
        }
    }

    private static Dictionary<string, TreeEntry> ParseTree(byte[] bytes)
    {
        var result = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)0, offset);
            if (end < 0) throw new ReviewPreflightException("Controlled Git tree listing is malformed.");
            var record = Encoding.UTF8.GetString(bytes, offset, end - offset);
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

    private static async Task VerifyPatchReconstructsBothDirectionsAsync(
        ControlledGitEvidenceContext context,
        byte[] patch,
        string baseCommit,
        string headCommit,
        string expectedBaseTree,
        string expectedHeadTree,
        CancellationToken cancellationToken)
    {
        var reverseIndex = Path.Combine(Path.GetTempPath(), $"delivery-forge-review-index-{Guid.NewGuid():N}");
        var forwardIndex = Path.Combine(Path.GetTempPath(), $"delivery-forge-review-index-{Guid.NewGuid():N}");
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
        }
        finally
        {
            foreach (var indexPath in new[] { reverseIndex, forwardIndex })
            {
                if (File.Exists(indexPath)) File.Delete(indexPath);
                if (File.Exists(indexPath + ".lock")) File.Delete(indexPath + ".lock");
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

internal static class GitProcess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string gitExecutable = "git",
        long maximumOutputBytes = long.MaxValue,
        byte[]? standardInput = null,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
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

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new ReviewPreflightException("Git process could not be started.");
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
            bounded.CancelAfter(Timeout);
            var outputTask = CopyBoundedAsync(process.StandardOutput.BaseStream, output, maximumOutputBytes, bounded.Token);
            var errorTask = CopyBoundedAsync(process.StandardError.BaseStream, error, maximumOutputBytes, bounded.Token);
            var inputTask = standardInput is null
                ? Task.CompletedTask
                : WriteInputAsync(process, standardInput, bounded.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(bounded.Token), outputTask, errorTask, inputTask);
            }
            catch (Exception cause) when (cause is OperationCanceledException or IOException)
            {
                Exception? killFailure = null;
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception killError) when (killError is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                        killFailure = killError;
                    }
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var exited = false;
                try
                {
                    await process.WaitForExitAsync(cleanup.Token);
                    exited = true;
                }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { exited = process.HasExited; }
                try
                {
                    await Task.WhenAll(outputTask, errorTask, inputTask).WaitAsync(cleanup.Token);
                }
                catch (Exception) when (!cleanup.IsCancellationRequested) { }
                catch (OperationCanceledException) { }
                var reason = cancellationToken.IsCancellationRequested
                    ? "Git evidence capture was cancelled."
                    : cause is IOException
                        ? "Git evidence exceeded the frozen output bound."
                        : $"Git evidence exceeded the {Timeout.TotalSeconds:0}-second timeout.";
                var residual = !exited || killFailure is not null;
                if (residual) reason += " Residual process termination is uncertain.";
                throw new ReviewPreflightException(reason, cause, residual);
            }
            return new GitProcessResult(process.ExitCode, output.ToArray(), error.ToArray());
        }
    }

    private static async Task WriteInputAsync(Process process, byte[] input, CancellationToken cancellationToken)
    {
        await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        process.StandardInput.Close();
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
