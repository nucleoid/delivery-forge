namespace DeliveryForge.Contracts.Validation;

public sealed record TrustedGatePluginBinding(
    string GateId,
    string PluginId,
    string PluginVersion,
    string Tool,
    string? ExpectedToolVersion = null,
    string? ExpectedFormatVersion = null);

/// <summary>
/// Code-owned gate/plugin authority. Hosts may construct an extended registry in trusted
/// application code; repository policy and patch content cannot add registrations.
/// </summary>
public sealed class TrustedGatePluginRegistry
{
    private readonly IReadOnlyDictionary<string, TrustedGatePluginBinding> _bindings;

    public TrustedGatePluginRegistry(IEnumerable<TrustedGatePluginBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var indexed = new Dictionary<string, TrustedGatePluginBinding>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.GateId) ||
                string.IsNullOrWhiteSpace(binding.PluginId) ||
                string.IsNullOrWhiteSpace(binding.PluginVersion) ||
                string.IsNullOrWhiteSpace(binding.Tool))
                throw new ArgumentException("Trusted gate/plugin registrations require stable non-empty identities.", nameof(bindings));
            if (!indexed.TryAdd(binding.GateId, binding))
                throw new ArgumentException($"Duplicate trusted gate registration '{binding.GateId}'.", nameof(bindings));
        }
        _bindings = indexed;
    }

    public static TrustedGatePluginRegistry BuiltIns { get; } = new(
    [
        new("build", "delivery-forge.dotnet", "1.0.0", "dotnet", "10.0.401", "trx-v1"),
        new("test", "delivery-forge.dotnet", "1.0.0", "dotnet", "10.0.401", "trx-v1"),
        new("coverage", "delivery-forge.dotnet", "1.0.0", "dotnet", "10.0.401", "trx-v1"),
        new("crap", "delivery-forge.crap4csharp", "1.0.0", "crap4csharp", "0.1.0+1129c9d3280168b294f9fed1542f81ca716c6d53", "1.0"),
        new("crap4csharp", "delivery-forge.crap4csharp", "1.0.0", "crap4csharp", "0.1.0+1129c9d3280168b294f9fed1542f81ca716c6d53", "1.0"),
        new("mutation", "delivery-forge.mutate4csharp", "1.0.0", "mutate4csharp", "0.1.0-format-only", "2"),
        new("anti-slop", "dmmulroy.anti-slop", "0.1.2+13909c9e07590f5a85d93c62712259fe14a883f7", "oxlint", "1.78.0", "oxlint-json-v1"),
        new("ci", "delivery-forge.ci", "1.0.0", "ci")
    ]);

    public bool TryGet(string gateId, out TrustedGatePluginBinding binding) =>
        _bindings.TryGetValue(gateId, out binding!);

    public IReadOnlyList<TrustedGatePluginBinding> Bindings =>
        _bindings.Values.OrderBy(binding => binding.GateId, StringComparer.Ordinal).ToArray();
}
