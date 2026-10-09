using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DeliveryForge.Review;

public enum ReviewPolicyAuthorityKind
{
    ProtectedGitBase,
    ParentAdmin,
    WorkerBranch,
    Caller
}

public sealed record ReviewPolicyAuthority(
    ReviewPolicyAuthorityKind Kind,
    string SourceRevision,
    string ContentIdentity);

public sealed record FamilyPair(string Author, string Reviewer);

public sealed record ReviewPolicyCandidate(
    string PolicyIdentity,
    string ContentIdentity,
    string SourceRevision,
    bool Fixture,
    bool Advisory,
    IReadOnlyList<FamilyPair> AllowedFamilies,
    IReadOnlyList<string> AllowedScopes,
    bool AllowExactReuse,
    ReviewBudget Budget,
    IReadOnlyList<string> ExemptionApprovers);

public sealed partial class ReviewPolicy
{
    private ReviewPolicy(
        string policyIdentity,
        string contentIdentity,
        string sourceRevision,
        ReviewPolicyAuthorityKind authorityKind,
        bool trusted,
        bool fixture,
        bool advisory,
        IEnumerable<FamilyPair> allowedFamilies,
        IEnumerable<string> allowedScopes,
        bool allowExactReuse,
        ReviewBudget budget,
        IEnumerable<string> exemptionApprovers)
    {
        PolicyIdentity = policyIdentity;
        ContentIdentity = contentIdentity;
        SourceRevision = sourceRevision;
        AuthorityKind = authorityKind;
        Trusted = trusted;
        Fixture = fixture;
        Advisory = advisory;
        AllowedFamilies = Array.AsReadOnly(allowedFamilies.ToArray());
        AllowedScopes = Array.AsReadOnly(allowedScopes.ToArray());
        AllowExactReuse = allowExactReuse;
        Budget = budget;
        ExemptionApprovers = Array.AsReadOnly(exemptionApprovers.ToArray());
    }

    public static ReviewPolicy Missing { get; } = new(
        string.Empty, string.Empty, string.Empty, ReviewPolicyAuthorityKind.Caller,
        false, false, false, [], [], false, ReviewBudget.None, []);

    public string PolicyIdentity { get; }
    public string ContentIdentity { get; }
    public string SourceRevision { get; }
    public ReviewPolicyAuthorityKind AuthorityKind { get; }
    public bool Trusted { get; }
    public bool Fixture { get; }
    public bool Advisory { get; }
    public IReadOnlyList<FamilyPair> AllowedFamilies { get; }
    public IReadOnlyList<string> AllowedScopes { get; }
    public bool AllowExactReuse { get; }
    public ReviewBudget Budget { get; }
    public IReadOnlyList<string> ExemptionApprovers { get; }

    public static ReviewPolicy Resolve(ReviewPolicyCandidate candidate, ReviewPolicyAuthority authority)
    {
        Validate(candidate);
        // A caller-created authority record is descriptive only. As in #7, verified authority is
        // issued by a resolver that reads the exact protected source, never by this raw factory.
        return Create(candidate, authority.Kind, trusted: false);
    }

    internal static ReviewPolicy TrustedFixture(
        string policyIdentity,
        IReadOnlyList<FamilyPair> allowedFamilies,
        IReadOnlyList<string> allowedScopes,
        bool allowExactReuse,
        ReviewBudget budget)
    {
        var candidate = new ReviewPolicyCandidate(
            policyIdentity, Identity('f'), new string('f', 40), true, false,
            allowedFamilies, allowedScopes, allowExactReuse, budget, []);
        Validate(candidate);
        return Create(candidate, ReviewPolicyAuthorityKind.Caller, trusted: true);
    }

    internal static ReviewPolicy FromVerifiedProtectedSource(ReviewPolicyCandidate candidate)
    {
        Validate(candidate);
        if (candidate.Fixture || candidate.Advisory)
            throw new ReviewPolicyException("A protected production policy cannot be fixture or advisory.");
        return Create(candidate, ReviewPolicyAuthorityKind.ProtectedGitBase, trusted: true);
    }

    private static ReviewPolicy Create(
        ReviewPolicyCandidate candidate,
        ReviewPolicyAuthorityKind authorityKind,
        bool trusted) =>
        new(candidate.PolicyIdentity, candidate.ContentIdentity, candidate.SourceRevision, authorityKind,
            trusted, candidate.Fixture, candidate.Advisory, candidate.AllowedFamilies,
            candidate.AllowedScopes, candidate.AllowExactReuse, candidate.Budget,
            candidate.ExemptionApprovers);

    private static void Validate(ReviewPolicyCandidate candidate)
    {
        if (candidate is null ||
            candidate.AllowedFamilies is null ||
            candidate.AllowedScopes is null ||
            candidate.Budget is null ||
            candidate.ExemptionApprovers is null)
            throw new ReviewPolicyException("Review policy is incomplete.");
        if (!IdentityPattern().IsMatch(candidate.PolicyIdentity) ||
            !IdentityPattern().IsMatch(candidate.ContentIdentity) ||
            !GitPattern().IsMatch(candidate.SourceRevision))
            throw new ReviewPolicyException("Review policy identities are malformed.");
        if (candidate.AllowedFamilies.Count == 0 ||
            candidate.AllowedFamilies.Any(pair => Unknown(pair.Author) || Unknown(pair.Reviewer) ||
                                                  Same(pair.Author, pair.Reviewer)) ||
            candidate.AllowedFamilies.Distinct().Count() != candidate.AllowedFamilies.Count)
            throw new ReviewPolicyException("Review family policy is empty, duplicated, unknown, or self-reviewing.");
        if (candidate.AllowedScopes.Count == 0 ||
            candidate.AllowedScopes.Any(string.IsNullOrWhiteSpace) ||
            candidate.AllowedScopes.Distinct(StringComparer.Ordinal).Count() != candidate.AllowedScopes.Count)
            throw new ReviewPolicyException("Review scopes are empty, malformed, or duplicated.");
        if (!candidate.Budget.IsFinite)
            throw new ReviewPolicyException("Review policy requires an explicit finite budget.");
        if (candidate.ExemptionApprovers.Any(string.IsNullOrWhiteSpace))
            throw new ReviewPolicyException("Exemption approvers are malformed.");
    }

    internal static bool Unknown(string value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase);

    internal static bool Same(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Identity(char value) => $"sha256:{new string(value, 64)}";

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    [GeneratedRegex("^[0-9a-f]{40}(?:[0-9a-f]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitPattern();
}

public sealed class ProtectedReviewPolicyResolver(string repositoryRoot, string protectedBaseCommit)
{
    private readonly string _root = Path.GetFullPath(repositoryRoot);

    public async Task<ReviewPolicy> ResolveAsync(
        string repositoryRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (Path.IsPathFullyQualified(repositoryRelativePath) ||
            repositoryRelativePath.Split(['/', '\\']).Any(segment => segment is "" or "." or ".."))
            throw new ReviewPolicyException("A safe repository-relative policy path is required.");

        var exactBase = await GitTextAsync(["rev-parse", "--verify", $"{protectedBaseCommit}^{{commit}}"], cancellationToken);
        if (!string.Equals(exactBase.Trim(), protectedBaseCommit, StringComparison.Ordinal))
            throw new ReviewPolicyException("Git did not resolve the exact protected review-policy base.");

        var head = (await GitTextAsync(["rev-parse", "HEAD"], cancellationToken)).Trim();
        await GitTextAsync(["merge-base", "--is-ancestor", protectedBaseCommit, head], cancellationToken);
        var bytes = await GitBytesAsync(
            ["show", $"{protectedBaseCommit}:{repositoryRelativePath.Replace('\\', '/')}"], cancellationToken);
        ReviewPolicyFile candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<ReviewPolicyFile>(
                bytes,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = false,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                }) ?? throw new ReviewPolicyException("Protected review policy is empty.");
        }
        catch (JsonException error)
        {
            throw new ReviewPolicyException("Protected review policy is malformed.", error);
        }

        var contentIdentity = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        var resolved = new ReviewPolicyCandidate(
            contentIdentity, contentIdentity, protectedBaseCommit, candidate.Fixture,
            candidate.Advisory, candidate.AllowedFamilies, candidate.AllowedScopes,
            candidate.AllowExactReuse, candidate.Budget, candidate.ExemptionApprovers);
        return ReviewPolicy.FromVerifiedProtectedSource(resolved);
    }

    private async Task<string> GitTextAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        Encoding.UTF8.GetString(await GitBytesAsync(args, cancellationToken));

    private async Task<byte[]> GitBytesAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await GitProcess.RunAsync(_root, args, cancellationToken);
        if (result.ExitCode != 0)
            throw new ReviewPolicyException($"Protected review policy Git read failed: git {string.Join(" ", args)}.");
        return result.StandardOutput;
    }

    private sealed class ReviewPolicyFile
    {
        public required string PolicyIdentity { get; init; }
        public required bool Fixture { get; init; }
        public required bool Advisory { get; init; }
        public required IReadOnlyList<FamilyPair> AllowedFamilies { get; init; }
        public required IReadOnlyList<string> AllowedScopes { get; init; }
        public required bool AllowExactReuse { get; init; }
        public required ReviewBudget Budget { get; init; }
        public required IReadOnlyList<string> ExemptionApprovers { get; init; }
    }
}

public sealed class ReviewPolicyException(string message, Exception? inner = null) : Exception(message, inner);
