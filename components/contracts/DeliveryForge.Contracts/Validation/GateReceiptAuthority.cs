using System.Text.Json;

namespace DeliveryForge.Contracts.Validation;

/// <summary>
/// Code-owned authority boundary for gate receipts. Schema-v1 receipts remain readable
/// historical data, but can never satisfy an authoritative PASS gate.
/// </summary>
public static class GateReceiptAuthority
{
    public static bool TryValidateAuthoritativePass(
        JsonElement receipt,
        out string reason,
        TrustedGatePluginRegistry? registry = null)
    {
        reason = "Gate PASS lacks a canonical authority binding.";
        if (!receipt.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.GetString() != "2.0.0" ||
            receipt.GetProperty("outcome").GetString() != "PASS" ||
            !receipt.TryGetProperty("authorityBinding", out var binding) ||
            binding.ValueKind != JsonValueKind.Object)
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
            !Same(binding, "status", "EXECUTED") ||
            !Same(binding, "authority", "production") ||
            !True(binding, "enabled") || !True(binding, "productionCapable"))
        {
            reason = "Gate PASS authority binding is mismatched, advisory, disabled, or impersonates the trusted gate/plugin mapping.";
            return false;
        }

        foreach (var field in new[] { "toolVersion", "toolIdentity", "configurationIdentity", "inputSetIdentity" })
        {
            if (!binding.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                reason = $"Gate PASS authority binding omits {field}.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool Same(JsonElement value, string name, string? expected) =>
        value.TryGetProperty(name, out var actual) && actual.ValueKind == JsonValueKind.String &&
        string.Equals(actual.GetString(), expected, StringComparison.Ordinal);

    private static bool True(JsonElement value, string name) =>
        value.TryGetProperty(name, out var actual) && actual.ValueKind == JsonValueKind.True;
}
