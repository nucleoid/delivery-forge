using System.Diagnostics;
using DeliveryForge.Coordination;

namespace DeliveryForge.Tool;

// Private to one original parent. This barrier conveys settlement, never dispatch authority.
internal sealed class ParentRenewalHandoff(object originalGate, ToolLifetime original)
{
    internal sealed class Pending(string runId, TimeSpan safeDeadline)
    {
        internal readonly string RunId = runId;
        internal readonly TimeSpan SafeDeadline = safeDeadline;
        internal readonly TaskCompletionSource Settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Installed;
        internal int WaitingCount;
    }

    Pending? pending;
    Exception? revoked;
    internal TimeSpan Elapsed => Stopwatch.GetElapsedTime(original.OriginalStarted);
    void CheckOriginal()
    {
        original.Check();
        if (original.HasExplicitUnknown) throw new CoordinationUncertainException("Original retained an explicitly unknown resource.");
    }

    internal Pending Begin(string runId, TimeSpan previousSafeDeadline)
    {
        lock (originalGate)
        {
            CheckOriginal();
            if (string.IsNullOrWhiteSpace(runId) || runId.Length > 256 || revoked is not null || pending is not null || previousSafeDeadline <= Elapsed)
                throw new CoordinationUncertainException("Original renewal cannot begin outside its previous safe grant.");
            return pending = new(runId, previousSafeDeadline);
        }
    }

    internal Pending? Snapshot(string runId)
    {
        lock (originalGate)
        {
            CheckOriginal();
            if (revoked is not null) throw new CoordinationUncertainException("Original renewal handoff was revoked.");
            if (pending is not null && !string.Equals(pending.RunId, runId, StringComparison.Ordinal))
                throw new CoordinationUncertainException("Foreign run cannot wait for this original renewal.");
            return pending;
        }
    }

    internal void Install(Pending actual, Action installFreshAuthority)
    {
        lock (originalGate)
        {
            CheckOriginal();
            if (revoked is not null || !ReferenceEquals(pending, actual) || Elapsed >= actual.SafeDeadline)
                throw new CoordinationUncertainException("Original renewal did not settle before its previous safe deadline.");
            installFreshAuthority();
            CheckOriginal();
            if (Elapsed >= actual.SafeDeadline)
                throw new CoordinationUncertainException("Original safe deadline passed during renewal installation.");
            actual.Installed = true;
            pending = null;
            actual.Settlement.TrySetResult();
        }
    }

    internal void Revoke(Exception error)
    {
        lock (originalGate)
        {
            revoked ??= error;
            if (pending is not null)
            {
                pending.Settlement.TrySetException(revoked);
                _ = pending.Settlement.Task.Exception;
            }
        }
    }

    internal static TimeSpan RemainingRestriction(long existingStarted, TimeSpan existingWorkLimit)
    {
        if (existingStarted <= 0 || existingStarted > Stopwatch.GetTimestamp() ||
            existingWorkLimit <= TimeSpan.Zero || existingWorkLimit > TimeSpan.FromSeconds(1200))
            throw new ArgumentOutOfRangeException(nameof(existingWorkLimit), "Finite existing work restriction required.");
        var remaining = existingWorkLimit - Stopwatch.GetElapsedTime(existingStarted);
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Existing subordinate WORK endpoint expired during parent currentness check.");
        return remaining;
    }

    internal void Wait(Pending actual, long? existingStarted = null, TimeSpan existingWorkLimit = default)
    {
        // A reentrant caller must refuse rather than strand the renewal installer.
        if (Monitor.IsEntered(originalGate)) throw new CoordinationUncertainException("Cannot wait for renewal while holding original admission.");
        CheckOriginal();
        if (existingStarted is { } beforeStart) _ = RemainingRestriction(beforeStart, existingWorkLimit);
        while (true)
        {
            var count = Volatile.Read(ref actual.WaitingCount);
            if (count >= 1024) throw new IOException("Finite original renewal waiter credit exhausted.");
            if (Interlocked.CompareExchange(ref actual.WaitingCount, count + 1, count) == count) break;
        }
        try
        {
            CheckOriginal();
            if (!actual.Settlement.Task.IsCompleted)
            {
                var remaining = original.Remaining;
                var grantRemaining = actual.SafeDeadline - Elapsed;
                if (grantRemaining <= TimeSpan.Zero) throw new TimeoutException("Previous original safe grant expired during renewal.");
                var limit = remaining < grantRemaining ? remaining : grantRemaining;
                if (existingStarted is { } start)
                {
                    var restricted = RemainingRestriction(start, existingWorkLimit);
                    if (restricted < limit) limit = restricted;
                }
                actual.Settlement.Task.WaitAsync(limit, original.Token).GetAwaiter().GetResult();
            }
            else actual.Settlement.Task.GetAwaiter().GetResult();
            CheckOriginal();
            if (existingStarted is { } afterStart) _ = RemainingRestriction(afterStart, existingWorkLimit);
            lock (originalGate)
            {
                if (revoked is not null || !actual.Installed)
                    throw new CoordinationUncertainException("Original renewal did not install fresh authority.");
            }
        }
        finally { Interlocked.Decrement(ref actual.WaitingCount); }
    }
}
