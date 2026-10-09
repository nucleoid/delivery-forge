using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeliveryForge.Evidence;

public enum ExecutableSource
{
    TrustedExplicit,
    Path,
    RepositoryToolManifest
}

public sealed record CapabilityProbe(
    string Tool,
    ExecutableSource Source,
    string? ExplicitExecutable,
    string? RepositoryRoot,
    string? ManifestRelativePath,
    IReadOnlyList<string> VersionArguments,
    IReadOnlyList<string> HelpArguments,
    string RequiredVersionPrefix,
    string DocumentedFormatVersion,
    bool Fixture);

public sealed record CapabilityDetection(
    ToolCapability Capability,
    CommandInvocation? VersionInvocation,
    CommandResult? VersionResult,
    CommandInvocation? HelpInvocation,
    CommandResult? HelpResult);

public sealed partial class ToolCapabilityDetector(ICommandExecutor executor)
{
    public async Task<CapabilityDetection> DetectAsync(
        CapabilityProbe probe,
        CancellationToken cancellationToken = default)
    {
        var resolution = Resolve(probe);
        if (probe.Source == ExecutableSource.RepositoryToolManifest)
            return Unsupported(
                probe,
                "Repository tool-manifest execution is non-production until the exact entry package bytes are resolved and hashed.");

        if (resolution is null)
            return Unsupported(probe, "Executable was not found in the trusted configured source.");

        var (executable, prefixArguments) = resolution.Value;
        var executableIdentity = HashExecutable(executable);
        var workingDirectory = probe.RepositoryRoot is not null
            ? Path.GetFullPath(probe.RepositoryRoot)
            : Path.GetDirectoryName(executable)!;
        var versionInvocation = new CommandInvocation(
            executable, [.. prefixArguments, .. probe.VersionArguments], workingDirectory);
        var versionResult = await executor.ExecuteAsync(
            versionInvocation, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (versionResult.TimedOut || versionResult.Cancelled || versionResult.ExitCode != 0)
            return Unsupported(probe, "Version probe did not complete successfully.", versionInvocation, versionResult);

        var version = ParseVersion(versionResult.StandardOutput, versionResult.StandardError);
        if (version is null || !VersionMatches(version, probe.RequiredVersionPrefix))
            return Unsupported(probe, "Observed executable version is missing or outside the protected policy range.", versionInvocation, versionResult);

        var helpInvocation = new CommandInvocation(
            executable, [.. prefixArguments, .. probe.HelpArguments], workingDirectory);
        var helpResult = await executor.ExecuteAsync(
            helpInvocation, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (helpResult.TimedOut || helpResult.Cancelled || helpResult.ExitCode != 0 ||
            string.IsNullOrWhiteSpace(helpResult.StandardOutput + helpResult.StandardError))
            return Unsupported(
                probe, "Help/capability probe did not complete with documented output.",
                versionInvocation, versionResult, helpInvocation, helpResult);

        if (!string.Equals(executableIdentity, HashExecutable(executable), StringComparison.Ordinal))
            return Unsupported(
                probe, "Executable bytes changed during capability probing.",
                versionInvocation, versionResult, helpInvocation, helpResult);
        var capability = new ToolCapability(
            probe.Tool, version, probe.DocumentedFormatVersion, true, probe.Fixture, [],
            executable, executableIdentity, ["version", "help", "structured-report"]);
        return new CapabilityDetection(capability, versionInvocation, versionResult, helpInvocation, helpResult);
    }

    private static (string Executable, IReadOnlyList<string> PrefixArguments)? Resolve(CapabilityProbe probe)
    {
        return probe.Source switch
        {
            ExecutableSource.TrustedExplicit => ResolveExplicit(probe.ExplicitExecutable),
            ExecutableSource.Path => ResolvePath(probe.Tool),
            ExecutableSource.RepositoryToolManifest => ResolveManifest(probe),
            _ => null
        };
    }

    private static (string, IReadOnlyList<string>)? ResolveExplicit(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            return null;
        return (Path.GetFullPath(path), Array.Empty<string>());
    }

    private static (string, IReadOnlyList<string>)? ResolvePath(string tool)
    {
        if (Path.GetFileName(tool) != tool)
            return null;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory))
                continue;
            foreach (var name in OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool } : new[] { tool })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return (Path.GetFullPath(candidate), Array.Empty<string>());
            }
        }
        return null;
    }

    private static (string, IReadOnlyList<string>)? ResolveManifest(CapabilityProbe probe)
    {
        if (string.IsNullOrWhiteSpace(probe.RepositoryRoot) ||
            string.IsNullOrWhiteSpace(probe.ManifestRelativePath) ||
            Path.IsPathFullyQualified(probe.ManifestRelativePath))
            return null;
        var root = Path.GetFullPath(probe.RepositoryRoot);
        var manifest = Path.GetFullPath(Path.Combine(root, probe.ManifestRelativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!manifest.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(manifest))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            if (!document.RootElement.TryGetProperty("tools", out var tools) ||
                tools.ValueKind != JsonValueKind.Object ||
                !tools.EnumerateObject().Any(package =>
                    package.Value.TryGetProperty("commands", out var commands) &&
                    commands.ValueKind == JsonValueKind.Array &&
                    commands.EnumerateArray().Any(command =>
                        command.ValueKind == JsonValueKind.String &&
                        string.Equals(command.GetString(), probe.Tool, StringComparison.Ordinal))))
                return null;
        }
        catch (JsonException)
        {
            return null;
        }
        var dotnet = ResolvePath("dotnet");
        return dotnet is null ? null : (dotnet.Value.Item1, new[] { "tool", "run", probe.Tool, "--" });
    }

    private static string? ParseVersion(string stdout, string stderr)
    {
        var match = VersionPattern().Match(stdout + "\n" + stderr);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool VersionMatches(string observed, string required) =>
        string.Equals(observed, required, StringComparison.Ordinal) ||
        observed.StartsWith(required + ".", StringComparison.Ordinal) ||
        observed.StartsWith(required + "-", StringComparison.Ordinal);

    private static string HashExecutable(string executable) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable)))}";

    private static CapabilityDetection Unsupported(
        CapabilityProbe probe,
        string reason,
        CommandInvocation? versionInvocation = null,
        CommandResult? versionResult = null,
        CommandInvocation? helpInvocation = null,
        CommandResult? helpResult = null) =>
        new(
            new ToolCapability(
                probe.Tool, "unavailable", probe.DocumentedFormatVersion, false, probe.Fixture,
                [reason], null, null, []),
            versionInvocation, versionResult, helpInvocation, helpResult);

    [GeneratedRegex(@"(?m)^\s*(?:[A-Za-z][A-Za-z0-9.-]*\s+)?([0-9]+\.[0-9]+(?:\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?)?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
