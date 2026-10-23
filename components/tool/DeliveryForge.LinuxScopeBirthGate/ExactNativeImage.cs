using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
namespace DeliveryForge.LinuxScopeBirthGate;

// Child-private sealed descriptor. Selection/READY is DATA; the retained parent
// independently checks this same descriptor before authorizing its release.
internal sealed class ExactNativeImage : IDisposable
{
    internal const long MaximumBytes = 512L * 1024 * 1024;
    const int RequiredSeals = 0x2f; // SEAL | SHRINK | GROW | WRITE | EXEC.
    readonly Action check; int source = -1, image = -1; bool closeEntered;
    internal int Descriptor => image;
    internal string Ready { get; private set; } = "";
    ExactNativeImage(Action actualCheck) { check = actualCheck; }
    internal enum RefusalKind { Extent, Hash, Elf, Source, Copy, Memfd, Seal, Disposition, Exec }
    internal sealed class Refusal(RefusalKind kind) : IOException("Selected exact native ELF operation refused.")
    {
        internal string Code => kind switch { RefusalKind.Extent => "extent", RefusalKind.Hash => "hash", RefusalKind.Elf => "elf", RefusalKind.Source => "source", RefusalKind.Copy => "copy", RefusalKind.Memfd => "memfd", RefusalKind.Seal => "seal", RefusalKind.Disposition => "disposition", RefusalKind.Exec => "exec", _ => "other" };
    }
    static void Required(bool value, RefusalKind kind) { if (!value) throw new Refusal(kind); }
    // Preserve the original creation failure when its first-close also failed.
    // Only a closed reason code is emitted; no path, argv or exception text.
    internal static string? RefusalCode(Exception error) => error is Refusal actual ? actual.Code
        : error is AggregateException aggregate && aggregate.InnerExceptions.Count == 2 && aggregate.InnerExceptions[0] is Refusal primary ? primary.Code : null;
    internal static ExactNativeImage Create(string path, long length, string expectedHash, Action originalCheck)
    {
        if (length is < 64 or > MaximumBytes) throw new Refusal(RefusalKind.Extent);
        if (expectedHash.Length != 64 || expectedHash.Any(c => !char.IsAsciiHexDigitLower(c))) throw new Refusal(RefusalKind.Hash);
        var owner = new ExactNativeImage(originalCheck);
        try
        {
            originalCheck(); owner.source = open(path, 0x20000 | 0x80000); Required(owner.source > 2, RefusalKind.Source); originalCheck();
            Statx before = default; originalCheck(); Required(statx(owner.source, "", 0x1000, 0x7ff, out before) == 0, RefusalKind.Source); originalCheck();
            Required((before.Mask & 0x303) == 0x303 && (before.Mode & 0xf000) == 0x8000 && (before.Mode & 0x49) != 0, RefusalKind.Source);
            Required(before.Size == (ulong)length, RefusalKind.Extent);
            originalCheck(); owner.image = memfd_create("delivery-forge-exact-native", 1 | 2 | 0x10); Required(owner.image > 2, RefusalKind.Memfd); originalCheck();
            // These shmem pages and source-cache/loader/runtime/descendant pages
            // all consume the SAME scope MemoryMax=4GiB, MemorySwapMax=0. The
            // profile never promises 4GiB of target heap in addition to the copy.
            // Exact Linux6.8 profile: MFD_EXEC, full write/extent/exec seals.
            // No fallback to noexec policy, scripts, a pathname, or another fd.
            originalCheck(); Required(fchmod(owner.image, 0x140) == 0, RefusalKind.Memfd); originalCheck();
            var buffer = new byte[65536]; long copied = 0; var header = new byte[64]; var headerUsed = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (copied < length)
            {
                originalCheck(); var count = (long)read(owner.source, buffer, (nuint)Math.Min(buffer.Length, length - copied)); originalCheck();
                Required(count > 0 && count <= Math.Min(buffer.Length, length - copied), RefusalKind.Copy);
                if (headerUsed < header.Length) { var take = Math.Min(header.Length - headerUsed, (int)count); Buffer.BlockCopy(buffer, 0, header, headerUsed, take); headerUsed += take; }
                hash.AppendData(buffer, 0, (int)count);
                var written = 0; while (written < count)
                {
                    originalCheck(); var segment = written == 0 ? buffer : buffer.AsSpan(written, (int)count - written).ToArray();
                    var n = (long)write(owner.image, segment, (nuint)((int)count - written)); originalCheck(); Required(n > 0 && n <= count - written, RefusalKind.Copy); written += (int)n;
                }
                copied += count;
            }
            originalCheck(); Required(read(owner.source, buffer, 1) == 0, RefusalKind.Extent); originalCheck();
            Required(headerUsed == 64 && header[0] == 0x7f && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F' && header[4] == 2 && header[5] == 1 && header[6] == 1 && header[7] is 0 or 3, RefusalKind.Elf);
            var kind = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16));
            var machine = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18));
            var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
            var phoff = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
            var ehsize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(52));
            var phsize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(54));
            var phcount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(56));
            Required(kind is 2 or 3 && machine == 62 && version == 1 && ehsize == 64 && phsize == 56 && phcount is > 0 and <= 128 && phoff <= (ulong)length && (ulong)phcount * phsize <= (ulong)length - phoff, RefusalKind.Elf);
            Required(Convert.ToHexStringLower(hash.GetHashAndReset()) == expectedHash, RefusalKind.Hash); originalCheck();
            owner.CloseSource();
            originalCheck(); Required(fcntl(owner.image, 1033, RequiredSeals) == 0, RefusalKind.Seal); originalCheck();
            originalCheck(); var seals = fcntl(owner.image, 1034, 0); originalCheck(); Required(seals >= 0 && (seals & RequiredSeals) == RequiredSeals, RefusalKind.Seal);
            Statx sealedIdentity = default; originalCheck(); Required(statx(owner.image, "", 0x1000, 0x7ff, out sealedIdentity) == 0, RefusalKind.Seal); originalCheck();
            Required((sealedIdentity.Mask & 0x303) == 0x303 && (sealedIdentity.Mode & 0xf000) == 0x8000 && (sealedIdentity.Mode & 0x1ff) == 0x140 && sealedIdentity.Size == (ulong)length, RefusalKind.Seal);
            owner.Ready = string.Join('|', "ELF", owner.image.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture), expectedHash, sealedIdentity.Inode.ToString(CultureInfo.InvariantCulture), sealedIdentity.Major.ToString(CultureInfo.InvariantCulture), sealedIdentity.Minor.ToString(CultureInfo.InvariantCulture));
            return owner;
        }
        catch (Exception primary)
        {
            try { owner.Dispose(); }
            catch (Exception disposition) { throw new AggregateException("Exact native image creation and original first-close.", primary, disposition); }
            throw;
        }
    }
    void CloseSource()
    {
        if (source < 0) return; check(); var fd = source; source = -1;
        // No retry after any close result; remaining process resources stay on
        // this creation owner until its actual root/EOF/group disposition.
        Required(close(fd) == 0, RefusalKind.Disposition); check();
    }
    internal void Execute(IntPtr exactArgv)
    {
        check(); Required(image > 2 && !closeEntered, RefusalKind.Exec);
        var libc = NativeLibrary.Load("libc.so.6");
        try
        {
            check(); var environment = Marshal.ReadIntPtr(NativeLibrary.GetExport(libc, "environ")); check(); Required(environment != IntPtr.Zero, RefusalKind.Exec);
            // SAME sealed fd, native existing environment, empty path, no fork.
            check(); _ = execveat(image, "", exactArgv, environment, 0x1000);
            throw new Refusal(RefusalKind.Exec);
        }
        finally { NativeLibrary.Free(libc); }
    }
    public void Dispose()
    {
        // On success exec never returns and CLOEXEC closes this fd. At failure,
        // a refused close stays non-PASS; exiting this same gate closes handles.
        if (source >= 0) CloseSource();
        if (image < 0 || closeEntered) return;
        check(); closeEntered = true; Required(close(image) == 0, RefusalKind.Disposition); image = -1; check();
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)] struct Statx { [FieldOffset(0)] public uint Mask; [FieldOffset(28)] public ushort Mode; [FieldOffset(32)] public ulong Inode; [FieldOffset(40)] public ulong Size; [FieldOffset(136)] public uint Major; [FieldOffset(140)] public uint Minor; }
    [DllImport("libc", SetLastError = true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);
    [DllImport("libc", SetLastError = true)] static extern int fchmod(int fd, uint mode);
    [DllImport("libc", SetLastError = true)] static extern int fcntl(int fd, int command, int value);
    [DllImport("libc", SetLastError = true)] static extern int statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx value);
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, [Out] byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] static extern nint write(int fd, byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] static extern int execveat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr argv, IntPtr environment, int flags);
}
