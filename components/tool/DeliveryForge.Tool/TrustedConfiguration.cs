using DeliveryForge.Contracts.Models;
namespace DeliveryForge.Tool;
// Configuration selects trusted inputs; it never contains capability/accepted receipt flags.
public sealed record TrustedConfiguration(string Repository, string RepositoryRoot, string MutableReference, string GitExecutable,
    string Adapter = "codex.cli", string AdapterVersion = "unverified", string MaximumCeiling = "plan", int OperationSeconds = 120,
    string? ExpectedBundleIdentity = null, string? ExpectedContractIdentity = null, string? ExpectedWorkItem = null, string? ExpectedPlanCeiling = null, string? PolicyFile = null, AdmissionFile? StandaloneRoot = null, ParentCoordinationConfiguration? Coordination = null, InstalledQualitySelection? InstalledQuality = null, InstalledReviewSelection? InstalledReview = null, LocalRealGoldenSelection? LocalRealGolden = null, LocalSingleSchedulingSelection? LocalSingle = null, HistoricalQualitySelection? HistoricalQuality = null, ProductionBatchSelection? ProductionBatch = null)
{
    public static TrustedConfiguration Load(string path, ToolLifetime life)
    {
        var bytes = BoundedFiles.Read(path, life, 65536); return Parse(bytes, life);
    }
    internal static TrustedConfiguration LoadBound(string path, ToolLifetime life, out AdmissionFile exact)
    {
        var bytes = BoundedFiles.Read(path, life, 65536); var value = Parse(bytes, life); exact = new(Path.GetFullPath(path), bytes.LongLength, BoundedFiles.Sha(bytes)); life.Check(); return value;
    }
    internal static TrustedConfiguration LoadSelected(string path, ToolLifetime life, AdmissionFile? exact)
    {
        if (exact is null) return Load(path, life);
        if (Path.GetFullPath(path) != Path.GetFullPath(exact.Path)) throw new ToolUsageException("Actual configuration path differs from containing root.");
        var bytes = InstalledHostAdmission.ReadExact(exact, life, 65536); return Parse(bytes, life);
    }
    static TrustedConfiguration Parse(byte[] bytes, ToolLifetime life)
    {
        _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(bytes); life.Check();
        var value = System.Text.Json.JsonSerializer.Deserialize<TrustedConfiguration>(bytes, BoundedFiles.Json) ?? throw new IOException("Trusted configuration is null."); life.Check();
        if (string.IsNullOrWhiteSpace(value.Repository) || string.IsNullOrWhiteSpace(value.MutableReference) || value.OperationSeconds is < 1 or > 1200) throw new ToolUsageException("Trusted configuration identity/budget is incomplete.");
        BoundedFiles.Plain(value.RepositoryRoot, true); BoundedFiles.Plain(value.GitExecutable);
        if (value.Adapter is not ("codex.cli" or "claude-code.cli" or "pi.cli" or "openclaw.sessions")) throw new ToolUsageException("Unknown adapter id.");
        _ = Ceiling(value.MaximumCeiling); if (value.InstalledQuality is { } quality) InstalledQualityHost.ValidateSelection(quality);
        if (value.InstalledReview is { } review) InstalledReviewHost.ValidateSelection(review);
        if (value.LocalRealGolden is { } golden) LocalRealGoldenOwner.Validate(golden);
        if (value.HistoricalQuality is { } historical) HistoricalQualityHost.ValidateSelection(value, historical);
        if (value.ProductionBatch is { } batch) ProductionBatchOwner.ValidateSelection(value, batch);
        return value;
    }
    public static AuthorizationCeiling Ceiling(string value) => value switch { "plan" => AuthorizationCeiling.Plan, "implement" => AuthorizationCeiling.Implement, "pr" => AuthorizationCeiling.Pr, "merge" => AuthorizationCeiling.Merge, _ => throw new ToolUsageException("Unknown immutable ceiling.") };
}
