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
    private readonly TrustedGatePluginRegistry authorityRegistry;

    public AuthoritativeEvaluationHost(
        ICommandExecutor executor,
        IRepositoryIdentityReader repository,
        string trustedWorkingDirectory,
        string trustedProtectedBaseCommit,
        string hostOwnedReceiptRoot,
        ReadOnlySpan<byte> hostHeldReceiptSigningKey,
        GateExecutionIsolationAttestation isolation,
        DeterministicEvaluationPluginRegistry? plugins = null,
        TrustedGatePluginRegistry? authorityRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(isolation);
        if (!isolation.Verified || !isolation.SeparateSecurityPrincipal ||
            string.IsNullOrWhiteSpace(isolation.Issuer) || string.IsNullOrWhiteSpace(isolation.Evidence))
            throw new ArgumentException(
                "Authoritative v2 requires verified out-of-process gate isolation under a separate security principal.",
                nameof(isolation));
        this.authorityRegistry = authorityRegistry ?? TrustedGatePluginRegistry.BuiltIns;
        receipts = new HostOwnedReceiptStore(hostOwnedReceiptRoot, hostHeldReceiptSigningKey);
        runner = new GateRunner(
            executor,
            repository,
            trustedWorkingDirectory,
            trustedProtectedBaseCommit,
            plugins,
            emitAuthoritativeV2: true,
            authorityRegistry: this.authorityRegistry,
            authoritativeReceiptStore: receipts,
            hostOwnedClaimRoot: Path.Combine(hostOwnedReceiptRoot, "claims"));
    }

    public TrustedReceiptProvenance ReceiptProvenance => receipts.Provenance;
    public TrustedGatePluginRegistry AuthorityRegistry => authorityRegistry;

    public Task<GateRunResult> RunAsync(
        GateRequest request,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(request, cancellationToken);

    public ValidatedContract ReadReceipt(string identity) => receipts.Read(identity);
}

/// <summary>
/// Host-issued evidence that untrusted gate code cannot read host signing material. This is an
/// explicit deployment attestation, not an in-process HMAC security boundary.
/// </summary>
public sealed record GateExecutionIsolationAttestation(
    bool Verified,
    bool SeparateSecurityPrincipal,
    string Issuer,
    string Evidence);
