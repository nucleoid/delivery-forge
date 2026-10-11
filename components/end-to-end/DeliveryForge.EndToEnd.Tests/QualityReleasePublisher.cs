namespace DeliveryForge.EndToEnd.Tests;

// Fixture-only publication; the callback observes the real staging writer before close, never authority.
internal static class QualityReleasePublisher
{
    internal static Task PublishAsync(OwnedQualityInterruptionRuntime runtime, string ownedDirectory, string nonce,
        Func<string, CancellationToken, Task>? beforeClose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("Completed staging-file release publication is RED; final pathname must remain absent until the owned writer closes.");
    }
}
