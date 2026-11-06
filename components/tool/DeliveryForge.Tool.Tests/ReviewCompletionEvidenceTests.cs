using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using FileFact = DeliveryForge.Execution.Host.ProductionReviewSourceOwner.VerifiedSourceFileFact;
using SnapshotFacts = DeliveryForge.Execution.Host.ProductionReviewSourceOwner.VerifiedSourceSnapshotFacts;

namespace DeliveryForge.Tool.Tests;

// Pure presentation DATA controls. These fixtures issue no owner, completion,
// model result, quality gate pass or independent review authority.
public sealed class ReviewCompletionEvidenceTests
{
    static readonly string Before = new('a', 40), Head = new('b', 40), Tree = new('c', 40);
    static FileFact Fact(string path, int length = 10)
    {
        var bytes = length == 10 ? Encoding.UTF8.GetBytes("candidate\n") : Enumerable.Repeat((byte)97, length).ToArray();
        return new(path, "100644", new string('d', 40), bytes.LongLength, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
            length <= 128 ? Convert.ToHexStringLower(bytes) : null, length <= 128 ? null : "FileExceeds128Bytes",
            true, false, 0, length == 10 ? 1 : 0, length == 10);
    }
    static SnapshotFacts Snapshot(params FileFact[] files) => new(Head, Tree, "sha256:" + new string('e', 64), Array.AsReadOnly(files));
    static AgentCompletionReceipt Completion() => new("1.0.0", "projection-DATA", "sha256:" + new string('f', 64), "codex.cli", "fixture-v1",
        "runtime-thread-DATA", "runtime-turn-DATA", "success", Path.GetFullPath("worker-DATA"), Head, Tree, DateTimeOffset.UnixEpoch, [], []);
    static JsonElement Project(SnapshotFacts snapshot, IReadOnlyList<string> changedPaths, string? ancestry = null) =>
        ReviewCompletionEvidence.Create(Completion(), Before, snapshot, changedPaths, "", ancestry ?? Head + " " + Before + "\n",
            "quality/evidence-policy.json", 358, "sha256:" + new string('1', 64), "sha256:" + new string('2', 64));

    [Fact]
    public void Hundreds_of_unselected_small_files_do_not_spend_hex_or_grow_changed_file_presentation()
    {
        var changed = Fact("z-changed.txt");
        var unselected = Enumerable.Range(0, 500).Select(index => Fact($"a-{index:D3}.txt")).Append(changed).ToArray();
        var large = Project(Snapshot(unselected), new[] { changed.RelativePath });
        var small = Project(Snapshot(changed), new[] { changed.RelativePath });
        var rows = large.GetProperty("SourceBytes").GetProperty("Files");
        Assert.Equal(changed.Hex, Assert.Single(rows.EnumerateArray()).GetProperty("Hex").GetString());
        Assert.Equal(small.GetRawText(), large.GetRawText());
        Assert.DoesNotContain("a-000.txt", large.GetRawText(), StringComparison.Ordinal);
        // Match the production outer JSON-string measurement, including quote
        // escaping, for this evidence-bearing prompt fragment. This does not
        // claim that every complete plan and patch fits the production profile.
        var prompt = "<plan>" + JsonSerializer.Serialize(new { OwnerCompletionEvidence = large }, BoundedFiles.Json) + "</plan>";
        var outerBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(prompt));
        Assert.True(outerBytes > Encoding.UTF8.GetByteCount(prompt));
        Assert.InRange(outerBytes, 1, 60000);
        Assert.False(large.GetProperty("SourceBytes").TryGetProperty("NonDeletedChangedPaths", out _));
        Assert.True(large.GetProperty("SourceBytes").TryGetProperty("ObservationView", out _));
    }
    [Fact]
    public void Global_hex_budget_is_applied_only_to_changed_rows_without_losing_digest_or_encoding_facts()
    {
        var files = Enumerable.Range(0, 33).Select(index => Fact($"changed-{index:D2}.txt", 128)).ToArray();
        var rows = ReviewCompletionEvidence.SelectChangedFiles(Snapshot(files), files.Select(row => row.RelativePath).ToArray());
        Assert.Equal(33, rows.Count); Assert.Equal(4096, rows.Sum(row => (row.Hex?.Length ?? 0) / 2));
        Assert.Equal("PresentationHexBudgetExhausted", rows[^1].HexOmittedReason); Assert.Null(rows[^1].Hex);
        Assert.Equal(files[^1].Sha256, rows[^1].Sha256); Assert.True(rows[^1].ValidUtf8);
    }
    [Fact]
    public void Larger_files_keep_closed_line_ending_facts_when_hex_is_unavailable()
    {
        var observed = Fact("large.txt", 256) with { HasUtf8Bom = true, CrCount = 1, LfCount = 2, EndsWithLf = true };
        var row = Assert.Single(Project(Snapshot(observed), new[] { "large.txt" }).GetProperty("SourceBytes").GetProperty("Files").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("Hex").ValueKind); Assert.True(row.GetProperty("ValidUtf8").GetBoolean());
        Assert.True(row.GetProperty("HasUtf8Bom").GetBoolean()); Assert.Equal(1, row.GetProperty("CrCount").GetInt64());
        Assert.Equal(2, row.GetProperty("LfCount").GetInt64()); Assert.True(row.GetProperty("EndsWithLf").GetBoolean());
    }
    [Fact]
    public void Observed_current_changed_path_without_an_exact_fact_refuses_instead_of_disappearing()
    {
        Assert.Throws<WorkerEnvelopeException>(() => Project(Snapshot(Fact("other.txt")), new[] { "missing.txt" }));
        Assert.Throws<WorkerEnvelopeException>(() => Project(Snapshot(Fact("case.txt")), new[] { "Case.txt" }));
    }
    [Theory]
    [InlineData("file.txt")]
    [InlineData("file.txt\0\0")]
    [InlineData("file.txt\0file.txt\0")]
    [InlineData("../file.txt\0")]
    [InlineData("/file.txt\0")]
    public void Incomplete_duplicate_or_noncanonical_changed_path_output_refuses(string output) =>
        Assert.Throws<WorkerEnvelopeException>(() => ReviewCompletionEvidence.ParseChangedPaths(output));

    [Fact]
    public void Nul_path_observation_is_complete_and_deletion_only_patch_has_no_current_rows()
    {
        Assert.Equal(new[] { "a.txt", "z.txt" }, ReviewCompletionEvidence.ParseChangedPaths("z.txt\0a.txt\0"));
        var deletedOnly = ReviewCompletionEvidence.ParseChangedPaths(""); Assert.Empty(deletedOnly);
        Assert.Empty(Project(Snapshot(Fact("unchanged.txt")), deletedOnly).GetProperty("SourceBytes").GetProperty("Files").EnumerateArray());
    }
    [Fact]
    public void Embedded_controls_are_preserved_by_NUL_parsing_for_the_exact_census_join()
    {
        foreach (var control in new[] { (char)9, (char)127, (char)133 })
        {
            var path = "src/file" + control + ".txt";
            var paths = ReviewCompletionEvidence.ParseChangedPaths(path + (char)0);
            Assert.Equal(path, Assert.Single(paths));
            var row = Assert.Single(Project(Snapshot(Fact(path)), paths).GetProperty("SourceBytes").GetProperty("Files").EnumerateArray());
            Assert.Equal(path, row.GetProperty("RelativePath").GetString());
        }
    }
    [Fact]
    public void Actual_serializer_preserves_status_merge_parents_and_raw_to_canonical_policy_mapping()
    {
        var rawPolicy = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ContractFixtures", "evidence-policy.json"));
        var policy = ContractValidator.ParseAndValidate(rawPolicy); var rawHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(rawPolicy));
        Assert.NotEqual(rawHash, policy.Identity);
        var parents = Head + " " + Before + " " + new string('9', 40) + "\n";
        var data = ReviewCompletionEvidence.Create(Completion(), Before, Snapshot(Fact("subject.txt")), new[] { "subject.txt" }, "", parents,
            "quality/evidence-policy.json", rawPolicy.LongLength, rawHash, policy.Identity);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(data, BoundedFiles.Json); using var roundTrip = JsonDocument.Parse(serialized);
        var output = roundTrip.RootElement;
        Assert.Equal("", output.GetProperty("FinalWorkerStatus").GetProperty("StandardOutput").GetString());
        Assert.True(output.GetProperty("FinalWorkerStatus").TryGetProperty("CommandSummary", out _));
        Assert.Equal(parents, output.GetProperty("CommitAncestry").GetProperty("StandardOutput").GetString());
        Assert.False(output.GetProperty("CommitAncestry").TryGetProperty("BaseIsAncestor", out _));
        var derivation = output.GetProperty("EvidencePolicyDerivation");
        Assert.Equal(rawHash, derivation.GetProperty("RawSourceSha256").GetString()); Assert.Equal(policy.Identity, derivation.GetProperty("ContractIdentity").GetString());
        Assert.Equal(rawPolicy.LongLength, derivation.GetProperty("RawSourceBytes").GetInt64());
    }
}