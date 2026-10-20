using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Evidence;

public sealed class ProtectedGitPolicyResolver(
    ICommandExecutor executor,
    string repositoryRoot,
    string protectedBaseCommit,
    IReadOnlyList<RequiredGatePluginBinding>? trustedGatePluginBindings = null)
{
    private readonly string _repositoryRoot = Path.GetFullPath(repositoryRoot);

    public async Task<ResolvedEvidencePolicy> ResolveAsync(
        string repositoryRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (!IsGitObject(protectedBaseCommit) ||
            string.IsNullOrWhiteSpace(repositoryRelativePath) ||
            Path.IsPathFullyQualified(repositoryRelativePath) ||
            repositoryRelativePath.Split(['/', '\\']).Any(segment => segment is "" or "." or ".."))
            throw new EvidencePolicyException("A full protected base and safe repository-relative policy path are required.");

        var resolvedBase = await GitAsync(
            ["rev-parse", "--verify", $"{protectedBaseCommit}^{{commit}}"], cancellationToken).ConfigureAwait(false);
        var baseId = resolvedBase.StandardOutput.Trim();
        if (!string.Equals(baseId, protectedBaseCommit, StringComparison.Ordinal))
            throw new EvidencePolicyException("Git did not resolve the exact protected policy base.");

        var head = await GitAsync(["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        await GitAsync(
            ["merge-base", "--is-ancestor", baseId, head.StandardOutput.Trim()], cancellationToken).ConfigureAwait(false);
        var policyResult = await GitAsync(
            ["show", $"{baseId}:{repositoryRelativePath.Replace('\\', '/')}"], cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(policyResult.StandardOutput);

        try
        {
            var validated = ContractValidator.ParseAndValidate(bytes);
            if (!string.Equals(validated.SchemaName, "evidence-policy", StringComparison.Ordinal))
                throw new EvidencePolicyException("Protected content is not an evidence-policy contract.");
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var gates = root.GetProperty("requiredGates");
            var requiredGates = gates.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .ToArray();
            if (requiredGates.Length == 0 || requiredGates.Any(string.IsNullOrWhiteSpace) ||
                requiredGates.Distinct(StringComparer.Ordinal).Count() != requiredGates.Length)
                throw new EvidencePolicyException("Protected policy required gates are empty, malformed, or duplicated.");

            var contentIdentity = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
            var bindings = ValidateBindings(requiredGates.Select(value => value!).ToArray());
            return new ResolvedEvidencePolicy(
                validated.Identity, contentIdentity, baseId, PolicyAuthorityKind.ProtectedGitBase,
                0m, requiredGates.Select(value => value!).ToArray(), authorityVerified: true,
                sourceRepositoryRoot: _repositoryRoot,
                requiredGatePluginBindings: bindings);
        }
        catch (ContractValidationException exception)
        {
            throw new EvidencePolicyException($"Protected policy contract is invalid: {exception.Message}");
        }
        catch (JsonException exception)
        {
            throw new EvidencePolicyException($"Protected policy JSON is malformed: {exception.Message}");
        }
    }

    private IReadOnlyList<RequiredGatePluginBinding> ValidateBindings(IReadOnlyList<string> requiredGates)
    {
        var supplied = trustedGatePluginBindings ?? RequiredGatePluginBindings.BuiltIns;
        if (supplied.Any(binding =>
                string.IsNullOrWhiteSpace(binding.GateId) ||
                string.IsNullOrWhiteSpace(binding.PluginId) ||
                string.IsNullOrWhiteSpace(binding.PluginVersion) ||
                string.IsNullOrWhiteSpace(binding.Tool)) ||
            supplied.GroupBy(binding => binding.GateId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new EvidencePolicyException("Trusted required-gate plugin bindings are malformed or duplicated.");

        return requiredGates
            .Select(gate => supplied.SingleOrDefault(binding =>
                string.Equals(binding.GateId, gate, StringComparison.Ordinal)))
            .Where(binding => binding is not null)
            .Select(binding => binding!)
            .ToArray();
    }

    private async Task<CommandResult> GitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new CommandInvocation(
                "git", arguments, _repositoryRoot,
                new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" }),
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.TimedOut || result.Cancelled || result.ExitCode != 0)
            throw new EvidencePolicyException($"Protected policy Git read failed: git {string.Join(" ", arguments)}");
        return result;
    }

    private static bool IsGitObject(string value) =>
        value.Length is 40 or 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class RequiredGatePluginBindings
{
    internal static IReadOnlyList<RequiredGatePluginBinding> BuiltIns { get; } =
        GateReceiptAuthority.BuiltIns
            .Where(binding => binding.GateId != "ci")
            .Select(binding => new RequiredGatePluginBinding(
                binding.GateId, binding.PluginId, binding.PluginVersion, binding.Tool))
            .ToArray();
}
