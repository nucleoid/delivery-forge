using DeliveryForge.Review;

namespace DeliveryForge.EndToEnd.Tests;

public sealed record ReviewFixtureObservation(string RawPath, string RawSha256, string Verdict, DateTimeOffset CompletedAt);
public sealed record ReviewFixtureCall(string AdapterId, string Family, string Scope, string RawPath);

// Only the external reviewer response is a fixture; no model/production/opposite-family certification.
internal sealed class ReviewFixturePort
{
    private readonly List<ReviewFixtureCall> _calls = [];
    public IReadOnlyList<ReviewFixtureCall> Calls => _calls.AsReadOnly();
    internal Task<ReviewFixtureObservation> ExecuteAsync(ReviewRequest request, string ownedRawPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        throw new NotImplementedException("Marked external reviewer raw-response fixture has not been invoked or implemented at RED.");
    }
}
