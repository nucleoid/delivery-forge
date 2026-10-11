using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace DeliveryForge.Execution.Tests.Processes;

public sealed class OwnedProcessImageTests
{
    [Theory(Timeout = 120_000)]
    [InlineData("absolute")]
    [InlineData("relative")]
    [InlineData("symlink")]
    public async Task Owned_image_observation_binds_actual_executable_without_lexical_launcher_equality(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-image-red-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(root));
        Directory.CreateDirectory(root);
        await WriteNew(root, "owner.json", new { fixture = true, root, nonce = Guid.NewGuid().ToString("N"), mode });
        var selected = SelectedExecutable();
        var requested = mode == "relative" ? Path.GetRelativePath(Environment.CurrentDirectory, selected) : selected;
        var createdLink = false;
        if (mode == "symlink")
        {
            requested = Path.Combine(root, OperatingSystem.IsWindows() ? "owned-launcher.exe" : "owned-launcher");
            try { File.CreateSymbolicLink(requested, selected); createdLink = true; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                await WriteNew(root, "image-capability.json", new { fixture = true, outcome = "INCOMPLETE", actualLinkCreated = false, reason = exception.GetType().Name, retained = true });
                throw new InvalidOperationException("INCOMPLETE actual executable symlink capability; owned fixture retained: " + root, exception);
            }
        }
        var control = new ProcessControl();
        OwnedProcess? child = null;
        try
        {
            child = control.StartOwned(new(requested, Arguments()));
            var observed = control.ObserveOwnedImage(child.Identity);
            await WriteNew(root, "image-observation.json", new { fixture = true, requested, selected, createdLink, child.Identity, observed });
            Assert.Equal(ProcessControlOutcome.LiveOwned, observed.Outcome);
            Assert.Equal(requested, observed.RequestedLaunch);
            Assert.Equal(child.Identity.PlatformStartIdentity, observed.PlatformStartIdentity);
            Assert.Equal(child.Identity.ExecutablePath, observed.ObservedImagePath);
            Assert.Equal(OperatingSystem.IsWindows() ? "windows-owned-handle-image" : "linux-owned-image", observed.Stage);
            Assert.NotEmpty(observed.Reason);
            Assert.InRange(observed.Reason.Length, 1, 512);
            Assert.True(SameExecutableFile(selected, observed.ObservedImagePath), "Actual observed image must name the selected physical executable, independently of lexical launch aliases.");
        }
        finally
        {
            if (child is not null)
            {
                var before = control.Observe(child.Identity);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var result = before.Outcome == ProcessControlOutcome.LiveOwned
                    ? await control.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(1), cleanup.Token) : before;
                await WriteNew(root, "image-cleanup.json", new { fixture = true, child.Identity, before, result, after = control.Observe(child.Identity), retained = true });
            }
            await control.DisposeAsync();
        }
        // Retain diagnostic bytes for both RED and later GREEN; no timestamp/absence cleanup authority.
    }

    [Fact(Timeout = 120_000)]
    public async Task Foreign_same_PID_different_start_cannot_query_or_adopt_this_controller_image()
    {
        await using var control = new ProcessControl();
        var child = control.StartOwned(new(SelectedExecutable(), Arguments()));
        try
        {
            var foreign = child.Identity with { PlatformStartIdentity = checked(child.Identity.PlatformStartIdentity + 1) };
            var refused = control.ObserveOwnedImage(foreign);
            Assert.Equal(ProcessControlOutcome.IdentityUnknown, refused.Outcome);
            Assert.Empty(refused.ObservedImagePath);
            Assert.Equal("registry-refusal", refused.Stage);
            Assert.NotEmpty(refused.Reason);
            Assert.Equal(ProcessControlOutcome.LiveOwned, control.Observe(child.Identity).Outcome);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (control.Observe(child.Identity).Outcome == ProcessControlOutcome.LiveOwned)
                await control.QuiesceAsync(child.Identity, TimeSpan.FromSeconds(1), cleanup.Token);
        }
    }

    private static string SelectedExecutable() => OperatingSystem.IsWindows()
        ? Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"))
        : "/bin/sleep";
    private static string[] Arguments() => OperatingSystem.IsWindows() ? ["127.0.0.1", "-n", "30"] : ["30"];
    private static async Task WriteNew(string root, string name, object value)
    {
        await using var stream = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, cancellationToken: CancellationToken.None);
    }

    // Independent FILE metadata oracle: no MainModule, process query or production helper.
    private static bool SameExecutableFile(string expected, string observed)
    {
        if (OperatingSystem.IsWindows())
        {
            using var left = File.OpenHandle(expected, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var right = File.OpenHandle(observed, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileId(left, 18, out var leftId, 24) || !GetFileId(right, 18, out var rightId, 24))
                throw new InvalidOperationException("INCOMPLETE independent Windows file-ID oracle; native error=" + Marshal.GetLastPInvokeError());
            if ((leftId.Low | leftId.High) == 0 || (rightId.Low | rightId.High) == 0)
                throw new InvalidOperationException("INCOMPLETE independent Windows file-ID oracle fields.");
            return leftId.Volume == rightId.Volume && leftId.Low == rightId.Low && leftId.High == rightId.High;
        }
        if (OperatingSystem.IsLinux())
        {
            if (Statx(-100, expected, 0, 0x101, out var left) != 0 || Statx(-100, observed, 0, 0x101, out var right) != 0)
                throw new InvalidOperationException("INCOMPLETE independent Linux file-ID oracle; errno=" + Marshal.GetLastPInvokeError());
            if ((left.Mask & 0x101) != 0x101 || (right.Mask & 0x101) != 0x101 || left.Inode == 0 || right.Inode == 0)
                throw new InvalidOperationException("INCOMPLETE independent file-ID oracle fields.");
            return left.DeviceMajor == right.DeviceMajor && left.DeviceMinor == right.DeviceMinor && left.Inode == right.Inode;
        }
        throw new PlatformNotSupportedException("INCOMPLETE independent image-file oracle.");
    }
    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct FileId { public ulong Volume; public ulong Low; public ulong High; }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileId
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileId(SafeFileHandle file, int informationClass, out FileId identity, uint size);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxFileId identity);
}
