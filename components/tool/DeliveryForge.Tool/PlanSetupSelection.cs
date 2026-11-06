using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Planning;

namespace DeliveryForge.Tool;

public enum PlanSetupPurpose { Revise, Execute }

// The trusted containing agent/operator prepares this DATA from the actual user
// request and independently selected identities, never from an issue label or
// by adopting identity fields supplied by the untrusted bundle itself.
public sealed record ParentPlanSetupSelection(AdmissionFile Bundle, string BundleIdentity, string ContractIdentity,
    string WorkItem, string OldCeiling, PlanSetupPurpose Purpose, string RequestedCeiling);

public sealed record SelectedPlanSetupData(TrustedConfiguration Configuration, StandaloneRootSelection Root,
    string BundleIdentity, string ContractIdentity, string OriginalPlanCeiling, PlanSetupPurpose Purpose)
{
    public bool RuntimeCertified => false;
    public bool ExecutionAuthorityIssued => false;
}

// The setup entrypoint retains this actual task on the same original. It returns
// immutable configuration/root DATA for AutomaticSetup.RebaseSelected only.
// The same actual owner/executor performs all current-source reconciliation.
internal static class PlanSetupSelection
{
    internal static async Task<SelectedPlanSetupData> SelectAsync(AdmissionFile selectedTemplate,
        ParentPlanSetupSelection selected, ProcessCommandExecutor actualExecutor, ToolLifetime original)
    {
        ArgumentNullException.ThrowIfNull(selectedTemplate); ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(actualExecutor); ArgumentNullException.ThrowIfNull(original);
        original.Check();
        if (!Enum.IsDefined(selected.Purpose)) throw new ToolUsageException("Explicit plan setup purpose required.");
        var requested = TrustedConfiguration.Ceiling(selected.RequestedCeiling);
        var oldCeiling = TrustedConfiguration.Ceiling(selected.OldCeiling);
        if (requested < DeliveryForge.Contracts.Models.AuthorizationCeiling.Implement)
            throw new ToolUsageException("Plan revision/execution selection requires an explicit implementation ceiling or higher.");
        var template = TrustedConfiguration.LoadSelected(selectedTemplate.Path, original, selectedTemplate);
        if (requested > TrustedConfiguration.Ceiling(template.MaximumCeiling))
            throw new ToolUsageException("Requested plan setup ceiling exceeds the independently selected owner template.");
        if (TimeSpan.FromSeconds(template.OperationSeconds) < original.WholeLimit)
            original.Restrict(TimeSpan.FromSeconds(template.OperationSeconds));
        var root = AutomaticSetup.ReadOrdinaryTemplateRoot(template, selectedTemplate.Path, original);
        if (selected.Bundle is null || string.IsNullOrWhiteSpace(selected.BundleIdentity) ||
            string.IsNullOrWhiteSpace(selected.ContractIdentity) || string.IsNullOrWhiteSpace(selected.WorkItem))
            throw new ToolUsageException("Independently selected complete previous-plan expectations required.");
        var bundle = InstalledHostAdmission.ReadExact(selected.Bundle, original, PlanImporter.MaximumBundleBytes);
        var source = new PlanImportSource(template.RepositoryRoot, template.MutableReference, template.GitExecutable);
        var expectations = new PlanImportExpectations(selected.BundleIdentity, selected.ContractIdentity, template.Repository,
            selected.WorkItem, template.RepositoryRoot, template.MutableReference, selected.OldCeiling);
        var remaining = original.Remaining;
        var import = await PlanImporter.ImportAndReconcileOwnedAsync(bundle, source, expectations,
            remaining < TimeSpan.FromSeconds(120) ? remaining : TimeSpan.FromSeconds(120), actualExecutor, original,
            cancellationToken: original.Token).ConfigureAwait(false);
        original.Check();
        if (import.Plan is null || !import.DownstreamReady)
            throw new ToolUsageException("Selected plan did not freshly reconcile; no setup selection was produced.");
        using var document = JsonDocument.Parse(import.Plan.CanonicalBytes);
        var material = document.RootElement.GetProperty("plan");
        if (selected.Purpose == PlanSetupPurpose.Execute &&
            (requested > oldCeiling || material.GetProperty("mode").GetString() != "implement"))
            throw new ToolUsageException("Execution selection cannot raise or reinterpret the actual imported plan ceiling/mode.");
        var changes = material.GetProperty("changeMap");
        if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() is < 1 or > 128)
            throw new ToolUsageException("A complete finite imported change map is required.");
        var paths = changes.EnumerateArray().Select(change => change.GetProperty("path").GetString()
            ?? throw new ToolUsageException("Imported change-map path missing.")).ToArray();
        var allowed = ConstrainPaths(paths, root.WorkerPaths);
        // The registration, config, root and selected bundle remain exact DATA
        // throughout reconciliation. This observes no host/runtime acceptance.
        _ = InstalledHostAdmission.ReadExact(selectedTemplate, original, 65536);
        _ = InstalledHostAdmission.ReadExact(template.StandaloneRoot!, original, 65536);
        _ = InstalledHostAdmission.ReadExact(selected.Bundle, original, PlanImporter.MaximumBundleBytes);
        original.Check();
        var narrowedRoot = root with { WorkerPaths = root.WorkerPaths with { AllowedPaths = allowed } };
        var configuration = template with
        {
            MaximumCeiling = selected.RequestedCeiling,
            ExpectedBundleIdentity = selected.BundleIdentity,
            ExpectedContractIdentity = selected.ContractIdentity,
            ExpectedWorkItem = selected.WorkItem,
            ExpectedPlanCeiling = selected.OldCeiling
        };
        // Revise deliberately keeps OLD expectations for plan --previous-plan.
        // It still needs the explicit new typed intent and existing unchanged
        // reviewed-scope/base guards. Execute requires re-selection of the new
        // frozen revision. Neither path creates a writer, lease or capability.
        return new(configuration, narrowedRoot, import.Plan.Identity, import.Plan.ContractIdentity,
            selected.OldCeiling, selected.Purpose);
    }

    internal static IReadOnlyList<string> ConstrainPaths(IReadOnlyList<string> paths, LocalWorkerPaths owner)
    {
        if (owner.AllowedPaths is null || owner.AllowedPaths.Count is < 1 or > 128 ||
            owner.Exclusions is null || owner.Exclusions.Count > 128 || paths.Count is < 1 or > 128)
            throw new ToolUsageException("Finite nonempty owner allowlist and imported file set required.");
        var allow = owner.AllowedPaths.Select(Relative).ToArray();
        var exclusions = owner.Exclusions.Select(Relative).ToArray();
        var selected = paths.Select(Relative).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // Preserve exact plan spellings. Distinct names that alias the same
        // Windows path are ambiguous, not permission to silently drop a file.
        if (OperatingSystem.IsWindows() && selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new ToolUsageException("Imported file scope contains case-colliding Windows paths.");
        foreach (var path in selected)
            if (!allow.Any(prefix => Within(path, prefix)) || exclusions.Any(prefix => Within(path, prefix) || Within(prefix, path)))
                throw new ToolUsageException("Imported file scope is outside the owner allowlist or overlaps an exclusion.");
        return Array.AsReadOnly(selected);
    }

    static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    static bool Within(string path, string prefix) => string.Equals(path, prefix, PathComparison) || path.StartsWith(prefix + "/", PathComparison);
    static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) ||
            Path.IsPathRooted(value) || value.Contains(':') || value.IndexOfAny(['*', '?', '[', ']']) >= 0)
            throw new ToolUsageException("Only complete normalized repository-relative path prefixes are supported.");
        var normalized = value.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new ToolUsageException("Traversal or ambiguous repository path refused.");
        return normalized;
    }
}
