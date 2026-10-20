namespace DeliveryForge.Contracts.Validation;

public sealed record TrustedGatePluginBinding(
    string GateId,
    string PluginId,
    string PluginVersion,
    string Tool);

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
        new("build", "delivery-forge.dotnet", "1.0.0", "dotnet"),
        new("test", "delivery-forge.dotnet", "1.0.0", "dotnet"),
        new("coverage", "delivery-forge.dotnet", "1.0.0", "dotnet"),
        new("crap", "delivery-forge.crap4csharp", "1.0.0", "crap4csharp"),
        new("crap4csharp", "delivery-forge.crap4csharp", "1.0.0", "crap4csharp"),
        new("mutation", "delivery-forge.mutate4csharp", "1.0.0", "mutate4csharp"),
        new("anti-slop", "dmmulroy.anti-slop", "0.1.2+13909c9e07590f5a85d93c62712259fe14a883f7", "oxlint"),
        new("ci", "delivery-forge.dotnet", "1.0.0", "dotnet")
    ]);

    public bool TryGet(string gateId, out TrustedGatePluginBinding binding) =>
        _bindings.TryGetValue(gateId, out binding!);

    public IReadOnlyList<TrustedGatePluginBinding> Bindings =>
        _bindings.Values.OrderBy(binding => binding.GateId, StringComparer.Ordinal).ToArray();
}
