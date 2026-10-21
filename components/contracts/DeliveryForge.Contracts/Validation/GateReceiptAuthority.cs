using System.Text.Json;

namespace DeliveryForge.Contracts.Validation;

/// <summary>
/// Code-owned authority boundary for policy-satisfying gate outcomes. Schema-v1
/// receipts remain readable historical data but are never authoritative.
/// Receipt provenance is supplied by the host-owned append-only receipt store;
/// the content identity is integrity evidence, not a signature.
/// </summary>
public static class GateReceiptAuthority
{
    public static bool TryValidateAuthoritativeOutcome(
        JsonElement receipt,
        out string reason,
        TrustedGatePluginRegistry? registry = null)
    {
        reason = "Gate outcome lacks a canonical authority binding.";
        if (!receipt.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.GetString() != "2.0.0" ||
            !receipt.TryGetProperty("outcome", out var outcomeElement) ||
            !receipt.TryGetProperty("authorityBinding", out var binding) ||
            binding.ValueKind != JsonValueKind.Object)
            return false;

        var outcome = outcomeElement.GetString();
        if (outcome is not ("PASS" or "NOT_APPLICABLE"))
            return false;
        var gateId = receipt.GetProperty("gateId").GetString()!;
        if (!(registry ?? TrustedGatePluginRegistry.BuiltIns).TryGet(gateId, out var trusted))
        {
            reason = $"Gate '{gateId}' has no trusted code-owned plugin binding.";
            return false;
        }

        if (!Same(binding, "gateId", gateId) ||
            !Same(binding, "policyIdentity", receipt.GetProperty("policyIdentity").GetString()) ||
            !Same(binding, "headCommit", receipt.GetProperty("headCommit").GetString()) ||
            !Same(binding, "pluginId", trusted.PluginId) ||
            !Same(binding, "pluginVersion", trusted.PluginVersion) ||
            !Same(binding, "tool", trusted.Tool) ||
            !Same(binding, "status", outcome == "PASS" ? "EXECUTED" : "NOT_APPLICABLE") ||
            !Same(binding, "authority", "production") ||
            !True(binding, "enabled") || !True(binding, "productionCapable"))
        {
            reason = "Gate authority binding is mismatched, advisory, disabled, or impersonates the trusted gate/plugin mapping.";
            return false;
        }

        foreach (var field in new[] { "toolVersion", "toolIdentity", "configurationIdentity", "runtimeClosureIdentity", "inputSetIdentity" })
        {
            if (!binding.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                reason = $"Gate authority binding omits {field}.";
                return false;
            }
        }

        if (outcome == "NOT_APPLICABLE" &&
            (!receipt.TryGetProperty("notApplicableRationale", out var rationale) ||
             rationale.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(rationale.GetString())))
        {
            reason = "Authoritative NOT_APPLICABLE evidence requires a concrete rationale.";
            return false;
        }

        if (!receipt.TryGetProperty("command", out var commandValue) ||
            commandValue.ValueKind != JsonValueKind.String ||
            !TryValidateCommandEnvelope(commandValue.GetString()!, binding, trusted, out reason))
            return false;

        if (trusted.ExpectedRuntimeClosureIdentity is not null &&
            !Same(binding, "runtimeClosureIdentity", trusted.ExpectedRuntimeClosureIdentity))
        {
            reason = "Gate authority binding does not use the registry-pinned executable runtime closure.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool TryValidateAuthoritativePass(
        JsonElement receipt,
        out string reason,
        TrustedGatePluginRegistry? registry = null)
    {
        if (!receipt.TryGetProperty("outcome", out var outcome) || outcome.GetString() != "PASS")
        {
            reason = "Gate receipt is not an authoritative PASS.";
            return false;
        }
        return TryValidateAuthoritativeOutcome(receipt, out reason, registry);
    }

    private static bool TryValidateCommandEnvelope(
        string serialized,
        JsonElement binding,
        TrustedGatePluginBinding trusted,
        out string reason)
    {
        try
        {
            using var document = JsonDocument.Parse(serialized);
            var command = document.RootElement;
            if (command.ValueKind != JsonValueKind.Object ||
                !Same(binding, "tool", Text(command, "capabilityTool")) ||
                !Same(binding, "toolVersion", Text(command, "capabilityVersion")) ||
                !Same(binding, "toolIdentity", Text(command, "executableIdentity")) ||
                !Same(binding, "configurationIdentity", Text(command, "configurationIdentity")) ||
                !command.TryGetProperty("plugin", out var plugin) ||
                !Same(binding, "runtimeClosureIdentity", Text(plugin, "runtimeClosureIdentity")) ||
                !Same(binding, "inputSetIdentity", Text(plugin, "inputSetIdentity")))
            {
                reason = "Gate authority binding disagrees with its immutable command/configuration/input envelope.";
                return false;
            }
            if ((trusted.ExpectedToolVersion is not null &&
                  !string.Equals(Text(command, "capabilityVersion"), trusted.ExpectedToolVersion, StringComparison.Ordinal)) ||
                 (trusted.ExpectedFormatVersion is not null &&
                  !string.Equals(Text(command, "capabilityFormat"), trusted.ExpectedFormatVersion, StringComparison.Ordinal)))
            {
                reason = "Gate authority binding does not use the trusted pinned tool version/format.";
                return false;
            }
            reason = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            reason = "Authoritative gate command must be the canonical structured execution envelope.";
            return false;
        }
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var actual) && actual.ValueKind == JsonValueKind.String
            ? actual.GetString()
            : null;

    private static bool Same(JsonElement value, string name, string? expected) =>
        value.TryGetProperty(name, out var actual) && actual.ValueKind == JsonValueKind.String &&
        string.Equals(actual.GetString(), expected, StringComparison.Ordinal);

    private static bool True(JsonElement value, string name) =>
        value.TryGetProperty(name, out var actual) && actual.ValueKind == JsonValueKind.True;
}
