using System.Text.Json;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;
// Actual default entrypoint calls, not a fabricated host/owner result. These
// definitions require the containing test CLR's admitted two-CPU/four-GiB
// prebirth profile. No installed-host conformance is inferred from them.
public sealed class StandaloneEntrypointTests
{
    static IntPtr ReadActualAffinity(System.Diagnostics.Process actual)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) return actual.ProcessorAffinity;
        throw new PlatformNotSupportedException("Actual caller affinity is Windows/Linux only.");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public async Task Default_diagnostics_have_no_host_or_configuration_requirement(string flag)
    {
        var before = Console.Out; using var output = new StringWriter(); Console.SetOut(output);
        try { Assert.Equal(0, await Program.Main([flag]).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)); Assert.NotEmpty(output.ToString()); }
        finally { Console.SetOut(before); }
    }
    [Fact(Timeout = 30000)]
    public async Task Default_missing_config_returns_incomplete_and_restores_actual_caller_affinity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Actual standalone caller resource profile requires Windows/Linux.");
        using var actual = System.Diagnostics.Process.GetCurrentProcess(); var beforeMask = ReadActualAffinity(actual);
        var beforeOut = Console.Out; var beforeError = Console.Error; using var output = new StringWriter(); using var errors = new StringWriter(); Console.SetOut(output); Console.SetError(errors);
        try
        {
            Assert.Equal(2, await Program.Main(["capabilities"]).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            using var doc = JsonDocument.Parse(output.ToString()); Assert.Equal("INCOMPLETE", doc.RootElement.GetProperty("Outcome").GetString()); Assert.Equal("TRUSTED_CONFIG_REQUIRED", doc.RootElement.GetProperty("Code").GetString()); Assert.False(doc.RootElement.GetProperty("RuntimeCertified").GetBoolean()); Assert.Empty(errors.ToString()); Assert.Equal(beforeMask, ReadActualAffinity(actual));
        }
        finally { Console.SetOut(beforeOut); Console.SetError(beforeError); }
    }
    [Fact]
    public async Task Bound_configuration_replacement_refuses_before_plan_or_Git_invocation()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-config-bound-" + Guid.NewGuid().ToString("N") + ".json");
        var config = new TrustedConfiguration("nucleoid/delivery-forge", Path.GetTempPath(), "HEAD", Environment.ProcessPath ?? throw new IOException("Actual current test host path unavailable."), OperationSeconds: 20);
        var original = JsonSerializer.SerializeToUtf8Bytes(config, BoundedFiles.Json); File.WriteAllBytes(path, original);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        try
        {
            var selected = new AdmissionFile(path, original.Length, BoundedFiles.Sha(original)); var application = new ToolApplication(actualOriginal: life, actualConfigurationPin: selected);
            var command = CommandLine.Parse(["implement", "--plan", path, "--config", path]);
            var positive = await application.ExecuteAsync(command, TestContext.Current.CancellationToken); Assert.Equal("PLAN_EXPECTATIONS_REQUIRED", positive.Code); Assert.Empty(application.RetainedExternalCalls);
            // Same length, valid JSON, changed identity: no parse failure can
            // accidentally stand in for the whole-byte binding guard.
            var changed = JsonSerializer.SerializeToUtf8Bytes(config with { Repository = "nucleoid/delivery-FORGE" }, BoundedFiles.Json); Assert.Equal(original.Length, changed.Length); File.WriteAllBytes(path, changed);
            var error = await Assert.ThrowsAsync<IOException>(() => application.ExecuteAsync(command, TestContext.Current.CancellationToken));
            Assert.Contains("admission bytes changed", error.Message, StringComparison.Ordinal); Assert.Empty(application.RetainedExternalCalls);
        }
        finally { File.Delete(path); }
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    // Actual default implement route, with deliberately UNVERIFIED admission
    // DATA. An absent image cannot be hashed/born, and no authority is issued.
    [Theory(Timeout = 30000, Skip = "Linux dispatch extent admission requires Linux.", SkipUnless = nameof(IsLinux))]
    [InlineData(63L)]
    [InlineData((long)DeliveryForge.Evidence.OwnedDuplexProcess.MaximumExactLinuxImageBytes + 1)]
    public async Task Linux_implement_refuses_known_unsupported_extent_before_image_read_or_store_effect(long bytes)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-implement-extent-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repo"); Directory.CreateDirectory(repository);
        var hostPath = Path.Combine(root, "absent-worker"); var store = Path.Combine(root, "store");
        var worker = new LocalWorkerPaths(Path.Combine(root, "work"), Path.Combine(root, "results"), ["source.cs"], []);
        AdmissionFile Write<T>(string name, T value)
        {
            var path = Path.Combine(root, name); var raw = JsonSerializer.SerializeToUtf8Bytes(value, BoundedFiles.Json);
            File.WriteAllBytes(path, raw); return new(path, raw.LongLength, BoundedFiles.Sha(raw));
        }
        var host = Write("host.json", new InstalledHostRootAcceptance("UNVERIFIED-REFUSAL-DATA", "linux", "codex.cli", "fixture",
            new(hostPath, bytes, new string('a', 64), "fixture"), new(hostPath + "-tool", 1, new string('b', 64)), [], []));
        var selectedRoot = Write("root.json", new StandaloneRootSelection("DeliveryForgeStandaloneRoot-v1", store, worker, host));
        var slots = new DeliveryForge.Scheduling.ResourceSlots(1, 1, 64, 1, 1, 1);
        var policy = new DeliveryForge.Scheduling.BudgetPolicy("refusal-only", true, slots, TimeSpan.FromSeconds(15), 1, 0);
        var demand = new DeliveryForge.Scheduling.RunDemand(slots, [], TimeSpan.FromSeconds(15), 1, 0);
        var config = Write("config.json", new TrustedConfiguration("fixture/repo", repository, "refs/heads/main", hostPath + "-git",
            AdapterVersion: "fixture", MaximumCeiling: "implement", OperationSeconds: 20, StandaloneRoot: selectedRoot, LocalSingle: new(policy, demand)));
        var beforeOut = Console.Out; var beforeError = Console.Error; using var output = new StringWriter(); using var errors = new StringWriter();
        Console.SetOut(output); Console.SetError(errors);
        try
        {
            Assert.Equal(1, await Program.Main(["implement", "--plan", Path.Combine(root, "absent-plan"), "--config", config.Path, "--json"])
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.Empty(output.ToString()); Assert.Contains("Selected Linux worker image must fit the 64-byte through eleven-MiB exact-image profile before dispatch.", errors.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(hostPath)); Assert.False(Directory.Exists(store));
            Assert.False(Directory.Exists(worker.Worktree)); Assert.False(Directory.Exists(worker.ResultDirectory));
        }
        finally { Console.SetOut(beforeOut); Console.SetError(beforeError); }
    }

    // Shared composition also serves quality-baseline/pr/merge. Constructing
    // UNVERIFIED ports is not worker dispatch, actual scope proof or admission.
    [Theory(Timeout = 30000, Skip = "Linux non-dispatch composition requires Linux.", SkipUnless = nameof(IsLinux))]
    [InlineData("codex.cli")]
    [InlineData("claude-code.cli")]
    [InlineData("pi.cli")]
    public async Task Linux_shared_non_dispatch_composition_does_not_apply_worker_extent_policy(string adapter)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-nondispatch-extent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var absent = Path.Combine(root, "absent-image");
        var image = new DeliveryForge.Evidence.LinuxScopeImage(absent, 1, new string('a', 64));
        var scope = new DeliveryForge.Evidence.LinuxScopeSelection(image, image, "unexecuted-DATA", "unexecuted-DATA", image, [], image);
        var config = new TrustedConfiguration("fixture/repo", root, "refs/heads/main", absent + "-git", Adapter: adapter, AdapterVersion: "unverified-fixture");
        var host = new StandaloneHostSelection(absent, DeliveryForge.Evidence.OwnedDuplexProcess.MaximumExactLinuxImageBytes + 1, new string('b', 64), "unverified-fixture");
        var profile = new DeliveryForge.Execution.Host.AgentAdapterProfile(adapter, host.Version,
            Enum.GetValues<DeliveryForge.Execution.Host.AgentCapability>().Select(capability => new DeliveryForge.Execution.Host.AgentCapabilityEvidence(capability,
                DeliveryForge.Execution.Host.AgentCapabilityStatus.Unverified, "non-dispatch fixture DATA", "fixture:not-conformance")).ToArray());
        var worker = new LocalWorkerPaths(root + "-work", root + "-results", ["source.cs"], []);
        var actual = new StandaloneOwnerComposition(config, root + "-store", worker, original, host, profile, scope);
        try
        {
            Assert.Empty(actual.ActualBridge.RetainedFrames); Assert.Empty(actual.Application.RetainedExternalCalls);
            Assert.All(actual.ActualBridge.AdapterProfile.Capabilities, row => Assert.Equal(DeliveryForge.Execution.Host.AgentCapabilityStatus.Unverified, row.Status));
            Assert.False(File.Exists(absent)); Assert.False(Directory.Exists(worker.Worktree)); Assert.False(Directory.Exists(worker.ResultDirectory));
            original.Check();
        }
        finally { await actual.DisposeAsync(); }
        Assert.False(original.HasRetainedUnknown);
    }
}
