using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DeliveryForge.Review;

public static partial class GitPatchIdentity
{
    private static readonly string[] DiffPrefix =
        ["-c", "core.quotepath=false", "diff", "--binary", "--full-index", "--no-color", "--no-ext-diff"];

    public static async Task<PatchIdentity> ComputeAsync(
        string repositoryRoot,
        string baseCommit,
        string headCommit,
        string planIdentity,
        IReadOnlyDictionary<string, string> artifactIdentities,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root) || !GitObjectPattern().IsMatch(baseCommit) ||
            !GitObjectPattern().IsMatch(headCommit) || !IdentityPattern().IsMatch(planIdentity) ||
            artifactIdentities.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                                           !IdentityPattern().IsMatch(pair.Value)))
            throw new ReviewPreflightException("Repository and supplied identities must be explicit and well formed.");

        var actualRoot = Text(await Git(root, ["rev-parse", "--show-toplevel"], cancellationToken));
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualRoot)),
                Path.TrimEndingDirectorySeparator(root),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ReviewPreflightException("Git repository root does not match the requested root.");

        var actualHead = Text(await Git(root, ["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken));
        if (!string.Equals(actualHead, headCommit, StringComparison.Ordinal))
            throw new ReviewPreflightException("Requested head is not the checked-out committed HEAD.");

        var status = await Git(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        if (status.StandardOutput.Length != 0)
            throw new ReviewPreflightException("Review requires a clean committed head; staged, dirty, or untracked bytes exist.");

        var exactBase = Text(await Git(root, ["rev-parse", "--verify", $"{baseCommit}^{{commit}}"], cancellationToken));
        var exactHead = Text(await Git(root, ["rev-parse", "--verify", $"{headCommit}^{{commit}}"], cancellationToken));
        if (!string.Equals(exactBase, baseCommit, StringComparison.Ordinal) ||
            !string.Equals(exactHead, headCommit, StringComparison.Ordinal))
            throw new ReviewPreflightException("Base or head did not resolve to the exact supplied commit.");

        await Git(root, ["merge-base", "--is-ancestor", baseCommit, headCommit], cancellationToken);
        var tree = Text(await Git(root, ["rev-parse", $"{headCommit}^{{tree}}"], cancellationToken));
        var version = Text(await Git(root, ["--version"], cancellationToken));
        var args = DiffPrefix.Concat([baseCommit, headCommit, "--"]).ToArray();
        var patch = await Git(root, args, cancellationToken);
        var patchHash = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(patch.StandardOutput))}";

        return new PatchIdentity(
            baseCommit, headCommit, tree, patchHash, planIdentity,
            artifactIdentities.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            version, ["git", .. args]);
    }

    private static async Task<GitProcessResult> Git(
        string root,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var result = await GitProcess.RunAsync(root, args, cancellationToken);
        if (result.ExitCode != 0)
            throw new ReviewPreflightException(
                $"Git preflight failed for 'git {string.Join(" ", args)}': {Encoding.UTF8.GetString(result.StandardError).Trim()}");
        return result;
    }

    private static string Text(GitProcessResult result) =>
        Encoding.UTF8.GetString(result.StandardOutput).Trim();

    [GeneratedRegex("^[0-9a-f]{40}(?:[0-9a-f]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitObjectPattern();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();
}

internal sealed record GitProcessResult(int ExitCode, byte[] StandardOutput, byte[] StandardError);

internal static class GitProcess
{
    public static async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new ReviewPreflightException("Git process could not be started.");
        await using var output = new MemoryStream();
        await using var error = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.BaseStream.CopyToAsync(error, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask);
        return new GitProcessResult(process.ExitCode, output.ToArray(), error.ToArray());
    }
}

public sealed class ReviewPreflightException(string message) : Exception(message);
