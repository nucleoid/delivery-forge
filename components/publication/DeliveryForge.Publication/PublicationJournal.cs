using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Publication;

public enum PublicationIntentState { Prepared, Sent, Uncertain, Confirmed }
public sealed record PublicationIntent(
    string IdempotencyKey,
    string RunIdentity,
    string RevisionIdentity,
    PublicationAction Action,
    PublicationTarget Target,
    string BaseCommit,
    string HeadCommit,
    string TreeId,
    PublicationIntentState State,
    string? ResultId = null,
    string? ReceiptIdentity = null);

/// <summary>Local append-only evidence; durable records do not reconstruct parent authority.</summary>
public interface IPublicationJournal
{
    Task<PublicationIntent?> ReadAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task AppendAsync(PublicationIntent intent, CancellationToken cancellationToken);
    Task AppendReceiptAsync(ValidatedContract receipt, CancellationToken cancellationToken);
}
