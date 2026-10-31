namespace DeliveryForge.Coordination;

/// <summary>Original parent-owned admission. Implementations enroll the actual invocation under the same revocation gate.
/// Saved DATA cannot implement ownership; the bound parent retains the original task and uncertain invocation.</summary>
public interface ICoordinatedEffectAdmission
{
    Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke);
    void RetainUncertain(string runId, string reason);
}

/// <summary>Enrolls a known synchronous operation without holding the lease-state
/// monitor for its duration. The original operation remains pending for quiescence.
/// Implementations still check the same current parent before each subsequent effect.
/// A returned synchronous refusal revokes admission but can settle; callers must retain
/// any unresolved resource through RetainUncertain before returning or throwing.</summary>
public interface ICoordinatedSynchronousEffectAdmission : ICoordinatedEffectAdmission
{
    T EnterSynchronous<T>(string runId, Func<T> invoke);
    void CheckCurrent(string runId);
}

/// <summary>Optional restriction on waiting for currentness. The concrete owner supplies
/// its existing monotonic start and work limit. These values only restrict waiting;
/// no saved timing DATA can establish dispatch authority or extend an original.</summary>
public interface ICoordinatedBoundedCurrentness
{
    void CheckCurrent(string runId, long existingStarted, TimeSpan existingWorkLimit);
}
