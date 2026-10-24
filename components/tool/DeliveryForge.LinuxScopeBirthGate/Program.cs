using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
namespace DeliveryForge.LinuxScopeBirthGate;

// Fixed creation barrier, not a model/CLI dispatcher or an ownership issuer.
// No Task/async/thread-pool exec. The original kernel process leader execs the
// exact selected target only AFTER its real parent releases this same birth.
public static class Program
{
    const int MaximumArguments = 64; static readonly UTF8Encoding Utf8 = new(false, true);
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return 125;
        var allocations = new List<IntPtr>(); ExactNativeImage? exactImage = null;
        try
        {
            if (args.Length is < 12 or > 79 || args[0] != "--original" || args[2] != "--frequency" || args[4] != "--work-ticks" || args[6] != "--parent" || args[8] != "--ticket" || args[10] is not ("--" or "--exact-native-elf" or "--exact-native-elf-nnp-user1000-ancestor-v3")) throw new IOException("Exact private birth-gate grammar required.");
            var original = long.Parse(args[1], NumberStyles.None, CultureInfo.InvariantCulture); var frequency = long.Parse(args[3], NumberStyles.None, CultureInfo.InvariantCulture); var workTicks = long.Parse(args[5], NumberStyles.None, CultureInfo.InvariantCulture); var parent = int.Parse(args[7], NumberStyles.None, CultureInfo.InvariantCulture); var ticket = args[9];
            if (original <= 0 || frequency != Stopwatch.Frequency || workTicks <= 0 || workTicks > TimeSpan.FromSeconds(1100).Ticks || parent <= 0 || ticket.Length != 32 || ticket.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9'))) throw new IOException("Actual original birth identity/budget differs.");
            void Check() { var elapsed = Stopwatch.GetElapsedTime(original); if (elapsed < TimeSpan.Zero || elapsed >= TimeSpan.FromTicks(workTicks)) throw new TimeoutException("SAME original birth work endpoint expired."); }
            Check(); if (getpid() != Environment.ProcessId || gettid() != getpid() || getppid() != parent) throw new IOException("Actual original parent/leader differs.");
            // Actual parent death cannot promote a stranded gate to success.
            if (prctl(1, 9, 0, 0, 0) != 0 || getppid() != parent) throw new IOException("Actual original parent-death guard unavailable."); Check();
            var unprivileged = args[10] == "--exact-native-elf-nnp-user1000-ancestor-v3";
            var exact = args[10] == "--exact-native-elf" || unprivileged;
            if (exact && (args.Length < 15 || args[13] != "--")) throw new IOException("Exact native ELF birth grammar required.");
            var targetIndex = exact ? 14 : 11;
            var target = args[targetIndex]; var arguments = args.Skip(targetIndex).ToArray(); if (arguments.Length > MaximumArguments || !Path.IsPathFullyQualified(target) || arguments.Any(x => x.Length > 8192 || x.Contains('\0'))) throw new IOException("Finite exact target argv required.");
            for (var fd = 0; fd < 3; fd++) { var flags = fcntl(fd, 1, 0); if (flags < 0 || fcntl(fd, 2, flags & ~1) != 0) throw new IOException("Original stdio CLOEXEC observation/set failed."); Check(); }
            if (exact)
            {
                try { exactImage = ExactNativeImage.Create(target, long.Parse(args[11], NumberStyles.None, CultureInfo.InvariantCulture), args[12], Check); }
                catch (ExactNativeImage.Refusal refused)
                {
                    // A known preparation refusal stays alive only long enough
                    // for this original parent to bind/retire its actual group.
                    // No READY, RELEASE interpretation, retry or target entry.
                    Console.Error.WriteLine("DF_SCOPE_EXACT_IMAGE_REFUSAL|" + refused.Code);
                    var nonready = Encoding.ASCII.GetBytes("DF_SCOPE_NONREADY|EXACT_IMAGE_REFUSED\n");
                    Check(); if (write(1, nonready, (nuint)nonready.Length) != nonready.Length) throw new IOException("Complete fixed refusal delivery failed."); Check();
                    while (true)
                    {
                        Check(); if (getppid() != parent) throw new IOException("Original parent left during refused preparation.");
                        var left = TimeSpan.FromTicks(workTicks) - Stopwatch.GetElapsedTime(original);
                        var milliseconds = (int)Math.Min(1000, Math.Floor(left.TotalMilliseconds));
                        if (milliseconds <= 0) throw new TimeoutException("SAME original refused-preparation endpoint expired.");
                        var row = new PollFd { Fd = 0, Events = 1 }; var polled = poll(ref row, 1, milliseconds); Check();
                        if (polled < 0 || (row.Returned & ~(1 | 16)) != 0) throw new IOException("Original refused-preparation poll failed.");
                        if (polled == 0) continue;
                        var one = new byte[1]; var count = read(0, one, 1); Check();
                        if (count == 0) return 125;
                        throw new IOException("Refused preparation cannot consume a release or execute a target.");
                    }
                }
            }
            // Restriction applies to THIS main-thread leader only, then survives its
            // exec and all descendants. Installer/parent/global guest policy is untouched.
            Check(); if (unprivileged && (prctl(38, 1, 0, 0, 0) != 0 || prctl(39, 0, 0, 0, 0) != 1)) throw new IOException("Selected child no-new-privileges unavailable."); Check();
            var ready = Encoding.ASCII.GetBytes("DF_SCOPE_READY|" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "|" + original.ToString(CultureInfo.InvariantCulture) + "|" + ticket + "|" + frequency.ToString(CultureInfo.InvariantCulture) + (exactImage is null ? "" : "|" + exactImage.Ready) + "\n");
            Check(); if (write(1, ready, (nuint)ready.Length) != ready.Length) throw new IOException("Complete actual birth READY delivery failed; no replay."); Check();
            var expected = Encoding.ASCII.GetBytes("DF_SCOPE_RELEASE|" + ticket + "|" + original.ToString(CultureInfo.InvariantCulture) + "\n"); var actual = new byte[expected.Length]; var offset = 0;
            while (offset < actual.Length) { Check(); var left = TimeSpan.FromTicks(workTicks) - Stopwatch.GetElapsedTime(original); var milliseconds = (int)Math.Min(1000, Math.Floor(left.TotalMilliseconds)); if (milliseconds <= 0) throw new TimeoutException("Original birth release endpoint expired."); var row = new PollFd { Fd = 0, Events = 1 }; var polled = poll(ref row, 1, milliseconds); Check(); if (polled < 0) throw new IOException("Actual original release poll failed."); if (polled == 0) continue; if ((row.Returned & ~(1 | 16)) != 0) throw new IOException("Actual release descriptor error."); var chunk = new byte[actual.Length - offset]; var count = (int)read(0, chunk, (nuint)chunk.Length); Check(); if (count <= 0) throw new IOException("Actual original release missing/truncated; target never entered."); Buffer.BlockCopy(chunk, 0, actual, offset, count); offset += count; }
            if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException("Actual private release differs; no target execution."); Check();
            var total = 0L; foreach (var value in arguments) { total += Utf8.GetByteCount(value) + 1; if (total > 2097152) throw new IOException("Target argv UTF8 allocation credit exceeded."); }
            var vector = Marshal.AllocCoTaskMem(checked((arguments.Length + 1) * IntPtr.Size)); allocations.Add(vector);
            for (var index = 0; index < arguments.Length; index++) { Check(); var pointer = Marshal.StringToCoTaskMemUTF8(arguments[index]); allocations.Add(pointer); Marshal.WriteIntPtr(vector, index * IntPtr.Size, pointer); }
            Marshal.WriteIntPtr(vector, arguments.Length * IntPtr.Size, IntPtr.Zero);
            Check(); if (getppid() != parent || gettid() != getpid()) throw new IOException("Original parent/leader left before synchronous exec.");
            Check(); if (unprivileged && prctl(39, 0, 0, 0, 0) != 1) throw new IOException("Selected child no-new-privileges changed before exec."); Check();
            // execv preserves the actual environment and stdio; success never
            // returns. No fork, shell interpolation or Process.Start exists here.
            if (exactImage is not null) { exactImage.Execute(vector); throw new IOException("Exact native ELF exec unexpectedly returned."); }
            var returned = execv(target, vector); throw new IOException("Original synchronous target exec returned failure: " + returned.ToString(CultureInfo.InvariantCulture) + " errno=" + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("DF_SCOPE_BIRTH_NONPASS|" + error.GetType().Name);
            if (ExactNativeImage.RefusalCode(error) is { } code) Console.Error.WriteLine("DF_SCOPE_EXACT_IMAGE_REFUSAL|" + code);
            return 125;
        }
        finally { try { exactImage?.Dispose(); } catch (Exception failure) { Console.Error.WriteLine("DF_SCOPE_BIRTH_DISPOSITION_NONPASS|" + failure.GetType().Name); } foreach (var pointer in allocations) Marshal.FreeCoTaskMem(pointer); }
    }
    [StructLayout(LayoutKind.Sequential)] struct PollFd { public int Fd; public short Events; public short Returned; }
    [DllImport("libc", SetLastError = true)] static extern nint read(int fd, [Out] byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] static extern nint write(int fd, byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] static extern int poll(ref PollFd descriptor, nuint count, int milliseconds);
    [DllImport("libc", SetLastError = true)] static extern int getpid();
    [DllImport("libc", SetLastError = true)] static extern int gettid();
    [DllImport("libc", SetLastError = true)] static extern int getppid();
    [DllImport("libc", SetLastError = true)] static extern int prctl(int option, ulong a, ulong b, ulong c, ulong d);
    [DllImport("libc", SetLastError = true)] static extern int fcntl(int fd, int command, int value);
    [DllImport("libc", SetLastError = true)] static extern int execv([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr argv);
}
