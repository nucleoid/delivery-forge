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
}
