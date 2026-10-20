using DeliveryForge.Contracts.Models;
namespace DeliveryForge.Tool;

public sealed partial class ToolApplication
{
    async Task<ToolResult> HistoricalQualityAsync(ToolCommand command, TrustedConfiguration config, ToolLifetime life)
    {
        if (config.HistoricalQuality is null) throw new ToolUsageException("quality-baseline requires independently selected historical quality inputs.");
        if (config.ExpectedBundleIdentity is null || config.ExpectedContractIdentity is null || config.ExpectedWorkItem is null || config.ExpectedPlanCeiling is null)
            return ToolResult.Incomplete("PLAN_EXPECTATIONS_REQUIRED", "Independent exact plan/P/issue expectations are required before historical gates.");
        if (TrustedConfiguration.Ceiling(config.ExpectedPlanCeiling) < AuthorizationCeiling.Implement ||
            TrustedConfiguration.Ceiling(config.ExpectedPlanCeiling) > TrustedConfiguration.Ceiling(config.MaximumCeiling) || TrustedConfiguration.Ceiling(config.MaximumCeiling) < AuthorizationCeiling.Implement)
            throw new ToolUsageException("Historical gate execution exceeds the independently selected immutable ceiling.");
        var bytes = BoundedFiles.Read(command.Get("plan")!, life);
        var imported = await Observe("ActualHistoricalPlanImport", () => ImportPlan(bytes, config, life), life).ConfigureAwait(false);
        if (imported.Plan is null || !imported.DownstreamReady) return ToolResult.Incomplete("PLAN_RECONCILIATION_INCOMPLETE", "Historical plan/P failed fresh reconciliation.", imported.Diagnostics);
        if (command.Has("dry-run")) return new("DRY_RUN", "HISTORICAL_QUALITY_PREPARED", "Historical plan freshly reconciled; no gate or worker started.", new { imported.Plan.Identity, BaselineApproved = false });
        if (host is null || assignments is null || !host.HasHistoricalQuality || actualOriginal is null || actualConfigurationPin is null || !ReferenceEquals(life, actualOriginal))
            return ToolResult.Incomplete("HISTORICAL_QUALITY_OWNER_REQUIRED", "Historical gates require the closed same-original standalone owner composition.");
        var request = new ParentAssignmentRequest(imported.Plan, config, null, null);
        var assignment = await SelectAssignmentAsync(request, life).ConfigureAwait(false);
        RequireAssignment(assignment, request);
        host.BindCoordinationSelection(imported.Plan, assignment, config, life);
        return await host.HistoricalQualityAsync(assignment, life).ConfigureAwait(false);
    }
}
