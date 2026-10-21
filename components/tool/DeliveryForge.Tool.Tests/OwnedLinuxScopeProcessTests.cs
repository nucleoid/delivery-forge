using DeliveryForge.Evidence;
using DeliveryForge.Tool;
using Run = DeliveryForge.Tool.Tests.OwnedDuplexProcessTests.Run;
using Affinity = DeliveryForge.Tool.Tests.OwnedDuplexProcessTests.Affinity;
namespace DeliveryForge.Tool.Tests;
// Only the two additional Linux cases. Existing six genuine bothOS controls are
// retained in OwnedDuplexProcessTests, not duplicated as mirrored cases here.
public sealed class OwnedLinuxScopeProcessTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();
    static void AssertPhysicalScope(DuplexRetirement actual) { Assert.True(actual.KnownClosed, string.Join(";", actual.Failures)); Assert.True(actual.RootExited); Assert.Equal(0u, actual.ActiveProcesses); Assert.True(actual.DispositionReturned); }
    [Fact(Timeout = 90000, Skip = "Creation-owned Linux scope requires Linux.", SkipUnless = nameof(IsLinux))]
    public async Task Actual_fast_target_returns_only_after_real_scope_empty_and_disposition()
    {
        using var affinity = new Affinity(); await using var run = new Run("fast-exit"); await run.Ready(); var closed = await run.Session.RetireAsync(false); AssertPhysicalScope(closed); Assert.Equal(0, closed.ExitCode); Assert.Empty(closed.Failures); run.Preserve(closed);
    }
    [Fact(Timeout = 90000, Skip = "Creation-owned Linux scope requires Linux.", SkipUnless = nameof(IsLinux))]
    public async Task Actual_partial_birth_READY_never_releases_target_and_real_bound_group_retires()
    {
        using var affinity = new Affinity(); await using var run = new Run("echo", malformedGate: true);
        // The separately compiled fixture deliberately emits a partial birth
        // frame and waits without releasing a target. Parent retains only its
        // actual creation-owned group identity, never a name/PID adoption.
        Assert.NotNull(run.Session.StartupFailure); var closed = await run.Session.RetireAsync(true);
        // Private actual PID+generation/cgroup/FD binding preceded READY. The
        // original group can therefore be stopped even though target release
        // never returned. No success or foreign name/PID adoption is inferred.
        AssertPhysicalScope(closed); Assert.NotEmpty(closed.Failures); Assert.NotEqual(0, closed.ExitCode); run.Preserve(closed);
    }
}
