namespace DeliveryForge.Evidence.Tests;

// Pure predicates used by actual selected-policy birth. These controls grant no
// namespace, process, placement, descriptor-disposition or runtime authority.
public sealed class LinuxScopeOwnerBoundaryTests
{
    [Fact]
    public void Sealed_backing_remains_default_and_root_disk_has_distinct_gate_and_ready_names()
    {
        var image = new LinuxScopeImage("/image", 64, new string('a', 64));
        var selection = new LinuxScopeSelection(image, image, "255", "6.8.0", image, [image], image);
        Assert.Equal(LinuxExactImageBacking.SealedMemfd, selection.ExactImageBacking);
        Assert.Equal("--exact-native-elf", LinuxExactImageBackingRules.GateArgument(selection.ExactImageBacking, false));
        Assert.Equal("ELF", LinuxExactImageBackingRules.ReadyMarker(selection.ExactImageBacking));
        Assert.Equal("--root-controlled-native-elf", LinuxExactImageBackingRules.GateArgument(LinuxExactImageBacking.RootControlledDisk, false));
        Assert.Equal("--root-controlled-native-elf-nnp-user1000-ancestor-v3", LinuxExactImageBackingRules.GateArgument(LinuxExactImageBacking.RootControlledDisk, true));
        Assert.Equal("ROOT_DISK_ELF", LinuxExactImageBackingRules.ReadyMarker(LinuxExactImageBacking.RootControlledDisk));
        Assert.Throws<IOException>(() => LinuxExactImageBackingRules.Require((LinuxExactImageBacking)2, "/image"));
    }
    [Theory]
    [InlineData("/")]
    [InlineData("relative/image")]
    [InlineData("/a//image")]
    [InlineData("/a/../image")]
    [InlineData("/a/./image")]
    [InlineData("/a/image/")]
    [InlineData("/a/line\nimage")]
    [InlineData("/a/\\image")]
    public void Root_disk_noncanonical_path_refuses_before_native_birth(string path)
        => Assert.Throws<IOException>(() => LinuxExactImageBackingRules.Require(LinuxExactImageBacking.RootControlledDisk, path));
    [Fact]
    public void Root_disk_path_depth_and_extent_are_bounded_before_native_birth()
    {
        LinuxExactImageBackingRules.Require(LinuxExactImageBacking.RootControlledDisk, "/opt/forge/images/codex");
        Assert.Throws<IOException>(() => LinuxExactImageBackingRules.Require(LinuxExactImageBacking.RootControlledDisk, "/" + string.Join('/', Enumerable.Repeat("a", 33))));
        Assert.Throws<IOException>(() => LinuxExactImageBackingRules.Require(LinuxExactImageBacking.RootControlledDisk, "/" + new string('a', 4096)));
    }
    [Theory]
    [InlineData(0, 1, 0x816d, true)] // Regular root-owned 0555.
    [InlineData(0, 1, 0x8140, false)] // Root-only 0500 cannot serve UID1000.
    [InlineData(0, 1, 0x8164, false)] // 0544 lacks other execute.
    [InlineData(1000, 1, 0x816d, false)]
    [InlineData(0, 2, 0x816d, false)]
    [InlineData(0, 0, 0x816d, false)]
    [InlineData(0, 1, 0x81ed, false)] // Owner writable 0755.
    [InlineData(0, 1, 0x817d, false)] // Group writable.
    [InlineData(0, 1, 0x816f, false)] // Other writable.
    [InlineData(0, 1, 0x896d, false)] // setuid.
    [InlineData(0, 1, 0x856d, false)] // setgid.
    [InlineData(0, 1, 0x836d, false)] // sticky file.
    [InlineData(0, 1, 0x8124, false)] // No executable bits.
    [InlineData(0, 1, 0xa16d, false)] // Symbolic link.
    public void Root_disk_file_metadata_requires_exact_protected_shape(int uid, int links, int mode, bool accepted)
    {
        var failure = Record.Exception(() => LinuxExactImageBackingRules.RequireRootFileMetadata((uint)uid, (uint)links, (ushort)mode));
        if (accepted) Assert.Null(failure); else Assert.IsType<IOException>(failure);
    }
    [Theory]
    [InlineData(0, 0x41ed, true)] // Root-owned 0755 directory permits owner write.
    [InlineData(1000, 0x41ed, false)]
    [InlineData(0, 0x41fd, false)] // Group writable.
    [InlineData(0, 0x41ef, false)] // Other writable.
    [InlineData(0, 0xa1ed, false)] // Symbolic link.
    public void Root_disk_directory_metadata_refuses_untrusted_replacement(int uid, int mode, bool accepted)
    {
        var failure = Record.Exception(() => LinuxExactImageBackingRules.RequireRootDirectoryMetadata((uint)uid, (ushort)mode));
        if (accepted) Assert.Null(failure); else Assert.IsType<IOException>(failure);
    }

    [Theory]
    [InlineData(0xef53L, 0x20UL, true)] // ext4 family, kernel-valid and executable.
    [InlineData(0x58465342L, 0x20UL, true)] // XFS.
    [InlineData(0x9123683eL, 0x20UL, true)] // Btrfs.
    [InlineData(0x65735546L, 0x20UL, false)] // FUSE can report arbitrary ownership.
    [InlineData(0x6969L, 0x20UL, false)] // NFS.
    [InlineData(0x01021994L, 0x20UL, false)] // tmpfs.
    [InlineData(0x794c7630L, 0x20UL, false)] // overlay.
    [InlineData(0xef53L, 0UL, false)] // No kernel-valid flags.
    [InlineData(0xef53L, 0x28UL, false)] // ST_NOEXEC.
    public void Root_disk_filesystem_refuses_untrusted_or_nonexecutable_backing(long type, ulong flags, bool accepted)
    {
        var error = Record.Exception(() => LinuxExactImageBackingRules.RequireRootLocalFileSystem(type, flags));
        if (accepted) Assert.Null(error); else Assert.IsType<IOException>(error);
    }
    [Theory]
    [InlineData(100UL, 8U, 1U, true)]
    [InlineData(101UL, 8U, 1U, false)] // Same device, different mount.
    [InlineData(100UL, 9U, 1U, false)] // Different major.
    [InlineData(100UL, 8U, 2U, false)] // Different minor.
    public void Root_disk_chain_cannot_cross_the_observed_root_mount_or_device(ulong mount, uint major, uint minor, bool accepted)
    {
        var error = Record.Exception(() => LinuxExactImageBackingRules.RequireRootMount(mount, major, minor, 100, 8, 1));
        if (accepted) Assert.Null(error); else Assert.IsType<IOException>(error);
        Assert.Throws<IOException>(() => LinuxExactImageBackingRules.RequireRootMount(0, 8, 1, 0, 8, 1));
    }

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
