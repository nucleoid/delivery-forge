using DeliveryForge.Coordination;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// Same actual lifetime and existing restriction; deployment DTOs below are DATA,
// never a readiness facet, credential or permission to contact a service.
public sealed class ParentEvidenceWindowTests
{
    static ContinuumDeploymentEvidence Evidence(DateTimeOffset verified, DateTimeOffset until)
        => new(new Uri("https://example.test/mcp"), "fixture-project", "fixture-principal", new string('a', 64),
            "fixture-operator", new string('b', 40), new string('c', 64), new string('d', 64), new string('e', 64), verified, until);

    [Fact]
    public void Pinned_evidence_before_original_start_restricts_1200_ceiling_without_rebasing_or_renewing()
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = Evidence(now - TimeSpan.FromSeconds(30), now + TimeSpan.FromMinutes(20));
        using var life = new ToolLifetime(TimeSpan.FromSeconds(1200), TestContext.Current.CancellationToken);
        var started = life.OriginalStarted; var token = life.Token;
        ParentCoordinationHost.RestrictOriginalToPinnedEvidence(life, evidence);
        Assert.Equal(started, life.OriginalStarted); Assert.Equal(token, life.Token);
        Assert.True(life.WholeLimit > TimeSpan.Zero && life.WholeLimit < TimeSpan.FromSeconds(1190));
        Assert.True(life.WorkLimit <= TimeSpan.FromSeconds(1100)); life.Check();
        Assert.Equal(evidence.VerifiedAt, now - TimeSpan.FromSeconds(30));
        Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Explicit_earlier_expiry_clips_same_original_and_an_already_shorter_original_never_expands()
    {
        var now = DateTimeOffset.UtcNow;
        using var clipped = new ToolLifetime(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        ParentCoordinationHost.RestrictOriginalToPinnedEvidence(clipped, Evidence(now - TimeSpan.FromSeconds(1), now + TimeSpan.FromSeconds(15)));
        Assert.True(clipped.WholeLimit > TimeSpan.Zero && clipped.WholeLimit < TimeSpan.FromSeconds(30));
        using var shorter = new ToolLifetime(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var before = shorter.WholeLimit;
        ParentCoordinationHost.RestrictOriginalToPinnedEvidence(shorter, Evidence(now - TimeSpan.FromSeconds(1), now + TimeSpan.FromMinutes(10)));
        Assert.Equal(before, shorter.WholeLimit);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("age")]
    [InlineData("inverted")]
    public void Invalid_current_interval_refuses_before_mutating_original(string fault)
    {
        var now = DateTimeOffset.UtcNow;
        var data = fault switch
        {
            "future" => Evidence(now + TimeSpan.FromMinutes(1), now + TimeSpan.FromMinutes(2)),
            "expired" => Evidence(now - TimeSpan.FromMinutes(1), now - TimeSpan.FromSeconds(1)),
            "age" => Evidence(now - TimeSpan.FromMinutes(21), now + TimeSpan.FromMinutes(1)),
            _ => Evidence(now - TimeSpan.FromSeconds(1), now - TimeSpan.FromSeconds(2))
        };
        using var life = new ToolLifetime(TimeSpan.FromSeconds(1200), TestContext.Current.CancellationToken);
        Assert.Throws<ToolUsageException>(() => ParentCoordinationHost.RestrictOriginalToPinnedEvidence(life, data));
        Assert.Equal(TimeSpan.FromSeconds(1200), life.WholeLimit); Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Existing_original_cancellation_cannot_be_reset_by_current_evidence()
    {
        using var cancellation = new CancellationTokenSource();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(1200), cancellation.Token);
        var started = life.OriginalStarted; cancellation.Cancel(); var now = DateTimeOffset.UtcNow;
        Assert.ThrowsAny<OperationCanceledException>(() => ParentCoordinationHost.RestrictOriginalToPinnedEvidence(life, Evidence(now, now + TimeSpan.FromMinutes(10))));
        Assert.Equal(started, life.OriginalStarted); Assert.Equal(TimeSpan.FromSeconds(1200), life.WholeLimit);
    }
}
