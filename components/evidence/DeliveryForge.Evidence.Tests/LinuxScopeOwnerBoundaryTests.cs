namespace DeliveryForge.Evidence.Tests;

// Pure predicates used by actual selected-policy birth. These controls grant no
// namespace, process, placement, descriptor-disposition or runtime authority.
public sealed class LinuxScopeOwnerBoundaryTests
{
    [Fact]
    public void Same_actual_managed_thread_is_accepted_and_missing_proof_refuses()
    {
        LinuxScopeOwnerBoundary.RequireCallingThread(System.Threading.Thread.CurrentThread);
        Assert.Throws<IOException>(() => LinuxScopeOwnerBoundary.RequireCallingThread(null));
    }

    [Fact]
    public void Another_actual_managed_thread_refuses_the_original_thread()
    {
        var owner = System.Threading.Thread.CurrentThread;
        Exception? failure = null;
        var other = new System.Threading.Thread(() => failure = Record.Exception(() => LinuxScopeOwnerBoundary.RequireCallingThread(owner))) { IsBackground = true };
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(5)), "Pure calling-thread refusal did not return within its finite test wait.");
        Assert.IsType<IOException>(failure);
    }

    [Theory]
    [InlineData("0::/\n")]
    [InlineData("0::/a//b\n")]
    [InlineData("0::/a/..\n")]
    [InlineData("0::/user.slice/user-1000.slice\n")]
    [InlineData("0::/user.slice/user-1000.slice/session-1.scope\n")]
    [InlineData("0::/safe\n0::/other\n")]
    [InlineData("0::/safe")]
    public void Noncanonical_root_or_restricted_membership_refuses(string membership)
        => Assert.Throws<IOException>(() => LinuxScopeOwnerBoundary.RequireOutsideAncestorMembership(membership));

    [Theory]
    [InlineData("0::/system.slice/forge-owner.scope\n")]
    [InlineData("0::/user.slice/user-1000.slice-sibling\n")]
    public void Canonical_outside_membership_and_prefix_sibling_are_accepted(string membership)
        => LinuxScopeOwnerBoundary.RequireOutsideAncestorMembership(membership);
}
