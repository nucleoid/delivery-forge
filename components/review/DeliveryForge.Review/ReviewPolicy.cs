using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeliveryForge.Contracts.State;

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
        IEnumerable<string> exemptionApprovers,
        object? sourceOwner = null,
        ReviewAuthorityRealm sourceRealm = ReviewAuthorityRealm.Test,
        string sourceAuthorityIdentity = "",
        string protectedObjectViewIdentity = "")
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
        SourceOwner = sourceOwner;
        SourceRealm = sourceRealm;
        SourceAuthorityIdentity = sourceAuthorityIdentity;
        ProtectedObjectViewIdentity = protectedObjectViewIdentity;
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
    internal object? SourceOwner { get; }
    public ReviewAuthorityRealm SourceRealm { get; }
    public string SourceAuthorityIdentity { get; }
    public string ProtectedObjectViewIdentity { get; }
    internal bool HasSourceProvenance => SourceOwner is not null;

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

    internal static ReviewPolicy FromVerifiedProtectedSource(
        ReviewPolicyCandidate candidate,
        ProtectedReviewSourceContext source)
    {
        Validate(candidate);
        if (candidate.Fixture || candidate.Advisory)
            throw new ReviewPolicyException("A protected production policy cannot be fixture or advisory.");
        return Create(
            candidate,
            ReviewPolicyAuthorityKind.ProtectedGitBase,
            trusted: true,
            source.ObjectView.QuiescenceAuthority,
            source.ObjectView.SourceRealm,
            source.SourceAuthorityIdentity,
            source.ProtectedObjectViewIdentity);
    }

    private static ReviewPolicy Create(
        ReviewPolicyCandidate candidate,
        ReviewPolicyAuthorityKind authorityKind,
        bool trusted,
        object? sourceOwner = null,
        ReviewAuthorityRealm sourceRealm = ReviewAuthorityRealm.Test,
        string sourceAuthorityIdentity = "",
        string protectedObjectViewIdentity = "") =>
        new(candidate.PolicyIdentity, candidate.ContentIdentity, candidate.SourceRevision, authorityKind,
            trusted, candidate.Fixture, candidate.Advisory, candidate.AllowedFamilies,
            candidate.AllowedScopes, candidate.AllowExactReuse, candidate.Budget,
            candidate.ExemptionApprovers, sourceOwner, sourceRealm, sourceAuthorityIdentity,
            protectedObjectViewIdentity);

    internal bool SourceMatches(
        object issuer,
        ReviewAuthorityRealm realm,
        string sourceAuthorityIdentity,
        string protectedObjectViewIdentity) =>
        SourceOwner is null
            ? realm == ReviewAuthorityRealm.Test
            : ReferenceEquals(SourceOwner, issuer) &&
              SourceRealm == realm &&
              string.Equals(SourceAuthorityIdentity, sourceAuthorityIdentity, StringComparison.Ordinal) &&
              string.Equals(ProtectedObjectViewIdentity, protectedObjectViewIdentity, StringComparison.Ordinal);

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
            candidate.AllowedFamilies.Any(pair => pair is null || Unknown(pair.Author) || Unknown(pair.Reviewer) ||
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

    internal static bool Unknown(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase);

    internal static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Identity(char value) => $"sha256:{new string(value, 64)}";

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    [GeneratedRegex("^[0-9a-f]{40}(?:[0-9a-f]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitPattern();
}

public sealed class ProtectedReviewSourceContext
{
    internal ProtectedReviewSourceContext(
        ControlledGitEvidenceContext objectView,
        string protectedBaseCommit,
        string sourceAuthorityIdentity)
    {
        ObjectView = objectView;
        ProtectedBaseCommit = protectedBaseCommit;
        SourceAuthorityIdentity = sourceAuthorityIdentity;
    }

    internal ControlledGitEvidenceContext ObjectView { get; }
    public string ProtectedBaseCommit { get; }
    public string SourceAuthorityIdentity { get; }
    public string ProtectedObjectViewIdentity => ObjectView.ProtectedObjectViewIdentity;
}

public sealed class ProtectedReviewPolicyResolver(ProtectedReviewSourceContext sourceContext)
{
    private readonly ProtectedReviewSourceContext _source = sourceContext ??
        throw new ArgumentNullException(nameof(sourceContext));

    public async Task<ReviewPolicy> ResolveAsync(
        string repositoryRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (Path.IsPathFullyQualified(repositoryRelativePath) ||
            repositoryRelativePath.Split(['/', '\\']).Any(segment => segment is "" or "." or ".."))
            throw new ReviewPolicyException("A safe repository-relative policy path is required.");

        ValidateObjectView();
        var exactBase = await GitTextAsync(["rev-parse", "--verify", $"{_source.ProtectedBaseCommit}^{{commit}}"], cancellationToken);
        if (!string.Equals(exactBase.Trim(), _source.ProtectedBaseCommit, StringComparison.Ordinal))
            throw new ReviewPolicyException("Git did not resolve the exact protected review-policy base.");

        var objectId = (await GitTextAsync(
            ["rev-parse", $"{_source.ProtectedBaseCommit}:{repositoryRelativePath.Replace('\\', '/')}"], cancellationToken)).Trim();
        var objectType = (await GitTextAsync(["cat-file", "-t", objectId], cancellationToken)).Trim();
        var objectSizeText = (await GitTextAsync(["cat-file", "-s", objectId], cancellationToken)).Trim();
        if (objectType != "blob" || !long.TryParse(objectSizeText, out var objectSize) ||
            objectSize < 0 || objectSize > _source.ObjectView.MaximumEvidenceBytes)
            throw new ReviewPolicyException("Protected review policy object is not a bounded blob.");
        var bytes = await GitBytesAsync(["cat-file", "blob", objectId], cancellationToken);
        ReviewPolicyFile candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<ReviewPolicyFile>(
                bytes,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = false,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                    AllowDuplicateProperties = false
                }) ?? throw new ReviewPolicyException("Protected review policy is empty.");
        }
        catch (JsonException error)
        {
            throw new ReviewPolicyException("Protected review policy is malformed.", error);
        }

        var contentIdentity = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
        var resolved = new ReviewPolicyCandidate(
            contentIdentity, contentIdentity, _source.ProtectedBaseCommit, candidate.Fixture,
            candidate.Advisory, candidate.AllowedFamilies, candidate.AllowedScopes,
            candidate.AllowExactReuse, candidate.Budget, candidate.ExemptionApprovers);
        return ReviewPolicy.FromVerifiedProtectedSource(resolved, _source);
    }

    private async Task<string> GitTextAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        Encoding.UTF8.GetString(await GitBytesAsync(args, cancellationToken));

    private async Task<byte[]> GitBytesAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var prefix = new[]
        {
            "--no-replace-objects",
            $"--git-dir={_source.ObjectView.ControlledGitDirectory}",
            $"--work-tree={_source.ObjectView.SourceRoot}",
            "-c", "core.fsmonitor=false",
            "-c", $"core.hooksPath={(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")}"
        };
        var result = await GitProcess.RunAsync(
            _source.ObjectView.SourceRoot,
            prefix.Concat(args).ToArray(),
            cancellationToken,
            _source.ObjectView.TrustedGitExecutable,
            _source.ObjectView.MaximumEvidenceBytes);
        if (result.ExitCode != 0)
            throw new ReviewPolicyException($"Protected review policy Git read failed: git {string.Join(" ", args)}.");
        return result.StandardOutput;
    }

    private void ValidateObjectView()
    {
        var git = _source.ObjectView.ControlledGitDirectory;
        var objects = Path.Combine(git, "objects");
        if (!Directory.Exists(objects) ||
            File.Exists(Path.Combine(objects, "info", "alternates")) ||
            File.Exists(Path.Combine(git, "info", "grafts")) ||
            File.Exists(Path.Combine(git, "shallow")) ||
            Directory.Exists(Path.Combine(git, "refs", "replace")))
            throw new ReviewPolicyException("Protected object view contains replacement, graft, alternate, or incomplete authority.");
        var config = Path.Combine(git, "config");
        var text = File.Exists(config) ? File.ReadAllText(config) : string.Empty;
        if (new[] { "include", "promisor", "partialclone", "alternates", "worktree" }
            .Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase)))
            throw new ReviewPolicyException("Protected object view contains external configuration authority.");
    }

    private sealed class ReviewPolicyFile
    {
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
