using System.Text.Json;
namespace DeliveryForge.Tool;

public sealed record ModeConfigurationReadiness(bool ConfigurationComplete, IReadOnlyList<string> MissingPrerequisites);
public sealed record ConfiguredReadinessReport(string DiagnosticKind, string Profile, string Repository, string MaximumCeiling,
    ModeConfigurationReadiness Plan, ModeConfigurationReadiness Implement, string HostAdmissionSelection,
    IReadOnlyList<string> UnevaluatedExecutionPrerequisites, bool IsExecutionPreflight = false,
    bool NativeDispatchEvaluated = false, bool CapabilityIssued = false, bool RuntimeCertified = false);

// Selected configuration DATA only. Never constructs an owner, verifies host
// conformance, binds RuntimeTrust, or probes a native/coordination endpoint.
internal static class ConfiguredReadiness
{
    internal static ToolResult Inspect(string profile, string configPath, TrustedConfiguration config, ToolLifetime life)
    {
        life.Check();
        var plan = new List<string>(); var implement = new List<string>();
        var admission = "NotSelected";
        if (config.ProductionBatch is not null)
        {
            plan.Add("SINGLE_REPOSITORY_CONFIGURATION_REQUIRED");
            implement.Add("BATCH_CONFIGURATION_PREFLIGHT_REQUIRED");
        }
        else if (config.StandaloneRoot is null) plan.Add("STANDALONE_ROOT_REQUIRED");
        else
        {
            var bytes = InstalledHostAdmission.ReadExact(config.StandaloneRoot, life, 65536);
            _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(bytes);
            var root = JsonSerializer.Deserialize<StandaloneRootSelection>(bytes, BoundedFiles.Json)
                ?? throw new ToolUsageException("Selected standalone root is null.");
            StandaloneEntrypoint.SeparateSelection(config, configPath, root);
            if (OperatingSystem.IsLinux() && root.LinuxScopeProfile is null) plan.Add("LINUX_SCOPE_PROFILE_REQUIRED");
            // Inspect only the bounded selected marker/receipt kind. Even an
            // accepted-looking receipt is not verified or promoted to authority.
            var selected = InstalledHostAdmission.ReadExact(root.InstalledHostAdmission, life, 1048576);
            using var document = JsonDocument.Parse(selected);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                throw new ToolUsageException("Selected host admission DATA requires a kind.");
            admission = kind.GetString() == "RootActualInstalledHostConformanceAcceptance-v1"
                ? "ReceiptSelectedNotVerified" : "NotAdmitted";
        }
        implement.AddRange(plan);
        if (admission != "ReceiptSelectedNotVerified") implement.Add("INSTALLED_HOST_ADMISSION_REQUIRED");
        if (TrustedConfiguration.Ceiling(config.MaximumCeiling) < DeliveryForge.Contracts.Models.AuthorizationCeiling.Implement)
            implement.Add("IMPLEMENT_AUTHORIZATION_REQUIRED");
        if (config.ExpectedBundleIdentity is null || config.ExpectedContractIdentity is null ||
            config.ExpectedWorkItem is null || config.ExpectedPlanCeiling is null) implement.Add("PLAN_EXPECTATIONS_REQUIRED");
        if (config.ExpectedPlanCeiling is not null &&
            TrustedConfiguration.Ceiling(config.ExpectedPlanCeiling) < DeliveryForge.Contracts.Models.AuthorizationCeiling.Implement)
            implement.Add("IMPLEMENT_PLAN_REVISION_REQUIRED");
        if (config.LocalSingle is null) implement.Add("LOCAL_SINGLE_POLICY_REQUIRED");
        if (config.Coordination is null) implement.Add("CONTINUUM_PARENT_CONFIGURATION_REQUIRED");
        life.Check();
        var data = new ConfiguredReadinessReport("SelectedConfiguration", profile, config.Repository, config.MaximumCeiling,
            new(plan.Count == 0, plan.ToArray()), new(implement.Count == 0, implement.ToArray()), admission,
            ["CURRENT_PLAN_RECONCILIATION_REQUIRED", "INSTALLED_HOST_ADMISSION_VERIFICATION_REQUIRED",
                "SAME_ORIGINAL_RESOURCE_AND_COORDINATION_ADMISSION_REQUIRED"]);
        return new("COMPLETE", "CONFIGURED_READINESS_OBSERVED",
            "Selected configuration inspected; configuration completeness is not execution readiness or runtime certification.", data);
    }
}
