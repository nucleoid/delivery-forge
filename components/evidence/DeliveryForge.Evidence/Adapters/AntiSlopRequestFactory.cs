using System.Text;
using System.Text.Json;

namespace DeliveryForge.Evidence.Adapters;

/// <summary>Code-owned construction of the complete canonical anti-slop request.</summary>
public static class AntiSlopRequestFactory
{
    private const int MaximumManifestBytes = 16 * 1024 * 1024;

    public static GateRequest Create(
        string gateId,
        ResolvedEvidencePolicy policy,
        ToolCapability capability,
        RepositoryIdentity expectedRepository,
        string trustedWorkingDirectory,
        string bundleRoot,
        string nodePath,
        IReadOnlyList<string> inputs,
        string scope,
        TimeSpan timeout,
        string outputDirectory,
        bool enableEffectRules = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(expectedRepository);
        var workingRoot = Path.GetFullPath(trustedWorkingDirectory);
        var bundle = Path.GetFullPath(bundleRoot);
        var node = Path.GetFullPath(nodePath);
        var selectedInputs = DeterministicEvaluationPluginRegistry.NormalizeInputs(inputs);
        long inputBytes = 0;
        foreach (var input in selectedInputs)
        {
            inputBytes = checked(inputBytes + Encoding.UTF8.GetByteCount(input) + 1L);
            if (inputBytes > MaximumManifestBytes)
                throw new EvaluationPluginException(
                    $"Anti-slop input paths exceed the {MaximumManifestBytes}-byte request composition ceiling.");
        }
        var config = Path.Combine(bundle, enableEffectRules ? "oxlint.effect.config.ts" : "oxlint.config.ts");
        var oxlint = Path.Combine(bundle, "node_modules", "oxlint", "bin", "oxlint");
        var direct = new List<string>
        {
            oxlint, "--config", config, "--format", "json", "--no-ignore", "--disable-nested-config"
        };
        direct.AddRange(selectedInputs);

        IReadOnlyList<string> arguments;
        if (direct.Sum(value => Encoding.UTF8.GetByteCount(value) + 1) <=
            AntiSlopEvaluationPlugin.MaximumDirectArgumentBytes)
        {
            arguments = direct.AsReadOnly();
        }
        else
        {
            var manifestRoot = Path.Combine(Path.GetFullPath(outputDirectory), "anti-slop-manifests");
            Directory.CreateDirectory(manifestRoot);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(selectedInputs);
            if (manifestBytes.Length > MaximumManifestBytes)
                throw new EvaluationPluginException(
                    $"Canonical anti-slop manifest exceeds the {MaximumManifestBytes}-byte composition ceiling.");
            var manifestIdentity = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(manifestBytes));
            var manifest = Path.Combine(manifestRoot, manifestIdentity + ".json");
            if (File.Exists(manifest))
            {
                if (!File.ReadAllBytes(manifest).AsSpan().SequenceEqual(manifestBytes))
                    throw new EvaluationPluginException("Canonical anti-slop manifest identity collides with different bytes.");
            }
            else
            {
                using var stream = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.WriteThrough);
                stream.Write(manifestBytes);
                stream.Flush(flushToDisk: true);
            }
            arguments =
            [
                Path.Combine(bundle, "run-oxlint-batches.mjs"), "--oxlint", oxlint,
                "--config", config, "--inputs", manifest
            ];
        }

        var invocation = new CommandInvocation(node, arguments, workingRoot,
            new Dictionary<string, string>(StringComparer.Ordinal), InheritEnvironment: false);
        return new GateRequest(
            gateId, policy, capability, EvidenceAdapterKind.External,
            "unavailable",
            expectedRepository, invocation, scope, timeout, Path.GetFullPath(outputDirectory),
            new EvaluationPluginSelection(
                AntiSlopEvaluationPlugin.CreateBundled(bundle, node).Id,
                Enabled: true,
                selectedInputs,
                enableEffectRules));
    }
}
