namespace DeliveryForge.Coordination;

/// <summary>Original parent-owned admission. Implementations enroll the actual invocation under the same revocation gate.
/// Saved DATA cannot implement ownership; the bound parent retains the original task and uncertain invocation.</summary>
public interface ICoordinatedEffectAdmission
{
    Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke);
    void RetainUncertain(string runId, string reason);
}
