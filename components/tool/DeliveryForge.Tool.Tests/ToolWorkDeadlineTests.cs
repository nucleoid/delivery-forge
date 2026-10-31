using System.Diagnostics;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;

public sealed class ToolWorkDeadlineTests
{
    [Theory]
    [InlineData(120)]
    [InlineData(1200)]
    public async Task Reservation_work_clamp_cancels_same_token_without_whole_or_cleanup_refill(int wholeSeconds)
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(wholeSeconds));
        var started = life.OriginalStarted;
        var whole = life.WholeLimit;
        var token = life.Token;
        var endpoint = Stopwatch.GetElapsedTime(started) + TimeSpan.FromSeconds(2);
        life.RestrictWork(endpoint);
        Assert.Equal(endpoint, life.WorkLimit);
        Assert.Equal(whole, life.WholeLimit);
        Assert.Equal(started, life.OriginalStarted);
        Assert.Equal(token, life.Token);
        life.RestrictWork(whole); // A later leaf/request cannot refill the shared original.
        Assert.Equal(endpoint, life.WorkLimit);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, wait.Token));
        Assert.True(token.IsCancellationRequested);
        Assert.ThrowsAny<OperationCanceledException>(() => life.CheckWork());
        var firstCleanup = life.CleanupRemaining;
        Assert.True(firstCleanup > TimeSpan.Zero && firstCleanup <= TimeSpan.FromSeconds(60));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(life.CleanupRemaining < firstCleanup);
        Assert.Equal(whole, life.WholeLimit);
        Assert.False(life.HasExplicitUnknown);
    }

    [Fact]
    public void Work_clamp_cannot_reopen_cleanup_or_change_its_endpoints()
    {
        using var life = new ToolLifetime(TimeSpan.FromSeconds(120));
        var whole = life.WholeLimit;
        var work = life.WorkLimit;
        var firstCleanup = life.CleanupRemaining;
        Assert.Throws<TimeoutException>(() => life.RestrictWork(TimeSpan.FromSeconds(100)));
        Assert.Equal(whole, life.WholeLimit);
        Assert.Equal(work, life.WorkLimit);
        Assert.True(life.CleanupRemaining <= firstCleanup);
    }
}
