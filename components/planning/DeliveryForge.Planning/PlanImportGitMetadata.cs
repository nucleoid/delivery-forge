using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Planning;

// Actual source metadata DATA captured without running Git/config/filters.
internal sealed record PlanImportGitMetadata(string Root, string Metadata, string Common,
    IReadOnlyDictionary<string, byte[]> Files, string Digest)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static PlanImportGitMetadata Read(string root, PlanImportLifetime life)
    {
        life.Check(); root = Path.GetFullPath(root); PlanImportFilesystem.Plain(root);
        var metadata = Path.Combine(root, ".git");
        byte[]? gitMarker = null; byte[]? commonMarker = null;
        if (!Directory.Exists(metadata))
        {
            gitMarker = PlanImportFilesystem.Read(metadata, 4096, life);
            var marker = Utf8.GetString(gitMarker).Trim();
            RichPlanImportCodec.Require(marker.StartsWith("gitdir: ", StringComparison.Ordinal), "Unsupported worktree metadata marker.");
            metadata = Path.GetFullPath(marker[8..], root);
        }
        PlanImportFilesystem.Plain(metadata);
        var common = metadata;
        if (Path.Exists(Path.Combine(metadata, "commondir")))
        {
            commonMarker = PlanImportFilesystem.Read(Path.Combine(metadata, "commondir"), 4096, life);
            common = Path.GetFullPath(Utf8.GetString(commonMarker).Trim(), metadata);
        }
        PlanImportFilesystem.Plain(common);
        foreach (var forbidden in new[] { Path.Combine(metadata, "config.worktree"), Path.Combine(common, "info", "grafts"), Path.Combine(common, "objects", "info", "alternates") })
        { life.Check(); if (Path.Exists(forbidden)) throw new PlanningException("Import Git routing/external object authority is unsupported."); }
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void ReadFile(string key, string path, int maximum, bool required = false)
        {
            life.Check();
            if (!Path.Exists(path)) { if (required) throw new PlanningException("Required actual Git metadata missing: " + key); return; }
            files[key] = PlanImportFilesystem.Read(path, maximum, life);
        }
        ReadFile("config", Path.Combine(common, "config"), 1024 * 1024, true);
        PlanImportSourceSnapshot.ValidateConfiguration(Utf8.GetString(files["config"]));
        ReadFile("index", Path.Combine(metadata, "index"), 16 * 1024 * 1024, true);
        ReadFile("HEAD", Path.Combine(metadata, "HEAD"), 4096, true);
        ReadFile("packed-refs", Path.Combine(common, "packed-refs"), 8 * 1024 * 1024);
        ReadFile("shallow", Path.Combine(common, "shallow"), 1024 * 1024);
        ReadFile("info/attributes", Path.Combine(common, "info", "attributes"), 1024 * 1024);
        ReadFile("info/exclude", Path.Combine(common, "info", "exclude"), 1024 * 1024);
        void ReadRefs(string source, bool worktree)
        {
            if (!Directory.Exists(source)) return;
            var pending = new Stack<string>(); pending.Push(source); var directories = 0; var references = 0;
            var discovered = 1; var entries = 0;
            life.Resources.Observe(discovered, pending.Count, entries);
            while (pending.TryPop(out var directory))
            {
                life.Check(); PlanImportFilesystem.Plain(directory);
                RichPlanImportCodec.Require(++directories <= 512, "Actual Git reference directory bound exceeded.");
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    life.Check(); PlanImportFilesystem.Plain(path);
                    try { entries = checked(entries + 1); }
                    catch (OverflowException) { throw new PlanningException("Reference observation counter overflow."); }
                    life.Resources.Observe(discovered, pending.Count, entries);
                    if (Directory.Exists(path))
                    {
                        try { discovered = checked(discovered + 1); }
                        catch (OverflowException) { throw new PlanningException("Reference discovery observation overflow."); }
                        // RECORD actual frontier before push; intentionally no quota repair
                        // until Root has compiled this resource-contract RED.
                        int queued;
                        try { queued = checked(pending.Count + 1); }
                        catch (OverflowException) { throw new PlanningException("Reference frontier observation overflow."); }
                        life.Resources.Observe(discovered, queued, entries);
                        pending.Push(path); continue;
                    }
                    RichPlanImportCodec.Require(++references <= 1024, "Actual Git reference file bound exceeded.");
                    var relative = Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/');
                    RichPlanImportCodec.Require(!relative.Split('/').Any(part => part is "" or "." or "..") && !relative.EndsWith(".lock", StringComparison.Ordinal), "Unsupported/in-progress Git reference.");
                    if (worktree && !relative.StartsWith("bisect/", StringComparison.Ordinal) && !relative.StartsWith("worktree/", StringComparison.Ordinal) && !relative.StartsWith("rewritten/", StringComparison.Ordinal))
                        throw new PlanningException("Unknown linked-worktree reference routing.");
                    ReadFile("refs/" + relative, path, 4096, true);
                }
            }
        }
        ReadRefs(Path.Combine(common, "refs"), false);
        if (metadata != common) ReadRefs(Path.Combine(metadata, "refs"), true);
        RichPlanImportCodec.Require(files.Sum(pair => (long)pair.Value.Length) <= 32 * 1024 * 1024, "Actual Git metadata aggregate bound exceeded.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Utf8.GetBytes(root + "\0" + metadata + "\0" + common + "\0"));
        foreach (var marker in new[] { gitMarker, commonMarker })
        { hash.AppendData(new byte[] { marker is null ? (byte)0 : (byte)1 }); if (marker is not null) hash.AppendData(SHA256.HashData(marker)); }
        foreach (var file in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        { hash.AppendData(Utf8.GetBytes(file.Key + "\0")); hash.AppendData(SHA256.HashData(file.Value)); }
        life.Check(); return new(root, metadata, common, files, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
