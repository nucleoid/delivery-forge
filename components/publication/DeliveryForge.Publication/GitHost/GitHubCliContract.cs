using System.Collections.Concurrent;
using DeliveryForge.Contracts.Models;

namespace DeliveryForge.Publication.GitHost;

public sealed record GitHubReadConfiguration(string ExecutablePath, string ExecutableIdentity, string WorkingDirectory,
    string ExpectedVersion, TimeSpan ReadTimeout, TimeSpan ActionTimeout, TimeSpan CleanupTimeout);
public sealed record GitHubCliCapability(bool Supported, string Version, string Reason);
public sealed record GitHubCliResult(int? ExitCode, string StandardOutput, string StandardError,
    bool TimedOut = false, bool ResidualProcessUncertain = false);
public interface IGitHubCliExecutor
{
    Task<GitHubCliResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>Trusted parent composition for an explicitly approved disposable integration target only.</summary>
public sealed class ParentDisposableGitHubAuthority
{
    private readonly ConcurrentDictionary<ParentDisposableGitHubGrant, byte> grants = new();
    private ParentDisposableGitHubAuthority(string reference, PublicationTarget target, AuthorizationCeiling ceiling, DateTimeOffset validUntil)
    {
        AuthorizationReference = reference;
        Target = target;
        Ceiling = ceiling;
        ValidUntil = validUntil;
    }
    internal string AuthorizationReference { get; }
    internal PublicationTarget Target { get; }
    internal AuthorizationCeiling Ceiling { get; }
    internal DateTimeOffset ValidUntil { get; }
    public static ParentDisposableGitHubAuthority CreateForExplicitUserAuthorization(string authorizationReference,
        PublicationTarget target, string disposableRepositoryPrefix, AuthorizationCeiling ceiling, DateTimeOffset validUntil)
    {
        var parts = target.Repository.Split('/');
        if (string.IsNullOrWhiteSpace(authorizationReference) || string.IsNullOrWhiteSpace(disposableRepositoryPrefix) ||
            parts.Length != 2 || !parts[1].StartsWith(disposableRepositoryPrefix, StringComparison.Ordinal) ||
            ceiling is not (AuthorizationCeiling.Pr or AuthorizationCeiling.Merge) || validUntil <= DateTimeOffset.UtcNow)
            throw new ArgumentException("An explicit unexpired parent user grant for a named disposable repository is required.");
        return new(authorizationReference, target, ceiling, validUntil);
    }
    public ParentDisposableGitHubGrant IssueGrant()
    {
        var grant = new ParentDisposableGitHubGrant(this);
        grants.TryAdd(grant, 0);
        return grant;
    }
    public void Revoke(ParentDisposableGitHubGrant grant) => grants.TryRemove(grant, out _);
    internal bool Owns(ParentDisposableGitHubGrant grant) => ReferenceEquals(grant.Issuer, this) &&
        grants.ContainsKey(grant) && DateTimeOffset.UtcNow <= ValidUntil;
}

public sealed class ParentDisposableGitHubGrant
{
    internal ParentDisposableGitHubGrant(ParentDisposableGitHubAuthority issuer) => Issuer = issuer;
    internal ParentDisposableGitHubAuthority Issuer { get; }
}
