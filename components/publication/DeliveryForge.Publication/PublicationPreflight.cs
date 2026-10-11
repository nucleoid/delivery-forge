using DeliveryForge.Publication.GitHost;

namespace DeliveryForge.Publication;

public static class PublicationPreflight
{
    public static PublicationPreflightResult Evaluate(
        PublicationAuthorityHost host,
        PublicationRun run,
        PublicationRequest request,
        PublicationLocalObservation local,
        GitHostObservation remote) =>
        new(false, PublicationDenial.NotImplemented, "Publication preflight is not implemented at this RED checkpoint.");
}
