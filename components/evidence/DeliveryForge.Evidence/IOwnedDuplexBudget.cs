namespace DeliveryForge.Evidence;

// Shared unchanged contract from the ToolLifetime/duplex owner. Implementations restrict the
// existing owner's grants; this interface issues no process, lease or completion authority.
public interface IOwnedDuplexBudget
{
    long OriginalStarted { get; }
    TimeSpan WholeLimit { get; }
    TimeSpan WorkLimit { get; }
    TimeSpan CleanupRemaining { get; }
    CancellationToken Token { get; }
    void CheckWork();
}