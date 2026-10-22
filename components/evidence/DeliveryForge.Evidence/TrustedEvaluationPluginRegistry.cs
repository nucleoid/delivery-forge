using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Evidence;

/// <summary>A single code-owned registration binding execution and receipt authority metadata.</summary>
public sealed record TrustedEvaluationPluginRegistration(
    string GateId,
    IDeterministicEvaluationPlugin Plugin,
    string? ExpectedToolVersion = null,
    string? ExpectedFormatVersion = null,
    string? ExpectedRuntimeClosureIdentity = null);

/// <summary>
/// Host composition for externally supplied deterministic plugins. Repository policy selects
/// registrations but cannot create them or supply commands.
/// </summary>
public sealed class TrustedEvaluationPluginRegistry
{
    internal DeterministicEvaluationPluginRegistry Plugins { get; }
    internal TrustedGatePluginRegistry Authority { get; }

    public TrustedEvaluationPluginRegistry(IEnumerable<TrustedEvaluationPluginRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var materialized = registrations.ToArray();
        if (materialized.Length == 0)
            throw new EvaluationPluginException("At least one trusted evaluation plugin registration is required.");
        if (materialized.Any(registration => registration is null ||
                string.IsNullOrWhiteSpace(registration.GateId) || registration.Plugin is null))
            throw new EvaluationPluginException("Trusted plugin registrations require a gate id and implementation.");

        Plugins = new DeterministicEvaluationPluginRegistry(
            materialized.Select(registration => registration.Plugin).DistinctBy(plugin => plugin.Id));
        Authority = new TrustedGatePluginRegistry(materialized.Select(registration =>
            new TrustedGatePluginBinding(
                registration.GateId,
                registration.Plugin.Id,
                registration.Plugin.Version,
                registration.Plugin.ExpectedTool,
                registration.ExpectedToolVersion,
                registration.ExpectedFormatVersion,
                registration.ExpectedRuntimeClosureIdentity)));
        Plugins.AssertMatchesAuthorityRegistry(Authority);
    }
}
