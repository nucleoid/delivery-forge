namespace DeliveryForge.Evidence;

// Comparison DATA only. Matching this tuple never approves a baseline or issues
// authority; the protected registry still verifies its real artifacts and owner pin.
internal sealed record CallableProducerIdentity(string Profile, string ToolSource, string Version,
    string PackageVersion, string Package, string Configuration, string Scope, string Project)
{
    internal static CallableProducerIdentity FromBinding(byte[] bytes)
    {
        using var document = ProducerJson.Read(bytes); var root = document.RootElement;
        string Text(string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String ||
                value.GetString() is not { Length: > 0 and <= 4096 } text)
                throw new EvidencePolicyException("Complete callable producer identity required.");
            return text;
        }
        var result = new CallableProducerIdentity(Text("profile"), Text("toolSourceCommit"), Text("Version"),
            Text("PackageVersion"), Text("package"), Text("configuration"), Text("Scope"), Text("ProjectRelativePath"));
        if (!InstalledToolFiles.Hash(result.Profile) || !InstalledToolFiles.ObjectId(result.ToolSource) ||
            !InstalledToolFiles.Hash(result.Package) || !InstalledToolFiles.Hash(result.Configuration) ||
            result.Scope is not ("base" or "all") || result.Project.Contains('\\') || result.Project.Contains(':') ||
            Path.IsPathFullyQualified(result.Project) || result.Project.Split('/').Any(p => p is "" or "." or ".." or ".git") ||
            !result.Project.EndsWith(".csproj", StringComparison.Ordinal))
            throw new EvidencePolicyException("Exact callable producer/configuration/scope identity required.");
        return result;
    }

    internal static CallableProducerIdentity Current(InstalledToolProfile profile, InstalledToolGateRequest request) =>
        new(profile.Identity, profile.SourceCommit, profile.Version, profile.Data.PackageVersion,
            profile.Data.PackageSha256, profile.Data.ConfigurationSha256, request.Scope,
            request.ProjectRelativePath ?? throw new EvidencePolicyException("Callable project selection required."));
}
