using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Packaging;

namespace DeliveryForge.Tool.Tests;

// Explicitly selected actual acceptance only. Root prepares and admits the real
// installed prefix, host conformance, owner-approved baseline and native tools.
public sealed class InstalledLocalRealGoldenTests
{
    public sealed record Fixture(string Kind, AdmissionFile Package, InstallationReceipt Installation,
        AdmissionFile Configuration, AdmissionFile Plan, AdmissionFile? LinuxScopeProfile, string RunDirectory, string ResultDirectory);
    [Fact(Timeout = 1_300_000)]
    [Trait("Category", "InstalledLocalRealAcceptance")]
    public async Task Actual_packaged_local_real_chain_uses_native_worker_independent_reviewer_protected_tools_and_local_fake_publication()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity(); using var original = new ToolLifetime(TimeSpan.FromSeconds(1200), TestContext.Current.CancellationToken);
        var path = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LOCAL_REAL_FIXTURE") ?? throw new IOException("Explicit real installed local-real fixture required; no skip/fake capability.");
        var sha = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LOCAL_REAL_FIXTURE_SHA256") ?? throw new IOException("Independent exact fixture pin required.");
        var raw = BoundedFiles.Read(path, original, 65536); Assert.Equal(sha, BoundedFiles.Sha(raw));
        var fixture = JsonSerializer.Deserialize<Fixture>(raw, BoundedFiles.Json) ?? throw new IOException("Null local-real fixture.");
        Assert.Equal("RootPreparedInstalledLocalRealFixture-v1", fixture.Kind);
        _ = InstalledHostAdmission.ReadExact(fixture.Package, original, 64 * 1024 * 1024);
        _ = InstalledHostAdmission.ReadExact(fixture.Plan, original, 524288);
        _ = InstalledHostAdmission.ReadExact(fixture.Configuration, original, 65536);
        Assert.Equal(fixture.Package.Sha256, fixture.Installation.PackageSha256);
        var manifest = PackageLifecycle.Verify(fixture.Installation, original);
        var entry = Path.Combine(fixture.Installation.Prefix, "payload", "DeliveryForge.Tool.dll");
        Assert.Contains(manifest.Files, row => row.Path == "payload/DeliveryForge.Tool.dll");
        var config = TrustedConfiguration.Load(fixture.Configuration.Path, original);
        Assert.NotNull(config.LocalRealGolden); Assert.NotNull(config.InstalledQuality?.Registry); Assert.NotNull(config.InstalledReview?.IndependentReviewer);
        BoundedFiles.Separate(fixture.Installation.Prefix, config.LocalRealGolden.ArtifactRoot);
        Assert.True(config.OperationSeconds <= Math.Floor(original.Remaining.TotalSeconds) - 60, "Installed child's complete declared interval must fit inside this already-running original; no refreshed outer budget.");
        LinuxScopeSelection? scope = null;
        if (OperatingSystem.IsLinux())
        {
            var pin = fixture.LinuxScopeProfile ?? throw new IOException("Actual current Linux creation-owned profile required.");
            scope = JsonSerializer.Deserialize<LinuxScopeSelection>(InstalledHostAdmission.ReadExact(pin, original, 65536), BoundedFiles.Json) ?? throw new IOException("Null Linux profile.");
        }
        var executor = OperatingSystem.IsLinux() ? new ProcessCommandExecutor(scope!, original) : new ProcessCommandExecutor();
        var actual = await executor.ExecuteBoundedCaptureAsync(new(manifest.RuntimeHost,
            [entry, "implement", "--config", fixture.Configuration.Path, "--plan", fixture.Plan.Path,
                "--run-dir", fixture.RunDirectory, "--result-dir", fixture.ResultDirectory, "--json"],
            config.RepositoryRoot, ActualOrdinaryCapture.ResourceEnvironment()), original.Remaining, 1048576,
            original.Token, new OwnedWorkPhaseBudget(original));
        ActualOrdinaryCapture.Require(actual); Assert.Equal(0, actual.Command.ExitCode); Assert.False(actual.Command.TimedOut); Assert.False(actual.Command.Cancelled);
        using var returned = JsonDocument.Parse(actual.StandardOutputBytes);
        Assert.Equal("ACTUAL_LOCAL_REAL_CHAIN_PASSED", returned.RootElement.GetProperty("Code").GetString());
        var receipt = returned.RootElement.GetProperty("Data").GetProperty("Receipt").GetString()!;
        var runDirectory = returned.RootElement.GetProperty("Data").GetProperty("RunDirectory").GetString()!;
        var outputRoot = Path.GetFullPath(config.LocalRealGolden.ArtifactRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(outputRoot, Path.GetFullPath(receipt));
        Assert.Equal(Path.GetDirectoryName(receipt), runDirectory);
        var actualSelection = BoundedFiles.ReadJson<RunSelection>(Path.Combine(runDirectory, "selection.json"), original, 65536);
        Assert.Equal(config.Repository, actualSelection.Repository); Assert.Equal(Path.GetFullPath(config.RepositoryRoot), Path.GetFullPath(actualSelection.RepositoryRoot));
        using var evidence = JsonDocument.Parse(BoundedFiles.Read(receipt, original, 1048576)); var row = evidence.RootElement;
        Assert.Equal("ActualInstalledLocalRealChainData-v1", row.GetProperty("Kind").GetString()); Assert.Equal("PASS", row.GetProperty("Outcome").GetString());
        Assert.Equal(actualSelection.RunId, row.GetProperty("RunId").GetString());
        Assert.True(row.GetProperty("ActualWorker").GetBoolean()); Assert.True(row.GetProperty("ActualQualityWithOwnerBaseline").GetBoolean());
        Assert.True(row.GetProperty("ActualOppositeFamilyReview").GetBoolean()); Assert.True(row.GetProperty("FakePublication").GetBoolean());
        Assert.Equal(0, row.GetProperty("RemoteWrites").GetInt32()); Assert.Equal(0, row.GetProperty("MergeCalls").GetInt32());
        Assert.False(row.GetProperty("ProductionPublicationAuthority").GetBoolean()); Assert.False(row.GetProperty("RecoveryCertified").GetBoolean());
        Assert.False(row.GetProperty("SchedulingCertified").GetBoolean());
        Assert.Equal(2, row.GetProperty("GateReceipts").GetArrayLength());
        _ = PackageLifecycle.Verify(fixture.Installation, original); // Retain prefix and all real artifacts; lifecycle removal is separate.
    }

    [Fact(Timeout = 150_000)]
    [Trait("Category", "InstalledLocalRealAcceptance")]
    public async Task Actual_fresh_installed_process_refuses_to_adopt_saved_native_owner_and_leaves_history_unchanged()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity(); using var original = new ToolLifetime(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        var path = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LOCAL_REAL_RECOVERY_FIXTURE") ?? throw new IOException("Root must select a real prior run only after its original native scope has been independently observed settled.");
        var sha = Environment.GetEnvironmentVariable("DELIVERY_FORGE_LOCAL_REAL_RECOVERY_FIXTURE_SHA256") ?? throw new IOException("Exact independent recovery fixture pin required.");
        var raw = BoundedFiles.Read(path, original, 65536); Assert.Equal(sha, BoundedFiles.Sha(raw));
        var fixture = JsonSerializer.Deserialize<Fixture>(raw, BoundedFiles.Json) ?? throw new IOException("Null recovery fixture.");
        Assert.Equal("RootPreparedInstalledLocalRealFixture-v1", fixture.Kind);
        _ = InstalledHostAdmission.ReadExact(fixture.Package, original, 64 * 1024 * 1024); Assert.Equal(fixture.Package.Sha256, fixture.Installation.PackageSha256);
        _ = InstalledHostAdmission.ReadExact(fixture.Configuration, original, 65536);
        var manifest = PackageLifecycle.Verify(fixture.Installation, original); var config = TrustedConfiguration.Load(fixture.Configuration.Path, original);
        var selected = BoundedFiles.ReadJson<RunSelection>(Path.Combine(fixture.RunDirectory, "selection.json"), original, 65536);
        var store = new RunStore(selected.StoreRoot); var before = await store.RecoverAsync(selected.RunId, original.Token); Assert.NotEmpty(before.Records);
        LinuxScopeSelection? scope = null;
        if (OperatingSystem.IsLinux())
        {
            var pin = fixture.LinuxScopeProfile ?? throw new IOException("Actual creation-owned Linux profile required.");
            scope = JsonSerializer.Deserialize<LinuxScopeSelection>(InstalledHostAdmission.ReadExact(pin, original, 65536), BoundedFiles.Json) ?? throw new IOException("Null Linux profile.");
        }
        var executor = OperatingSystem.IsLinux() ? new ProcessCommandExecutor(scope!, original) : new ProcessCommandExecutor();
        var actual = await executor.ExecuteBoundedCaptureAsync(new(manifest.RuntimeHost,
            [Path.Combine(fixture.Installation.Prefix, "payload", "DeliveryForge.Tool.dll"), "resume", "--run-dir", fixture.RunDirectory, "--config", fixture.Configuration.Path, "--json"],
            config.RepositoryRoot, ActualOrdinaryCapture.ResourceEnvironment()), original.Remaining, 1048576, original.Token, new OwnedWorkPhaseBudget(original));
        ActualOrdinaryCapture.Require(actual); Assert.Equal(2, actual.Command.ExitCode); Assert.False(actual.Command.TimedOut); Assert.False(actual.Command.Cancelled);
        using var returned = JsonDocument.Parse(actual.StandardOutputBytes); Assert.Equal("ORIGINAL_OWNER_REQUIRED", returned.RootElement.GetProperty("Code").GetString());
        var after = await store.RecoverAsync(selected.RunId, original.Token);
        Assert.Equal(before.Records.Select(row => row.Sha256), after.Records.Select(row => row.Sha256));
        _ = PackageLifecycle.Verify(fixture.Installation, original);
        // A real fresh-process refusal is not a successful worker resume, does
        // not reconcile any old unknown native owner and grants no new budget.
    }
}
