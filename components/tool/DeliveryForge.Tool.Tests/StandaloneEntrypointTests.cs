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

    // These are actual composition constructor calls with no scope/native owner.
    // Empty profile DATA cannot promote capabilities; rejection must precede dispatch.
    [Theory(Timeout = 30000, Skip = "Linux composition extent admission requires Linux.", SkipUnless = nameof(IsLinux))]
    [InlineData("codex.cli", 63L)]
    [InlineData("claude-code.cli", 63L)]
    [InlineData("pi.cli", 63L)]
    [InlineData("codex.cli", (long)DeliveryForge.Evidence.OwnedDuplexProcess.MaximumExactLinuxImageBytes + 1)]
    [InlineData("claude-code.cli", (long)DeliveryForge.Evidence.OwnedDuplexProcess.MaximumExactLinuxImageBytes + 1)]
    [InlineData("pi.cli", (long)DeliveryForge.Evidence.OwnedDuplexProcess.MaximumExactLinuxImageBytes + 1)]
    public void Linux_composition_refuses_known_unsupported_extent_before_store_or_dispatch(string adapter, long bytes)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-composition-extent-" + Guid.NewGuid().ToString("N"));
        var sentinel = new byte[] { 17, 29, 43 }; File.WriteAllBytes(root, sentinel);
        using var original = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        try
        {
            var config = new TrustedConfiguration("fixture/repo", Path.GetTempPath(), "HEAD", root + "-git", Adapter: adapter, AdapterVersion: "extent-fixture");
            var selected = new StandaloneHostSelection(root + "-absent-host", bytes, new string('a', 64), "extent-fixture");
            var profile = new DeliveryForge.Execution.Host.AgentAdapterProfile(adapter, "extent-fixture", []);
            var worker = new LocalWorkerPaths(root + "-worker", root + "-results", ["source.cs"], []);
            // The store is deliberately a file: a later store inspection cannot
            // accidentally satisfy the exact selected-extent refusal assertion.
            var refusal = Assert.Throws<ToolUsageException>(() => new StandaloneOwnerComposition(config, root, worker, original, selected, profile));
            Assert.Equal("INCOMPLETE: Selected Linux worker image must fit the 64-byte through eleven-MiB exact-image profile before dispatch.", refusal.Message);
            Assert.Equal(sentinel, File.ReadAllBytes(root)); Assert.False(File.Exists(selected.Executable));
            Assert.False(Directory.Exists(worker.Worktree)); Assert.False(Directory.Exists(worker.ResultDirectory));
            Assert.False(original.HasRetainedUnknown); original.Check();
        }
        finally { File.Delete(root); }
    }

    [Theory(Timeout = 30000, Skip = "Linux composition extent admission requires Linux.", SkipUnless = nameof(IsLinux))]
    [InlineData("codex.cli", 64L)]
    [InlineData("claude-code.cli", 64L)]
    [InlineData("pi.cli", 64L)]
    [InlineData("codex.cli", (long)DeliveryForge.Evidence.OwnedDuplexProcess.MaximumExactLinuxImageBytes)]
    [InlineData("claude-code.cli", (long)DeliveryForge.Evidence.OwnedDuplexProcess.MaximumExactLinuxImageBytes)]
    [InlineData("pi.cli", (long)DeliveryForge.Evidence.OwnedDuplexProcess.MaximumExactLinuxImageBytes)]
    public void Linux_composition_inclusive_extent_boundaries_still_require_actual_scope(string adapter, long bytes)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-composition-boundary-" + Guid.NewGuid().ToString("N"));
        using var original = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var config = new TrustedConfiguration("fixture/repo", Path.GetTempPath(), "HEAD", root + "-absent-git", Adapter: adapter, AdapterVersion: "extent-fixture");
        var selected = new StandaloneHostSelection(root + "-absent-host", bytes, new string('a', 64), "extent-fixture");
        var profile = new DeliveryForge.Execution.Host.AgentAdapterProfile(adapter, "extent-fixture", []);
        var worker = new LocalWorkerPaths(root + "-worker", root + "-results", ["source.cs"], []);
        var refusal = Assert.Throws<ToolUsageException>(() => new StandaloneOwnerComposition(config, root, worker, original, selected, profile));
        Assert.Equal("Actual selected Linux scope profile required before native use.", refusal.Message);
        Assert.False(File.Exists(selected.Executable)); Assert.False(Directory.Exists(root));
        Assert.False(Directory.Exists(worker.Worktree)); Assert.False(Directory.Exists(worker.ResultDirectory));
        Assert.False(original.HasRetainedUnknown); original.Check();
    }
}
