namespace DeliveryForge.Contracts.State;

public sealed class AppendOnlyContractStore
{
    private readonly string _rootDirectory;

    public AppendOnlyContractStore(string rootDirectory) => _rootDirectory = rootDirectory;

    public Task<string> WriteImmutableAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException($"Append-only storage at '{_rootDirectory}' is not implemented.");

    public Task UpdateCurrentPointerAsync(string identity, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Atomic current-pointer updates are not implemented.");
}
