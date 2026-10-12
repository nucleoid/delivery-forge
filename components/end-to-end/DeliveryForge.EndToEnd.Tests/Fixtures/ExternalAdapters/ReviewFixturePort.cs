using DeliveryForge.Review;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeliveryForge.EndToEnd.Tests;

public sealed record ReviewFixtureObservation(string RawPath, string RawSha256, string Verdict, DateTimeOffset CompletedAt);
public sealed record ReviewFixtureCall(string RequestIdentity, string AdapterId, string Family, string Scope, string RawPath);

// Only the external reviewer response is a fixture; no model/production/opposite-family certification.
internal sealed class ReviewFixturePort
{
    private readonly List<ReviewFixtureCall> _calls = [];
    public IReadOnlyList<ReviewFixtureCall> Calls => _calls.AsReadOnly();
    internal async Task<ReviewFixtureObservation> ExecuteAsync(RegisteredReviewRequest registration, string ownedRawPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = registration.Request;
        if (!request.Capability.Fixture || !Path.IsPathFullyQualified(ownedRawPath))
            throw new InvalidOperationException("Only an explicitly marked external reviewer fixture is supported.");
        for (DirectoryInfo? current = new(Path.GetDirectoryName(ownedRawPath)!); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reviewer raw fixture parent is redirected.");
        var completedAt = DateTimeOffset.UtcNow;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            fixture = true,
            actualModelCall = false,
            oppositeFamilyCertification = false,
            requestIdentity = registration.RequestIdentity,
            request,
            verdict = "ship",
            findings = Array.Empty<ReviewFinding>(),
            completedAt
        });
        if (bytes.Length > 1024 * 1024) throw new IOException("Reviewer fixture raw bytes exceed1MiB.");
        await using (var writer = new FileStream(ownedRawPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await writer.WriteAsync(bytes, token); await writer.FlushAsync(token);
        }
        var actual = await File.ReadAllBytesAsync(ownedRawPath, token);
        if (!actual.AsSpan().SequenceEqual(bytes)) throw new IOException("Actual raw fixture artifact changed before observation.");
        _calls.Add(new(registration.RequestIdentity, request.Capability.AdapterId, request.Capability.Family, request.Scope, ownedRawPath));
        return new(ownedRawPath, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(actual)), "ship", completedAt);
    }
}
