using System.Collections.ObjectModel;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence;

// Locators selected in the immutable, external parent configuration. None is an
// approval/supported flag, report, result, executor or worker-writable identity.
public sealed record RegistryApprovalPin(string Path, long Bytes, string Sha256);
public sealed record ProtectedBaselineRegistrySelection(string RegistryCommit, string ProtectedTip,
    string ProtectedReference, string RegistryRelativePath, string RegistryIdentity,
    string ArtifactRoot, RegistryApprovalPin OwnerApproval)
{
    // Pure locator/shape validation. This does not issue or approve anything.
    public void Validate() => ProtectedBaselineRegistry.ValidateSelection(this);
}

// Only this closed actual-Git/whole-artifact/out-of-band-approval issuer can use
// the internal ReviewedBaseline authority constructor. Public construction stays advisory.
internal sealed class ProtectedBaselineRegistry
{
    internal ReviewedBaseline Baseline { get; }
    internal string RegistryCommit { get; }
    internal string HistoricalPolicyBaseRevision { get; }
    internal string HistoricalPolicyBaseTree { get; }
    internal string ArtifactManifestIdentity { get; }
    internal CallableProducerIdentity CallableProducer { get; }
    readonly ProtectedBaselineRegistrySelection selection;
    readonly byte[] approvalBytes;
    ProtectedBaselineRegistry(ReviewedBaseline baseline, ProtectedBaselineRegistrySelection selected,
        string artifactManifestIdentity, byte[] approval, string historicalPolicyBaseRevision, string historicalPolicyBaseTree,
        CallableProducerIdentity callableProducer)
    {
        Baseline = baseline; selection = selected; RegistryCommit = selected.RegistryCommit;
        ArtifactManifestIdentity = artifactManifestIdentity; approvalBytes = approval;
        HistoricalPolicyBaseRevision = historicalPolicyBaseRevision; HistoricalPolicyBaseTree = historicalPolicyBaseTree;
        CallableProducer = callableProducer;
    }
    internal void RecheckApproval(InstalledToolFiles files) => files.Effect(() =>
    {
        var current = ApprovalBytes(selection.OwnerApproval, files);
        if (!current.AsSpan().SequenceEqual(approvalBytes)) throw Refuse("Owner approval changed before consumption.");
        using var document = ProducerJson.Read(current); ApprovalTime(document.RootElement);
    });
    internal static void ValidateSelection(ProtectedBaselineRegistrySelection value)
    {
        if (!InstalledToolFiles.ObjectId(value.RegistryCommit) || !InstalledToolFiles.ObjectId(value.ProtectedTip) ||
            value.ProtectedReference is not ("refs/heads/main" or "refs/remotes/origin/main") ||
            !InstalledToolFiles.Hash(value.RegistryIdentity)) throw Refuse("Exact immutable registry commit/protected main tip/hash required.");
        Relative(value.RegistryRelativePath); InstalledToolFiles.PlainPath(value.ArtifactRoot);
        if (value.OwnerApproval is null || value.OwnerApproval.Bytes is < 1 or > 65536 ||
            value.OwnerApproval.Sha256.Length != 64 || value.OwnerApproval.Sha256.Any(c => !char.IsAsciiHexDigitLower(c))) throw Refuse("Independent finite exact owner approval pin required.");
        InstalledToolFiles.PlainPath(value.OwnerApproval.Path);
    }
    internal static async Task<ProtectedBaselineRegistry> ResolveAsync(InstalledToolGateProvider.ActualExecutor owner,
        InstalledToolFiles files, string repository, ResolvedEvidencePolicy policy,
        ProtectedBaselineRegistrySelection selected, CancellationToken cancellationToken)
    {
        ValidateSelection(selected);
        if (!policy.AuthorityVerified || policy.AuthorityKind != PolicyAuthorityKind.ProtectedGitBase ||
            policy.MaximumChangedCodeDebtIncrease != 0m || !InstalledToolFiles.PathComparer.Equals(Path.GetFullPath(repository), policy.SourceRepositoryRoot))
            throw Refuse("Same original protected policy/root with zero approved debt increase required.");
        // Approval is an independently pinned external human statement. A worker
        // registry field or Approved=true is neither read nor sufficient.
        var approval = files.Effect(() => ApprovalBytes(selected.OwnerApproval, files));
        var resolved = await Git(["rev-parse", "--verify", selected.RegistryCommit + "^{commit}"]);
        if (Text(resolved) != selected.RegistryCommit) throw Refuse("Exact registry commit unresolved.");
        var tip = await Git(["rev-parse", "--verify", selected.ProtectedReference + "^{commit}"]);
        if (Text(tip) != selected.ProtectedTip) throw Refuse("Protected main tip differs from independently selected immutable tip.");
        await Git(["merge-base", "--is-ancestor", selected.RegistryCommit, selected.ProtectedTip]);
        var registry = await Blob(selected.RegistryRelativePath);
        var declaredRoles = files.Effect(() =>
        {
            using var doc = ProducerJson.Read(registry); var root = doc.RootElement;
            Fields(root, "schema", "historicalPolicyBaseRevision", "historicalPolicyBaseTree", "historicalPolicyRelativePath", "baselineSourceRevision", "baselineSourceTree", "policyIdentity", "policyContentIdentity", "artifactManifestPath", "artifactManifestIdentity", "exemptions");
            if (root.GetProperty("schema").GetString() != "forge-protected-baseline-registry-v2" || Identity(registry) != selected.RegistryIdentity ||
                root.GetProperty("policyIdentity").GetString() != policy.PolicyIdentity || root.GetProperty("policyContentIdentity").GetString() != policy.ContentIdentity ||
                root.GetProperty("exemptions").ValueKind != JsonValueKind.Array || root.GetProperty("exemptions").GetArrayLength() != 0)
                throw Refuse("Exact policy-bound registry and initially empty exemption registry required; no exception fallback.");
            return (Source: root.GetProperty("baselineSourceRevision").GetString()!, Tree: root.GetProperty("baselineSourceTree").GetString()!,
                HistoricalBase: root.GetProperty("historicalPolicyBaseRevision").GetString()!, HistoricalTree: root.GetProperty("historicalPolicyBaseTree").GetString()!,
                PolicyPath: root.GetProperty("historicalPolicyRelativePath").GetString()!, ManifestPath: root.GetProperty("artifactManifestPath").GetString()!, ManifestIdentity: root.GetProperty("artifactManifestIdentity").GetString()!);
        });
        var source = declaredRoles.Source; var tree = declaredRoles.Tree; var historicalBase = declaredRoles.HistoricalBase;
        var historicalTree = declaredRoles.HistoricalTree; var historicalPath = declaredRoles.PolicyPath;
        var manifestPath = declaredRoles.ManifestPath; var manifestIdentity = declaredRoles.ManifestIdentity;
        RequireHistoricalRoles(historicalBase, source, policy.SourceRevision);
        if (source == selected.RegistryCommit || historicalBase == selected.RegistryCommit || !InstalledToolFiles.ObjectId(tree) ||
            !InstalledToolFiles.ObjectId(historicalTree) || !InstalledToolFiles.Hash(manifestIdentity)) throw Refuse("Distinct historical P/source H0/registry R with exact trees/artifact identity required.");
        Relative(manifestPath); Relative(historicalPath);
        var sourceTree = await Git(["rev-parse", "--verify", source + "^{tree}"]);
        if (Text(sourceTree) != tree) throw Refuse("Actual baseline source tree differs.");
        var historicalBaseTree = await Git(["rev-parse", "--verify", historicalBase + "^{tree}"]);
        if (Text(historicalBaseTree) != historicalTree) throw Refuse("Actual historical protected policy base tree differs.");
        await Git(["merge-base", "--is-ancestor", historicalBase, source]);
        await Git(["merge-base", "--is-ancestor", source, selected.RegistryCommit]);
        // Resolve the real protected policy bytes at P independently of the current H0 policy.
        // Identical content identity is necessary; Git revisions must not be conflated.
        var historicalPolicy = await new ProtectedGitPolicyResolver(owner, repository, historicalBase)
            .ResolveAsync(historicalPath, cancellationToken).ConfigureAwait(false);
        files.Effect(() =>
        {
            if (!historicalPolicy.AuthorityVerified || historicalPolicy.AuthorityKind != PolicyAuthorityKind.ProtectedGitBase ||
                historicalPolicy.SourceRevision != historicalBase || historicalPolicy.PolicyIdentity != policy.PolicyIdentity ||
                historicalPolicy.ContentIdentity != policy.ContentIdentity || historicalPolicy.MaximumChangedCodeDebtIncrease != policy.MaximumChangedCodeDebtIncrease ||
                !InstalledToolFiles.PathComparer.Equals(historicalPolicy.SourceRepositoryRoot, policy.SourceRepositoryRoot) ||
                !historicalPolicy.RequiredGates.Order(StringComparer.Ordinal).SequenceEqual(policy.RequiredGates.Order(StringComparer.Ordinal)))
                throw Refuse("Historical P and current H0 must independently resolve the exact approved same policy content.");
            return true;
        });
        var artifactManifest = await Blob(manifestPath);
        return files.Effect(() =>
        {
            if (Identity(artifactManifest) != manifestIdentity) throw Refuse("Exact immutable complete artifact manifest differs.");
            using var manifestDoc = ProducerJson.Read(artifactManifest); var manifest = manifestDoc.RootElement;
            Fields(manifest, "schema", "sourceRevision", "sourceTree", "files");
            if (manifest.GetProperty("schema").GetString() != "forge-reviewed-quality-artifacts-v1" || manifest.GetProperty("sourceRevision").GetString() != source || manifest.GetProperty("sourceTree").GetString() != tree)
                throw Refuse("Complete approved artifact source differs.");
            using var approvalDoc = ProducerJson.Read(approval); var a = approvalDoc.RootElement;
            Fields(a, "schema", "approver", "purpose", "approvedAtUtc", "expiresAtUtc", "registryCommit", "registryPath", "registryIdentity", "historicalPolicyBaseRevision", "historicalPolicyBaseTree", "historicalPolicyRelativePath", "baselineSourceRevision", "baselineSourceTree", "policyIdentity", "policyContentIdentity", "artifactManifestIdentity", "exemptionRegistry");
            ApprovalTime(a);
            if (a.GetProperty("schema").GetString() != "forge-owner-quality-approval-v2" || a.GetProperty("approver").GetString() != "mstat" ||
                a.GetProperty("purpose").GetString() != "historical-baseline-and-empty-exemptions-v2" || a.GetProperty("registryCommit").GetString() != selected.RegistryCommit ||
                a.GetProperty("registryPath").GetString() != selected.RegistryRelativePath || a.GetProperty("registryIdentity").GetString() != selected.RegistryIdentity ||
                a.GetProperty("historicalPolicyBaseRevision").GetString() != historicalBase || a.GetProperty("historicalPolicyBaseTree").GetString() != historicalTree ||
                a.GetProperty("historicalPolicyRelativePath").GetString() != historicalPath ||
                a.GetProperty("baselineSourceRevision").GetString() != source || a.GetProperty("baselineSourceTree").GetString() != tree ||
                a.GetProperty("policyIdentity").GetString() != policy.PolicyIdentity || a.GetProperty("policyContentIdentity").GetString() != policy.ContentIdentity ||
                a.GetProperty("artifactManifestIdentity").GetString() != manifestIdentity || a.GetProperty("exemptionRegistry").GetString() != "empty-v1")
                throw Refuse("Independent owner statement does not approve this exact complete immutable artifact/registry/policy.");
            var expected = new Dictionary<string, string>(InstalledToolFiles.PathComparer);
            var roles = new Dictionary<string, byte[]>(StringComparer.Ordinal); var rolePaths = new Dictionary<string, string>(StringComparer.Ordinal); var declared = manifest.GetProperty("files");
            if (declared.ValueKind != JsonValueKind.Array || declared.GetArrayLength() is < 6 or > 20000) throw Refuse("Complete finite artifact set required.");
            foreach (var row in declared.EnumerateArray())
            {
                files.Check(); Fields(row, "path", "bytes", "sha256", "role");
                var path = row.GetProperty("path").GetString()!; Relative(path); var length = row.GetProperty("bytes").GetInt64(); var sha = row.GetProperty("sha256").GetString()!;
                if (length is < 0 or > 134217728 || sha.Length != 64 || sha.Any(c => !char.IsAsciiHexDigitLower(c)) || !expected.TryAdd(path, length + ":sha256:" + sha)) throw Refuse("Duplicate or malformed exact artifact member.");
                var role = row.GetProperty("role").GetString()!;
                if (role is not ("crap-report" or "crap-binding" or "crap-receipt" or "mutation-report" or "mutation-binding" or "mutation-receipt" or "artifact")) throw Refuse("Closed complete quality artifact roles required.");
                var bytes = files.Read(Path.Combine(selected.ArtifactRoot, path), length);
                if (bytes.LongLength != length || InstalledToolFiles.Identity(bytes) != "sha256:" + sha) throw Refuse("Approved artifact bytes changed.");
                if (role != "artifact") { if (!roles.TryAdd(role, bytes)) throw Refuse("Exactly one primary artifact per tool role required."); rolePaths.Add(role, path); }
            }
            if (roles.Count != 6) throw Refuse("Both real producer reports/bindings/gate receipts required.");
            InstalledToolFiles.Same(expected, files.Inventory(selected.ArtifactRoot, 20000, 1024L * 1024 * 1024, false), false);
            ValidateBinding(roles["crap-binding"], roles["crap-report"], roles["crap-receipt"], "crap4csharp", source, tree, historicalBase, policy);
            ValidateBinding(roles["mutation-binding"], roles["mutation-report"], roles["mutation-receipt"], "mutate4csharp", source, tree, historicalBase, policy);
            foreach (var prefix in new[] { "crap", "mutation" })
            {
                using var gate = ProducerJson.Read(roles[prefix + "-receipt"]);
                var directory = Path.GetDirectoryName(rolePaths[prefix + "-receipt"])!.Replace('\\', '/');
                // A gate receipt is under <gate>/receipts/sha256-*.json.
                if (!directory.EndsWith("/receipts", StringComparison.Ordinal)) throw Refuse("Actual immutable receipt directory shape required.");
                directory = directory[..^9];
                foreach (var artifact in gate.RootElement.GetProperty("artifactHashes").EnumerateObject())
                {
                    Relative(artifact.Name);
                    if (!expected.TryGetValue(directory + "/" + artifact.Name, out var tuple) || !tuple.EndsWith(":" + artifact.Value.GetString(), StringComparison.Ordinal)) throw Refuse("Approved artifact set omits an original gate artifact.");
                }
                using var binding = ProducerJson.Read(roles[prefix + "-binding"]);
                foreach (var member in binding.RootElement.GetProperty("bundleFiles").EnumerateObject())
                {
                    Relative(member.Name);
                    if (!expected.TryGetValue(directory + "/producer-bundle/" + member.Name, out var tuple) || tuple != member.Value.GetString()) throw Refuse("Approved artifact set omits or substitutes original complete bundle membership.");
                }
            }
            var mutation = Mutate4CSharpReportV2.ReadData(roles["mutation-report"]);
            if (mutation.ClaimedOutcome != GateOutcome.Pass || mutation.ClaimedExitCode != 0 || mutation.Selected is < 1 or > 100 || mutation.Enumerated != mutation.Selected || mutation.Executed != mutation.Selected || mutation.Killed != mutation.Selected || mutation.CompileInvalid != 0 || mutation.Survived != 0 || mutation.FreshUncovered != 0 || mutation.Omitted != 0 || mutation.Errors != 0)
                throw Refuse("Complete zero-survivor/nonpartial mutation baseline required, within approved100 cap.");
            var producer = CallableProducerIdentity.FromBinding(roles["crap-binding"]);
            var metrics = CallableMetrics(roles["crap-report"]);
            if (metrics.Count == 0) throw Refuse("Complete comparable callable metrics required.");
            // This internal authority constructor is reachable only through the complete
            // original protected-Git/artifact/out-of-band owner approval checks above.
            var baseline = new ReviewedBaseline(selected.RegistryIdentity, policy.PolicyIdentity, source,
                InstalledToolFiles.Identity(roles["crap-report"]), false, new ReadOnlyDictionary<string, decimal>(metrics), authorityVerified: true);
            var issued = new ProtectedBaselineRegistry(baseline, selected, manifestIdentity, approval, historicalBase, historicalTree, producer);
            issued.RecheckApproval(files); return issued;
        });
        async Task<CommandResult> Git(string[] args)
        {
            var r = await owner.ExecuteAsync(new("git", args, repository), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!owner.Known(r) || r.ExitCode != 0) throw Refuse("Actual bounded protected registry Git read failed.");
            return r;
        }
        string Text(CommandResult r) => r.StandardOutput.Trim();
        async Task<byte[]> Blob(string path)
        {
            Relative(path); var r = await Git(["show", "--no-ext-diff", "--no-textconv", selected.RegistryCommit + ":" + path]);
            var raw = owner.OriginalOutput(r); if (raw.Length is < 1 or > 8388608) throw Refuse("Complete registry metadata blob exceeds supported8MiB profile."); return raw;
        }
    }
    // Extracted historical-role predicate. No receipt/approval authority is issued here.
    internal static void RequireHistoricalRoles(string historicalPolicyBase, string baselineSource, string currentPolicyBase)
    {
        if (!InstalledToolFiles.ObjectId(historicalPolicyBase) || !InstalledToolFiles.ObjectId(baselineSource) ||
            !InstalledToolFiles.ObjectId(currentPolicyBase) || historicalPolicyBase == baselineSource || baselineSource != currentPolicyBase)
            throw Refuse("Exact historical policy/base/source roles required.");
    }
    static byte[] ApprovalBytes(RegistryApprovalPin pin, InstalledToolFiles files)
    {
        var raw = files.Read(pin.Path, 65536);
        if (raw.LongLength != pin.Bytes || InstalledToolFiles.Identity(raw) != "sha256:" + pin.Sha256) throw Refuse("Independent owner approval pin differs.");
        return raw;
    }
    static void ApprovalTime(JsonElement a)
    {
        var now = DateTimeOffset.UtcNow; var from = a.GetProperty("approvedAtUtc").GetDateTimeOffset(); var to = a.GetProperty("expiresAtUtc").GetDateTimeOffset();
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from > now || to <= now || to <= from) throw Refuse("Owner approval is future, expired or not exactUTC.");
    }
    static void ValidateBinding(byte[] binding, byte[] report, byte[] receipt, string tool, string source, string tree, string historicalBase, ResolvedEvidencePolicy policy)
    {
        using var b = ProducerJson.Read(binding); var p = b.RootElement; var r = p.GetProperty("repository");
        if (p.GetProperty("schema").GetString() != "forge-current-installed-tool-v1" || p.GetProperty("toolSourceCommit").GetString() is not { } commit || !InstalledToolFiles.ObjectId(commit) ||
            p.GetProperty("Fixture").GetBoolean() || p.GetProperty("report").GetString() != InstalledToolFiles.Identity(report) || p.GetProperty("reportBytes").GetInt64() != report.LongLength ||
            r.GetProperty("BaseCommit").GetString() != historicalBase || r.GetProperty("HeadCommit").GetString() != source || r.GetProperty("TreeId").GetString() != tree || !r.GetProperty("IsClean").GetBoolean() || p.GetProperty("ExitCode").GetInt32() != 0 || !p.GetProperty("OwnedProcessQuiescent").GetBoolean())
            throw Refuse("Approved artifact is fixture/stale/incomplete or differs from its real producer binding.");
        _ = ContractValidator.ParseAndValidate(receipt); using var gd = ProducerJson.Read(receipt); var g = gd.RootElement;
        if (g.GetProperty("kind").GetString() != "gate-receipt" || g.GetProperty("baseCommit").GetString() != historicalBase || g.GetProperty("headCommit").GetString() != source || g.GetProperty("treeId").GetString() != tree ||
            g.GetProperty("policyIdentity").GetString() != policy.PolicyIdentity || g.GetProperty("outcome").GetString() != "PASS" || g.GetProperty("exitCode").GetInt32() != 0 || g.GetProperty("sourceChanged").GetBoolean())
            throw Refuse("Complete genuine baseline gate PASS/policy/source required.");
        var hashes = g.GetProperty("artifactHashes");
        if (hashes.GetProperty("producer-current-binding.json").GetString() != InstalledToolFiles.Identity(binding) || hashes.GetProperty("producer-report.json").GetString() != InstalledToolFiles.Identity(report)) throw Refuse("Baseline gate receipt does not bind the exact report/current proof.");
        using var command = ProducerJson.Read(System.Text.Encoding.UTF8.GetBytes(g.GetProperty("command").GetString()!));
        if (command.RootElement.GetProperty("capabilityTool").GetString() != tool || command.RootElement.GetProperty("capabilityFixture").GetBoolean() ||
            command.RootElement.GetProperty("policySourceRevision").GetString() != historicalBase || command.RootElement.GetProperty("policyContentIdentity").GetString() != policy.ContentIdentity)
            throw Refuse("Actual baseline producer identity/fixture/protected policy source mismatch.");
    }
    internal static Dictionary<string, decimal> CallableMetrics(byte[] report)
    {
        using var d = ProducerJson.Read(report); var root = d.RootElement; var e = root.GetProperty("evaluation");
        if (Crap4CSharpResultSchema.Validate(root) is not null) throw Refuse("Complete supported baseline callable report schema required.");
        if (root.GetProperty("complexityRulesetVersion").GetString() != "callables-v1" || e.GetProperty("decision").GetProperty("policyDecision").GetString() != "pass" || !e.GetProperty("decision").GetProperty("completed").GetBoolean() ||
            e.GetProperty("coverage").GetProperty("unknown").GetInt32() != 0 || root.GetProperty("run").GetProperty("exitCode").GetInt32() != 0 || root.GetProperty("run").GetProperty("status").GetString() != "completed") throw Refuse("Complete applicable current callable PASS evidence required.");
        var metrics = e.GetProperty("metrics"); if (metrics.GetArrayLength() is < 1 or > 100000 || metrics.GetArrayLength() != e.GetProperty("coverage").GetProperty("methods").GetInt32()) throw Refuse("Full callable inventory required.");
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var m in metrics.EnumerateArray())
        {
            var path = m.GetProperty("path").GetString()!; Relative(path); var context = m.GetProperty("contextId").GetString(); var callable = m.GetProperty("methodIdentity").GetString();
            if (string.IsNullOrWhiteSpace(context) || context.Length > 4096 || string.IsNullOrWhiteSpace(callable) || callable.Length > 32768 || m.GetProperty("crap").ValueKind != JsonValueKind.Number || !m.GetProperty("crap").TryGetDecimal(out var crap) || crap < 0 || m.GetProperty("coverage").ValueKind != JsonValueKind.Number || !m.GetProperty("coverage").TryGetDecimal(out var coverage) || coverage is < 0 or > 1) throw Refuse("Unknown/not-applicable/ambiguous callable metric is not an approved no-regression proof.");
            var key = Identity(JsonSerializer.SerializeToUtf8Bytes(new { context, path, callable }));
            if (!result.TryAdd("crap/" + key, crap) || !result.TryAdd("coverage-debt/" + key, 1m - coverage)) throw Refuse("Duplicate callable metric identity.");
        }
        return result;
    }
    static string Identity(byte[] bytes) => InstalledToolFiles.Identity(CanonicalJson.Canonicalize(bytes));
    static void Relative(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 4096 || s.Contains('\\') || s.Contains(':') || Path.IsPathFullyQualified(s) || s.Split('/').Any(p => p is "" or "." or ".." or ".git")) throw Refuse("Closed plain registry/artifact relative path required.");
    }
    static void Fields(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names) || e.EnumerateObject().Count() != names.Length) throw Refuse("Exact closed registry/approval fields required.");
    }
    static EvidencePolicyException Refuse(string message) => new(message);
}
