using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Evidence;

/// <summary>
/// Production composition for required v2 gates. It refuses fixture capabilities, executes the
/// exact protected required-gate set, and carries the durable provenance verifier into consumers.
/// The caller must provision the host signing key outside every gate subprocess boundary.
/// </summary>
public sealed class AuthoritativeEvaluationComposition(AuthoritativeEvaluationHost host)
{
    public async Task<AuthoritativeGateBundle> RunRequiredAsync(
        IReadOnlyList<GateRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
            throw new InvalidOperationException("A production evaluation requires a non-empty required-gate set.");
        var policy = requests[0].Policy;
        var requested = requests.Select(value => value.GateId).ToHashSet(StringComparer.Ordinal);
        if (!policy.AuthorityVerified || policy.RequiredGates.Count != requested.Count ||
            !policy.RequiredGates.All(requested.Contains) ||
            requests.Any(value => !ReferenceEquals(value.Policy, policy) || value.Capability.Fixture))
            throw new InvalidOperationException(
                "Production evaluation requires one protected policy, its exact required-gate set, and non-fixture capabilities.");

        var receipts = new List<ValidatedContract>(requests.Count);
        foreach (var request in requests.OrderBy(value => value.GateId, StringComparer.Ordinal))
        {
            var result = await host.RunAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.ProductionCapable || result.Fixture ||
                result.Outcome is not (GateOutcome.Pass or GateOutcome.NotApplicable) ||
                result.TrustedReceiptProvenance?.Contains(
                    ContractValidator.ParseAndValidate(File.ReadAllBytes(result.ReceiptPath)).Identity) != true)
                throw new InvalidOperationException(
                    $"Required gate '{request.GateId}' did not produce an authoritative durable outcome.");
            receipts.Add(host.ReadReceipt(
                ContractValidator.ParseAndValidate(File.ReadAllBytes(result.ReceiptPath)).Identity));
        }
        return new(receipts.AsReadOnly(), host.ReceiptProvenance, host.AuthorityRegistry);
    }
}

public sealed record AuthoritativeGateBundle(
    IReadOnlyList<ValidatedContract> Receipts,
    TrustedReceiptProvenance ReceiptProvenance,
    TrustedGatePluginRegistry AuthorityRegistry)
{
    public TransitionEvidence BindTransition(
        AuthorizationCeiling ceiling,
        string baseCommit,
        string headCommit,
        string treeId,
        ValidatedContract policy,
        TrustedGatePluginRegistry? authorityRegistry = null)
    {
        if (authorityRegistry is not null && !ReferenceEquals(authorityRegistry, AuthorityRegistry))
            throw new InvalidOperationException("The consumer registry differs from the producer-owned authority registry.");
        return new(ceiling, baseCommit, headCommit, treeId, Policy: policy, GateReceipts: Receipts,
            GateAuthorityRegistry: AuthorityRegistry,
            GateReceiptProvenance: ReceiptProvenance,
            EnforceAuthoritativeGateReceipts: true,
            GateAuthorityRegistryIdentity: AuthorityRegistry.Identity);
    }
}
