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
    public static InstallationReceipt Install(string archive, string prefix, FilePin trustedHost, ToolLifetime life)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Install profile is Linux/Windows only; macOS is unclaimed.");
        life.Check(); prefix = BoundedFiles.Plain(prefix, true); archive = BoundedFiles.Plain(archive);
        if (Directory.Exists(prefix) || File.Exists(prefix)) throw new IOException("Install prefix must be new; existing ownership is never adopted.");
        var hostBytes = BoundedFiles.Read(trustedHost.Path, life, 16 * 1024 * 1024);
        if (hostBytes.LongLength != trustedHost.Bytes || BoundedFiles.Sha(hostBytes) != trustedHost.Sha256) throw new IOException("Explicit runtime host pin differs.");
        var package = BoundedFiles.Read(archive, life, MaximumArchiveBytes); var packageSha = BoundedFiles.Sha(package);
        var nonce = Guid.NewGuid().ToString("N"); var owned = new List<InstalledFile>(); var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(prefix); life.Check(); BoundedFiles.Plain(prefix, true);
        // First CreateNew claim must succeed before extracting any payload. Failure preserves prefix.
        var claim = JsonSerializer.SerializeToUtf8Bytes(new { Kind = "DeliveryForgeDisposablePrefix-v1", Nonce = nonce });
        BoundedFiles.WriteNew(Path.Combine(prefix, ClaimName), claim, life); owned.Add(new(ClaimName, claim.Length, BoundedFiles.Sha(claim)));
        using (var memory = new MemoryStream(package, false)) using (var zip = new ZipArchive(memory, ZipArchiveMode.Read, false))
        {
            life.Check(); if (zip.Entries.Count is 0 or > MaximumEntries) throw new IOException("Package entry count exceeds finite profile.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; var payloads = 0;
            foreach (var entry in zip.Entries)
            {
                life.Check(); var name = Relative(entry.FullName); if (!seen.Add(name)) throw new IOException("Package duplicate/case-alias path.");
                var kind = (entry.ExternalAttributes >> 16) & 0xf000; if (kind is not (0 or 0x8000 or 0x4000)) throw new IOException("Package special/reparse entry refused.");
                if (entry.Length < 0 || entry.Length > 16 * 1024 * 1024 || total > MaximumBytes - entry.Length) throw new IOException("Package expanded bytes exceed finite profile."); total += entry.Length;
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                // Inspect COMPLETE archive text/binary bytes; no public private-path/credential waiver.
                byte[] bytes; using (var input = entry.Open()) { bytes = new byte[(int)entry.Length]; life.Check(); input.ReadExactly(bytes); life.Check(); if (input.ReadByte() != -1) throw new IOException("Package entry has extra bytes."); life.Check(); }
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) PublicContentScanner.RequireJsonSafe(bytes); else PublicContentScanner.RequireSafe(bytes);
                const string runtime = "tools/net10.0/any/";
                if (!name.StartsWith(runtime, StringComparison.Ordinal)) continue;
                var relative = Relative("payload/" + name[runtime.Length..]); var target = Inside(prefix, relative);
                Parents(prefix, relative, directories, life); BoundedFiles.WriteNew(target, bytes, life);
                int? mode = null;
                if (OperatingSystem.IsLinux() && relative == "payload/runtime/linux-x64/gate/DeliveryForge.LinuxScopeBirthGate")
                {
                    life.Check(); File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); life.Check(); mode = (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    RequireOwnedMode(target, new(relative, bytes.Length, BoundedFiles.Sha(bytes), mode), life);
                }
                owned.Add(new(relative, bytes.Length, BoundedFiles.Sha(bytes), mode)); payloads++;
            }
            if (payloads == 0 || !owned.Any(x => x.Path == "payload/DeliveryForge.Tool.dll") || !owned.Any(x => x.Path == "payload/DeliveryForge.Tool.runtimeconfig.json") || !owned.Any(x => x.Path == "payload/content/SKILL.md")) throw new IOException("Complete tool/runtime/skill payload missing.");
        }
        var entryPath = Inside(prefix, "payload/DeliveryForge.Tool.dll"); var shimName = OperatingSystem.IsWindows() ? "delivery-forge.cmd" : "delivery-forge";
        var shim = OperatingSystem.IsWindows() ? "@echo off\r\nsetlocal\r\nset DOTNET_PROCESSOR_COUNT=2\r\nset DOTNET_GCHeapHardLimit=0x100000000\r\nset DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1\r\nset MSBUILDDISABLENODEREUSE=1\r\n\"" + Cmd(trustedHost.Path) + "\" \"" + Cmd(entryPath) + "\" %*\r\nexit /b %errorlevel%\r\n" : "#!/bin/sh\nDOTNET_PROCESSOR_COUNT=2 DOTNET_GCHeapHardLimit=0x100000000 DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1 MSBUILDDISABLENODEREUSE=1 exec " + Shell(trustedHost.Path) + " " + Shell(entryPath) + " \"$@\"\n";
        var shimBytes = Encoding.UTF8.GetBytes(shim); BoundedFiles.WriteNew(Path.Combine(prefix, shimName), shimBytes, life);
        if (!OperatingSystem.IsWindows()) { life.Check(); File.SetUnixFileMode(Path.Combine(prefix, shimName), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); life.Check(); }
        owned.Add(new(shimName, shimBytes.Length, BoundedFiles.Sha(shimBytes), OperatingSystem.IsLinux() ? (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute) : null));
        var payloadDigest = BoundedFiles.Sha(JsonSerializer.SerializeToUtf8Bytes(owned.Where(x => x.Path.StartsWith("payload/", StringComparison.Ordinal)).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray()));
        var manifest = new PackageManifest("DeliveryForgeDisposableInstall-v1", packageSha, nonce, Path.GetFullPath(trustedHost.Path), trustedHost.Sha256, owned.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray(), directories.OrderBy(x => x, StringComparer.Ordinal).ToArray(), payloadDigest);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, BoundedFiles.Json); BoundedFiles.WriteNew(Path.Combine(prefix, ManifestName), manifestBytes, life);
        var receipt = new InstallationReceipt(prefix, nonce, BoundedFiles.Sha(manifestBytes), packageSha, payloadDigest); Verify(receipt, life); return receipt;
    }
    public static PackageManifest Verify(InstallationReceipt receipt, ToolLifetime life)
    {
        var prefix = BoundedFiles.Plain(receipt.Prefix, true); var bytes = BoundedFiles.Read(Path.Combine(prefix, ManifestName), life, 1048576);
        if (BoundedFiles.Sha(bytes) != receipt.ManifestSha256) throw new IOException("Install manifest differs from independently retained pin.");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(bytes, BoundedFiles.Json) ?? throw new IOException("Missing manifest.");
        if (manifest.Kind != "DeliveryForgeDisposableInstall-v1" || manifest.Nonce != receipt.Nonce || manifest.PackageSha256 != receipt.PackageSha256 || manifest.PayloadDigest != receipt.PayloadDigest || manifest.Files.Count is 0 or > MaximumEntries || manifest.Directories.Count > MaximumEntries) throw new IOException("Install receipt/manifest identity mismatch.");
        var actualHost = BoundedFiles.Read(manifest.RuntimeHost, life); if (BoundedFiles.Sha(actualHost) != manifest.RuntimeHostSha256) throw new IOException("Original selected runtime host changed.");
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
        var removal = BeginRemoval(receipt, life); ContinueRemoval(removal, life);
    }
    public static InstallationReceipt Reinstall(string archive, InstallationReceipt previous, FilePin host, ToolLifetime life)
    {
        if (BoundedFiles.Sha(BoundedFiles.Read(archive, life, MaximumArchiveBytes)) != previous.PackageSha256) throw new IOException("Reinstall package identity changed.");
        Uninstall(previous, life); var installed = Install(archive, previous.Prefix, host, life); if (installed.PayloadDigest != previous.PayloadDigest) throw new IOException("Reinstalled payload digest differs."); return installed;
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
