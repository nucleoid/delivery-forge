using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
// Test-only actual process. No model/provider/auth/SDK/production capability.
if (args.Length is >= 12 and <= 79 && args[0] == "--original")
{
    // Negative birth fixture only: never release or execute the supplied target.
    var exact = args[10] == "--exact-native-elf";
    if ((!exact && args.Length > 76) || (exact && (args.Length < 15 || args[13] != "--"))) return 64;
    if (!OperatingSystem.IsLinux() || args[2] != "--frequency" || args[4] != "--work-ticks" || args[6] != "--parent" || args[8] != "--ticket" || (!exact && args[10] != "--") ||
        !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var original) || original <= 0 || original > Stopwatch.GetTimestamp() ||
        !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var frequency) || frequency != Stopwatch.Frequency ||
        !long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var workTicks) || workTicks <= 0 || workTicks > TimeSpan.FromSeconds(1100).Ticks ||
        !int.TryParse(args[7], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) || parent <= 0 ||
        args[9].Length != 32 || args[9].Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c))) return 64;
    var workLimit = TimeSpan.FromTicks(workTicks);
    if (Stopwatch.GetElapsedTime(original) >= workLimit) return 125;
    void CheckOriginal() { if (Stopwatch.GetElapsedTime(original) < TimeSpan.Zero || Stopwatch.GetElapsedTime(original) >= workLimit) throw new TimeoutException("Original negative fixture WORK expired."); }
    using var image = exact ? FixtureUnsealedImage.Create(args[14], args[11], args[12], CheckOriginal) : null;
    CheckOriginal(); Console.Out.NewLine = "\n";
    Console.WriteLine(image is null ? "DF_SCOPE_READY|partial" : "DF_SCOPE_READY|" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "|" + original.ToString(CultureInfo.InvariantCulture) + "|" + args[9] + "|" + frequency.ToString(CultureInfo.InvariantCulture) + "|" + image.Ready);
    Console.Out.Flush(); CheckOriginal();
    // Stay live for the actual parent's private group binding and refusal;
    // its original WORK endpoint also bounds a lost-parent fixture lifetime.
    while (Stopwatch.GetElapsedTime(original) < workLimit) await Task.Delay(5);
    return 125;
}
if (args.Length != 3) return 64;
var mode = args[0]; var directory = Path.GetFullPath(args[1]); var nonce = args[2];
if (!Directory.Exists(directory) || nonce.Length != 32 || nonce.Any(x => !char.IsAsciiHexDigit(x))) return 65;
if (mode == "orphan-child")
{
    using (var file = new FileStream(Path.Combine(directory, nonce + "-child-entered"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.WriteByte(1);
    Console.Out.Close(); Console.Error.Close(); Console.In.Close(); await Task.Delay(4000);
    using (var file = new FileStream(Path.Combine(directory, nonce + "-child-returned"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.WriteByte(1);
    return 0;
}
if (mode == "orphan-child-release")
{
    using (var file = new FileStream(Path.Combine(directory, nonce + "-child-entered"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.WriteByte(1);
    Console.Out.Close(); Console.Error.Close(); Console.In.Close();
    // This control's child cannot spend its four-second future effect while
    // the test is still establishing actual root exit/owned membership.
    var release = Path.Combine(directory, nonce + "-child-release");
    var waiting = Stopwatch.GetTimestamp();
    while (!File.Exists(release)) { if (Stopwatch.GetElapsedTime(waiting) > TimeSpan.FromSeconds(30)) return 66; await Task.Delay(5); }
    if (await File.ReadAllTextAsync(release) != nonce) return 70;
    var futureMarker = Task.Delay(4000);
    var armed = Path.Combine(directory, nonce + "-child-armed");
    using (var file = new FileStream(armed + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
    {
        file.Write(System.Text.Encoding.UTF8.GetBytes(nonce)); file.Flush(true);
    }
    File.Move(armed + ".tmp", armed); // Only publish the complete closed acknowledgement.
    await futureMarker;
    using (var file = new FileStream(Path.Combine(directory, nonce + "-child-returned"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.WriteByte(1);
    return 0;
}
if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return 68;
if (mode is "parent-contract" or "parent-contract-hold") Console.Out.NewLine = "\n";
using (var actual = Process.GetCurrentProcess())
{
    Console.WriteLine("READY|" + actual.ProcessorAffinity.ToInt64() + "|" + Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT") + "|" + Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit")); Console.Out.Flush();
}
if (mode == "fast-exit") return 0;
if (mode is "parent-contract" or "parent-contract-hold")
{
    // Explicit fixture task, bounded stdin protocol; no model/provider/auth.
    var bytes = new List<byte>(); var one = new byte[1]; var input = Console.OpenStandardInput();
    while (true) { if (await input.ReadAsync(one) != 1) return 69; if (one[0] == 10) break; if (bytes.Count >= 8192) return 70; bytes.Add(one[0]); }
    using var doc = System.Text.Json.JsonDocument.Parse(bytes.ToArray()); var body = doc.RootElement;
    var path = body.GetProperty("Path").GetString()!; var git = body.GetProperty("Git").GetString()!;
    if (body.GetProperty("Nonce").GetString() != nonce || !Path.IsPathFullyQualified(git) || path.Length is < 1 or > 1024 || path.Contains('\\') || path.StartsWith('/') || path.Split('/').Any(piece => piece is "" or "." or "..") || path.Split('/').Any(piece => piece.Equals(".git", StringComparison.OrdinalIgnoreCase))) return 71;
    var target = Path.GetFullPath(Path.Combine(directory, path)); if (!target.StartsWith(directory + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return 72;
    var parent = Path.GetDirectoryName(target)!; for (var cursor = parent; cursor.Length >= directory.Length; cursor = Path.GetDirectoryName(cursor)!) { if (File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint)) return 73; if (cursor == directory) break; }
    if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint)) return 74;
    await File.WriteAllTextAsync(target, "genuine native parent contract fixture " + nonce + "\n");
    if (mode == "parent-contract-hold") { Console.WriteLine("STARTED|" + nonce); Console.Out.Flush(); await Task.Delay(Timeout.Infinite); return 76; }
    Console.WriteLine("WRITTEN|" + nonce); Console.Out.Flush(); return 0;
}
if (mode == "echo") { var data = new byte[16]; await Console.OpenStandardInput().ReadExactlyAsync(data); await Console.OpenStandardOutput().WriteAsync(data); await Console.Error.WriteAsync("actual-stderr"); return 0; }
if (mode == "input-block") { await Task.Delay(20000); return 0; }
if (mode == "output-block") { var data = new byte[2 * 1024 * 1024]; Array.Fill(data, (byte)'x'); await Console.OpenStandardOutput().WriteAsync(data); return 0; }
if (mode == "stderr-overflow") { var data = new byte[1048577]; Array.Fill(data, (byte)'e'); await Console.OpenStandardError().WriteAsync(data); Console.WriteLine("{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"actual-fixture-thread\",\"turn\":{\"id\":\"actual-fixture-turn\",\"status\":\"completed\"}}}"); return 0; }
if (mode == "orphan-parent")
{
    var image = Environment.ProcessPath ?? throw new IOException("Actual fixture apphost required.");
    var info = new ProcessStartInfo(image) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "orphan-child", directory, nonce }) info.ArgumentList.Add(arg);
    using var child = Process.Start(info) ?? throw new IOException("Actual fixture child did not start.");
    var bound = Stopwatch.GetTimestamp(); while (!File.Exists(Path.Combine(directory, nonce + "-child-entered"))) { if (Stopwatch.GetElapsedTime(bound) > TimeSpan.FromSeconds(3)) return 66; await Task.Delay(5); }
    return 0; // Ordinary descendant intentionally outlives this root, with closed pipes.
}
if (mode == "orphan-parent-release")
{
    var image = Environment.ProcessPath ?? throw new IOException("Actual fixture apphost required.");
    var info = new ProcessStartInfo(image) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "orphan-child-release", directory, nonce }) info.ArgumentList.Add(arg);
    using var child = Process.Start(info) ?? throw new IOException("Actual fixture child did not start.");
    var bound = Stopwatch.GetTimestamp(); while (!File.Exists(Path.Combine(directory, nonce + "-child-entered"))) { if (Stopwatch.GetElapsedTime(bound) > TimeSpan.FromSeconds(3)) return 66; await Task.Delay(5); }
    return 0; // The independent test releases this live descendant after observing root exit.
}
return 67;

// Test-only actual descriptor; deliberately NEVER F_ADD_SEALS or exec a target.
// Parent must refuse this real unsealed fd rather than merely reject its profile.
internal sealed class FixtureUnsealedImage : IDisposable
{
    readonly Action check; int source = -1, image = -1; bool closeEntered;
    internal string Ready { get; private set; } = "";
    FixtureUnsealedImage(Action originalCheck) { check = originalCheck; }
    static void Require(bool ok) { if (!ok) throw new IOException("Actual unsealed negative fixture setup refused."); }
    internal static FixtureUnsealedImage Create(string path, string size, string hash, Action originalCheck)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Path.IsPathFullyQualified(path) ||
            !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < 64 or > 1048576 ||
            hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c))) throw new IOException("Closed small native negative fixture selection required.");
        var owner = new FixtureUnsealedImage(originalCheck);
        try
        {
            originalCheck(); owner.source = open(path, 0x20000 | 0x80000); Require(owner.source > 2); originalCheck();
            Statx before = default; originalCheck(); Require(statx(owner.source, "", 0x1000, 0x7ff, out before) == 0); originalCheck();
            Require((before.Mask & 0x303) == 0x303 && (before.Mode & 0xf000) == 0x8000 && (before.Mode & 0x49) != 0 && before.Size == (ulong)length);
            originalCheck(); owner.image = memfd_create("delivery-forge-negative-unsealed", 1 | 2 | 0x10); Require(owner.image > 2); originalCheck();
            originalCheck(); Require(fchmod(owner.image, 0x140) == 0); originalCheck();
            var buffer = new byte[65536]; var header = new byte[64]; var headerUsed = 0; long copied = 0;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (copied < length)
            {
                originalCheck(); var count = (long)read(owner.source, buffer, (nuint)Math.Min(buffer.Length, length - copied)); originalCheck();
                Require(count > 0 && count <= Math.Min(buffer.Length, length - copied));
                if (headerUsed < header.Length) { var take = Math.Min(header.Length - headerUsed, (int)count); Buffer.BlockCopy(buffer, 0, header, headerUsed, take); headerUsed += take; }
                digest.AppendData(buffer, 0, (int)count);
                var written = 0;
                while (written < count)
                {
                    var segment = written == 0 ? buffer : buffer.AsSpan(written, (int)count - written).ToArray();
                    originalCheck(); var n = (long)write(owner.image, segment, (nuint)((int)count - written)); originalCheck();
                    Require(n > 0 && n <= count - written); written += (int)n;
                }
                copied += count;
            }
            originalCheck(); Require(read(owner.source, buffer, 1) == 0); originalCheck();
            Require(headerUsed == 64 && header[0] == 0x7f && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F' && header[4] == 2 && header[5] == 1 && Convert.ToHexStringLower(digest.GetHashAndReset()) == hash);
            owner.CloseSource();
            // Actual correct byte/extent/mode/inode identity, but exactly zero seals.
            originalCheck(); Require(fcntl(owner.image, 1034, 0) == 0); originalCheck();
            Statx identity = default; originalCheck(); Require(statx(owner.image, "", 0x1000, 0x7ff, out identity) == 0); originalCheck();
            Require((identity.Mask & 0x303) == 0x303 && (identity.Mode & 0xf000) == 0x8000 && (identity.Mode & 0x1ff) == 0x140 && identity.Size == (ulong)length && identity.Inode != 0);
            owner.Ready = string.Join('|', "ELF", owner.image.ToString(CultureInfo.InvariantCulture), size, hash, identity.Inode.ToString(CultureInfo.InvariantCulture), identity.Major.ToString(CultureInfo.InvariantCulture), identity.Minor.ToString(CultureInfo.InvariantCulture));
            return owner;
        }
        catch (Exception primary)
        {
            try { owner.Dispose(); }
            catch (Exception post) { throw new AggregateException("Unsealed fixture creation and original first-close.", primary, post); }
            throw;
        }
    }
    void CloseSource()
    {
        if (source < 0) return; check(); var fd = source; source = -1; Require(close(fd) == 0); check();
    }
    public void Dispose()
    {
        CloseSource(); if (image < 0 || closeEntered) return; check(); closeEntered = true; Require(close(image) == 0); image = -1; check();
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
}
