namespace DeliveryForge.Contracts.State;

public static class CheckpointSequence
{
    public static void EnsureIncreasing(long previous, long next)
    {
        if (next <= previous)
        {
            throw new CheckpointSequenceException($"Checkpoint sequence must increase: previous={previous}, next={next}.");
        }
    }
}

public sealed class CheckpointSequenceException(string message) : Exception(message);
