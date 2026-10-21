using System.Diagnostics;

namespace DeliveryForge.Evidence.Tests;

// Managed scalar/refusal controls only; no native owner, scope, closure or
// production authority is inferred from these deliberately controlled inputs.
public sealed class ScopedCaptureBudgetBoundaryTests
{
    [Fact]
    public void Native_observation_crossing_same_endpoint_refuses_before_negative_delay_is_constructed()
    {
        var actual = new Budget(); using var budget = new ScopedCaptureBudget(actual, TimeSpan.FromSeconds(10), CancellationToken.None);
        var started = budget.OriginalStarted; var initial = budget.NextObservationDelay();
        Assert.InRange(initial, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(1));
        actual.Limit = TimeSpan.FromTicks(1); // Restrict the same origin; never reset it.
        Assert.Throws<TimeoutException>(() => budget.NextObservationDelay());
        Assert.Equal(started, budget.OriginalStarted);
    }
    [Theory]
    [InlineData("cancelled")]
    [InlineData("timeout")]
    [InlineData("io")]
    public void Same_elapsed_WORK_endpoint_is_timeout_even_when_parent_timer_surfaces_as_cancellation(string kind)
    {
        using var stop = new CancellationTokenSource(); var actual = new Budget(stop.Token);
        using var budget = new ScopedCaptureBudget(actual, TimeSpan.FromSeconds(10), CancellationToken.None);
        actual.Limit = TimeSpan.FromTicks(1); stop.Cancel();
        Exception failure = kind switch { "timeout" => new TimeoutException(), "io" => new IOException(), _ => new OperationCanceledException(stop.Token) };
        Assert.Equal((true, false), budget.ClassifyFailure(failure, callerCancellationRequested: false));
    }
    [Fact]
    public void Explicit_caller_cancellation_is_retained_without_relabeling_it_as_deadline()
    {
        var actual = new Budget(); using var budget = new ScopedCaptureBudget(actual, TimeSpan.FromSeconds(10), CancellationToken.None);
        actual.Limit = TimeSpan.FromTicks(1);
        Assert.Equal((false, true), budget.ClassifyFailure(new OperationCanceledException(), callerCancellationRequested: true));
    }
    [Fact]
    public void Parent_cancellation_before_endpoint_and_non_deadline_failure_keep_distinct_classification()
    {
        using var stop = new CancellationTokenSource(); var actual = new Budget(stop.Token);
        using var budget = new ScopedCaptureBudget(actual, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal((false, false), budget.ClassifyFailure(new IOException(), callerCancellationRequested: false));
        stop.Cancel();
        Assert.Equal((false, true), budget.ClassifyFailure(new OperationCanceledException(stop.Token), callerCancellationRequested: false));
    }
    [Fact]
    public void First_and_later_parent_checks_receive_same_local_origin_and_bound_and_local_postcheck_still_refuses()
    {
        var actual = new RestrictedBudget();
        using var budget = new ScopedCaptureBudget(actual, TimeSpan.FromSeconds(10), CancellationToken.None);
        var first = actual.Restrictions.Single();
        Assert.InRange(first.Started, actual.OriginalStarted, Stopwatch.GetTimestamp());
        Assert.Equal(BoundedCaptureStream.WorkBudget(TimeSpan.FromSeconds(10)), first.Work);
        budget.CheckWork(); Assert.Equal(first, actual.Restrictions[1]);
        actual.ExpireDuringCheck = true;
        Assert.Throws<TimeoutException>(budget.CheckWork);
        Assert.Equal(first, actual.Restrictions[2]); Assert.Equal(0, actual.UnboundedChecks);
    }
    [Fact]
    public void Initial_restricted_parent_refusal_keeps_exact_primary_without_any_native_birth()
    {
        var failure = new IOException("Actual managed parent refused its first check.");
        var actual = new RestrictedBudget { CheckFailure = failure };
        Assert.Same(failure, Assert.Throws<IOException>(() => new ScopedCaptureBudget(actual, TimeSpan.FromSeconds(10), CancellationToken.None)));
        Assert.Single(actual.Restrictions); Assert.Equal(0, actual.UnboundedChecks);
    }
    sealed class RestrictedBudget : IOwnedDuplexBudget, IOwnedRestrictedWorkCheck
    {
        internal readonly List<(long Started, TimeSpan Work)> Restrictions = [];
        internal int UnboundedChecks;
        internal bool ExpireDuringCheck;
        internal Exception? CheckFailure;
        TimeSpan limit = TimeSpan.FromSeconds(30);
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(40);
        public TimeSpan WorkLimit => limit;
        public CancellationToken Token => CancellationToken.None;
        public void CheckWork() => UnboundedChecks++;
        public void CheckWork(long existingStarted, TimeSpan existingWork)
        {
            Restrictions.Add((existingStarted, existingWork));
            if (CheckFailure is not null) throw CheckFailure;
            if (ExpireDuringCheck) limit = TimeSpan.FromTicks(1);
        }
        public TimeSpan CleanupRemaining => TimeSpan.FromSeconds(10);
    }
    sealed class Budget(CancellationToken token = default) : IOwnedDuplexBudget
    {
        internal TimeSpan Limit = TimeSpan.FromSeconds(30);
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(40);
        public TimeSpan WorkLimit => Limit;
        public CancellationToken Token => token;
        public void CheckWork() { }
        public TimeSpan CleanupRemaining => TimeSpan.FromSeconds(10);
    }
}
