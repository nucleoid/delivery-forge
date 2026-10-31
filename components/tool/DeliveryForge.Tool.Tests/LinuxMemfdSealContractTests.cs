using System.Runtime.InteropServices;
namespace DeliveryForge.Tool.Tests;

// Independent adversarial kernel-contract control, not a process/issuer double.
// This proves only the observed profile's seal semantics; the actual gate tests
// must separately bind their selected target descriptor before release.
[Trait("Category", "InstalledLocalRealAcceptance")]
public sealed class LinuxMemfdSealContractTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();
    [Fact(Timeout = 90000, Skip = "Native executable memfd seals require Linux.", SkipUnless = nameof(IsLinux))]
    public void Actual_executable_memfd_rejects_inplace_write_growth_shrink_and_exec_mode_change_after_full_seal()
    {
        var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var descriptor = -1; var closeEntered = false; var closed = false;
        var mapping = new IntPtr(-1); var mappingCloseEntered = false; var mappingClosed = true;
        Exception? primary = null, disposition = null;
        try
        {
            original.CheckWork(); descriptor = memfd_create("forge-seal-contract", 1 | 2 | 0x10); original.CheckWork(); Assert.True(descriptor > 2);
            long Work(Func<long> operation) { original.CheckWork(); var result = operation(); original.CheckWork(); return result; }
            var bytes = new byte[] { 1, 3, 5, 7, 9, 11, 13, 15 };
            Assert.Equal(8L, Work(() => write(descriptor, bytes, 8)));
            // Establish that these same operations are allowed before sealing;
            // an unrelated filesystem/LSM denial cannot satisfy the negatives.
            Assert.Equal(0L, Work(() => ftruncate(descriptor, 16))); Assert.Equal(0L, Work(() => ftruncate(descriptor, 8)));
            Assert.Equal(0L, Work(() => fchmod(descriptor, 0x100))); Assert.Equal(0L, Work(() => fchmod(descriptor, 0x140)));
            original.CheckWork(); mapping = mmap(IntPtr.Zero, 8, 3, 1, descriptor, 0);
            mappingClosed = mapping == new IntPtr(-1); original.CheckWork(); Assert.False(mappingClosed);
            original.CheckWork(); Marshal.WriteByte(mapping, 0, bytes[0]); Assert.Equal(bytes[0], Marshal.ReadByte(mapping)); original.CheckWork();
            mappingCloseEntered = true; mappingClosed = munmap(mapping, 8) == 0;
            Assert.True(mappingClosed); original.CheckWork();
            Assert.Equal(0L, Work(() => fcntl(descriptor, 1033, 0x2f)));
            var seals = Work(() => fcntl(descriptor, 1034, 0)); Assert.True(seals >= 0 && (seals & 0x2f) == 0x2f);
            void Denied(Func<long> operation)
            {
                original.CheckWork(); Marshal.SetLastPInvokeError(0); var result = operation(); var actualError = Marshal.GetLastPInvokeError(); original.CheckWork();
                Assert.Equal(-1L, result); Assert.Equal(1, actualError); // Linux EPERM.
            }
            Assert.Equal(0L, Work(() => lseek(descriptor, 0, 0)));
            Denied(() => write(descriptor, new byte[] { 99 }, 1)); // no growth: WRITE itself.
            Denied(() => ftruncate(descriptor, 16)); Denied(() => ftruncate(descriptor, 4));
            Denied(() => fchmod(descriptor, 0x100)); // remove the original execute bit.
            Denied(() => fcntl(descriptor, 1033, 0x10)); // later valid F_ADD_SEALS must refuse after SEAL_SEAL.
            original.CheckWork(); Marshal.SetLastPInvokeError(0);
            var rejectedMapping = mmap(IntPtr.Zero, 8, 3, 1, descriptor, 0); var mappingError = Marshal.GetLastPInvokeError();
            if (rejectedMapping != new IntPtr(-1)) { mapping = rejectedMapping; mappingCloseEntered = false; mappingClosed = false; }
            original.CheckWork(); Assert.Equal(new IntPtr(-1), rejectedMapping); Assert.Equal(1, mappingError);
            Assert.Equal(seals, Work(() => fcntl(descriptor, 1034, 0)));
            Assert.Equal(8L, Work(() => lseek(descriptor, 0, 2))); Assert.Equal(0L, Work(() => lseek(descriptor, 0, 0)));
            var retained = new byte[8]; Assert.Equal(8L, Work(() => read(descriptor, retained, 8))); Assert.Equal(bytes, retained);
        }
        catch (Exception failure) { primary = failure; }
        if (!mappingClosed && !mappingCloseEntered)
        {
            try
            {
                original.BeginFailureCleanup(); _ = original.CleanupRemaining;
                mappingCloseEntered = true; mappingClosed = munmap(mapping, 8) == 0;
                if (!mappingClosed) throw new IOException("Original writable mapping first-close refused; no retry.");
                _ = original.CleanupRemaining;
            }
            catch (Exception failure) { disposition = failure; }
        }
        if (descriptor >= 0)
        {
            try
            {
                // Proof ended in WORK; even a crossing before first-close uses
                // only this original's existing cleanup/whole allowance.
                original.BeginFailureCleanup(); _ = original.CleanupRemaining;
                closeEntered = true; var result = close(descriptor); closed = result == 0;
                if (!closed) throw new IOException("Original kernel seal-control descriptor close refused; no retry.");
                _ = original.CleanupRemaining;
            }
            catch (Exception failure) { disposition = disposition is null ? failure : new AggregateException(disposition, failure); }
        }
        if ((descriptor < 0 || closed) && mappingClosed) original.Dispose();
        else
        {
            original.RetainUnknown(); var retainedFailure = primary ?? disposition;
            if (retainedFailure is not null) { retainedFailure.Data["OriginalDescriptorCloseEntered"] = closeEntered; retainedFailure.Data["OriginalMappingCloseEntered"] = mappingCloseEntered; }
        }
        if (primary is not null && disposition is not null) throw new AggregateException("Actual seal control and original descriptor disposition.", primary, disposition);
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (disposition is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposition).Throw();
    }
    [DllImport("libc", SetLastError = true)] static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);
    [DllImport("libc", SetLastError = true)] static extern int fcntl(int fd, int command, int value);
    [DllImport("libc", SetLastError = true)] static extern int fchmod(int fd, uint mode);
    [DllImport("libc", SetLastError = true)] static extern int ftruncate(int fd, long extent);
    [DllImport("libc", SetLastError = true)] static extern long lseek(int fd, long offset, int origin);
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, [Out] byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] static extern nint write(int fd, byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] static extern IntPtr mmap(IntPtr address, nuint extent, int protection, int flags, int fd, long offset);
    [DllImport("libc", SetLastError = true)] static extern int munmap(IntPtr address, nuint extent);
}
