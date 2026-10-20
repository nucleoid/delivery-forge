using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Evidence;

/// <summary>
/// Authoritative producer composition. Hosts provision the receipt ledger first, then use
/// this composition to emit only v2 receipts and pass its durable provenance verifier to
/// workflow/publication consumers. The legacy <see cref="GateRunner"/> default remains v1
/// for reader compatibility but is not an authority producer.
/// </summary>
public sealed class AuthoritativeEvaluationHost
{
    private readonly GateRunner runner;
    private readonly HostOwnedReceiptStore receipts;

    public AuthoritativeEvaluationHost(
        ICommandExecutor executor,
        IRepositoryIdentityReader repository,
        string trustedWorkingDirectory,
        string trustedProtectedBaseCommit,
        string hostOwnedReceiptRoot,
        ReadOnlySpan<byte> hostHeldReceiptSigningKey,
        DeterministicEvaluationPluginRegistry? plugins = null,
        TrustedGatePluginRegistry? authorityRegistry = null)
    {
        receipts = new HostOwnedReceiptStore(hostOwnedReceiptRoot, hostHeldReceiptSigningKey);
        runner = new GateRunner(
            executor,
            repository,
            trustedWorkingDirectory,
            trustedProtectedBaseCommit,
            plugins,
            emitAuthoritativeV2: true,
            authorityRegistry: authorityRegistry,
            authoritativeReceiptStore: receipts);
    }

    public TrustedReceiptProvenance ReceiptProvenance => receipts.Provenance;

    public Task<GateRunResult> RunAsync(
        GateRequest request,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(request, cancellationToken);

    public ValidatedContract ReadReceipt(string identity) => receipts.Read(identity);
}
