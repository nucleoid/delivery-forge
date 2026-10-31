namespace DeliveryForge.Coordination;

/// <summary>Data returned by the trusted parent host after checking its operator deployment evidence.
/// This is not a remote capability response, signature, or worker-issued acceptance.</summary>
public sealed record ContinuumDeploymentEvidence(
    Uri Endpoint,
    string Project,
    string PrincipalId,
    string CredentialSha256,
    string OperatorIssuer,
    string SourceRevision,
    string ImageSha256,
    string MigrationEvidenceSha256,
    string IdentityProfileEvidenceSha256,
    DateTimeOffset VerifiedAt,
    DateTimeOffset ValidUntil)
{
    public override string ToString() => "Continuum operator evidence [private credential/principal binding]";
}

/// <summary>The parent host supplies current approved deployment, migration and credential-to-principal
/// evidence. Tool discovery and a caller's claim of a principal are insufficient substitutes.</summary>
public interface IContinuumDeploymentEvidenceSource
{
    Task<ContinuumDeploymentEvidence> ObserveAsync(CancellationToken cancellationToken = default);
}

public sealed record ProductionCoordinationDependencies(
    IOwnedRunQuiescenceSource Quiescence,
    IMonotonicClock Clock,
    ICoordinationCheckpointStore Checkpoints,
    TimeSpan MaximumClockUncertainty,
    TimeSpan OperationTimeout);
