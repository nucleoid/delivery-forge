using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeliveryForge.Evidence.Tests;

// UNEXECUTED source cases; the fake text payloads below are never launched.
public sealed class InstalledToolProfileTests
{
    [Fact]
    public void Approved_package_version_and_observed_informational_version_remain_distinct()
    {
        using var fixture = new ProfileFiles();
        var profile = fixture.Load();
        Assert.True(profile.Fixture);
        Assert.Equal("0.1.0-handoff.gshort", profile.Data.PackageVersion);
        Assert.Equal("0.1.0-observed", profile.Version);
        Assert.DoesNotContain(profile.SourceCommit, profile.Version, StringComparison.Ordinal);
        var first = InstalledToolGateProvider.CaptureInstallation(profile, new InstalledToolFiles(TimeSpan.FromSeconds(10)));
        Assert.Single(first);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("package")]
    [InlineData("host")]
    [InlineData("git")]
    [InlineData("extra-payload")]
    public void Current_real_file_positive_precedes_each_payload_or_host_drift_refusal(string target)
    {
        using var fixture = new ProfileFiles(); var profile = fixture.Load();
        InstalledToolGateProvider.CaptureInstallation(profile, new InstalledToolFiles(TimeSpan.FromSeconds(10)));
        File.AppendAllText(target == "extra-payload" ? Path.Combine(fixture.Install, "unreviewed.dll") : fixture.Paths[target], "changed");
        Assert.Throws<EvidenceWriteException>(() => InstalledToolGateProvider.CaptureInstallation(profile, new InstalledToolFiles(TimeSpan.FromSeconds(10))));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("traversal")]
    [InlineData("source")]
    [InlineData("root")]
    [InlineData("cpu")]
    [InlineData("heap")]
    [InlineData("duplicate-payload")]
    [InlineData("unknown-field")]
    public void Explicit_unsupported_or_ambiguous_profiles_never_become_detected_capability(string corruption)
    {
        using var fixture = new ProfileFiles(); var json = JsonNode.Parse(fixture.Bytes)!.AsObject();
        switch (corruption)
        {
            case "format": json["Format"] = "preview"; break;
            case "traversal": json["ExecutableRelativePath"] = "../foreign"; break;
            case "source": json["SourceCommit"] = "short"; break;
            case "root": json["Environment"]!["DOTNET_ROOT"] = Path.GetTempPath(); break;
            case "cpu": json["Environment"]!["DOTNET_PROCESSOR_COUNT"] = "16"; break;
            case "heap": json["Environment"]!["DOTNET_GCHeapHardLimit"] = "0x200000000"; break;
            case "duplicate-payload": json["Payload"]!.AsArray().Add(json["Payload"]![0]!.DeepClone()); break;
            case "unknown-field": json["ApprovedByTool"] = true; break;
        }
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString()); fixture.WriteProfile(bytes);
        Assert.ThrowsAny<Exception>(() => InstalledToolProfile.LoadPinned(fixture.ProfilePath, InstalledToolFiles.Identity(bytes)));
    }

    [Fact]
    public void Profile_hash_is_out_of_band_and_cannot_approve_itself()
    {
        using var fixture = new ProfileFiles();
        Assert.Throws<EvidenceWriteException>(() => InstalledToolProfile.LoadPinned(fixture.ProfilePath, TestEvidence.Identity('9')));
        var duplicate = "{\"Schema\":\"forge-installed-quality-v1\"," + Encoding.UTF8.GetString(fixture.Bytes)[1..];
        var bytes = Encoding.UTF8.GetBytes(duplicate); fixture.WriteProfile(bytes);
        Assert.Throws<EvidenceWriteException>(() => InstalledToolProfile.LoadPinned(fixture.ProfilePath, InstalledToolFiles.Identity(bytes)));
    }

    [Fact]
    public async Task Unsupported_current_owner_refuses_before_tool_birth_or_artifact_writes()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Exercises the non-Windows safety refusal; Windows actual owned-process positives remain separate mandatory tests.");
        using var fixture = new ProfileFiles();
        var profile = fixture.Load();
        // Genuine complete file/profile positive first. The following payloads
        // are nonexecutable DATA and must never be launched by this refusal test.
        Assert.Single(InstalledToolGateProvider.CaptureInstallation(profile, new InstalledToolFiles(TimeSpan.FromSeconds(10))));
        using var consumer = new TempDirectory();
        using var artifacts = new TempDirectory();
        var output = Path.Combine(artifacts.Path, "must-stay-absent");
        var expected = TestEvidence.Repository();
        var provider = new InstalledToolGateProvider(consumer.Path, expected.BaseCommit);
        // If the owner guard is moved after probing, missing original paths or
        // the non-Git consumer fail differently. No fake executor can hide that.
        File.Delete(fixture.Paths["payload"]);
        File.Delete(fixture.Paths["host"]);
        File.Delete(fixture.Paths["git"]);
        var error = await Assert.ThrowsAsync<EvidenceWriteException>(() => provider.RunAsync(profile,
            new InstalledToolGateRequest("quality", TestEvidence.ResolvedPolicy(), expected, "base", null,
                TimeSpan.FromMinutes(5), output), TestContext.Current.CancellationToken));
        Assert.Contains("genuine creation-owned process scope", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(consumer.Path));
    }
    [Fact]
    public async Task Linux_containing_quality_without_selected_scope_refuses_before_effects()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux containing-scope safety boundary; actual Windows execution remains separate.");
        using var fixture = new ProfileFiles();
        var original = new ProfileOriginal(); var containing = new InstalledToolHostScope(original);
        var profile = containing.LoadProfile(fixture.ProfilePath, InstalledToolFiles.Identity(fixture.Bytes));
        using var consumer = new TempDirectory(); using var artifacts = new TempDirectory();
        var output = Path.Combine(artifacts.Path, "must-stay-absent"); var expected = TestEvidence.Repository();
        // A real complete profile read precedes the absence refusal; no tool or Git is usable.
        File.Delete(fixture.Paths["payload"]); File.Delete(fixture.Paths["git"]);
        var error = await Assert.ThrowsAsync<EvidenceWriteException>(() => containing.RunAsync(
            consumer.Path, expected.BaseCommit, "policy.json", TestEvidence.Identity('1'), profile,
            "quality", expected, "base", null, TimeSpan.FromSeconds(10), output, TestContext.Current.CancellationToken));
        Assert.Contains("explicitly selected Linux creation-owned scope", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output)); Assert.Empty(Directory.EnumerateFileSystemEntries(consumer.Path));
        Assert.False(containing.HasUncertainOriginals);
    }
    [Fact]
    public async Task Linux_internal_provider_without_scoped_files_owner_refuses_before_effects()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Linux internal ownership boundary; real Linux positive remains mandatory.");
        using var fixture = new ProfileFiles(); var profile = fixture.Load();
        using var consumer = new TempDirectory(); using var artifacts = new TempDirectory();
        var expected = TestEvidence.Repository(); var output = Path.Combine(artifacts.Path, "must-stay-absent");
        var files = new InstalledToolFiles(TimeSpan.FromSeconds(10));
        var owner = new InstalledToolGateProvider.ActualExecutor(files, new Dictionary<string,string>(), fixture.Paths["git"]);
        File.Delete(fixture.Paths["payload"]); File.Delete(fixture.Paths["git"]);
        var provider = new InstalledToolGateProvider(consumer.Path, expected.BaseCommit);
        var error = await Assert.ThrowsAsync<EvidenceWriteException>(() => provider.RunOwnedAsync(profile,
            new InstalledToolGateRequest("quality", TestEvidence.ResolvedPolicy(), expected, "base", null,
                TimeSpan.FromSeconds(10), output), files, owner, TestContext.Current.CancellationToken));
        Assert.Contains("genuine creation-owned process scope", error.Message, StringComparison.Ordinal);
        Assert.Empty(owner.Calls); Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(consumer.Path));
    }
    [Fact]
    public void Containing_cleanup_closes_real_profile_io_without_a_new_loader_original()
    {
        using var fixture = new ProfileFiles();
        var original = new ProfileOriginal();
        var containing = new InstalledToolHostScope(original);
        // This complete actual file/profile positive precedes the original closing.
        // The fixture contains nonexecutable DATA; no tool outcome is injected.
        Assert.True(containing.LoadProfile(fixture.ProfilePath,
            InstalledToolFiles.Identity(fixture.Bytes)).Fixture);
        original.BeginFailureCleanup();
        var refusal = Assert.Throws<TimeoutException>(() => containing.LoadProfile(
            fixture.ProfilePath, InstalledToolFiles.Identity(fixture.Bytes)));
        Assert.Equal("Actual containing profile WORK is closed.", refusal.Message);
        Assert.Equal(1, original.CleanupEntries);
    }

    sealed class ProfileOriginal : IOwnedDuplexBudget, IOwnedFailureCleanup
    {
        bool closing;
        public int CleanupEntries { get; private set; }
        public long OriginalStarted { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(30);
        public TimeSpan WorkLimit => TimeSpan.FromSeconds(20);
        public CancellationToken Token => TestContext.Current.CancellationToken;
        public void CheckWork()
        {
            Token.ThrowIfCancellationRequested();
            if (closing) throw new TimeoutException("Actual containing profile WORK is closed.");
        }
        public TimeSpan CleanupRemaining => TimeSpan.FromSeconds(10) -
            System.Diagnostics.Stopwatch.GetElapsedTime(OriginalStarted);
        public void BeginFailureCleanup() { if (!closing) { closing = true; CleanupEntries++; } }
    }

    private sealed class ProfileFiles : IDisposable
    {
        private readonly TempDirectory _temp = new();
        internal string Install { get; }
        internal string ProfilePath { get; }
        internal Dictionary<string, string> Paths { get; } = new(StringComparer.Ordinal);
        internal byte[] Bytes { get; }
        internal ProfileFiles()
        {
            Install = Path.Combine(_temp.Path, "install"); Directory.CreateDirectory(Install);
            ProfilePath = Path.Combine(_temp.Path, "profile.json");
            foreach (var key in new[] { "payload", "package", "host", "git" })
            {
                var path = Path.Combine(key == "payload" ? Install : _temp.Path, key + ".bin");
                File.WriteAllText(path, "NONEXECUTABLE DATA " + key); Paths.Add(key, path);
            }
            var environment = new Dictionary<string, string>
            {
                ["DOTNET_ROOT"] = _temp.Path,
                ["DOTNET_HOST_PATH"] = Paths["host"],
                ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
                ["DOTNET_PROCESSOR_COUNT"] = "2",
                ["DOTNET_GCHeapHardLimit"] = "0x100000000",
                ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1",
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_ROOT_X64"] = _temp.Path,
                ["DOTNET_ROOT_X86"] = _temp.Path,
                ["DOTNET_ROOT_ARM64"] = _temp.Path
            };
            foreach (var hook in new[] { "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "CORECLR_ENABLE_PROFILING",
                "COR_ENABLE_PROFILING", "MSBuildSDKsPath", "MSBUILD_EXE_PATH" }) environment[hook] = "";
            var payload = File.ReadAllBytes(Paths["payload"]);
            var data = new InstalledToolProfile.ProfileData("forge-installed-quality-v1", "mutate4csharp", "0.1.0-observed",
                "0.1.0-handoff.gshort", new string('a', 40), "2", Install, "payload.bin", Paths["package"], Hash(Paths["package"]),
                [new("payload.bin", payload.Length, InstalledToolFiles.Identity(payload))], Paths["host"], Hash(Paths["host"]), "10.0.103",
                Paths["git"], Hash(Paths["git"]), "net10.0", "vstest", "coverlet.collector/6.0.4", "mutate4csharp.json", TestEvidence.Identity('1'),
                environment, ["FORMAT/FILE ONLY: nothing executed"], true);
            Bytes = JsonSerializer.SerializeToUtf8Bytes(data); WriteProfile(Bytes);
        }
        internal void WriteProfile(byte[] bytes) => File.WriteAllBytes(ProfilePath, bytes);
        internal InstalledToolProfile Load() => InstalledToolProfile.LoadPinned(ProfilePath, InstalledToolFiles.Identity(Bytes));
        private static string Hash(string path) => InstalledToolFiles.Identity(File.ReadAllBytes(path));
        public void Dispose() => _temp.Dispose();
    }
}