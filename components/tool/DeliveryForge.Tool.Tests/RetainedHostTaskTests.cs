using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;

public sealed class RetainedHostTaskTests
{
    static readonly ToolResult NonAuthority = ToolResult.Incomplete("UNIT_OBSERVED", "Actual task unit result; no installed/native authority.");
    [Fact]
    public async Task Actual_task_is_retained_and_duplicate_start_does_not_reinvoke()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var pending = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new RetainedHostTask<ToolResult>(life); var entered = 0;
        operation.Start(life, () => { entered++; return pending.Task; });
        operation.Start(life, () => throw new InvalidOperationException("duplicate effect"));
        Assert.Same(pending.Task, operation.Actual); Assert.True(operation.Unknown); Assert.Equal(1, entered);
        var first = operation.ObserveAsync(life); var second = operation.ObserveAsync(life);
        pending.SetResult(NonAuthority);
        Assert.Same(NonAuthority, await first); Assert.Same(NonAuthority, await second);
        Assert.False(operation.Unknown); Assert.Equal(1, entered);
    }
    [Fact]
    public async Task Cancellation_ignoring_actual_task_retains_original_and_sticky_nonpass()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), cancel.Token);
        var pending = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new RetainedHostTask<ToolResult>(life); operation.Start(life, () => pending.Task);
        cancel.Cancel();
        _ = await Assert.ThrowsAnyAsync<Exception>(() => operation.ObserveAsync(life));
        Assert.Same(pending.Task, operation.Actual); Assert.False(pending.Task.IsCompleted); Assert.True(operation.Unknown);
        pending.SetResult(NonAuthority);
        _ = await Assert.ThrowsAnyAsync<Exception>(() => operation.ObserveAsync(life));
        Assert.NotNull(operation.Primary); Assert.Same(pending.Task, operation.Actual);
    }
    [Fact]
    public async Task Terminal_actual_fault_and_independent_endpoint_are_both_retained()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), cancel.Token);
        var failure = new IOException("real terminal task failure");
        var actual = Task.FromException<ToolResult>(failure);
        var operation = new RetainedHostTask<ToolResult>(life); operation.Start(life, () => actual); cancel.Cancel();
        var error = await Assert.ThrowsAsync<AggregateException>(() => operation.ObserveAsync(life));
        Assert.Contains(failure, error.InnerExceptions); Assert.Same(failure, operation.Primary); Assert.Same(actual, operation.Actual);
    }
    [Fact]
    public async Task Foreign_original_cannot_adopt_task_or_enter_effect()
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var foreign = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var operation = new RetainedHostTask<ToolResult>(original); var entered = 0;
        Assert.Throws<WorkerEnvelopeException>(() => operation.Start(foreign, () => { entered++; return Task.FromResult(NonAuthority); }));
        Assert.Equal(0, entered); Assert.False(operation.Started);
        operation.Start(original, () => Task.FromResult(NonAuthority));
        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => operation.ObserveAsync(foreign));
        Assert.Same(NonAuthority, await operation.ObserveAsync(original));
    }
    [Fact]
    public async Task Concurrent_start_while_invocation_has_not_returned_is_unknown_and_not_replayed()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var birthEntered = new ManualResetEventSlim(); using var returnAllowed = new ManualResetEventSlim();
        var actual = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new RetainedHostTask<ToolResult>(life); var calls = 0;
        var first = Task.Run(() => operation.Start(life, () => { Interlocked.Increment(ref calls); birthEntered.Set(); if (!returnAllowed.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) throw new TimeoutException("test invocation return"); return actual.Task; }), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(birthEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            operation.Start(life, () => throw new IOException("duplicate invocation"));
            Assert.True(operation.Unknown); Assert.Null(operation.Actual); Assert.Equal(1, calls);
        }
        finally { returnAllowed.Set(); }
        await first; Assert.Same(actual.Task, operation.Actual); actual.SetResult(NonAuthority);
        Assert.Same(NonAuthority, await operation.ObserveAsync(life));
    }
    [Fact]
    public async Task Synchronous_invocation_failure_stays_unknown_and_cannot_retry()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var failure = new IOException("invocation failed before returning Task");
        var operation = new RetainedHostTask<ToolResult>(life); operation.Start(life, () => throw failure);
        operation.Start(life, () => throw new InvalidOperationException("retry"));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => operation.ObserveAsync(life)));
        Assert.True(operation.Unknown); Assert.Null(operation.Actual);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reserved_control_publication_and_terminal_result_precede_quality_birth(bool permit)
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var actualControl = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new RetainedHostTask<bool>(life); var quality = new RetainedHostTask<ToolResult>(life);
        var birth = control.Prepare(life, () => actualControl.Task);
        Assert.NotNull(birth); Assert.True(control.Unknown); Assert.Null(control.Actual);
        var qualityCalls = 0;
        async Task<ToolResult> Continue()
        {
            if (!await control.ObserveAsync(life)) throw new IOException("actual control refused");
            quality.Start(life, () => { Interlocked.Increment(ref qualityCalls); return Task.FromResult(NonAuthority); });
            return await quality.ObserveAsync(life);
        }
        var first = Continue(); var second = Continue();
        Assert.Equal(0, qualityCalls); birth(); Assert.Same(actualControl.Task, control.Actual);
        Assert.Equal(0, qualityCalls); actualControl.SetResult(permit);
        if (permit) { Assert.Same(NonAuthority, await first); Assert.Same(NonAuthority, await second); Assert.Equal(1, qualityCalls); }
        else { await Assert.ThrowsAsync<IOException>(() => first); await Assert.ThrowsAsync<IOException>(() => second); Assert.Equal(0, qualityCalls); Assert.False(quality.Started); }
    }
}
