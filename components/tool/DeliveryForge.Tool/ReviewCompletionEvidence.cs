using System.Text.Json;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using FileFact = DeliveryForge.Execution.Host.ProductionReviewSourceOwner.VerifiedSourceFileFact;
using SnapshotFacts = DeliveryForge.Execution.Host.ProductionReviewSourceOwner.VerifiedSourceSnapshotFacts;

namespace DeliveryForge.Tool;

// Bounded presentation of observations supplied only by the containing owner.
// This pure projection issues no authority and does not replace its owner checks.
internal static class ReviewCompletionEvidence
{
    internal static string[] ChangedPathArguments(string before, string after) =>
        ["diff", "--name-only", "-z", "--no-renames", "--no-ext-diff", "--no-textconv", "--diff-filter=ACMT", before, after, "--"];

    internal static IReadOnlyList<string> ParseChangedPaths(string output)
    {
        if (output.Length == 0) return Array.Empty<string>(); // A deletion-only patch has no current file bytes.
        if (output[^1] != '\0') throw new WorkerEnvelopeException("Changed-path output is not a complete NUL-delimited observation.");
        var paths = output[..^1].Split('\0'); var seen = new HashSet<string>(StringComparer.Ordinal);
        if (paths.Length > 4096) throw new WorkerEnvelopeException("Changed-path observation exceeds the existing source entry bound.");
        foreach (var path in paths)
            if (path.Length is < 1 or > 4096 || path.StartsWith('/') || path.Contains((char)92) ||
                path.Split('/').Any(part => part is "" or "." or "..") || !seen.Add(path))
                throw new WorkerEnvelopeException("Changed-path observation contains an empty, duplicate or noncanonical path.");
        return Array.AsReadOnly(paths.Order(StringComparer.Ordinal).ToArray());
    }
    internal static IReadOnlyList<FileFact> SelectChangedFiles(SnapshotFacts snapshot, IReadOnlyList<string> paths)
    {
        if (paths.Count > 4096 || paths.Distinct(StringComparer.Ordinal).Count() != paths.Count)
            throw new WorkerEnvelopeException("Changed-path selection is not a complete unique bounded observation.");
        var current = snapshot.Files.ToDictionary(row => row.RelativePath, StringComparer.Ordinal);
        var selected = new List<FileFact>(); var remaining = 4096L;
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            if (!current.TryGetValue(path, out var fact)) throw new WorkerEnvelopeException("An observed non-deleted changed path has no exact verified source fact.");
            if (fact.Hex is not null)
            {
                if (fact.Bytes > remaining) fact = fact with { Hex = null, HexOmittedReason = "PresentationHexBudgetExhausted" };
                else remaining -= fact.Bytes;
            }
            selected.Add(fact);
        }
        return Array.AsReadOnly(selected.ToArray());
    }
    internal static JsonElement Create(AgentCompletionReceipt completion, string baseCommit, SnapshotFacts snapshot,
        IReadOnlyList<string> changedPaths, string statusOutput, string ancestryOutput,
        string policyRelativePath, long policySourceBytes, string policySourceSha256, string policyContractIdentity)
    {
        if (completion.HeadCommit != snapshot.HeadCommit || completion.TreeId != snapshot.TreeId || statusOutput.Length != 0)
            throw new WorkerEnvelopeException("Review presentation differs from the verified completed source observations.");
        var files = SelectChangedFiles(snapshot, changedPaths);
        return JsonSerializer.SerializeToElement(new
        {
            Qualification = "Same containing owner observations, not an installed quality gate result or a new authority.",
            completion.RunId,
            completion.RequestIdentity,
            completion.RuntimeRunIdentity,
            completion.RuntimeTaskIdentity,
            BaseCommit = baseCommit,
            completion.HeadCommit,
            completion.TreeId,
            FinalWorkerStatus = new { CommandSummary = new[] { "git", "status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching" }, StandardOutput = statusOutput, Phase = "CompletedWorkerBeforeQuality", ObservationView = "Original worker metadata with the containing owner's fixed Git environment and flags." },
            CommitAncestry = new { CommandSummary = new[] { "git", "rev-list", "--parents", baseCommit + ".." + completion.HeadCommit }, StandardOutput = ancestryOutput, ObservationView = "Original worker metadata with replace/graft sources disabled; not the controlled object view.", ControlledViewQualification = "The caller completed same-source GitPatchIdentity, which separately requires base ancestry in its controlled view. Parent rows are actual bounded output; no single-commit result is assumed." },
            SourceBytes = new { snapshot.HeadCommit, snapshot.TreeId, snapshot.SourceFingerprint, Files = files, Selection = "Only exact current paths observed by bounded base-to-head diff; deleted files are represented by the complete patch only.", ObservationView = "Changed paths come from original worker metadata with replace/graft sources disabled; byte facts are joined exactly from the controlled archive/physical census.", ChangedPathCommandSummary = new[] { "git" }.Concat(ChangedPathArguments(baseCommit, completion.HeadCommit)).ToArray(), PerFileHexMaximumBytes = 128, PresentationHexMaximumBytes = 4096, Qualification = "Byte facts come from the completed full archive-to-physical-source census. Mode/blob identity come from the controlled tree; on Windows mode is not independently observed. The fingerprint describes that census, not a standalone acceptance capability. Hex omissions are explicit; encoding and line-ending facts remain available without hex." },
            EvidencePolicyDerivation = new { Source = "git:" + policyRelativePath, BaseCommit = baseCommit, RawSourceBytes = policySourceBytes, RawSourceSha256 = policySourceSha256, ContractIdentity = policyContractIdentity, IdentityAlgorithm = "SHA256 of RFC8785 canonical JSON with the top-level identity property removed; the raw source digest instead includes exact stored formatting and identity." }
        }, BoundedFiles.Json);
    }
}