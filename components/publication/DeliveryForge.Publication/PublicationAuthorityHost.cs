using System.Collections.Concurrent;
using System.Text.Json;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Publication;

/// <summary>Privileged parent composition. Fixture authority cannot enable a live adapter.</summary>
public sealed class PublicationAuthorityHost
{
    private readonly object issuer = new();
    private readonly ConcurrentDictionary<string, PublicationRun> revisions = new(StringComparer.Ordinal);
    private readonly PublicationAuthorityHost? predecessorHost;
    private readonly PublicationRun? permittedPredecessor;

    private PublicationAuthorityHost(ReviewAuthorityHost reviewHost,
        GateReceiptConsumerPolicy? gateReceiptPolicy = null,
        PublicationAuthorityHost? predecessorHost = null, PublicationRun? permittedPredecessor = null)
    {
        ReviewHost = reviewHost;
        GateReceiptPolicy = gateReceiptPolicy ?? GateReceiptConsumerPolicy.Disabled;
        this.predecessorHost = predecessorHost;
        this.permittedPredecessor = permittedPredecessor;
    }

    internal ReviewAuthorityHost ReviewHost { get; }
    internal GateReceiptConsumerPolicy GateReceiptPolicy { get; }

    public static PublicationAuthorityHost CreateIsolatedFixture(
        ReviewAuthorityHost reviewHost, GateReceiptConsumerPolicy? gateReceiptPolicy = null) =>
        new(reviewHost ?? throw new ArgumentNullException(nameof(reviewHost)), gateReceiptPolicy);

    public static PublicationAuthorityHost CreateReconciledFixture(
        PublicationAuthorityHost priorHost, PublicationRun registeredPrior, ReviewAuthorityHost newReviewHost,
        GateReceiptConsumerPolicy? gateReceiptPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(priorHost);
        ArgumentNullException.ThrowIfNull(registeredPrior);
        ArgumentNullException.ThrowIfNull(newReviewHost);
        if (!priorHost.Owns(registeredPrior))
            throw new InvalidOperationException("An actual registered predecessor from this exact parent issuer is required.");
        newReviewHost.EnsureContractFixturePredecessorScope(priorHost.ReviewHost);
        return new(newReviewHost, gateReceiptPolicy ?? priorHost.GateReceiptPolicy, priorHost, registeredPrior);
    }

    public static PublicationAuthorityHost CreateProduction() => throw new PublicationUnavailableException(
        "Production publication is INCOMPLETE: live source/ownership and disposable-target authorization are unconfigured.");

    public PublicationRun IssueRun(PublicationRunDefinition definition)
    {
        if (definition.PriorRevisionIdentity is not null || permittedPredecessor is not null)
            throw new ArgumentException("A continuation must use explicit reconciled revision issuance.", nameof(definition));
        return Issue(definition);
    }

    public PublicationRun IssueReconciledRevision(PublicationRun prior, PublicationRunDefinition definition)
    {
        var sameRoot = Owns(prior);
        var explicitLineage = ReferenceEquals(prior, permittedPredecessor) && predecessorHost is not null && predecessorHost.Owns(prior);
        if ((!sameRoot && !explicitLineage) || definition.PriorRevisionIdentity != prior.Definition.RevisionIdentity ||
            definition.RevisionIdentity == prior.Definition.RevisionIdentity ||
            (sameRoot && definition.RunIdentity != prior.Definition.RunIdentity) ||
            !PublicationPreflight.SameTarget(definition.Target, prior.Definition.Target))
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
        if (definition.Policy.SchemaName != "evidence-policy")
            throw new ArgumentException("A root-bound evidence policy is required.");
        using var policy = JsonDocument.Parse(definition.Policy.CanonicalBytes);
        var ceiling = policy.RootElement.GetProperty("authorizedCeiling").GetString() switch
        {
            "plan" => AuthorizationCeiling.Plan,
            "implement" => AuthorizationCeiling.Implement,
            "pr" => AuthorizationCeiling.Pr,
            "merge" => AuthorizationCeiling.Merge,
            _ => throw new ArgumentException("The root policy ceiling is undefined.")
        };
        if (definition.Ceiling > ceiling || !policy.RootElement.GetProperty("requireIndependentReview").GetBoolean())
            throw new ArgumentException("The explicit new ceiling cannot exceed or weaken its immutable root policy.");
        var run = new PublicationRun(issuer, definition);
        if (!revisions.TryAdd(definition.RevisionIdentity, run))
            throw new InvalidOperationException("A publication revision cannot be reissued or have its ceiling replaced.");
        return run;
    }

    internal bool Owns(PublicationRun run) => ReferenceEquals(run.Issuer, issuer) &&
        revisions.TryGetValue(run.Definition.RevisionIdentity, out var issued) && ReferenceEquals(issued, run);
}
