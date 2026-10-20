using DeliveryForge.Coordination;
using DeliveryForge.Tool;

namespace DeliveryForge.Tool.Tests;

// Conformance of the actual production gate implementation with local original Tasks. No live service or worker certification.
public sealed class ParentEffectAdmissionTests
{
    sealed class RenewalFixture : IDisposable
    {
        internal readonly object Gate = new();
        internal readonly ToolLifetime Original;
        internal readonly ParentRenewalHandoff Handoff;
        internal readonly ParentEffectAdmission Admission;
        internal bool Current = true, Revoked;
        internal RenewalFixture(CancellationToken cancellation = default)
        {
            Original = new(TimeSpan.FromSeconds(30), cancellation);
            Handoff = new(Gate, Original);
            Admission = new(Gate, run => run == "owned" && Current && !Revoked && ParentCoordinationHost.OriginalCanAdmitEffects(Original), Revoke, Handoff);
        }
        internal void Revoke(Exception error)
        {
            lock (Gate) { Revoked = true; Current = false; Handoff.Revoke(error); }
        }
        internal ParentRenewalHandoff.Pending Begin(TimeSpan? deadline = null)
        {
            lock (Gate)
            {
                var pending = Handoff.Begin("owned", deadline ?? Handoff.Elapsed + TimeSpan.FromSeconds(10));
                Current = false; // The reducer has withdrawn old dispatch authority.
                return pending;
            }
        }
        internal void Install(ParentRenewalHandoff.Pending pending) => Handoff.Install(pending, () => Current = true);
        public void Dispose() { Revoke(new OperationCanceledException("Fixture original closed.")); Original.Dispose(); }
    }

    internal static async Task WaitForWaiters(ParentRenewalHandoff.Pending pending, int count, Task actual)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        while (Volatile.Read(ref pending.WaitingCount) < count && !actual.IsCompleted &&
            System.Diagnostics.Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5))
            await Task.Delay(1, TestContext.Current.CancellationToken);
        Assert.True(Volatile.Read(ref pending.WaitingCount) >= count);
        Assert.False(actual.IsCompleted);
    }

    [Fact]
    public void Bounded_nested_fast_check_keeps_atomic_birth_and_actual_task_identity()
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        var actual = Task.FromResult(7);
        var returned = fixture.Admission.EnterAsync("owned", () =>
        {
            Assert.True(Monitor.IsEntered(fixture.Gate));
            fixture.Admission.CheckCurrent("owned", began, TimeSpan.FromSeconds(5));
            return actual;
        });
        Assert.Same(actual, returned); Assert.False(fixture.Revoked);
    }

    [Fact(Timeout = 15000)]
    public async Task Consecutive_pending_checks_keep_the_same_subordinate_endpoint()
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        var first = fixture.Begin();
        var waiting = Task.Run(() => fixture.Admission.CheckCurrent("owned", began, TimeSpan.FromSeconds(4)), TestContext.Current.CancellationToken);
        await WaitForWaiters(first, 1, waiting);
        await Task.Delay(2300, TestContext.Current.CancellationToken);
        ParentRenewalHandoff.Pending second;
        lock (fixture.Gate) { fixture.Install(first); second = fixture.Begin(); }
        await WaitForWaiters(second, 1, waiting);
        await Assert.ThrowsAnyAsync<TimeoutException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(fixture.Revoked); Assert.False(second.Installed); Assert.Equal(0, fixture.Admission.EnteredCount);
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(5));
    }

    [Fact(Timeout = 15000)]
    public void Bounded_fast_currentness_rechecks_expiry_after_current_predicate_returns()
    {
        var gate = new object(); var revoked = false; var called = false;
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        var actual = new ParentEffectAdmission(gate, _ =>
        {
            called = true;
            while (System.Diagnostics.Stopwatch.GetElapsedTime(began) < TimeSpan.FromMilliseconds(200)) Thread.Sleep(1);
            return true;
        }, _ => revoked = true);
        Assert.Throws<TimeoutException>(() => actual.CheckCurrent("owned", began, TimeSpan.FromMilliseconds(100)));
        Assert.True(called); Assert.True(revoked); Assert.Equal(0, actual.EnteredCount);
    }

    [Fact(Timeout = 15000)]
    public async Task Already_completed_settlement_cannot_refund_expired_subordinate_work()
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var pending = fixture.Begin(); fixture.Install(pending);
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.True(pending.Settlement.Task.IsCompletedSuccessfully);
        Assert.Throws<TimeoutException>(() => fixture.Handoff.Wait(pending, began, TimeSpan.FromMilliseconds(100)));
        Assert.Throws<TimeoutException>(() => fixture.Admission.CheckCurrent("owned", began, TimeSpan.FromMilliseconds(100)));
        Assert.True(fixture.Revoked); Assert.Equal(0, fixture.Admission.EnteredCount);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("zero")]
    [InlineData("unbounded")]
    public void Invalid_work_restriction_revokes_without_creating_authority(string invalid)
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        var work = TimeSpan.FromSeconds(5);
        if (invalid == "future") began += System.Diagnostics.Stopwatch.Frequency * 60;
        else if (invalid == "zero") work = TimeSpan.Zero;
        else work = TimeSpan.MaxValue;
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Admission.CheckCurrent("owned", began, work));
        Assert.True(fixture.Revoked); Assert.Equal(0, fixture.Admission.EnteredCount);
    }

    [Theory(Timeout = 15000)]
    [InlineData("check")]
    [InlineData("synchronous")]
    [InlineData("asynchronous")]
    public async Task Pending_renewal_defers_outside_gate_and_preserves_fresh_admission_and_actual_task(string operation)
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var pending = fixture.Begin(); var calls = 0;
        var actual = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? returned = null;
        var waiting = Task.Run(() =>
        {
            if (operation == "check") fixture.Admission.CheckCurrent("owned");
            else if (operation == "synchronous") Assert.Equal(7, fixture.Admission.EnterSynchronous("owned", () =>
            {
                fixture.Admission.CheckCurrent("owned"); Interlocked.Increment(ref calls); return 7;
            }));
            else returned = fixture.Admission.EnterAsync("owned", () =>
            {
                fixture.Admission.CheckCurrent("owned"); Interlocked.Increment(ref calls); return actual.Task;
            });
        }, TestContext.Current.CancellationToken);
        await WaitForWaiters(pending, 1, waiting);
        Assert.Equal(0, calls); Assert.Equal(0, fixture.Admission.EnteredCount); Assert.False(fixture.Revoked);
        fixture.Install(pending); // Would deadlock if a waiter retained the parent monitor.
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(fixture.Revoked);
        if (operation == "asynchronous")
        {
            Assert.Same(actual.Task, returned); actual.SetResult(7); Assert.Equal(7, await returned!);
        }
        Assert.Equal(operation == "check" ? 0 : 1, calls);
        await Task.WhenAll(fixture.Admission.OriginalObservers).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(fixture.Admission.AllKnownSettled);
    }

    [Fact(Timeout = 15000)]
    public async Task Second_pending_renewal_is_rechecked_before_any_birth()
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var first = fixture.Begin(); var calls = 0;
        var waiting = Task.Run(() => fixture.Admission.EnterSynchronous("owned", () => ++calls), TestContext.Current.CancellationToken);
        await WaitForWaiters(first, 1, waiting);
        ParentRenewalHandoff.Pending second;
        lock (fixture.Gate) { fixture.Install(first); second = fixture.Begin(); }
        await WaitForWaiters(second, 1, waiting);
        Assert.Equal(0, calls); Assert.Equal(0, fixture.Admission.EnteredCount);
        fixture.Install(second); Assert.Equal(1, await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_revocation_wakes_waiter_before_stop_can_wait_for_it(bool renewalFailure)
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var pending = fixture.Begin(); var calls = 0;
        var waiting = Task.Run(() => fixture.Admission.EnterSynchronous("owned", () => ++calls), TestContext.Current.CancellationToken);
        await WaitForWaiters(pending, 1, waiting);
        fixture.Revoke(renewalFailure ? new IOException("Actual renewal returned failure.") : new OperationCanceledException("Sticky revocation."));
        await Assert.ThrowsAnyAsync<Exception>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(pending.Settlement.Task.IsFaulted); Assert.True(fixture.Revoked);
        Assert.Throws<CoordinationUncertainException>(() => fixture.Install(pending));
        Assert.Equal(0, calls); Assert.Equal(0, fixture.Admission.EnteredCount); Assert.True(fixture.Admission.AllKnownSettled);
    }

    [Fact(Timeout = 15000)]
    public async Task Original_cancellation_wakes_pending_admission_without_a_birth()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var fixture = new RenewalFixture(cancellation.Token);
        var pending = fixture.Begin(); var calls = 0;
        var waiting = Task.Run(() => fixture.Admission.EnterAsync("owned", () => { calls++; return Task.FromResult(1); }), TestContext.Current.CancellationToken);
        await WaitForWaiters(pending, 1, waiting); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(fixture.Revoked); Assert.Equal(0, calls); Assert.Equal(0, fixture.Admission.EnteredCount);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Previous_safe_deadline_or_restricted_original_ends_wait_without_reset(bool originalEndpoint)
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var pending = fixture.Begin(originalEndpoint ? null : fixture.Handoff.Elapsed + TimeSpan.FromSeconds(2));
        var calls = 0;
        var waiting = Task.Run(() => fixture.Admission.EnterSynchronous("owned", () => ++calls), TestContext.Current.CancellationToken);
        await WaitForWaiters(pending, 1, waiting);
        if (originalEndpoint) fixture.Original.Restrict(fixture.Handoff.Elapsed + TimeSpan.FromSeconds(4));
        await Assert.ThrowsAnyAsync<Exception>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(fixture.Revoked); Assert.Equal(0, calls); Assert.False(pending.Installed);
    }

    [Fact(Timeout = 15000)]
    public async Task Already_completed_renewal_cannot_install_after_previous_safe_deadline()
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var pending = fixture.Begin(fixture.Handoff.Elapsed + TimeSpan.FromMilliseconds(300));
        var returnedRenewal = Task.FromResult(7);
        while (fixture.Handoff.Elapsed < pending.SafeDeadline) await Task.Delay(1, TestContext.Current.CancellationToken);
        Assert.True(returnedRenewal.IsCompletedSuccessfully);
        Assert.Throws<CoordinationUncertainException>(() => fixture.Install(pending));
        Assert.False(pending.Installed); Assert.False(fixture.Current); Assert.False(pending.Settlement.Task.IsCompletedSuccessfully);
    }

    [Fact(Timeout = 15000)]
    public void Installation_crossing_previous_safe_deadline_cannot_complete_the_handoff()
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var pending = fixture.Begin(fixture.Handoff.Elapsed + TimeSpan.FromMilliseconds(300));
        Assert.Throws<CoordinationUncertainException>(() => fixture.Handoff.Install(pending, () =>
        {
            while (fixture.Handoff.Elapsed < pending.SafeDeadline) { fixture.Original.Check(); Thread.Sleep(1); }
        }));
        Assert.False(pending.Installed); Assert.False(pending.Settlement.Task.IsCompletedSuccessfully);
        Assert.Same(pending, fixture.Handoff.Snapshot("owned"));
    }

    [Fact]
    public void Nested_pending_check_refuses_instead_of_waiting_under_outer_original_gate()
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        Assert.Throws<CoordinationUncertainException>(() =>
        {
            _ = fixture.Admission.EnterAsync("owned", () =>
            {
                fixture.Begin(); fixture.Admission.CheckCurrent("owned"); return Task.FromResult(1);
            });
        });
        Assert.True(fixture.Revoked); Assert.False(fixture.Admission.AllKnownSettled);
    }

    [Theory(Timeout = 15000)]
    [InlineData("check")]
    [InlineData("synchronous")]
    [InlineData("asynchronous")]
    public void Foreign_run_refuses_before_waiting_or_enrolling_behind_an_owned_renewal(string operation)
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var pending = fixture.Begin(); var calls = 0;
        Assert.Throws<CoordinationUncertainException>(() =>
        {
            if (operation == "check") fixture.Admission.CheckCurrent("foreign");
            else if (operation == "synchronous") fixture.Admission.EnterSynchronous("foreign", () => ++calls);
            else _ = fixture.Admission.EnterAsync("foreign", () => { calls++; return Task.FromResult(1); });
        });
        Assert.True(fixture.Revoked); Assert.Equal(0, calls); Assert.Equal(0, pending.WaitingCount); Assert.Equal(0, fixture.Admission.EnteredCount);
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("check")]
    [InlineData("wait")]
    [InlineData("install")]
    public void Explicit_original_unknown_cannot_begin_wait_or_install_a_fresh_handoff(string operation)
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var pending = operation == "begin" ? null : fixture.Begin();
        fixture.Original.RetainUnknown();
        Assert.Throws<CoordinationUncertainException>(() =>
        {
            if (operation == "begin") fixture.Begin();
            else if (operation == "check") fixture.Admission.CheckCurrent("owned");
            else if (operation == "wait") fixture.Handoff.Wait(pending!);
            else fixture.Install(pending!);
        });
        Assert.Equal(0, fixture.Admission.EnteredCount);
        if (pending is not null) { Assert.Equal(0, pending.WaitingCount); Assert.False(pending.Installed); }
    }

    [Fact]
    public void Finite_waiter_credit_refuses_before_incrementing_or_enrolling()
    {
        using var fixture = new RenewalFixture(TestContext.Current.CancellationToken);
        var pending = fixture.Begin();
        // Counter-boundary conformance only; no fake actual Tasks or 1024 threads.
        pending.WaitingCount = 1024;
        try
        {
            Assert.Throws<IOException>(() => fixture.Admission.CheckCurrent("owned"));
            Assert.Equal(1024, pending.WaitingCount); Assert.Equal(0, fixture.Admission.EnteredCount); Assert.True(fixture.Revoked);
        }
        finally { pending.WaitingCount = 0; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Returned_synchronous_refusal_revokes_new_work_but_only_explicit_unknown_prevents_quiescence(bool unknown)
    {
        var current = true;
        var actual = new ParentEffectAdmission(new object(), _ => current, _ => current = false);
        Assert.Throws<IOException>(() => actual.EnterSynchronous<int>("owned", () =>
        {
            if (unknown) actual.RetainUncertain("owned", "Actual unresolved resource disposition.");
            throw new IOException("Known returned file validation failure.");
        }));
        Assert.False(current);
        Assert.Equal(!unknown, actual.AllKnownSettled);
        Assert.Throws<CoordinationUncertainException>(() => actual.EnterSynchronous("owned", () => 2));
        Assert.Equal(1, actual.EnteredCount);
    }

    [Fact]
    public void Returned_synchronous_effect_has_settled_its_original_observer()
    {
        var actual = new ParentEffectAdmission(new object(), run => run == "owned", _ => { });
        Assert.Equal(7, actual.EnterSynchronous("owned", () => 7));
        Assert.Equal(1, actual.EnteredCount);
        Assert.True(actual.AllKnownSettled);
        Assert.All(actual.OriginalObservers, observer => Assert.True(observer.IsCompletedSuccessfully));
    }

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
