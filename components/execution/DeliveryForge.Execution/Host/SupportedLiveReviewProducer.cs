using System.Security.Cryptography;
using DeliveryForge.Review;

namespace DeliveryForge.Execution.Host;

public sealed record LiveReviewerObservation(
    bool CliAvailable,
    bool RuntimeObserved,
    bool AuthenticatedFirstParty,
    bool RequestAccepted,
    bool CompletionObserved,
    string AdapterId,
    string AdapterVersion,
    string Scope,
    string CapabilityProofIdentity,
    DateTimeOffset CapabilityValidFrom,
    DateTimeOffset CapabilityValidUntil,
    string RequestIdentity,
    string ExecutionIdentity,
    byte[] RawOutput,
    string RawVerdict,
    IReadOnlyList<ReviewFinding>? Findings,
    DateTimeOffset CompletedAt,
    bool Fixture);

public static class SupportedLiveReviewProducer
{
    public static NormalizedReview Produce(
        ReviewAuthorityHost host,
        ReviewRequest request,
        ReviewPolicy policy,
        LiveReviewerObservation observation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(observation);
        var artifactHash = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(observation.RawOutput ?? []))}";
        return ReviewNormalizer.Normalize(
            request,
            new ReviewResult(request.Identity, request.Capability.Family, "INCOMPLETE",
                observation.Findings ?? [], artifactHash, observation.CompletedAt,
                false, false, observation.Fixture, false),
            policy);
    }

    public static NormalizedReview Produce(
        ReviewAuthorityHost host,
        RegisteredReviewRequest registration,
        ReviewRequest request,
        ReviewPolicy policy,
        LiveReviewerObservation observation)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(observation);

        var artifactHash = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(observation.RawOutput ?? []))}";
        // This producer is deliberately a fixture conformance seam. A future real producer must
        // supply host-owned observations rather than promote caller booleans to production proof.
        var available = observation.Fixture && request.Capability.Fixture &&
                        observation.CliAvailable && observation.RuntimeObserved &&
                        observation.AuthenticatedFirstParty && observation.RequestAccepted &&
                        observation.CompletionObserved && observation.RawOutput is { Length: > 0 } &&
                        string.Equals(observation.AdapterId, request.Capability.AdapterId, StringComparison.Ordinal) &&
                        string.Equals(observation.AdapterVersion, request.Capability.AdapterVersion, StringComparison.Ordinal) &&
                        string.Equals(observation.Scope, request.Scope, StringComparison.Ordinal) &&
                        string.Equals(observation.RequestIdentity, registration.RequestIdentity, StringComparison.Ordinal) &&
                        ReviewNormalizer.SameRequest(registration.Request, request) &&
                        observation.Fixture == request.Capability.Fixture;
        var verdict = available ? MapVerdict(observation.RawVerdict) : "INCOMPLETE";
        var result = new ReviewResult(
            request.Identity,
            request.Capability.Family,
            verdict,
            observation.Findings,
            artifactHash,
            observation.CompletedAt,
            false,
            false,
            observation.Fixture,
            false);
        if (!available)
            return ReviewNormalizer.Normalize(request, result, policy);

        IssuedReviewerCapability capability;
        AcceptedReviewExecution execution;
        try
        {
            capability = host.IssueCapability(
                request.Capability,
                observation.CapabilityProofIdentity,
                observation.CapabilityValidFrom,
                observation.CapabilityValidUntil);
            execution = host.RecordExecution(
                registration,
                capability,
                observation.ExecutionIdentity,
                observation.RequestAccepted,
                observation.CompletionObserved,
                artifactHash,
                observation.RawVerdict,
                observation.CompletedAt);
        }
        catch (Exception) when (verdict != "ERROR")
        {
            return ReviewNormalizer.Normalize(
                request,
                result with { Verdict = "INCOMPLETE" },
                policy);
        }
        return ReviewNormalizer.Normalize(request, result, policy, capability, execution);
    }

    private static string MapVerdict(string? verdict) => verdict?.Trim().ToLowerInvariant() switch
    {
        "ship" => "PASS",
        "needs-attention" => "FAIL",
        "do-not-ship" => "FAIL",
        _ => "ERROR"
    };
}
