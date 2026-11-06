using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;

public sealed class ConfiguredReadinessTests
{
    sealed class Fixture
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("forge-readiness-").FullName;
        internal string ConfigPath => Path.Combine(Root, "config.json");
        internal string RootPath => Path.Combine(Root, "root.json");
        internal string AdmissionPath => Path.Combine(Root, "host.json");
        internal TrustedConfiguration Configuration;
        internal Fixture(string kind = "DeliveryForgeHostNotAdmitted-v1")
        {
            var admission = Write(AdmissionPath, new { Kind = kind });
            var root = new StandaloneRootSelection("DeliveryForgeStandaloneRoot-v1", Path.Combine(Root, "store"),
                new(Path.Combine(Root, "work"), Path.Combine(Root, "results"), ["change.cs"], []), admission);
            Configuration = new("selected/repository", Path.Combine(Root, "repository"), "HEAD",
                typeof(ToolApplication).Assembly.Location, StandaloneRoot: Write(RootPath, root));
            Save();
        }
        internal static AdmissionFile Write(string path, object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, BoundedFiles.Json);
            File.WriteAllBytes(path, bytes);
            return new(path, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        internal void Save() => Write(ConfigPath, Configuration);
        internal Task<ToolResult> Inspect(ToolApplication? application = null) => (application ?? new()).ExecuteAsync(
            CommandLine.Parse(["capabilities", "--config", ConfigPath]), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task No_config_is_explicitly_a_catalog_not_an_execution_preflight()
    {
        var result = await new ToolApplication().ExecuteAsync(CommandLine.Parse(["capabilities"]), TestContext.Current.CancellationToken);
        Assert.False(result.RuntimeCertified);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, BoundedFiles.Json));
        Assert.Equal("CapabilityCatalog", json.RootElement.GetProperty("DiagnosticKind").GetString());
        Assert.False(json.RootElement.GetProperty("IsExecutionPreflight").GetBoolean());
    }

    [Fact]
    public async Task Selected_plan_config_is_read_and_reports_its_actual_missing_execution_selections_without_effects()
    {
        var fixture = new Fixture(); var application = new ToolApplication();
        var before = Directory.GetFileSystemEntries(fixture.Root).Order().ToArray();
        var result = await fixture.Inspect(application);
        Assert.Equal("CONFIGURED_READINESS_OBSERVED", result.Code); Assert.False(result.RuntimeCertified);
        var data = Assert.IsType<ConfiguredReadinessReport>(result.Data);
        Assert.Equal("selected/repository", data.Repository); Assert.Equal("plan", data.MaximumCeiling);
        Assert.Equal(!OperatingSystem.IsLinux(), data.Plan.ConfigurationComplete);
        Assert.False(data.Implement.ConfigurationComplete); Assert.Equal("NotAdmitted", data.HostAdmissionSelection);
        Assert.Contains("IMPLEMENT_AUTHORIZATION_REQUIRED", data.Implement.MissingPrerequisites);
        Assert.Contains("PLAN_EXPECTATIONS_REQUIRED", data.Implement.MissingPrerequisites);
        Assert.Contains("INSTALLED_HOST_ADMISSION_REQUIRED", data.Implement.MissingPrerequisites);
        Assert.Contains("LOCAL_SINGLE_POLICY_REQUIRED", data.Implement.MissingPrerequisites);
        Assert.Contains("CONTINUUM_PARENT_CONFIGURATION_REQUIRED", data.Implement.MissingPrerequisites);
        Assert.False(data.NativeDispatchEvaluated); Assert.False(data.IsExecutionPreflight);
        Assert.False(data.CapabilityIssued); Assert.False(data.RuntimeCertified);
        Assert.Empty(application.RetainedExternalCalls); Assert.Empty(application.RetainedAssignments);
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.Root).Order().ToArray());
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "store")));
    }

    [Theory]
    [InlineData(30, 120, 30)]
    [InlineData(120, 30, 30)]
    public async Task Selected_operation_budget_bounds_diagnostics_without_extending_original(int configured, int originalSeconds, int expected)
    {
        var fixture = new Fixture(); fixture.Configuration = fixture.Configuration with { OperationSeconds = configured }; fixture.Save();
        using var original = new ToolLifetime(TimeSpan.FromSeconds(originalSeconds), TestContext.Current.CancellationToken);
        var result = await fixture.Inspect(new ToolApplication(actualOriginal: original));
        Assert.Equal("CONFIGURED_READINESS_OBSERVED", result.Code);
        Assert.Equal(TimeSpan.FromSeconds(expected), original.WholeLimit);
    }

    [Fact]
    public async Task Accepted_looking_receipt_shape_never_becomes_runtime_acceptance()
    {
        var fixture = new Fixture("RootActualInstalledHostConformanceAcceptance-v1");
        var data = Assert.IsType<ConfiguredReadinessReport>((await fixture.Inspect()).Data);
        Assert.Equal("ReceiptSelectedNotVerified", data.HostAdmissionSelection);
        Assert.Contains("INSTALLED_HOST_ADMISSION_VERIFICATION_REQUIRED", data.UnevaluatedExecutionPrerequisites);
        Assert.False(data.RuntimeCertified); Assert.False(data.CapabilityIssued);
    }

    [Fact]
    public async Task Missing_root_is_reported_as_a_configuration_prerequisite()
    {
        var fixture = new Fixture(); fixture.Configuration = fixture.Configuration with { StandaloneRoot = null }; fixture.Save();
        var data = Assert.IsType<ConfiguredReadinessReport>((await fixture.Inspect()).Data);
        Assert.Contains("STANDALONE_ROOT_REQUIRED", data.Plan.MissingPrerequisites);
        Assert.False(data.Plan.ConfigurationComplete);
    }

    [Fact]
    public async Task Plan_ceiling_revision_is_not_inferred_from_an_implement_maximum()
    {
        var fixture = new Fixture(); fixture.Configuration = fixture.Configuration with
        {
            MaximumCeiling = "implement",
            ExpectedBundleIdentity = "selected-bundle",
            ExpectedContractIdentity = "selected-contract",
            ExpectedWorkItem = "selected/repository#8",
            ExpectedPlanCeiling = "plan"
        }; fixture.Save();
        var data = Assert.IsType<ConfiguredReadinessReport>((await fixture.Inspect()).Data);
        Assert.DoesNotContain("IMPLEMENT_AUTHORIZATION_REQUIRED", data.Implement.MissingPrerequisites);
        Assert.Contains("IMPLEMENT_PLAN_REVISION_REQUIRED", data.Implement.MissingPrerequisites);
    }

    [Theory]
    [InlineData("missing-config")]
    [InlineData("malformed-config")]
    [InlineData("missing-root")]
    [InlineData("changed-root")]
    [InlineData("malformed-admission")]
    public async Task Selected_missing_changed_or_malformed_inputs_cannot_fall_back_to_catalog(string failure)
    {
        var fixture = new Fixture(); var application = new ToolApplication();
        switch (failure)
        {
            case "missing-config": File.Delete(fixture.ConfigPath); break;
            case "malformed-config": File.WriteAllText(fixture.ConfigPath, "{"); break;
            case "missing-root": File.Delete(fixture.RootPath); break;
            case "changed-root": File.AppendAllText(fixture.RootPath, " "); break;
            case "malformed-admission":
                var selected = Fixture.Write(fixture.AdmissionPath, new { NotKind = true });
                fixture.Configuration = fixture.Configuration with
                {
                    StandaloneRoot = Fixture.Write(fixture.RootPath,
                    new StandaloneRootSelection("DeliveryForgeStandaloneRoot-v1", Path.Combine(fixture.Root, "store"),
                        new(Path.Combine(fixture.Root, "work"), Path.Combine(fixture.Root, "results"), ["change.cs"], []), selected))
                };
                fixture.Save(); break;
        }
        var error = await Record.ExceptionAsync(() => fixture.Inspect(application));
        Assert.NotNull(error); Assert.Empty(application.RetainedExternalCalls); Assert.Empty(application.RetainedAssignments);
    }
}
