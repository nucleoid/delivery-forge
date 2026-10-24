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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_parent_create_refuses_missing_or_nonempty_checkpoint_before_evidence_or_owner_entry(bool nonempty)
    {
        using var sample = new CheckpointSample();
        if (nonempty) { Directory.CreateDirectory(sample.Checkpoint); File.WriteAllText(Path.Combine(sample.Checkpoint, "keep.txt"), "retained synthetic input"); }
        var before = Directory.GetFileSystemEntries(sample.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var started = life.OriginalStarted; var token = life.Token; var whole = life.WholeLimit; var work = life.WorkLimit;
        // These syntactically valid absent negative inputs prevent any service
        // access if the early precondition regresses. The exact checkpoint
        // refusal proves evidence reading was not reached by this real call.
        var selected = sample.Configuration with
        {
            PrivateKeyPath = Path.Combine(sample.Root, "absent-key"),
            DeploymentEvidence = new(Path.Combine(sample.Root, "absent-evidence.json"), 1, new string('a', 64))
        };
        var actual = ParentCoordinationHost.CreateAsync(selected, life);
        Assert.True(actual.IsCompleted); // synchronous local refusal, no async discovery/birth
        var refusal = await Assert.ThrowsAsync<ToolUsageException>(() => actual);
        Assert.Equal("Parent coordination requires an existing empty checkpoint directory.", refusal.Message);
        Assert.False(life.HasRetainedUnknown); Assert.False(life.HasExplicitUnknown); life.Check();
        Assert.Equal(started, life.OriginalStarted); Assert.Equal(token, life.Token);
        Assert.Equal(whole, life.WholeLimit); Assert.Equal(work, life.WorkLimit);
        Assert.Equal(before, Directory.GetFileSystemEntries(sample.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        Assert.False(File.Exists(Path.Combine(sample.Checkpoint, "parent-writer.lock")));
        Assert.Equal(nonempty, Directory.Exists(sample.Checkpoint));
        Assert.Equal(sample.EvidenceBytes, File.ReadAllBytes(sample.EvidencePath));
        if (nonempty) Assert.Equal("retained synthetic input", File.ReadAllText(Path.Combine(sample.Checkpoint, "keep.txt")));
    }
    [Fact]
    public async Task Empty_checkpoint_reaches_existing_evidence_mismatch_refusal_without_creating_a_writer_or_unknown()
    {
        using var sample = new CheckpointSample(); Directory.CreateDirectory(sample.Checkpoint);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        // A later deterministic DATA selection mismatch prevents any service
        // access while proving the checkpoint guard accepts an empty directory.
        var actual = ParentCoordinationHost.CreateAsync(sample.Configuration with { Project = "different-synthetic-project" }, life);
        Assert.True(actual.IsCompleted);
        var refusal = await Assert.ThrowsAsync<ToolUsageException>(() => actual);
        Assert.Equal("Pinned operator evidence differs from parent endpoint/project/principal selection.", refusal.Message);
        Assert.False(life.HasRetainedUnknown); Assert.False(life.HasExplicitUnknown); life.Check();
        Assert.Empty(Directory.EnumerateFileSystemEntries(sample.Checkpoint));
        Assert.Equal(sample.EvidenceBytes, File.ReadAllBytes(sample.EvidencePath));
    }
    sealed class CheckpointSample : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "delivery-forge-checkpoint-preflight-" + Guid.NewGuid().ToString("N"));
        internal string Checkpoint => Path.Combine(Root, "checkpoint");
        internal string EvidencePath => Path.Combine(Root, "operator-data.json");
        internal byte[] EvidenceBytes { get; }
        internal ParentCoordinationConfiguration Configuration { get; }
        internal CheckpointSample()
        {
            Directory.CreateDirectory(Root);
            var key = Path.Combine(Root, "synthetic-key.txt"); File.WriteAllText(key, "inert-not-a-service-credential");
            var now = DateTimeOffset.UtcNow; var data = Evidence(now - TimeSpan.FromSeconds(1), now + TimeSpan.FromMinutes(5));
            EvidenceBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data, BoundedFiles.Json);
            File.WriteAllBytes(EvidencePath, EvidenceBytes);
            Configuration = new(data.Endpoint, data.Project, data.PrincipalId, key, Checkpoint,
                new(EvidencePath, EvidenceBytes.LongLength, BoundedFiles.Sha(EvidenceBytes)));
        }
        public void Dispose()
        {
            // Only these self-created inert fixture files are removed.
            var keep = Path.Combine(Checkpoint, "keep.txt"); if (File.Exists(keep)) File.Delete(keep);
            if (Directory.Exists(Checkpoint)) Directory.Delete(Checkpoint);
            File.Delete(EvidencePath); File.Delete(Path.Combine(Root, "synthetic-key.txt")); Directory.Delete(Root);
        }
    }
}
