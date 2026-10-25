using DeliveryForge.Execution.Host;
using System.Text.Json;
namespace DeliveryForge.Tool;

// Exact independent trust-root pins selected by the human-owned containing
// configuration. None is a live process/session/writer or accepted worker DTO.
public sealed record AdmissionFile(string Path, long Bytes, string Sha256);
public sealed record InstalledHostCase(string Name, string Outcome, string ArtifactSha256);
public sealed record InstalledHostRootAcceptance(string Kind, string Platform, string Adapter, string Version,
    StandaloneHostSelection Host, AdmissionFile ToolAssembly, IReadOnlyList<AdmissionFile> Artifacts, IReadOnlyList<InstalledHostCase> Cases, Issue17RuntimeTrustSelection? RuntimeTrust = null);
public sealed partial class InstalledHostAdmission
{
    readonly InstalledHostRootAcceptance actual; readonly AgentAdapterProfile profile;
    readonly ToolLifetime actualOriginal; readonly TrustedConfiguration actualConfiguration; readonly AdmissionFile actualRoot;
    public StandaloneHostSelection Host => actual.Host;
    public AgentAdapterProfile Profile => profile;
    InstalledHostAdmission(InstalledHostRootAcceptance actual, AgentAdapterProfile profile, ToolLifetime original, TrustedConfiguration configuration, AdmissionFile root) { this.actual = actual; this.profile = profile; actualOriginal = original; actualConfiguration = configuration; actualRoot = root; }
    internal static byte[] ReadExact(AdmissionFile pin, ToolLifetime original, int maximum)
    {
        if (pin is null || pin.Path is null || pin.Bytes <= 0 || pin.Bytes > maximum || pin.Sha256 is null || pin.Sha256.Length != 64 || pin.Sha256.Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c))) throw new ToolUsageException("Finite exact independent lowercase admission pin required.");
        var bytes = BoundedFiles.Read(pin.Path, original, maximum); original.Check(); var digest = BoundedFiles.Sha(bytes); original.Check(); if (bytes.LongLength != pin.Bytes || digest != pin.Sha256) throw new IOException("Actual independently selected admission bytes changed."); return bytes;
    }
    internal static void RequireConformanceCases(IReadOnlyList<InstalledHostCase> cases, IReadOnlySet<string> artifacts, bool runtimeTrust)
    {
        var required = new HashSet<string>(new[] { "actual_launch_assignment", "actual_completion_commit", "actual_stop_same_original", "foreign_completion_refused", "malformed_protocol_refused" }, StringComparer.Ordinal);
        // The independent Root must actually qualify config/read(includeLayers),
        // experimentalApi=false, account-before-thread and all three route reads
        // on this exact native version. A managed fixture is not that proof.
        if (runtimeTrust) required.Add("actual_runtime_trust_route");
        if (cases.Count != required.Count) throw new ToolUsageException("Complete selected installed conformance required.");
        foreach (var row in cases)
            if (row is null || row.Outcome != "PASS" || !required.Remove(row.Name) || !artifacts.Contains(row.ArtifactSha256))
                throw new ToolUsageException("Actual independent host conformance case proof is missing, duplicated or incomplete.");
        if (required.Count != 0) throw new ToolUsageException("Complete selected installed conformance required.");
        if (runtimeTrust)
        {
            var route = cases.Single(row => row.Name == "actual_runtime_trust_route");
            if (cases.Any(row => row.Name != route.Name && row.ArtifactSha256 == route.ArtifactSha256))
                throw new ToolUsageException("The actual runtime-trust route requires its own distinct artifact.");
        }
    }
    // Structure of already-pinned actual conformance DATA, not a producer or issuer.
    internal static void RequireRuntimeTrustRouteArtifact(byte[] bytes, StandaloneHostSelection host, string version)
    {
        if (bytes.Length is < 1 or > 1048576) throw new ToolUsageException("Finite actual runtime-trust trace required.");
        static JsonDocument ParseArtifact(byte[] raw)
        {
            try
            {
                _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(raw);
                return JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 48 });
            }
            catch (DeliveryForge.Contracts.Serialization.ContractJsonException) { throw new ToolUsageException("Malformed actual runtime-trust trace."); }
            catch (JsonException) { throw new ToolUsageException("Malformed actual runtime-trust trace."); }
        }
        using var document = ParseArtifact(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ToolUsageException("Malformed actual runtime-trust trace.");
        static bool Field(JsonElement value, string name, JsonValueKind kind, out JsonElement field)
        {
            field = default;
            return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out field) && field.ValueKind == kind;
        }
        static bool Text(JsonElement value, string name, string expected) => Field(value, name, JsonValueKind.String, out var field) && field.GetString() == expected;
        if (!Text(root, "Kind", "ActualCodexAppServerTrace-v1") || !Field(root, "TrustRouteConformanceOnly", JsonValueKind.True, out _) ||
            !Field(root, "RuntimeCertified", JsonValueKind.False, out _) || !Text(root, "Version", version) || host.Version != version ||
            !Field(root, "Host", JsonValueKind.Object, out var image) || !Text(image, "Executable", host.Executable) ||
            !Text(image, "Sha256", host.Sha256) || !Text(image, "Version", version) ||
            !Field(image, "Bytes", JsonValueKind.Number, out var extent) || !extent.TryGetInt64(out var length) || length != host.Bytes ||
            !Field(root, "Events", JsonValueKind.Array, out var events))
            throw new ToolUsageException("Actual runtime-trust trace does not bind the exact selected host/version.");
        string[] methods = ["initialize", "config/read", "account/read", "thread/start", "config/read", "turn/start", "config/read"];
        var count = 0;
        foreach (var row in events.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new ToolUsageException("Malformed actual runtime-trust event.");
            if (!row.TryGetProperty("TrustRpcMethod", out var method)) continue;
            if (count >= methods.Length || method.ValueKind != JsonValueKind.String || method.GetString() != methods[count] ||
                !Field(row, "Message", JsonValueKind.Object, out var message) ||
                !Field(message, "id", JsonValueKind.Number, out var id) || !id.TryGetInt64(out var number) || number != count + 1 ||
                message.TryGetProperty("error", out _) || !message.TryGetProperty("result", out var result) ||
                methods[count] == "config/read" && !Text(result, "routeStatus", "owner-trusted-no-known-override"))
                throw new ToolUsageException("Actual runtime-trust RPC sequence or route observation is incomplete.");
            count++;
        }
        if (count != methods.Length) throw new ToolUsageException("Complete actual runtime-trust RPC sequence required.");
    }
    public static InstalledHostAdmission ReadRootAccepted(AdmissionFile rootPin, TrustedConfiguration config, ToolLifetime original, AdmissionFile? actualConfigurationPin = null)
        => ReadRootAcceptedCore(rootPin, config, original, actualConfigurationPin, null);
    internal static InstalledHostAdmission ReadRootAcceptedForBatch(AdmissionFile rootPin, TrustedConfiguration config, ToolLifetime original,
        AdmissionFile configurationPin, BatchWindowsImages images) => ReadRootAcceptedCore(rootPin, config, original, configurationPin, images);
    static InstalledHostAdmission ReadRootAcceptedCore(AdmissionFile rootPin, TrustedConfiguration config, ToolLifetime original,
        AdmissionFile? actualConfigurationPin, BatchWindowsImages? images)
    {
        var bytes = ReadExact(rootPin, original, 1048576); _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(bytes); original.Check(); var receipt = System.Text.Json.JsonSerializer.Deserialize<InstalledHostRootAcceptance>(bytes, BoundedFiles.Json) ?? throw new IOException("Root host admission is null."); original.Check();
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : throw new PlatformNotSupportedException("Installed host profile is Windows/Linux only.");
        if (receipt.Host is null || receipt.ToolAssembly is null || receipt.Artifacts is null || receipt.Cases is null || receipt.Kind != "RootActualInstalledHostConformanceAcceptance-v1" || receipt.Platform != os || receipt.Adapter != config.Adapter || receipt.Version != config.AdapterVersion || receipt.Host.Version != config.AdapterVersion || receipt.Artifacts.Count is < 1 or > 32 || receipt.Cases.Count != (receipt.RuntimeTrust is null ? 5 : 6)) throw new ToolUsageException("Actual independent installed host admission does not bind this platform/profile.");
        // Declared-cost refusal only, before image/artifact reads consume this
        // same credit. No bytes, conformance case or runtime trust is accepted
        // here; all exact content/proof checks below remain mandatory.
        if (receipt.RuntimeTrust is not null)
            RuntimeTrust.RequireReadCapacity(receipt, rootPin, actualConfigurationPin ?? throw new ToolUsageException("Owner runtime trust requires the containing configuration pin."), config, original, images);
        var self = System.Reflection.Assembly.GetExecutingAssembly().Location; if (Path.GetFullPath(receipt.ToolAssembly.Path) != Path.GetFullPath(self)) throw new ToolUsageException("Actual installed Tool assembly differs from independently accepted host conformance."); _ = ReadExact(receipt.ToolAssembly, original, 16777216);
        var image = new AdmissionFile(receipt.Host.Executable, receipt.Host.Bytes, receipt.Host.Sha256);
        if (images is null || receipt.RuntimeTrust is null) BoundedFiles.VerifyNativeImage(image, original); else images.Verify(image, original);
        var artifacts = new HashSet<string>(StringComparer.Ordinal); long total = 0; byte[]? routeArtifact = null;
        foreach (var pin in receipt.Artifacts) { if (pin is null) throw new ToolUsageException("Missing actual conformance artifact."); if (pin.Bytes <= 0 || pin.Bytes > 1048576 || total > 33554432 - pin.Bytes || !artifacts.Add(pin.Sha256)) throw new ToolUsageException("Actual conformance artifacts exceed finite complete profile or collide."); total += pin.Bytes; var raw = ReadExact(pin, original, 1048576); if (receipt.RuntimeTrust is not null && receipt.Cases.Any(row => row is not null && row.Name == "actual_runtime_trust_route" && row.ArtifactSha256 == pin.Sha256)) routeArtifact = raw; }
        RequireConformanceCases(receipt.Cases, artifacts, receipt.RuntimeTrust is not null);
        if (receipt.RuntimeTrust is not null)
        {
            original.Check(); RequireRuntimeTrustRouteArtifact(routeArtifact ?? throw new ToolUsageException("Actual runtime-trust artifact missing."), receipt.Host, receipt.Version); original.Check();
        }
        // This derives version-specific capability EVIDENCE only from a pinned
        // independent Root decision. No live native/task/session is adopted.
        var reference = "sha256:" + rootPin.Sha256; var rows = new[]{
            new AgentCapabilityEvidence(AgentCapability.Launch,AgentCapabilityStatus.Supported,"actual source-owned initialized native transport",reference),
            new AgentCapabilityEvidence(AgentCapability.CompletionEvidence,AgentCapabilityStatus.Supported,"actual exact accepted assignment/session/task and original native closure",reference),
            new AgentCapabilityEvidence(AgentCapability.Stop,AgentCapabilityStatus.Supported,"actual same-original stop attempt and native group/IO disposition",reference),
            new AgentCapabilityEvidence(AgentCapability.EvidenceExport,AgentCapabilityStatus.Supported,"actual complete bounded protocol/native evidence",reference),
            new AgentCapabilityEvidence(AgentCapability.Pause,AgentCapabilityStatus.Unsupported,"No actual same-turn pause capability is issued.",reference),
            new AgentCapabilityEvidence(AgentCapability.Resume,AgentCapabilityStatus.Unsupported,"No actual same-turn resume capability is issued.",reference)};
        original.Check(); var result = new InstalledHostAdmission(receipt, new(config.Adapter, config.AdapterVersion, Array.AsReadOnly(rows)), original, config, rootPin);
        if (receipt.RuntimeTrust is not null) result.TrustedRuntime = RuntimeTrust.Bind(result, actualConfigurationPin ?? throw new ToolUsageException("Owner runtime trust requires the actual containing configuration pin."), images);
        return result;
    }
}
