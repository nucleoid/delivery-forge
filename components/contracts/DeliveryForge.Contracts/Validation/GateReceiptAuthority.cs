using System.Text.Json;

namespace DeliveryForge.Contracts.Validation;

/// <summary>
/// Code-owned authority boundary for gate receipts. Schema-v1 receipts without this additive
/// binding remain readable historical data, but can never satisfy an authoritative PASS gate.
/// </summary>
public static class GateReceiptAuthority
{
    private sealed record TrustedBinding(string PluginId, string PluginVersion, string Tool);

    private static readonly IReadOnlyDictionary<string, TrustedBinding> TrustedBindings =
        new Dictionary<string, TrustedBinding>(StringComparer.Ordinal)
        {
            ["build"] = new("delivery-forge.dotnet", "1.0.0", "dotnet"),
            ["test"] = new("delivery-forge.dotnet", "1.0.0", "dotnet"),
            ["coverage"] = new("delivery-forge.dotnet", "1.0.0", "dotnet"),
            ["mutation"] = new("delivery-forge.mutate4csharp", "1.0.0", "mutate4csharp"),
            ["anti-slop"] = new("dmmulroy.anti-slop", "0.1.2+13909c9e07590f5a85d93c62712259fe14a883f7", "oxlint"),
            ["ci"] = new("delivery-forge.dotnet", "1.0.0", "dotnet")
        };

    public static bool TryValidateAuthoritativePass(JsonElement receipt, out string reason)
    {
        reason = "Gate PASS lacks a canonical authority binding.";
        if (receipt.GetProperty("outcome").GetString() != "PASS" ||
            !receipt.TryGetProperty("authorityBinding", out var binding) ||
            binding.ValueKind != JsonValueKind.Object)
            return false;

        var gateId = receipt.GetProperty("gateId").GetString()!;
        if (!TrustedBindings.TryGetValue(gateId, out var trusted))
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

    public static IReadOnlyList<(string GateId, string PluginId, string PluginVersion, string Tool)> BuiltIns =>
        TrustedBindings.Select(pair => (pair.Key, pair.Value.PluginId, pair.Value.PluginVersion, pair.Value.Tool)).ToArray();

    private static bool Same(JsonElement value, string name, string? expected) =>
        value.TryGetProperty(name, out var actual) && actual.ValueKind == JsonValueKind.String &&
        string.Equals(actual.GetString(), expected, StringComparison.Ordinal);

    private static bool True(JsonElement value, string name) =>
        value.TryGetProperty(name, out var actual) && actual.ValueKind == JsonValueKind.True;
}
