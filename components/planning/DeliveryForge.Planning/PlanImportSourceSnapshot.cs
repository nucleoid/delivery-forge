using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Planning;

internal sealed record PlanImportSourceSnapshot(string MetadataHash, string IndexHash, string ConfigurationHash, string WorktreeHash, string Flags,
    string Head, string Status)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static async Task<PlanImportSourceSnapshot> Capture(string root, GitRepositoryContextReader reader, PlanImportLifetime life)
    {
        var actualMetadata = await life.Observe(() => PlanImportGitMetadata.Read(root, life));
        var config = actualMetadata.Files["config"]; var index = actualMetadata.Files["index"];
        RichPlanImportCodec.Require(actualMetadata.Digest == reader.ImportMetadataDigest, "Actual Git metadata changed from the frozen original import view.");
        var flags = Utf8.GetString(await life.Run(() => reader.ImportGitBytes(root, life.Token, "ls-files", "-v", "-z")));
        var entries = flags.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        RichPlanImportCodec.Require(entries.Length <= 100_000 && entries.All(entry => entry.Length > 2 && entry[0] == 'H'),
            "Unsafe index assume-unchanged/skip-worktree/unmerged flags require fresh reconciliation.");
        var staged = Utf8.GetString(await life.Run(() => reader.ImportGitBytes(root, life.Token, "ls-files", "--stage", "-z")));
        using var worktree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var tracked = staged.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        RichPlanImportCodec.Require(tracked.Length <= 10_000, "Current tracked source exceeds the10000-file import profile.");
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var entry in tracked)
        {
            life.Check(); var tab = entry.IndexOf('\t');
            RichPlanImportCodec.Require(tab > 0, "Malformed current index listing.");
            var header = entry[..tab].Split(' '); var relative = entry[(tab + 1)..];
            RichPlanImportCodec.Require(header.Length == 3 && header[0] is "100644" or "100755" && header[2] == "0" &&
                header[1].Length is 40 or 64 && header[1].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                !relative.Contains('\\') && !relative.Contains('\0') && !PortableMaterial.IsAbsolutePath(relative) &&
                !relative.Split('/').Any(part => part is "" or "." or "..") && paths.Add(relative), "Unsupported/aliased current index source entry.");
            var actual = await life.Observe(() => PlanImportFilesystem.Read(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), 8 * 1024 * 1024, life));
            totalBytes += actual.Length;
            RichPlanImportCodec.Require(totalBytes <= 64 * 1024 * 1024, "Current source exceeds the64MiB import profile.");
            using var blob = IncrementalHash.CreateHash(header[1].Length == 40 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
            blob.AppendData(Encoding.ASCII.GetBytes("blob " + actual.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"));
            blob.AppendData(actual);
            RichPlanImportCodec.Require(Convert.ToHexStringLower(blob.GetHashAndReset()) == header[1], "Actual source bytes differ from current index object identity.");
            worktree.AppendData(Utf8.GetBytes(relative + "\0")); worktree.AppendData(SHA256.HashData(actual));
        }
        var head = Utf8.GetString(await life.Run(() => reader.ImportGitBytes(root, life.Token, "rev-parse", "--verify", "HEAD^{commit}"))).Trim();
        var status = Utf8.GetString(await life.Run(() => reader.ImportGitBytes(root, life.Token, "status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching")));
        life.Check();
        return new(actualMetadata.Digest, Convert.ToHexStringLower(SHA256.HashData(index)), Convert.ToHexStringLower(SHA256.HashData(config)),
            Convert.ToHexStringLower(worktree.GetHashAndReset()), flags, head, status);
    }
    internal static void ValidateConfiguration(string text)
    {
        if (text.Contains('\0') || text.Contains('\\')) throw new PlanningException("Escaped/continued import Git configuration is unsupported.");
        var section = "";
        foreach (var source in text.Split('\n'))
        {
            var line = source.Trim(); if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                if (line.Equals("[core]", StringComparison.OrdinalIgnoreCase)) section = "core";
                else if (line.Equals("[user]", StringComparison.OrdinalIgnoreCase)) section = "user";
                else if (line.StartsWith("[remote \"", StringComparison.OrdinalIgnoreCase) && line.EndsWith("\"]", StringComparison.Ordinal)) section = "remote";
                else if (line.StartsWith("[branch \"", StringComparison.OrdinalIgnoreCase) && line.EndsWith("\"]", StringComparison.Ordinal)) section = "branch";
                else throw new PlanningException("Executable/include/transform Git configuration is unsupported for import.");
                continue;
            }
            var separator = line.IndexOf('='); if (separator <= 0) throw new PlanningException("Unsupported import Git configuration syntax.");
            var key = line[..separator].Trim().ToLowerInvariant(); var value = line[(separator + 1)..].Trim();
            var safe = section switch
            {
                "core" => key is "repositoryformatversion" or "filemode" or "bare" or "logallrefupdates" or "ignorecase" or "symlinks" or "autocrlf" or "safecrlf" or "precomposeunicode",
                "user" => key is "name" or "email",
                "remote" => key is "url" or "fetch" or "pushurl",
                "branch" => key is "remote" or "merge",
                _ => false
            };
            if (!safe || key == "repositoryformatversion" && value != "0" || key == "bare" && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
                throw new PlanningException("Unsafe import Git configuration key: " + key);
        }
    }
}
