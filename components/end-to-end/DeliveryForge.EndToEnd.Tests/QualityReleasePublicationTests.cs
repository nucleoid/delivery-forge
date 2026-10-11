using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class QualityReleasePublicationTests
{
    [Fact(Timeout = 60_000)]
    public async Task Final_release_is_absent_while_real_staging_writer_is_held_then_reads_exact_nonce()
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        await using var artifacts = await OwnedScenarioRun.CreateAsync(bound.Token);
        artifacts.RetainArtifacts("Release publication boundary and failed RED evidence retained.");
        using var clock = new QualityInterruptionClock();
        var runtime = await Runtime(artifacts, clock, bound.Token);
        var directory = Path.Combine(runtime.Repository, "barrier"); Directory.CreateDirectory(directory);
        var nonce = Guid.NewGuid().ToString("N"); var final = Path.Combine(directory, "release-" + nonce);
        var callbackReached = false;
        await QualityReleasePublisher.PublishAsync(runtime, directory, nonce, async (staging, token) =>
        {
            callbackReached = true;
            Assert.True(File.Exists(staging));
            Assert.Equal(directory, Path.GetDirectoryName(staging));
            Assert.NotEqual(final, staging);
            Assert.False(File.Exists(final));
            if (OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() => { using var competing = new FileStream(staging, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); });
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Assert.False(File.Exists(final));
        }, bound.Token);
        Assert.True(callbackReached);
        Assert.Equal(nonce, await File.ReadAllTextAsync(final, bound.Token));
        Assert.Equal(0, runtime.SdkCommands);
    }

    [Fact(Timeout = 60_000)]
    public async Task Final_release_collision_preserves_existing_real_bytes_and_refuses_publication()
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(30));
        await using var artifacts = await OwnedScenarioRun.CreateAsync(bound.Token);
        artifacts.RetainArtifacts("Release collision sentinel and failed RED evidence retained.");
        using var clock = new QualityInterruptionClock();
        var runtime = await Runtime(artifacts, clock, bound.Token);
        var directory = Path.Combine(runtime.Repository, "barrier"); Directory.CreateDirectory(directory);
        var nonce = Guid.NewGuid().ToString("N"); var final = Path.Combine(directory, "release-" + nonce);
        var sentinel = System.Text.Encoding.UTF8.GetBytes("existing independently written sentinel");
        await OwnedQualityInterruptionRuntime.WriteNewAsync(final, sentinel, bound.Token);
        await Assert.ThrowsAsync<IOException>(() => QualityReleasePublisher.PublishAsync(runtime, directory, nonce, null, bound.Token));
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(final, bound.Token));
        Assert.Equal(0, runtime.SdkCommands);
    }

    private static async Task<OwnedQualityInterruptionRuntime> Runtime(OwnedScenarioRun artifacts, QualityInterruptionClock clock, CancellationToken token)
    {
        var seed = Path.Combine(artifacts.Root, "plain-source-only"); Directory.CreateDirectory(seed);
        await OwnedQualityInterruptionRuntime.WriteNewAsync(Path.Combine(seed, "seed.json"),
            DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { fixture = true })), token);
        var runtime = new OwnedQualityInterruptionRuntime(artifacts, clock, token);
        runtime.BeginStage(clock.WorkBudget(30));
        await runtime.InitializeAsync(seed, token);
        artifacts.RetainArtifacts("No recursive deletion authorized; plain publication runtime retained: " + runtime.Root);
        return runtime;
    }
}
