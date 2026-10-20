using System.Text;
using System.Text.Json;

namespace DeliveryForge.Evidence;

// Actual selected historical checkout DATA, not an owned worker/writer or a gate
// issuer. Ordinary full-checkout profile only. The existing cooperative bounded
// file checks do not claim hostile-filesystem atomic immutability.
internal sealed class HistoricalGitInput
{
    readonly InstalledToolFiles files;
    readonly RepositoryIdentity expected;
    readonly Dictionary<string, string> metadata;
    const int MaximumEntries = 4096;
    const long MaximumMetadataBytes = 64L * 1024 * 1024;
    internal string Root { get; }
    internal string GitDirectory { get; }
    internal string IndexPath => Path.Combine(GitDirectory, "index");
    internal string ObjectsPath => Path.Combine(GitDirectory, "objects");
    internal string Identity { get; }
    internal object Data => new
    {
        Kind = "HistoricalGitInput-DATA-v1", Root, GitDirectory,
        CommonDirectory = GitDirectory, IndexPath, ObjectsPath,
        Expected = expected, MetadataIdentity = Identity,
        ProvisionedWorker = false, WriterAuthority = false, BaselineAuthority = false
    };

    internal HistoricalGitInput(InstalledToolFiles actualFiles, string root, RepositoryIdentity actualExpected)
    {
        files = actualFiles;
        if (!Path.IsPathFullyQualified(root) || !actualExpected.IsClean ||
            !InstalledToolFiles.ObjectId(actualExpected.BaseCommit) || !InstalledToolFiles.ObjectId(actualExpected.HeadCommit) ||
            !InstalledToolFiles.ObjectId(actualExpected.TreeId)) throw new EvidenceWriteException("Exact historical root and expected P/H0/tree required.");
        Root = Path.GetFullPath(root); expected = actualExpected;
        GitDirectory = Path.Combine(Root, ".git");
        metadata = files.Effect(() =>
        {
            RequireLayout();
            var captured = files.Inventory(GitDirectory, MaximumEntries, MaximumMetadataBytes, false);
            foreach (var required in new[] { "config", "HEAD", "index" })
                if (!captured.ContainsKey(required)) throw new EvidenceWriteException("Historical checkout metadata is incomplete.");
            var configuration = files.Read(Path.Combine(GitDirectory, "config"), 1024 * 1024);
            if (captured["config"] != configuration.LongLength + ":" + InstalledToolFiles.Identity(configuration))
                throw new EvidenceWriteException("Historical configuration changed during initial binding.");
            RequireConfiguration(new UTF8Encoding(false, true).GetString(configuration));
            return captured;
        });
        Identity = InstalledToolFiles.Identity(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Root, GitDirectory, IndexPath, ObjectsPath, expected,
            Files = metadata.OrderBy(row => row.Key, StringComparer.Ordinal).ToArray()
        }));
        Check();
    }

    void RequireLayout()
    {
        files.Check(); InstalledToolFiles.PlainPath(Root); InstalledToolFiles.PlainPath(GitDirectory); InstalledToolFiles.PlainPath(ObjectsPath);
        if (!Directory.Exists(Root) || !Directory.Exists(GitDirectory) || File.Exists(GitDirectory) || !Directory.Exists(ObjectsPath))
            throw new EvidenceWriteException("Historical input requires an ordinary full checkout; linked metadata is unsupported.");
        foreach (var relative in new[] { "commondir", "config.worktree", "shallow", "info/grafts", "objects/info/alternates" })
            if (Path.Exists(Path.Combine(GitDirectory, relative.Replace('/', Path.DirectorySeparatorChar))))
                throw new EvidenceWriteException("Historical Git routing, shallow or alternate metadata is unsupported.");
        files.Check();
    }

    internal void Require(string root, RepositoryIdentity actualExpected)
    {
        if (!Path.IsPathFullyQualified(root) || !InstalledToolFiles.PathComparer.Equals(Path.GetFullPath(root), Root) || actualExpected != expected)
            throw new EvidenceWriteException("Historical source binding cannot move to another root or expected revision.");
        Check();
    }
    internal void Check() => files.Effect(() =>
    {
        RequireLayout();
        var current = files.Inventory(GitDirectory, MaximumEntries, MaximumMetadataBytes, false);
        InstalledToolFiles.Same(metadata, current, false, files, InstalledToolFiles.ComparisonPhase.GitCurrent);
        files.Check();
    });

    internal CommandInvocation Configure(CommandInvocation actual)
    {
        Require(actual.WorkingDirectory, expected);
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var closed = new Dictionary<string, string>(actual.Environment ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var pair in new Dictionary<string, string>
        {
            ["GIT_DIR"] = GitDirectory, ["GIT_COMMON_DIR"] = GitDirectory, ["GIT_WORK_TREE"] = Root,
            ["GIT_OBJECT_DIRECTORY"] = ObjectsPath, ["GIT_INDEX_FILE"] = IndexPath,
            ["GIT_CONFIG"] = Path.Combine(GitDirectory, "config"), ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = nullDevice, ["GIT_CONFIG_SYSTEM"] = nullDevice,
            ["GIT_CONFIG_COUNT"] = "0", ["GIT_CONFIG_PARAMETERS"] = "", ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "",
            ["GIT_NAMESPACE"] = "", ["GIT_NO_REPLACE_OBJECTS"] = "1", ["GIT_GRAFT_FILE"] = nullDevice,
            ["GIT_NO_LAZY_FETCH"] = "1", ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_ATTR_NOSYSTEM"] = "1", ["GIT_SHALLOW_FILE"] = nullDevice
        })
        {
            // Emit the exact uppercase Git variable even on case-sensitive
            // hosts; case aliases from caller/profile cannot shadow the role.
            foreach (var alias in closed.Keys.Where(key => string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase)).ToArray()) closed.Remove(alias);
            closed.Add(pair.Key, pair.Value);
        }
        // No effective filter/include/transform key exists in the validated
        // local configuration; global/system/parameter sources are closed.
        return actual with
        {
            Arguments = ["--no-pager", "-c", "core.hooksPath=" + nullDevice, "-c", "core.fsmonitor=false",
                "-c", "core.untrackedCache=false", "-c", "core.attributesFile=" + nullDevice,
                "-c", "diff.external=", "--no-optional-locks", .. actual.Arguments],
            Environment = closed
        };
    }

    // Same deliberately finite canonical-clone configuration profile as plan
    // import. Historical source selection cannot add executable/include routing.
    static void RequireConfiguration(string text)
    {
        if (text.Contains('\0') || text.Contains('\\')) throw new EvidenceWriteException("Escaped historical Git configuration is unsupported.");
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
                else throw new EvidenceWriteException("Historical executable/include/transform Git configuration is unsupported.");
                continue;
            }
            var separator = line.IndexOf('='); if (separator <= 0) throw new EvidenceWriteException("Unsupported historical Git configuration syntax.");
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
                throw new EvidenceWriteException("Unsafe historical Git configuration key: " + key);
        }
    }
}
