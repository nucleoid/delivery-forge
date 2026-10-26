using System.Text;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Tool;

// Review presentation only. The original canonical receipt and every artifact
// remain retained; this wrapper cannot be parsed or consumed as a gate receipt.
internal static class ReviewGateReceiptEvidence
{
    internal static JsonElement Create(string path, byte[] exactReceipt)
    {
        var validated = ContractValidator.ParseAndValidate(exactReceipt);
        if (validated.SchemaName != "gate-receipt")
            throw new IOException("Review quality evidence requires an actual gate receipt.");
        using var document = JsonDocument.Parse(exactReceipt);
        var root = document.RootElement;
        var artifacts = root.GetProperty("artifactHashes");
        var completeInventory = CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(artifacts.GetRawText()));
        var fields = root.EnumerateObject()
            .Where(field => field.Name is not ("artifactHashes" or "identity"))
            .ToDictionary(field => field.Name, field => field.Value.Clone(), StringComparer.Ordinal);
        return JsonSerializer.SerializeToElement(new
        {
            Kind = "ReviewGateReceiptEvidence-v1",
            RawReceipt = new
            {
                Path = path,
                Bytes = exactReceipt.LongLength,
                Sha256 = "sha256:" + BoundedFiles.Sha(exactReceipt),
                Identity = validated.Identity
            },
            ReceiptFields = fields,
            ArtifactInventory = new
            {
                Count = artifacts.EnumerateObject().Count(),
                CanonicalIdentity = "sha256:" + BoundedFiles.Sha(completeInventory)
            }
        }, BoundedFiles.Json);
    }
    // Presentation DATA only. The caller has already loaded these exact bytes
    // through InstalledToolProfile.LoadPinned and completed the original gate.
    internal static JsonElement CreateToolProvenance(InstalledQualityGateSelection selected,
        byte[] exactProfile, DeliveryForge.Evidence.GateRunResult actual)
    {
        if (exactProfile.LongLength != selected.Profile.Bytes || BoundedFiles.Sha(exactProfile) != selected.Profile.Sha256)
            throw new IOException("Review producer profile differs from its parent-selected pin.");
        using var document = JsonDocument.Parse(exactProfile);
        var profile = document.RootElement;
        var entrypoint = profile.GetProperty("ExecutableRelativePath").GetString()!;
        var executable = profile.GetProperty("Payload").EnumerateArray()
            .Single(file => file.GetProperty("Path").GetString() == entrypoint);
        var expectedPath = Path.GetFullPath(Path.Combine(profile.GetProperty("InstallationRoot").GetString()!, entrypoint));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (profile.GetProperty("Tool").GetString() != selected.Tool || profile.GetProperty("Fixture").GetBoolean() ||
            actual.Fixture || actual.SourceChanged || actual.Outcome != DeliveryForge.Evidence.GateOutcome.Pass ||
            !string.Equals(expectedPath, Path.GetFullPath(actual.Invocation.FileName), comparison) ||
            actual.CapabilityIdentity != executable.GetProperty("Sha256").GetString())
            throw new IOException("Review producer image does not match the actual selected gate.");
        return JsonSerializer.SerializeToElement(new
        {
            Kind = "ParentSelectedCompletedQualityToolData-v1",
            selected.GateId, selected.Tool, selected.Scope, selected.ProjectRelativePath,
            Profile = new { selected.Profile.Bytes, Sha256 = "sha256:" + selected.Profile.Sha256 },
            Producer = new
            {
                Version = profile.GetProperty("Version").GetString(),
                SourceCommit = profile.GetProperty("SourceCommit").GetString(),
                PackageVersion = profile.GetProperty("PackageVersion").GetString(),
                PackageSha256 = profile.GetProperty("PackageSha256").GetString(),
                Executable = new { RelativePath = entrypoint, Bytes = executable.GetProperty("Bytes").GetInt64(), Sha256 = actual.CapabilityIdentity },
                Configuration = new { RelativePath = profile.GetProperty("ConfigurationRelativePath").GetString(), Sha256 = profile.GetProperty("ConfigurationSha256").GetString() },
                SdkVersion = profile.GetProperty("SdkVersion").GetString(),
                Framework = profile.GetProperty("Framework").GetString(),
                Runner = profile.GetProperty("Runner").GetString(),
                Collector = profile.GetProperty("Collector").GetString()
            },
            actual.ProductionCapable, actual.Fixture,
            ParentSelectedLimitations = profile.GetProperty("Limitations").Clone(),
            Qualification = "Exact parent-selected profile metadata and the completed gate's observed executable/version binding; no independent source-build receipt or production/publication authority is asserted.",
            Omitted = new[] { "Absolute installation/package paths", "Environment", "Complete payload inventory (retained by profile digest)", "Independent source-build history (not imported)" }
        }, BoundedFiles.Json);
    }

    internal static void RequireRetainedResults(IReadOnlyList<DeliveryForge.Evidence.GateRunResult> retained,
        IReadOnlyList<DeliveryForge.Evidence.GateRunResult> supplied)
    {
        if (retained.Count != supplied.Count || retained.Where((row, index) => !ReferenceEquals(row, supplied[index])).Any())
            throw new IOException("Review provenance requires the same actual retained gate result objects.");
    }

}
