using System.Collections.ObjectModel;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Review;

namespace DeliveryForge.Publication;

public enum PublicationRealm { Fixture, Production }
public enum PublicationMode { Plan, Implement, Pr, Merge }
public enum PublicationAction { None, CreateOrUpdatePr, MergeNormal }
public enum PublicationOutcome { Incomplete, Blocked, DryRun, PublishedFixture, ReconciledFixture }
public enum PublicationDenial
{
    None, NotImplemented, Authority, Ceiling, Revision, LocalIdentity, Ownership,
    Stopped, PolicyGate, Review, RemoteIdentity, Protection, Checks, Idempotency, Uncertain
}
public enum PublicationOwnership { ReconciledFixture, Uncertain, Expired }

public sealed record PublicationTarget(string Repository, string SourceRef, string TargetRef);
public sealed record PublicationRequest(PublicationMode Mode, string IdempotencyKey, bool DryRun = true);
public sealed record PublicationRunDefinition(
    string RunIdentity,
    string RevisionIdentity,
    string? PriorRevisionIdentity,
    AuthorizationCeiling Ceiling,
    PublicationTarget Target,
    PatchIdentity Patch,
    ValidatedContract Policy,
    IReadOnlyList<ValidatedContract> GateReceipts,
    ValidatedContract? CiReceipt,
    ValidatedContract? ReviewReceipt,
    ValidatedContract? HostedReviewReceipt,
    ReviewTransitionReceiver Receiver);

/// <summary>Opaque parent-issued authority; serialized documents cannot construct one.</summary>
public sealed class PublicationRun
{
    internal PublicationRun(object issuer, PublicationRunDefinition definition)
    {
        Issuer = issuer;
        Definition = definition with
        {
            Patch = definition.Patch with
            {
                ArtifactIdentities = new ReadOnlyDictionary<string, string>(
                    definition.Patch.ArtifactIdentities.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
                Command = Array.AsReadOnly(definition.Patch.Command.ToArray())
            },
            GateReceipts = Array.AsReadOnly(definition.GateReceipts.ToArray())
        };
    }

    internal object Issuer { get; }
    public PublicationRunDefinition Definition { get; }
    public PublicationRealm Realm => PublicationRealm.Fixture;
}

// Fixture observations are data from parent-owned ports, never production authority.
public sealed record PublicationLocalObservation(
    PatchIdentity Patch,
    bool IsClean,
    PublicationOwnership Ownership,
    bool Stopped,
    string RevisionIdentity);

public interface IPublicationFactSource
{
    Task<PublicationLocalObservation> ObserveAsync(PublicationRun run, CancellationToken cancellationToken);
}

public sealed record PublicationResult(
    PublicationOutcome Outcome,
    PublicationDenial Denial,
    string Reason,
    PublicationAction Action,
    PublicationRealm Realm,
    ValidatedContract? Receipt = null);

public sealed record PublicationPreflightResult(bool Allowed, PublicationDenial Denial, string Reason);
public sealed record PublicationLimits(TimeSpan ReadTimeout, TimeSpan ActionTimeout, TimeSpan JournalTimeout, TimeSpan LockTimeout)
{
    public static PublicationLimits FixtureDefault { get; } = new(
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    internal void Validate()
    {
        if (new[] { ReadTimeout, ActionTimeout, JournalTimeout, LockTimeout }
            .Any(value => value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(5)))
            throw new ArgumentOutOfRangeException(nameof(ReadTimeout), "Publication operation budgets must be positive and finite.");
    }
}
public sealed class PublicationUnavailableException(string message) : Exception(message);
