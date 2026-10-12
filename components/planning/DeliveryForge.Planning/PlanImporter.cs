using System.Text.Json;

namespace DeliveryForge.Planning;

// Caller-selected DATA constraints, obtained independently of the untrusted bundle.
// None of these strings is execution, policy, review or publication authority.
public sealed record PlanImportSource(string RepositoryRoot, string MutableReference, string TrustedGitExecutable);

public sealed record PlanImportExpectations(string BundleIdentity, string ContractIdentity, string Repository,
    string WorkItem, string RepositoryRoot, string MutableReference, string RequestedCeiling);

public sealed class PlanImportResult
{
    internal PlanImportResult(FrozenPlan? plan, IReadOnlyList<string> diagnostics)
    { Plan = plan; Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); }
    public FrozenPlan? Plan { get; }
    public bool DownstreamReady => Plan?.DownstreamReady == true;
    public IReadOnlyList<string> Diagnostics { get; }
}

public static class PlanImporter
{
    public const int MaximumBundleBytes = 16 * 1024 * 1024;

    // Private prepared context comes from the ACTUAL trusted reader/root/ref and
    // working metadata before the shared owner constructs a FrozenPlan.
    // No public context/Ready setter, PlanContract-only projection or supplied Git flags.
    public static async Task<PlanImportResult> ImportAndReconcileAsync(ReadOnlyMemory<byte> richBundle,
        PlanImportSource source, PlanImportExpectations expectations, TimeSpan entireBudget,
        ImportedContextEnvelope? freshImportedContext = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); // No Git/path access for pre-cancelled callers.
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(expectations);
        if (entireBudget <= TimeSpan.Zero || entireBudget > TimeSpan.FromSeconds(120))
            throw new ArgumentOutOfRangeException(nameof(entireBudget));
        if (richBundle.Length is 0 or > MaximumBundleBytes)
            throw new PlanningException("Rich planning bundle exceeds the bounded import byte allowance.");
        if (string.IsNullOrWhiteSpace(source.TrustedGitExecutable) || !System.IO.Path.IsPathFullyQualified(source.TrustedGitExecutable))
            throw new PlanningException("An explicit absolute trusted Git executable is required for import; ambient fallback is refused.");
        using var life = new PlanImportLifetime(entireBudget, cancellationToken);
        // Own caller bytes before the first asynchronous handoff.
        var ownedBundle = richBundle.ToArray(); life.Check();
        // Plain existing regular selection BEFORE any source namespace/helper access.
        await life.Observe(() =>
        {
            try { using var selected = new PlanImportFilesystem.FileBinding(source.TrustedGitExecutable); selected.Verify(); return true; }
            catch (PlanningException error) { throw new PlanningException("Explicit trusted Git executable must be a plain existing regular file: " + error.Message); }
        });
        using var codec = await life.Observe(() => new RichPlanImportCodec(ownedBundle));
        var material = codec.Material;
        RichPlanImportCodec.Require(codec.Identity == expectations.BundleIdentity && codec.ContractIdentity == expectations.ContractIdentity,
            "Rich bundle identity differs from the independently selected original identity.");
        RichPlanImportCodec.Require(RichPlanImportCodec.Text(material, "repository") == expectations.Repository &&
            RichPlanImportCodec.Text(material, "workItem") == expectations.WorkItem &&
            RichPlanImportCodec.Text(material, "requestedCeiling") == expectations.RequestedCeiling,
            "Imported repository/work-item/immutable ceiling differs from trusted expectations.");
        var observations = material.GetProperty("repositoryObservations");
        if (!System.IO.Path.IsPathFullyQualified(source.RepositoryRoot) || !System.IO.Path.IsPathFullyQualified(expectations.RepositoryRoot) ||
            !SamePath(source.RepositoryRoot, expectations.RepositoryRoot)) return NotReady("Expected local root differs; explicit fresh parent relocation is required.");
        if (source.MutableReference != expectations.MutableReference || source.MutableReference != RichPlanImportCodec.Text(observations, "requestedRef") ||
            !RepositoryContext.IsFreshnessBearingReference(source.MutableReference)) return NotReady("Exact original mutable ref reconciliation is required.");
        var requirement = RichPlanImportCodec.Text(material.GetProperty("intake"), "importedContextRequirement");
        if (requirement == "required" && freshImportedContext is null) return NotReady("Required imported context must be freshly supplied and verified; saved availability is not proof.");
        var fresh = await life.Observe(() => CaptureFreshEnvelope(freshImportedContext));
        using var root = await life.Observe(() => new PlanImportFilesystem.DirectoryBinding(System.IO.Path.GetFullPath(source.RepositoryRoot)));
        PlanImportGitView view;
        try { view = await life.Observe(() => new PlanImportGitView(source.RepositoryRoot, source.TrustedGitExecutable, life)); }
        catch (PlanningException error) { return NotReady(error.Message); }
        using var ownedView = view;
        var reader = await life.Observe(() => GitRepositoryContextReader.ForImport(source.TrustedGitExecutable, life, view));
        PlanImportSourceSnapshot before;
        try { before = await PlanImportSourceSnapshot.Capture(source.RepositoryRoot, reader, life); }
        catch (PlanningException error) { return NotReady(error.Message); }
        if (before.Status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => !line.StartsWith("!! ", StringComparison.Ordinal)))
            return NotReady("Current source/index status is dirty; fresh reconciliation is required.");
        var current = await life.Run(() => reader.ReadAsync(source.RepositoryRoot, source.MutableReference, life.Token));
        if (!SamePath(current.RepositoryRoot, source.RepositoryRoot) || current.Commit != RichPlanImportCodec.Text(material, "baseCommit") ||
            current.Tree != RichPlanImportCodec.Text(material, "baseTree")) return NotReady("Actual base/root differs from the frozen plan.");
        if (current.HeadCommit != RichPlanImportCodec.Text(observations, "headCommit") || current.HeadTree != RichPlanImportCodec.Text(observations, "headTree") ||
            current.Dirty || current.Shallow || current.DetachedHead || !current.ExactBranchReferenceVerified || current.Submodules.Count != 0)
            return NotReady("Actual current HEAD/index/history/ref observations require fresh reconciliation.");
        var draft = await codec.Draft(current, reader, life, fresh);
        if (!draft.Intake.Ready) return new(null, draft.Intake.Limitations);
        // The same producer validator/digest/command/DAG projection is used, not a loader constructor.
        var frozen = await life.Observe(() => PlanFreezer.RestoreForImport(draft, codec.Revision, codec.CreatedAt, codec.Supersedes));
        RichPlanImportCodec.Require(frozen.Identity == codec.Identity && frozen.ContractIdentity == codec.ContractIdentity &&
            frozen.ContentDigest == RichPlanImportCodec.Text(codec.Envelope, "contentDigest") &&
            frozen.PlanRevision == RichPlanImportCodec.Text(codec.Envelope, "planRevision") &&
            frozen.CanonicalBytes.AsSpan().SequenceEqual(DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(ownedBundle)),
            "Rich identity/material/intake/provenance projections differ from freshly validated owner content.");
        var after = await PlanImportSourceSnapshot.Capture(source.RepositoryRoot, reader, life);
        var final = await life.Run(() => reader.ReadAsync(source.RepositoryRoot, source.MutableReference, life.Token));
        await life.Observe(() => { root.Verify(); return true; });
        if (before != after || final.Commit != current.Commit || final.Tree != current.Tree || final.HeadCommit != current.HeadCommit ||
            final.HeadTree != current.HeadTree || final.Dirty || final.DetachedHead || final.Shallow || !final.ExactBranchReferenceVerified)
            return NotReady("Source/index/root/ref changed during the original import operation.");
        life.Check();
        return new(frozen, []);
    }
    private static PlanImportResult NotReady(string reason) => new(null, [reason]);
    private static bool SamePath(string left, string right) => string.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static ImportedContextEnvelope? CaptureFreshEnvelope(ImportedContextEnvelope? input)
    {
        if (input is null) return null;
        var count = input.Entries.Count;
        if (count is < 0 or > 256) throw new PlanningException("Fresh imported context exceeds256 entries.");
        var characters = 0;
        string? Capture(string? value)
        { if (value is null) return null; characters += value.Length;
            if (value.Length > 4096 || characters > 256 * 1024) throw new PlanningException("Fresh context exceeds bounded advisory material."); return value; }
        string[] CaptureList(IReadOnlyList<string> values)
        { var length = values.Count; if (length is < 0 or > 256) throw new PlanningException("Fresh context advisory list exceeds256.");
            var copy = new string[length]; for (var i = 0; i < length; i++) copy[i] = Capture(values[i]) ?? throw new PlanningException("Null advisory item."); return copy; }
        var entries = new ImportedContextEntry[count];
        for (var i = 0; i < count; i++)
        {
            var entry = input.Entries[i] ?? throw new PlanningException("Null imported context entry.");
            entries[i] = new(Capture(entry.Kind)!, Capture(entry.Locator)!, Capture(entry.Summary)!, Capture(entry.Digest), entry.ObservedAt,
                entry.Stale, entry.Truncated, entry.Heuristic, CheckoutVerification.Unverified, Capture(entry.CheckoutDigest), entry.Requirement, entry.DistilledMeaning);
        }
        var conflicts = CaptureList(input.Conflicts); var limitations = CaptureList(input.Limitations);
        // Roundtrip the public closed envelope: private verification bindings/claimed flags are discarded.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        { schemaVersion = Capture(input.SchemaVersion), entries = entries.Select(entry => new
            { kind = entry.Kind, locator = entry.Locator, summary = entry.Summary, digest = entry.Digest,
                checkoutDigest = entry.CheckoutDigest, observedAt = entry.ObservedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                stale = entry.Stale, truncated = entry.Truncated, heuristic = entry.Heuristic,
                requirement = entry.Requirement.ToString(), distilledMeaning = entry.DistilledMeaning?.ToString() }).ToArray(),
            conflicts, limitations });
        return ImportedContextEnvelope.Parse(bytes);
    }
}
