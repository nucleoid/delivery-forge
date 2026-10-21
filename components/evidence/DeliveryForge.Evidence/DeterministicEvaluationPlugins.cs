using System.Security.Cryptography;
using System.Text;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence;

public enum EvaluationPluginStatus
{
    Selected,
    Disabled,
    Executed,
    NotApplicable
}

public sealed record EvaluationPluginSelection(
    string Id,
    bool Enabled,
    IReadOnlyList<string>? Inputs = null,
    bool EnableEffectRules = false);

public sealed record EvaluationPluginConfiguration(
    string Id,
    bool Enabled,
    bool EnableEffectRules = false);

public sealed record EvaluationPluginReceiptMetadata(
    string Id,
    string Version,
    bool Enabled,
    EvaluationPluginStatus Status,
    string ConfigurationIdentity,
    string RuntimeClosureIdentity,
    IReadOnlyList<string> Inputs,
    string InputSetIdentity,
    string? PluginRawOutcome)
{
    internal static EvaluationPluginReceiptMetadata Unbound { get; } = new(
        "unbound", "unbound", false, EvaluationPluginStatus.Selected,
        "unbound", "unbound", [], DeterministicEvaluationPluginRegistry.ComputeInputSetIdentity([]), null);
}

public sealed record EvaluationPluginSnapshot(object? State)
{
    public static EvaluationPluginSnapshot Empty { get; } = new(State: null);
}

public interface IDeterministicEvaluationPlugin
{
    string Id { get; }
    string Version { get; }
    string ExpectedTool { get; }
    EvidenceAdapterKind CompatibilityAdapter { get; }

    IReadOnlyList<string> DetectInputs(
        IReadOnlyList<string> candidateInputs,
        IReadOnlySet<string> ignoredInputs) => [];

    NormalizedEvidence? ValidateBeforeExecution(GateRequest request) => null;

    NormalizedEvidence? ValidateAuthorityWithoutExecution(GateRequest request) => null;

    NormalizedEvidence? ValidateAfterExecution(
        GateRequest request,
        EvaluationPluginSnapshot snapshot) => null;

    string GetRuntimeClosureIdentity(GateRequest request) => request.ConfigurationIdentity;

    EvaluationPluginSnapshot CaptureBefore(GateRequest request);

    NormalizedEvidence Normalize(
        GateRequest request,
        CommandResult execution,
        EvaluationPluginSnapshot snapshot);

    void AppendBoundArtifacts(
        GateRequest request,
        EvaluationPluginSnapshot snapshot,
        string gateDirectory,
        IDictionary<string, string> artifactHashes);
}

public sealed class DeterministicEvaluationPluginRegistry
{
    private readonly IReadOnlyDictionary<string, IDeterministicEvaluationPlugin> _plugins;
    internal bool IsCodeOwnedBuiltInRegistry { get; }

    public DeterministicEvaluationPluginRegistry(IEnumerable<IDeterministicEvaluationPlugin> plugins)
        : this(plugins, isCodeOwnedBuiltInRegistry: false)
    {
    }

    private DeterministicEvaluationPluginRegistry(
        IEnumerable<IDeterministicEvaluationPlugin> plugins,
        bool isCodeOwnedBuiltInRegistry)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        var indexed = new Dictionary<string, IDeterministicEvaluationPlugin>(StringComparer.Ordinal);
        foreach (var plugin in plugins)
        {
            if (plugin is null || string.IsNullOrWhiteSpace(plugin.Id) ||
                string.IsNullOrWhiteSpace(plugin.Version) || string.IsNullOrWhiteSpace(plugin.ExpectedTool))
                throw new EvaluationPluginException("Plugin registrations require stable id, version, and tool identities.");
            if (!indexed.TryAdd(plugin.Id, plugin))
                throw new EvaluationPluginException($"Duplicate deterministic evaluation plugin id '{plugin.Id}'.");
        }
        _plugins = indexed;
        IsCodeOwnedBuiltInRegistry = isCodeOwnedBuiltInRegistry;
    }

    public static DeterministicEvaluationPluginRegistry CreateBuiltIns(
        string? trustedAntiSlopBundleRoot = null,
        string? trustedNodePath = null)
    {
        if ((trustedAntiSlopBundleRoot is null) != (trustedNodePath is null))
            throw new EvaluationPluginException(
                "Anti-slop bundle and Node registration must be supplied together.");
        var registry = new DeterministicEvaluationPluginRegistry(
        [
            new SealedEvidenceAdapterPlugin("delivery-forge.dotnet", "1.0.0", "dotnet", EvidenceAdapterKind.DotNet),
            new SealedEvidenceAdapterPlugin("delivery-forge.crap4csharp", "1.0.0", "crap4csharp", EvidenceAdapterKind.Crap4CSharp),
            new SealedEvidenceAdapterPlugin("delivery-forge.mutate4csharp", "1.0.0", "mutate4csharp", EvidenceAdapterKind.Mutate4CSharp),
            trustedAntiSlopBundleRoot is not null
                ? AntiSlopEvaluationPlugin.CreateBundled(trustedAntiSlopBundleRoot, trustedNodePath!)
                : AntiSlopEvaluationPlugin.CreateBundled()
        ], isCodeOwnedBuiltInRegistry: true);
        registry.AssertMatchesAuthorityRegistry(TrustedGatePluginRegistry.BuiltIns);
        return registry;
    }

    public IDeterministicEvaluationPlugin Resolve(
        EvidenceAdapterKind compatibilityAdapter,
        EvaluationPluginSelection? selection)
    {
        var id = selection?.Id ?? compatibilityAdapter switch
        {
            EvidenceAdapterKind.DotNet => "delivery-forge.dotnet",
            EvidenceAdapterKind.Crap4CSharp => "delivery-forge.crap4csharp",
            EvidenceAdapterKind.Mutate4CSharp => "delivery-forge.mutate4csharp",
            _ => throw new EvaluationPluginException("External evaluation requires an explicit trusted plugin selection.")
        };
        if (!_plugins.TryGetValue(id, out var plugin))
            throw new EvaluationPluginException($"Deterministic evaluation plugin '{id}' is not registered by trusted host code.");
        if (plugin.CompatibilityAdapter != compatibilityAdapter)
            throw new EvaluationPluginException(
                $"Plugin '{id}' is incompatible with adapter '{compatibilityAdapter}'.");
        return plugin;
    }

    public IReadOnlyList<EvaluationPluginSelection> SelectApplicable(
        IReadOnlyList<EvaluationPluginConfiguration> configurations,
        IReadOnlyList<string> candidateInputs,
        IReadOnlySet<string>? ignoredInputs = null)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        ArgumentNullException.ThrowIfNull(candidateInputs);
        var candidates = NormalizeInputs(candidateInputs);
        var ignored = ignoredInputs is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : NormalizeInputs(ignoredInputs.ToArray()).ToHashSet(StringComparer.Ordinal);
        var configured = new HashSet<string>(StringComparer.Ordinal);
        var selections = new List<EvaluationPluginSelection>();

        foreach (var configuration in configurations)
        {
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.Id))
                throw new EvaluationPluginException("Plugin configurations require a stable registered id.");
            if (!configured.Add(configuration.Id))
                throw new EvaluationPluginException($"Duplicate deterministic evaluation plugin configuration '{configuration.Id}'.");
            if (!_plugins.TryGetValue(configuration.Id, out var plugin))
                throw new EvaluationPluginException(
                    $"Deterministic evaluation plugin '{configuration.Id}' is not registered by trusted host code.");

            var inputs = NormalizeInputs(plugin.DetectInputs(candidates, ignored));
            if (inputs.Any(input => !candidates.Contains(input, StringComparer.Ordinal) || ignored.Contains(input)))
                throw new EvaluationPluginException(
                    $"Plugin '{configuration.Id}' selected an input outside the frozen candidate scope.");
            selections.Add(new EvaluationPluginSelection(
                configuration.Id,
                configuration.Enabled,
                inputs,
                configuration.EnableEffectRules));
        }

        return selections;
    }

    internal static EvaluationPluginReceiptMetadata Bind(
        GateRequest request,
        IDeterministicEvaluationPlugin plugin,
        EvaluationPluginStatus status,
        string? pluginRawOutcome = null)
    {
        var explicitInputs = request.Plugin?.Inputs;
        var inputs = explicitInputs is null
            ? InferLegacyInputs(request)
            : NormalizeInputs(explicitInputs);
        return new EvaluationPluginReceiptMetadata(
            plugin.Id,
            plugin.Version,
            request.Plugin?.Enabled ?? true,
            status,
            request.ConfigurationIdentity,
            plugin.GetRuntimeClosureIdentity(request),
            inputs,
            ComputeInputSetIdentity(inputs),
            pluginRawOutcome);
    }

    internal void AssertMatchesAuthorityRegistry(TrustedGatePluginRegistry authority)
    {
        foreach (var plugin in _plugins.Values)
        {
            if (!authority.Bindings.Any(binding =>
                    binding.PluginId == plugin.Id && binding.PluginVersion == plugin.Version &&
                    binding.Tool == plugin.ExpectedTool))
                throw new EvaluationPluginException(
                    $"Plugin '{plugin.Id}' has no matching trusted gate authority binding.");
        }
        foreach (var binding in authority.Bindings.Where(binding => binding.GateId != "ci"))
        {
            if (!_plugins.TryGetValue(binding.PluginId, out var plugin) ||
                plugin.Version != binding.PluginVersion || plugin.ExpectedTool != binding.Tool)
                throw new EvaluationPluginException(
                    $"Trusted gate '{binding.GateId}' drifts from the deterministic plugin registry.");
        }
    }

    internal static IReadOnlyList<string> NormalizeInputs(IReadOnlyList<string> inputs)
    {
        var normalized = inputs.Select(input =>
        {
            if (string.IsNullOrWhiteSpace(input) || Path.IsPathRooted(input))
                throw new EvaluationPluginException("Plugin inputs must be non-empty repository-relative paths.");
            var value = input.Replace('\\', '/');
            if (value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
                throw new EvaluationPluginException("Plugin inputs cannot escape or ambiguously traverse the repository root.");
            if (value.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.StartsWith("-", StringComparison.Ordinal) == true)
                throw new EvaluationPluginException("Plugin input paths cannot begin with an option-like segment.");
            return value;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length != inputs.Count)
            throw new EvaluationPluginException("Plugin input paths must be unique after normalization.");
        return normalized;
    }

    internal static string ComputeInputSetIdentity(IReadOnlyList<string> inputs)
    {
        var joined = string.Join("\n", inputs) + "\n";
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))}";
    }

    private static IReadOnlyList<string> InferLegacyInputs(GateRequest request) =>
        request.Invocation.Arguments
            .Where(argument => !argument.StartsWith("-", StringComparison.Ordinal) &&
                (argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                 argument.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                 argument.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                 argument.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            .Select(argument => Path.IsPathFullyQualified(argument)
                ? Path.GetRelativePath(request.Invocation.WorkingDirectory, argument)
                : argument)
            .Append($"scope:{request.Scope}")
            .Select(value => value.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private sealed class SealedEvidenceAdapterPlugin(
        string id,
        string version,
        string expectedTool,
        EvidenceAdapterKind compatibilityAdapter) : IDeterministicEvaluationPlugin
    {
        public string Id => id;
        public string Version => version;
        public string ExpectedTool => expectedTool;
        public EvidenceAdapterKind CompatibilityAdapter => compatibilityAdapter;

        public EvaluationPluginSnapshot CaptureBefore(GateRequest request) =>
            new(EvidenceAdapterDispatcher.CaptureBefore(request));

        public NormalizedEvidence Normalize(
            GateRequest request,
            CommandResult execution,
            EvaluationPluginSnapshot snapshot) =>
            EvidenceAdapterDispatcher.Normalize(
                request,
                execution,
                snapshot.State as EvidenceExecutionSnapshot
                    ?? throw new EvaluationPluginException("Sealed adapter snapshot is missing or mismatched."));

        public void AppendBoundArtifacts(
            GateRequest request,
            EvaluationPluginSnapshot snapshot,
            string gateDirectory,
            IDictionary<string, string> artifactHashes) =>
            EvidenceAdapterDispatcher.AppendBoundArtifacts(
                request,
                snapshot.State as EvidenceExecutionSnapshot
                    ?? throw new EvaluationPluginException("Sealed adapter snapshot is missing or mismatched."),
                gateDirectory,
                artifactHashes);
    }
}

public sealed class EvaluationPluginException(string message) : Exception(message);
