using DeliveryForge.Execution.Host;

namespace DeliveryForge.Publication;

/// <summary>Privileged parent composition. Fixture authority cannot enable a live adapter.</summary>
public sealed class PublicationAuthorityHost
{
    private readonly object issuer = new();

    private PublicationAuthorityHost(ReviewAuthorityHost reviewHost) => ReviewHost = reviewHost;

    internal ReviewAuthorityHost ReviewHost { get; }

    public static PublicationAuthorityHost CreateIsolatedFixture(ReviewAuthorityHost reviewHost) =>
        new(reviewHost ?? throw new ArgumentNullException(nameof(reviewHost)));

    public static PublicationAuthorityHost CreateProduction() => throw new PublicationUnavailableException(
        "Production publication is INCOMPLETE: live source/ownership and disposable-target authorization are unconfigured.");

    public PublicationRun IssueRun(PublicationRunDefinition definition) => new(issuer, definition);

    public PublicationRun IssueReconciledRevision(PublicationRun prior, PublicationRunDefinition definition) =>
        throw new PublicationUnavailableException("Reconciled publication revisions are not implemented at this RED checkpoint.");

    internal bool Owns(PublicationRun run) => ReferenceEquals(run.Issuer, issuer);
}
