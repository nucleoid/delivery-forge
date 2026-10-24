using System.IO.Compression;
using System.Text;
using System.Text.Json;
namespace DeliveryForge.Tool.Packaging;

public sealed record FilePin(string Path, long Bytes, string Sha256);
public sealed record InstalledFile(string Path, long Bytes, string Sha256, int? UnixMode = null);
public sealed record PackageManifest(string Kind, string PackageSha256, string Nonce, string RuntimeHost, string RuntimeHostSha256,
    IReadOnlyList<InstalledFile> Files, IReadOnlyList<string> Directories, string PayloadDigest);
public sealed record InstallationReceipt(string Prefix, string Nonce, string ManifestSha256, string PackageSha256, string PayloadDigest);

// Explicit DISPOSABLE prefix APIs, intentionally not extra delivery execution modes.
// Cooperative ordinary filesystem ownership, not hostile atomic namespace containment.
public static partial class PackageLifecycle
{
    const int MaximumEntries = 2048; const long MaximumBytes = 256L * 1024 * 1024; const int MaximumArchiveBytes = 64 * 1024 * 1024;
    const string ManifestName = ".delivery-forge-install.json", ClaimName = ".delivery-forge-owner.json";
    sealed record PreparedPayload(string RelativePath, byte[] Bytes, int? UnixMode);
    sealed record PreparedPackage(byte[] ArchiveBytes, string PackageSha256, IReadOnlyList<PreparedPayload> Payloads, string PayloadDigest);
    public static InstallationReceipt Install(string archive, string prefix, FilePin trustedHost, ToolLifetime life)
    {
        RequirePlatform(); life.Check(); prefix = NewPrefix(prefix);
        VerifyRuntimeHost(trustedHost, life);
        var prepared = PreparePackage(BoundedFiles.Read(BoundedFiles.Plain(archive), life, MaximumArchiveBytes), life);
        return InstallPrepared(prepared, prefix, trustedHost, life);
    }
    static void RequirePlatform()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Install profile is Linux/Windows only; macOS is unclaimed.");
    }
    static string NewPrefix(string prefix)
    {
        prefix = BoundedFiles.Plain(prefix, true);
        if (Directory.Exists(prefix) || File.Exists(prefix)) throw new IOException("Install prefix must be new; existing ownership is never adopted.");
        return prefix;
    }
    static void VerifyRuntimeHost(FilePin host, ToolLifetime life)
    {
        var bytes = BoundedFiles.Read(host.Path, life, 16 * 1024 * 1024);
        if (bytes.LongLength != host.Bytes || BoundedFiles.Sha(bytes) != host.Sha256) throw new IOException("Explicit runtime host pin differs.");
    }
    static PreparedPackage PreparePackage(byte[] package, ToolLifetime life)
    {
        life.Check(); if (package.Length > MaximumArchiveBytes) throw new IOException("Package archive exceeds finite profile.");
        var payloads = new List<PreparedPayload>(); var payloadFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payloadDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var memory = new MemoryStream(package, false); using var zip = new ZipArchive(memory, ZipArchiveMode.Read, false);
        life.Check(); if (zip.Entries.Count is 0 or > MaximumEntries) throw new IOException("Package entry count exceeds finite profile.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            life.Check(); var name = Relative(entry.FullName); if (!seen.Add(name)) throw new IOException("Package duplicate/case-alias path.");
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            if (kind is not (0 or 0x8000 or 0x4000) || kind == 0x4000 && !directory || kind == 0x8000 && directory)
                throw new IOException("Package special/reparse or inconsistent entry refused.");
            if (entry.Length < 0 || entry.Length > 16 * 1024 * 1024 || total > MaximumBytes - entry.Length) throw new IOException("Package expanded bytes exceed finite profile."); total += entry.Length;
            if (directory) { if (entry.Length != 0) throw new IOException("Package directory has unexpected bytes."); continue; }
            byte[] bytes;
            using (var input = entry.Open())
            {
                bytes = new byte[(int)entry.Length]; life.Check(); input.ReadExactly(bytes); life.Check();
                if (input.ReadByte() != -1) throw new IOException("Package entry has extra bytes."); life.Check();
            }
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) PublicContentScanner.RequireJsonSafe(bytes); else PublicContentScanner.RequireSafe(bytes);
            const string runtime = "tools/net10.0/any/";
            if (!name.StartsWith(runtime, StringComparison.Ordinal)) continue;
            var relative = Relative("payload/" + name[runtime.Length..]);
            if (payloadDirectories.Contains(relative) || !payloadFiles.Add(relative)) throw new IOException("Package payload file/directory collision.");
            var parts = relative.Split('/'); var parent = "";
            for (var i = 0; i < parts.Length - 1; i++)
            {
                life.Check(); parent = parent.Length == 0 ? parts[i] : parent + "/" + parts[i];
                if (payloadFiles.Contains(parent)) throw new IOException("Package payload parent is an original file.");
                payloadDirectories.Add(parent);
            }
            if (payloadFiles.Count + 2 > MaximumEntries || payloadDirectories.Count > MaximumEntries) throw new IOException("Owned install manifest exceeds finite profile before prefix creation.");
            int? mode = OperatingSystem.IsLinux() && relative == "payload/runtime/linux-x64/gate/DeliveryForge.LinuxScopeBirthGate"
                ? (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute) : null;
            payloads.Add(new(relative, bytes, mode));
        }
        if (!payloads.Any(row => row.RelativePath == "payload/DeliveryForge.Tool.dll") || !payloads.Any(row => row.RelativePath == "payload/DeliveryForge.Tool.runtimeconfig.json") || !payloads.Any(row => row.RelativePath == "payload/content/SKILL.md"))
            throw new IOException("Complete tool/runtime/skill payload missing.");
        var digest = BoundedFiles.Sha(JsonSerializer.SerializeToUtf8Bytes(payloads.Select(row => new InstalledFile(row.RelativePath, row.Bytes.Length, BoundedFiles.Sha(row.Bytes), row.UnixMode)).OrderBy(row => row.Path, StringComparer.Ordinal).ToArray()));
        life.Check(); return new(package, BoundedFiles.Sha(package), payloads.AsReadOnly(), digest);
    }
    static InstallationReceipt InstallPrepared(PreparedPackage prepared, string prefix, FilePin trustedHost, ToolLifetime life)
    {
        RequirePlatform(); life.Check(); prefix = NewPrefix(prefix);
        var nonce = Guid.NewGuid().ToString("N"); var owned = new List<InstalledFile>(); var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastAttempted = null; InstallationReceipt? receipt = null;
        try
        {
            Directory.CreateDirectory(prefix); life.Check(); BoundedFiles.Plain(prefix, true);
            // All complete archive bytes/topology/text and required roles were
            // validated before this first prefix/claim effect. No path is reread.
            var claim = JsonSerializer.SerializeToUtf8Bytes(new { Kind = "DeliveryForgeDisposablePrefix-v1", Nonce = nonce });
            lastAttempted = Path.Combine(prefix, ClaimName); BoundedFiles.WriteNew(lastAttempted, claim, life); owned.Add(new(ClaimName, claim.Length, BoundedFiles.Sha(claim)));
            foreach (var payload in prepared.Payloads)
            {
                life.Check(); var target = Inside(prefix, payload.RelativePath); lastAttempted = target;
                Parents(prefix, payload.RelativePath, directories, life); BoundedFiles.WriteNew(target, payload.Bytes, life);
                if (payload.UnixMode is not null)
                {
                    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Owned Unix mode requires Linux.");
                    life.Check(); File.SetUnixFileMode(target, (UnixFileMode)payload.UnixMode.Value); life.Check();
                    RequireOwnedMode(target, new(payload.RelativePath, payload.Bytes.Length, BoundedFiles.Sha(payload.Bytes), payload.UnixMode), life);
                }
                owned.Add(new(payload.RelativePath, payload.Bytes.Length, BoundedFiles.Sha(payload.Bytes), payload.UnixMode));
            }
            var entryPath = Inside(prefix, "payload/DeliveryForge.Tool.dll"); var shimName = OperatingSystem.IsWindows() ? "delivery-forge.cmd" : "delivery-forge";
            var shim = OperatingSystem.IsWindows() ? "@echo off\r\nsetlocal\r\nset DOTNET_PROCESSOR_COUNT=2\r\nset DOTNET_GCHeapHardLimit=0x100000000\r\nset DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1\r\nset MSBUILDDISABLENODEREUSE=1\r\n\"" + Cmd(trustedHost.Path) + "\" \"" + Cmd(entryPath) + "\" %*\r\nexit /b %errorlevel%\r\n" : "#!/bin/sh\nDOTNET_PROCESSOR_COUNT=2 DOTNET_GCHeapHardLimit=0x100000000 DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1 MSBUILDDISABLENODEREUSE=1 exec " + Shell(trustedHost.Path) + " " + Shell(entryPath) + " \"$@\"\n";
            var shimBytes = Encoding.UTF8.GetBytes(shim); lastAttempted = Path.Combine(prefix, shimName); BoundedFiles.WriteNew(lastAttempted, shimBytes, life);
            if (!OperatingSystem.IsWindows()) { life.Check(); File.SetUnixFileMode(Path.Combine(prefix, shimName), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); life.Check(); }
            owned.Add(new(shimName, shimBytes.Length, BoundedFiles.Sha(shimBytes), OperatingSystem.IsLinux() ? (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute) : null));
            var payloadDigest = BoundedFiles.Sha(JsonSerializer.SerializeToUtf8Bytes(owned.Where(x => x.Path.StartsWith("payload/", StringComparison.Ordinal)).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray()));
            if (payloadDigest != prepared.PayloadDigest) throw new IOException("Written payload differs from the exact prevalidated archive bytes.");
            var manifest = new PackageManifest("DeliveryForgeDisposableInstall-v1", prepared.PackageSha256, nonce, Path.GetFullPath(trustedHost.Path), trustedHost.Sha256, owned.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray(), directories.OrderBy(x => x, StringComparer.Ordinal).ToArray(), payloadDigest);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, BoundedFiles.Json); lastAttempted = Path.Combine(prefix, ManifestName); BoundedFiles.WriteNew(lastAttempted, manifestBytes, life);
            receipt = new(prefix, nonce, BoundedFiles.Sha(manifestBytes), prepared.PackageSha256, payloadDigest); Verify(receipt, life); return receipt;
        }
        catch (Exception failure)
        {
            // Ordinary failure DATA only. No rollback, deletion, adopted owner,
            // durable receipt promise or automatic removal authority is created.
            failure.Data["RetainedPartialInstallationPrefix"] = prefix;
            failure.Data["RetainedPartialInstallationNonce"] = nonce;
            failure.Data["RetainedPartialInstallationPackageSha256"] = prepared.PackageSha256;
            failure.Data["KnownReturnedInstallationFiles"] = owned.ToArray();
            failure.Data["LastAttemptedInstallationPath"] = lastAttempted ?? prefix;
            if (receipt is not null) failure.Data["ActualInstallationReceipt"] = receipt;
            throw;
        }
    }
    public static PackageManifest Verify(InstallationReceipt receipt, ToolLifetime life) => VerifyOwnedInstallation(receipt, life, requireRuntimeHost: true);
    static PackageManifest VerifyOwnedInstallation(InstallationReceipt receipt, ToolLifetime life, bool requireRuntimeHost)
    {
        var prefix = BoundedFiles.Plain(receipt.Prefix, true); var bytes = BoundedFiles.Read(Path.Combine(prefix, ManifestName), life, 1048576);
        if (BoundedFiles.Sha(bytes) != receipt.ManifestSha256) throw new IOException("Install manifest differs from independently retained pin.");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(bytes, BoundedFiles.Json) ?? throw new IOException("Missing manifest.");
        if (manifest.Kind != "DeliveryForgeDisposableInstall-v1" || manifest.Nonce != receipt.Nonce || manifest.PackageSha256 != receipt.PackageSha256 || manifest.PayloadDigest != receipt.PayloadDigest || manifest.Files.Count is 0 or > MaximumEntries || manifest.Directories.Count > MaximumEntries) throw new IOException("Install receipt/manifest identity mismatch.");
        if (requireRuntimeHost)
        {
            var actualHost = BoundedFiles.Read(manifest.RuntimeHost, life);
            if (BoundedFiles.Sha(actualHost) != manifest.RuntimeHostSha256) throw new IOException("Original selected runtime host changed.");
        }
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName }; long total = 0;
        foreach (var row in manifest.Files)
        {
            life.Check(); var relative = Relative(row.Path); if (!expected.Add(relative) || row.Bytes < 0 || row.Bytes > 16 * 1024 * 1024 || total > MaximumBytes - row.Bytes) throw new IOException("Owned file shape/collision/bytes invalid."); total += row.Bytes;
            RequireOwnedMode(Inside(prefix, relative), row, life);
            var actual = BoundedFiles.Read(Inside(prefix, relative), life); if (actual.LongLength != row.Bytes || BoundedFiles.Sha(actual) != row.Sha256) throw new IOException("Owned file changed or missing; refusing uninstall.");
        }
        var claim = BoundedFiles.ReadJson<PrefixClaim>(Path.Combine(prefix, ClaimName), life, 4096); if (claim.Kind != "DeliveryForgeDisposablePrefix-v1" || claim.Nonce != receipt.Nonce) throw new IOException("Original prefix claim differs.");
        var expectedDirs = new HashSet<string>(manifest.Directories.Select(Relative), StringComparer.OrdinalIgnoreCase); if (expectedDirs.Count != manifest.Directories.Count) throw new IOException("Duplicate owned directory.");
        var actualFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var actualDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var pending = new Stack<string>(); pending.Push(prefix); var entries = 0;
        while (pending.Count != 0)
        {
            life.Check(); var directory = pending.Pop(); using var iterator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            while (iterator.MoveNext())
            {
                life.Check(); if (++entries > MaximumEntries * 2) throw new IOException("Owned prefix topology exceeds finite profile."); var path = iterator.Current; var attributes = File.GetAttributes(path); if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Owned prefix reparse path refused.");
                var relative = Relative(Path.GetRelativePath(prefix, path).Replace('\\', '/'));
                if ((attributes & FileAttributes.Directory) != 0) { if (!actualDirs.Add(relative)) throw new IOException("Directory alias collision."); pending.Push(path); } else if (!actualFiles.Add(relative)) throw new IOException("File alias collision.");
            }
        }
        if (!actualFiles.SetEquals(expected) || !actualDirs.SetEquals(expectedDirs)) throw new IOException("Unknown/added/missing owned prefix topology; refusing removal.");
        return manifest;
    }
    public static void Uninstall(InstallationReceipt receipt, ToolLifetime life)
    {
        var removal = BeginRemoval(receipt, life); ContinueRemovalRetainingReceipt(removal, life);
    }
    public static InstallationReceipt Reinstall(string archive, InstallationReceipt previous, FilePin host, ToolLifetime life)
    {
        RequirePlatform(); life.Check();
        var prepared = PreparePackage(BoundedFiles.Read(BoundedFiles.Plain(archive), life, MaximumArchiveBytes), life);
        if (prepared.PackageSha256 != previous.PackageSha256 || prepared.PayloadDigest != previous.PayloadDigest) throw new IOException("Reinstall package or prevalidated payload identity changed before removal.");
        VerifyRuntimeHost(host, life);
        Uninstall(previous, life);
        // SAME private complete archive byte[]/derived payloads; no second path
        // lookup can substitute another package after verified removal.
        return InstallPrepared(prepared, previous.Prefix, host, life);
    }
    internal static void ContinueRemovalRetainingReceipt(RemovalReceipt removal, ToolLifetime life)
    {
        try { ContinueRemoval(removal, life); }
        catch (Exception primary) { primary.Data["ActualRemovalReceipt"] = removal; throw; }
    }
    static void RequireOwnedMode(string path, InstalledFile row, ToolLifetime life)
    {
        if (row.UnixMode is null) return;
        if (!OperatingSystem.IsLinux() || row.UnixMode != (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute) || row.Path is not ("delivery-forge" or "payload/runtime/linux-x64/gate/DeliveryForge.LinuxScopeBirthGate")) throw new IOException("Unexpected executable-mode ownership role.");
        life.Check(); var actual = File.GetUnixFileMode(BoundedFiles.Plain(path)); life.Check(); if ((int)actual != row.UnixMode) throw new IOException("Actual owned executable mode drifted; refuse mutation.");
    }
    static string Relative(string path)
    {
        var value = path.TrimEnd('/'); if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Contains('\\') || Path.IsPathFullyQualified(value) || value.Contains(':') || value.Any(char.IsControl)) throw new IOException("Invalid package relative path.");
        var parts = value.Split('/'); if (parts.Length > 24 || parts.Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' '))) throw new IOException("Package path traversal/alias refused."); return value;
    }
    static string Inside(string prefix, string path, bool directory = false)
    {
        var root = Path.GetFullPath(prefix).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; var full = Path.GetFullPath(Path.Combine(root, Relative(path)));
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Package path escapes prefix."); return BoundedFiles.Plain(full, directory);
    }
    static void Parents(string prefix, string relative, HashSet<string> directories, ToolLifetime life)
    {
        var parts = relative.Split('/'); var current = "";
        for (var i = 0; i < parts.Length - 1; i++) { life.Check(); current = current.Length == 0 ? parts[i] : current + "/" + parts[i]; if (directories.Add(current)) { Directory.CreateDirectory(Path.Combine(prefix, current)); BoundedFiles.Plain(Path.Combine(prefix, current), true); } life.Check(); }
    }
    static string Shell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    static string Cmd(string value) { if (value.Contains('"') || value.Any(char.IsControl)) throw new IOException("Invalid Windows shim path."); return value.Replace("%", "%%", StringComparison.Ordinal); }
    sealed record PrefixClaim(string Kind, string Nonce);
}
