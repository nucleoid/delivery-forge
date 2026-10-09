using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeliveryForge.Coordination;

public enum CoordinationOperation { Acquire, Renew, Inspect, Release }
public enum CoordinationAvailability { Available, Unavailable, Incomplete }
public enum LeaseOwnership { Held, Contended, Uncertain, Lost, Released }
public enum TransportOutcome { Acquired, Renewed, Inspected, Released, Contended, LeaseLost, Uncertain }

public sealed record CoordinationCapabilities(
    string ContractVersion,
    CoordinationAvailability Availability,
    bool Fixture,
    IReadOnlySet<CoordinationOperation> Operations,
    TimeSpan MinimumTtl,
    TimeSpan MaximumTtl,
    TimeSpan IdempotencyHorizon,
    bool DecimalStringFencing,
    bool HolderIdentityMasking,
    bool AuthenticatedExactScope,
    IReadOnlyList<string> Limitations)
{
    public const string V1ContractVersion = "continuum.coordination.v1";

    internal static CoordinationCapabilities ValidV1ForFixture() => new(
        V1ContractVersion,
        CoordinationAvailability.Available,
        true,
        new HashSet<CoordinationOperation>(Enum.GetValues<CoordinationOperation>()),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(24),
        true,
        true,
        true,
        ["test-only fixture; never production authority"]);

    public static CoordinationCapabilities Unavailable(string limitation) => new(
        V1ContractVersion,
        CoordinationAvailability.Unavailable,
        false,
        new HashSet<CoordinationOperation>(),
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        false,
        false,
        false,
        [limitation]);
}

public sealed record LeaseOwner
{
    public LeaseOwner(string principalId, string runId)
    {
        PrincipalId = RequireOpaque(principalId, nameof(principalId));
        RunId = RequireOpaque(runId, nameof(runId));
    }

    public string PrincipalId { get; }
    public string RunId { get; }

    private static string RequireOpaque(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Owner identifiers must be non-blank, bounded, and control-free.", parameter);
        return value;
    }
}

public sealed record CoordinationResource
{
    private static readonly Regex IssueKey = new(
        "^github:[a-z0-9](?:[a-z0-9-]{0,38})/[a-z0-9](?:[a-z0-9_.-]{0,99}[a-z0-9])?:issue:[1-9][0-9]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public CoordinationResource(string scope, string resourceKey)
    {
        Scope = Validate(scope, nameof(scope), allowResourceForms: false);
        ResourceKey = Validate(resourceKey, nameof(resourceKey), allowResourceForms: true);
    }

    public string Scope { get; }
    public string ResourceKey { get; }

    public static CoordinationResource GitHubIssue(string scope, string owner, string repository, long issueNumber)
    {
        if (issueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(issueNumber));
        return new CoordinationResource(scope,
            $"github:{owner}/{repository}:issue:{issueNumber.ToString(CultureInfo.InvariantCulture)}");
    }

    public static CoordinationResource SchemaMigrations(string scope) => new(scope, "schema-migrations");

    private static string Validate(string value, string parameter, bool allowResourceForms)
    {
        if (string.IsNullOrEmpty(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > 512)
            throw new ArgumentException("Coordination keys must be 1-512 UTF-8 bytes with no controls or edge whitespace.", parameter);
        if (allowResourceForms && value != "schema-migrations" && !IssueKey.IsMatch(value))
            throw new ArgumentException("Resource key must be the canonical GitHub issue key or schema-migrations.", parameter);
        return value;
    }
}

public sealed record LeaseGrant(
    CoordinationResource Resource,
    LeaseOwner Owner,
    string LeaseId,
    string FencingToken,
    DateTimeOffset ServerTime,
    DateTimeOffset ExpiresAt,
    TimeSpan RenewalTarget,
    TimeSpan SafeDeadline,
    TimeSpan RenewAt)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LeaseId)) throw new CoordinationProtocolException("Lease ID is missing.");
        if (!long.TryParse(FencingToken, NumberStyles.None, CultureInfo.InvariantCulture, out var token) || token < 0 ||
            (FencingToken.Length > 1 && FencingToken[0] == '0'))
            throw new CoordinationProtocolException("Fencing token must be a canonical non-negative PostgreSQL BIGINT decimal string and is never parsed as floating point.");
        if (ExpiresAt <= ServerTime) throw new CoordinationProtocolException("Lease expiry must follow authoritative server time.");
        if (RenewAt >= SafeDeadline) throw new CoordinationProtocolException("Renewal target must precede the conservative safe deadline.");
    }
}

public sealed record LeaseAcquisition(LeaseOwner Owner, IReadOnlyList<LeaseGrant> Leases, LeaseOwnership Ownership, string Reason);

public sealed record QuiescenceEvidence(bool Quiescent, string EvidenceReference);

public interface IOwnedRunQuiescence
{
    Task<QuiescenceEvidence> ObserveAsync(LeaseOwner owner, CancellationToken cancellationToken = default);
}

public class CoordinationException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class CoordinationUnavailableException(string message, Exception? inner = null) : CoordinationException(message, inner);
public sealed class CoordinationUncertainException(string message, Exception? inner = null) : CoordinationException(message, inner);
public sealed class CoordinationProtocolException(string message, Exception? inner = null) : CoordinationException(message, inner);
