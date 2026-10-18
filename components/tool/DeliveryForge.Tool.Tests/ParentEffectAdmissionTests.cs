using DeliveryForge.Coordination;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// Conformance of the actual production gate implementation with local original Tasks. No live service or worker certification.
public sealed class ParentEffectAdmissionTests
{
    [Fact]
    public async Task Actual_pending_effect_is_retained_and_revocation_prevents_second_enrollment()
    {
        var gate = new object(); var current = true; var calls = 0; var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actual = new ParentEffectAdmission(gate, run => current && run == "owned", _ => current = false);
        var first = actual.EnterAsync("owned", () => { calls++; return pending.Task; });
        Assert.Same(pending.Task, first); Assert.False(actual.AllKnownSettled); Assert.Equal(1, actual.EnteredCount);
        lock (gate) current = false;
        Assert.Throws<CoordinationUncertainException>(() => { _ = actual.EnterAsync("owned", () => { calls++; return Task.FromResult(2); }); });
        Assert.Equal(1, calls); Assert.False(actual.AllKnownSettled);
        pending.SetResult(1); Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)); await Task.WhenAll(actual.OriginalObservers).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); Assert.True(actual.AllKnownSettled);
    }

    [Fact]
    public void Synchronous_entered_invocation_failure_stays_nonquiet_and_cannot_retry()
    {
        var gate = new object(); var current = true; var calls = 0;
        var actual = new ParentEffectAdmission(gate, _ => current, _ => current = false);
        Assert.Throws<IOException>(() => { _ = actual.EnterAsync<int>("owned", () => { calls++; throw new IOException("Actual invocation failed before returning a task."); }); });
        Assert.False(actual.AllKnownSettled); Assert.Equal(1, actual.EnteredCount);
        Assert.Throws<CoordinationUncertainException>(() => { _ = actual.EnterAsync("owned", () => { calls++; return Task.FromResult(2); }); }); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Completed_fault_does_not_become_successful_quiescence()
    {
        var actual = new ParentEffectAdmission(new object(), _ => true, _ => { });
        var failed = actual.EnterAsync("owned", () => Task.FromException<int>(new IOException("Actual returned failure.")));
        await Assert.ThrowsAsync<IOException>(() => failed); Assert.False(actual.AllKnownSettled); Assert.Equal(1, actual.EnteredCount);
    }
    [Fact]
    public async Task Semantic_remote_uncertainty_stays_latched_after_actual_task_returns_successfully()
    {
        var current = true; var actual = new ParentEffectAdmission(new object(), _ => current, _ => current = false);
        Assert.Equal(1, await actual.EnterAsync("owned", () => Task.FromResult(1)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(actual.AllKnownSettled);
        actual.RetainUncertain("owned", "Explicit fixture unresolved remote publication intent.");
        Assert.False(actual.AllKnownSettled);
        Assert.Throws<CoordinationUncertainException>(() => { _ = actual.EnterAsync("owned", () => Task.FromResult(2)); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Asynchronous_fault_or_cancellation_revokes_before_any_later_actual_effect(bool cancel)
    {
        var current = true; var calls = 0; var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actual = new ParentEffectAdmission(new object(), _ => current, _ => current = false);
        var first = actual.EnterAsync("owned", () => { calls++; return pending.Task; });
        if (cancel) pending.SetCanceled(TestContext.Current.CancellationToken); else pending.SetException(new IOException("Actual asynchronous effect failure."));
        Assert.Throws<CoordinationUncertainException>(() => { _ = actual.EnterAsync("owned", () => { calls++; return Task.FromResult(2); }); });
        await Assert.ThrowsAnyAsync<Exception>(() => first);
        await Task.WhenAll(actual.OriginalObservers).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(current); Assert.Equal(1, calls); Assert.False(actual.AllKnownSettled);
    }

}
