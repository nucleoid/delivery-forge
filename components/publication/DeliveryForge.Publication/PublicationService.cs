using DeliveryForge.Publication.GitHost;

namespace DeliveryForge.Publication;

public sealed class PublicationService
{
    public PublicationService(
        PublicationAuthorityHost authority,
        IPublicationFactSource facts,
        IGitHostAdapter adapter,
        IPublicationJournal journal)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(journal);
    }

    public Task<PublicationResult> ExecuteAsync(
        PublicationRun run,
        PublicationRequest request,
        CancellationToken cancellationToken = default) => Task.FromResult(new PublicationResult(
            PublicationOutcome.Incomplete, PublicationDenial.NotImplemented,
            "Publication behavior is not implemented at this RED checkpoint.",
            PublicationAction.None, PublicationRealm.Fixture));
}
