using System.Security.Cryptography;
using System.Text;

namespace DeliveryForge.Tool.Claude;

// Owns only this newly created private namespace, not any process/capability.
// No supplied path/marker DTO can construct it. It stays durable for evidence;
// there is no recursive removal, directory adoption or post-failure retry.
internal sealed class ClaudeReviewerWorkingDirectory
{
    readonly ToolLifetime original;
    readonly AdmissionFile marker;
    internal string Path { get; }
    ClaudeReviewerWorkingDirectory(ToolLifetime owner, string path, AdmissionFile actualMarker)
    { original = owner; Path = path; marker = actualMarker; }
    internal static ClaudeReviewerWorkingDirectory Create(ToolLifetime original, string runId, string artifactRoot,
        string parentCheckout, string workerRoot, string resultRoot)
    {
        original.Check();
        foreach (var source in new[] { parentCheckout, workerRoot, resultRoot })
            BoundedFiles.Separate(source, artifactRoot);
        var artifact = BoundedFiles.Plain(artifactRoot, true);
        var parent = System.IO.Path.GetDirectoryName(artifact.TrimEnd(System.IO.Path.DirectorySeparatorChar))
            ?? throw new IOException("Private review artifact parent required.");
        BoundedFiles.Plain(parent, true);
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var path = System.IO.Path.Combine(parent, ".forge-reviewer-" + nonce);
        foreach (var source in new[] { parentCheckout, workerRoot, resultRoot, artifactRoot }) BoundedFiles.Separate(source, path);
        BoundedFiles.Plain(path, true); original.Check();
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Fresh owner reviewer namespace required; no adoption.");
        Directory.CreateDirectory(path); original.Check(); BoundedFiles.Plain(path, true);
        // Fixed checks are bounded. Project/local inputs are also disabled by
        // argv; this does not claim atomicity against a hostile same-user OS.
        foreach (var name in new[] { ".claude", ".git", "CLAUDE.md", "CLAUDE.local.md", ".mcp.json" })
        {
            original.Check(); var input = System.IO.Path.Combine(path, name);
            if (File.Exists(input) || Directory.Exists(input)) throw new IOException("Unexpected input in fresh private reviewer namespace.");
        }
        var bytes = Encoding.UTF8.GetBytes("OwnerReviewerCwd-v1\n" + runId + "\n" + nonce + "\n");
        if (bytes.Length > 1024) throw new IOException("Finite private CWD marker exceeded.");
        var marker = new AdmissionFile(System.IO.Path.Combine(path, ".forge-reviewer-owner"), bytes.LongLength, BoundedFiles.Sha(bytes));
        // Existing bounded CreateNew/write/flush/first-close owner preserves any
        // unclosed actual stream. No directory/marker is deleted on failure.
        BoundedFiles.WriteNew(marker.Path, bytes, original);
        return new(original, path, marker);
    }
    // Called at each of the THREE actual births and once after native closure,
    // never on the protocol pump or each ordinary Current/IO check.
    internal void Check()
    {
        original.Check(); BoundedFiles.Plain(Path, true);
        _ = InstalledHostAdmission.ReadExact(marker, original, 1024);
        original.Check();
    }
}
