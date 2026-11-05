using System.Diagnostics;

namespace DeliveryForge.Evidence.Tests;

public sealed class BoundedCommandCaptureGuardTests
{
    [Fact]
    public async Task Injected_owner_delegates_cannot_mint_bounded_capture_proof()
    {
        var callbacks = 0;
        var executor = new ProcessCommandExecutor(
            killTree: _ => callbacks++, hasExited: _ => { callbacks++; return true; },
            waitForDrain: (_, _) => { callbacks++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteBoundedCaptureAsync(
            new CommandInvocation("not-an-executable-fixture", [], AppContext.BaseDirectory),
            TimeSpan.FromSeconds(1), 1024, TestContext.Current.CancellationToken));
        Assert.Equal(0, callbacks);
        // This is an injection-boundary refusal, not actual root/stream quiescence evidence.
    }
    static LinuxScopeSelection ScopeData()
    {
        var image = new LinuxScopeImage("/DATA/not-an-executable", 1, new string('a', 64));
        return new(image, image, "DATA-only", "DATA-only", image, [], image);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configured_capture_rejects_foreign_scope_or_original_before_budget_or_native_effect(bool foreignScope)
    {
        var actual = new Budget(); var foreign = new Budget(); var selected = ScopeData();
        // Value-equal selection DATA is still a foreign instance. No kernel or
        // image is opened to test this exact restrictive pre-birth boundary.
        var requested = foreignScope ? selected with { } : selected;
        Assert.Throws<InvalidOperationException>(() => ProcessCommandExecutor.RequireSelectedLinuxCaptureOwner(
            selected, actual, foreignScope ? actual : foreign, requested));
        Assert.Equal(0, actual.Checks); Assert.Equal(0, foreign.Checks);
    }
    [Fact]
    public void Same_configured_original_remains_bound_through_actual_restrictive_phase_views()
    {
        var actual = new Budget(); var selected = ScopeData();
        var work = new OwnedWorkPhaseBudget(actual); var cleanup = new OwnedCleanupPhaseBudget(actual);
        var workInsideCleanup = new OwnedWorkPhaseBudget(cleanup); var before = actual.Checks;
        foreach (var view in new IOwnedDuplexBudget[] { actual, work, cleanup, workInsideCleanup })
            ProcessCommandExecutor.RequireSelectedLinuxCaptureOwner(selected, actual, view, selected);
        Assert.Equal(before, actual.Checks);
        Assert.Same(actual, work.Parent); Assert.Same(actual, cleanup.Parent);
    }
    [Fact]
    public void Missing_containing_scope_refuses_owned_Linux_route_without_closing_or_checking_that_owner()
    {
        var actual = new Budget();
        Assert.Throws<PlatformNotSupportedException>(() => ProcessCommandExecutor.RequireUnscopedLinuxCaptureHasNoEnclosingOwner(actual));
        Assert.Equal(0, actual.Checks); Assert.Equal(0, actual.CleanupChecks);
        ProcessCommandExecutor.RequireUnscopedLinuxCaptureHasNoEnclosingOwner(null); // Legacy observation only.
    }
    sealed class Budget : IOwnedDuplexBudget
    {
        internal int Checks, CleanupChecks;
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(1200);
        public TimeSpan WorkLimit => TimeSpan.FromSeconds(1100);
        public CancellationToken Token => CancellationToken.None;
        public void CheckWork() => Checks++;
        public TimeSpan CleanupRemaining { get { CleanupChecks++; return TimeSpan.FromSeconds(60); } }
    }

}
