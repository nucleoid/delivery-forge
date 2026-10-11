using System.Collections.Concurrent;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Publication;

/// <summary>Privileged parent composition. Fixture authority cannot enable a live adapter.</summary>
public sealed class PublicationAuthorityHost
{
    private readonly object issuer = new();
    private readonly ConcurrentDictionary<string, PublicationRun> revisions = new(StringComparer.Ordinal);

    private PublicationAuthorityHost(ReviewAuthorityHost reviewHost) => ReviewHost = reviewHost;

    internal ReviewAuthorityHost ReviewHost { get; }

    public static PublicationAuthorityHost CreateIsolatedFixture(ReviewAuthorityHost reviewHost) =>
        new(reviewHost ?? throw new ArgumentNullException(nameof(reviewHost)));

    public static PublicationAuthorityHost CreateReconciledFixture(
        PublicationAuthorityHost priorHost, PublicationRun registeredPrior, ReviewAuthorityHost newReviewHost) =>
        throw new InvalidOperationException("New-root explicit publication lineage is not implemented at this RED checkpoint.");

    public static PublicationAuthorityHost CreateProduction() => throw new PublicationUnavailableException(
        "Production publication is INCOMPLETE: live source/ownership and disposable-target authorization are unconfigured.");

    public PublicationRun IssueRun(PublicationRunDefinition definition)
    {
        if (definition.PriorRevisionIdentity is not null)
            throw new ArgumentException("A continuation must use explicit reconciled revision issuance.", nameof(definition));
        return Issue(definition);
    }

    public PublicationRun IssueReconciledRevision(PublicationRun prior, PublicationRunDefinition definition)
    {
        if (!Owns(prior) || definition.PriorRevisionIdentity != prior.Definition.RevisionIdentity ||
            definition.RevisionIdentity == prior.Definition.RevisionIdentity || definition.RunIdentity != prior.Definition.RunIdentity ||
            definition.Target != prior.Definition.Target)
            throw new ArgumentException("Higher authority requires a new parent-reconciled revision referencing the exact prior run.");
        return Issue(definition);
    }

    private PublicationRun Issue(PublicationRunDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.RevisionIdentity) || !Enum.IsDefined(definition.Ceiling))
            throw new ArgumentException("An explicit immutable revision and defined ceiling are required.");
        ReviewHost.EnsureContractFixtureRootBinding(definition.RunIdentity, definition.Patch.PlanIdentity,
            definition.Policy.Identity, definition.Receiver);
        var run = new PublicationRun(issuer, definition);
        if (!revisions.TryAdd(definition.RevisionIdentity, run))
            throw new InvalidOperationException("A publication revision cannot be reissued or have its ceiling replaced.");
        return run;
    }

    internal bool Owns(PublicationRun run) => ReferenceEquals(run.Issuer, issuer) &&
        revisions.TryGetValue(run.Definition.RevisionIdentity, out var issued) && ReferenceEquals(issued, run);
}
