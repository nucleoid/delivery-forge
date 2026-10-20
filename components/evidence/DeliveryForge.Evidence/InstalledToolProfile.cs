using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeliveryForge.Evidence;

/// <summary>Immutable host configuration, not a producer-issued approval. The expected profile
/// hash is selected out of band by the parent/protected policy, never from the evaluated branch,
/// a tool receipt, a report or the profile itself. No package installation occurs here.</summary>
public sealed class InstalledToolProfile
{
    private readonly ProfileData _data;
    internal ProfileData Data => _data;
    public string Identity { get; }
    public string Tool => _data.Tool;
    public string Version => _data.Version;
    public string SourceCommit => _data.SourceCommit;
    public bool Fixture => _data.Fixture;

    private InstalledToolProfile(ProfileData data, string identity) { _data = data; Identity = identity; }

    public static InstalledToolProfile LoadPinned(string absoluteProfilePath, string parentApprovedSha256)
    {
        if (!InstalledToolFiles.Hash(parentApprovedSha256)) throw new ArgumentException("A parent-approved profile SHA256 is required.");
        var setup = new InstalledToolFiles(TimeSpan.FromSeconds(30));
        return LoadPinned(absoluteProfilePath, parentApprovedSha256, setup);
    }
    internal static InstalledToolProfile LoadPinned(string absoluteProfilePath, string parentApprovedSha256, InstalledToolFiles setup) => setup.Effect(() => LoadPinnedCore(absoluteProfilePath, parentApprovedSha256, setup));
    private static InstalledToolProfile LoadPinnedCore(string absoluteProfilePath, string parentApprovedSha256, InstalledToolFiles setup)
    {
        if (!InstalledToolFiles.Hash(parentApprovedSha256)) throw new ArgumentException("A parent-approved profile SHA256 is required.");
        var bytes = setup.Read(absoluteProfilePath, 1024 * 1024); setup.Check(); setup.ReleaseKnown();
        if (InstalledToolFiles.Identity(bytes) != parentApprovedSha256) throw new EvidenceWriteException("Approved tool profile bytes differ.");
        using (var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 })) Unique(document.RootElement);
        var data = JsonSerializer.Deserialize<ProfileData>(bytes, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new EvidenceWriteException("Missing installed tool profile.");
        Validate(data); setup.Check();
        return new(data, parentApprovedSha256);
    }

    internal static void Validate(ProfileData data)
    {
        if (new[] { data.Schema, data.Tool, data.Version, data.PackageVersion, data.SourceCommit, data.Format, data.InstallationRoot,
            data.ExecutableRelativePath, data.PackagePath, data.PackageSha256, data.DotNetHostPath, data.DotNetHostSha256,
            data.GitExecutablePath, data.GitExecutableSha256, data.SdkVersion, data.Framework, data.Runner, data.Collector,
            data.ConfigurationRelativePath, data.ConfigurationSha256 }.Any(string.IsNullOrWhiteSpace) ||
            data.Payload is null || data.Environment is null || data.Limitations is null)
            throw new EvidenceWriteException("Required profile fields are absent.");
        if (data.Schema != "forge-installed-quality-v1" || data.Tool is not ("crap4csharp" or "mutate4csharp") ||
            data.Format != (data.Tool == "crap4csharp" ? "1.0" : "2") ||
            !InstalledToolFiles.ObjectId(data.SourceCommit) || data.Version.Length > 256 || data.PackageVersion.Length > 128 ||
            data.Framework != "net10.0" || data.Runner != "vstest" || data.Collector != "coverlet.collector/6.0.4" ||
            !data.SdkVersion.StartsWith("10.0.", StringComparison.Ordinal) || data.SdkVersion.Length > 32 ||
            !InstalledToolFiles.Hash(data.PackageSha256) || !InstalledToolFiles.Hash(data.DotNetHostSha256) ||
            !InstalledToolFiles.Hash(data.ConfigurationSha256) || !InstalledToolFiles.Hash(data.GitExecutableSha256))
            throw new EvidenceWriteException("Unsupported or incomplete installed producer profile.");
        foreach (var path in new[] { data.InstallationRoot, data.PackagePath, data.DotNetHostPath, data.GitExecutablePath }) InstalledToolFiles.PlainPath(path);
        Relative(data.ExecutableRelativePath); Relative(data.ConfigurationRelativePath);
        if (data.Tool == "mutate4csharp" && data.ConfigurationRelativePath != "mutate4csharp.json")
            throw new EvidenceWriteException("Strict mutation uses the captured root configuration.");
        if (data.Payload.Any(p => p is null || string.IsNullOrWhiteSpace(p.Path) || string.IsNullOrWhiteSpace(p.Sha256)) ||
            data.Limitations.Any(value => value is null || value.Length > 8192))
            throw new EvidenceWriteException("Malformed payload/limitation member.");
        if (data.Payload.Length is < 1 or > 10000 || data.Payload.Select(p => p.Path).Distinct(InstalledToolFiles.PathComparer).Count() != data.Payload.Length)
            throw new EvidenceWriteException("Installed payload census is missing, duplicated or oversized.");
        long total = 0;
        foreach (var file in data.Payload)
        {
            Relative(file.Path);
            if (file.Bytes < 0 || file.Bytes > 128L * 1024 * 1024 || !InstalledToolFiles.Hash(file.Sha256) ||
                (total += file.Bytes) > 1024L * 1024 * 1024) throw new EvidenceWriteException("Invalid complete payload tuple.");
        }
        if (!data.Payload.Any(p => p.Path == data.ExecutableRelativePath)) throw new EvidenceWriteException("Executable is absent from complete payload.");
        if (!data.Environment.TryGetValue("DOTNET_ROOT", out var dotnetRoot) ||
            !InstalledToolFiles.PathComparer.Equals(Path.GetFullPath(dotnetRoot), Path.GetDirectoryName(data.DotNetHostPath)) ||
            !data.Environment.TryGetValue("DOTNET_MULTILEVEL_LOOKUP", out var multi) || multi != "0")
            throw new EvidenceWriteException("Actual selected apphost/runtime root must be explicitly bound.");
        if (!data.Environment.TryGetValue("DOTNET_HOST_PATH", out var selectedHost) ||
            !InstalledToolFiles.PathComparer.Equals(Path.GetFullPath(selectedHost), Path.GetFullPath(data.DotNetHostPath)))
            throw new EvidenceWriteException("Strict producer child commands must use the exact approved dotnet host.");
        if (data.Environment.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Environment.Count)
            throw new EvidenceWriteException("Case-aliased environment overrides are ambiguous.");
        RequireInstalledGraphDeploymentEnvironment(data.Tool, data.Environment.Keys);
        foreach (var rootKey in new[] { "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "DOTNET_ROOT_ARM64" })
        {
            var effective = data.Environment.FirstOrDefault(p => string.Equals(p.Key, rootKey, StringComparison.OrdinalIgnoreCase)).Value ?? Environment.GetEnvironmentVariable(rootKey);
            if (!string.IsNullOrEmpty(effective) && !InstalledToolFiles.PathComparer.Equals(Path.GetFullPath(effective), Path.GetDirectoryName(data.DotNetHostPath)))
                throw new EvidenceWriteException("Architecture-specific apphost root differs from the selected runtime root.");
        }
        foreach (var hook in new[] { "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE",
            "CORECLR_ENABLE_PROFILING", "COR_ENABLE_PROFILING", "MSBuildSDKsPath", "MSBUILD_EXE_PATH" })
            if (!string.IsNullOrEmpty(data.Environment.FirstOrDefault(p => string.Equals(p.Key, hook, StringComparison.OrdinalIgnoreCase)).Value ?? Environment.GetEnvironmentVariable(hook)))
                throw new EvidenceWriteException("Unreviewed runtime/build injection hook is unsupported.");
        if (data.Environment.Count > 32 || data.Environment.Any(p => p.Key.Length is < 1 or > 128 || p.Value is null || p.Value.Length > 8192 ||
            p.Key.Contains('=') || p.Key.Contains('\0') || p.Value.Contains('\0')))
            throw new EvidenceWriteException("Invalid finite process environment profile.");
        if (!data.Environment.TryGetValue("DOTNET_PROCESSOR_COUNT", out var cpu) || cpu != "2" ||
            !data.Environment.TryGetValue("DOTNET_GCHeapHardLimit", out var heap) || heap != "0x100000000" ||
            !data.Environment.TryGetValue("MSBUILDDISABLENODEREUSE", out var reuse) || reuse != "1" ||
            !data.Environment.TryGetValue("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", out var server) || server != "1")
            throw new EvidenceWriteException("The approved2CPU/4GiB/no persistent-server environment is required.");
        if (data.Limitations.Length > 32 || data.Limitations.Any(s => s.Length is < 1 or > 1024))
            throw new EvidenceWriteException("Invalid profile limitations.");
    }

    internal static void RequireInstalledGraphDeploymentEnvironment(string? tool, IEnumerable<string> overrides)
    {
        if (tool == "mutate4csharp" &&
            (overrides.Any(IsGraphDeploymentOverride) ||
             Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(IsGraphDeploymentOverride)))
            throw new EvidenceWriteException("Installed mutation requires the default packaged graph deployment; graph manifest override keys must be absent.");
    }

    private static bool IsGraphDeploymentOverride(string name) =>
        name.Equals("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256", StringComparison.OrdinalIgnoreCase);

    internal static void Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains(':') || path.Contains('\\') ||
            path.StartsWith('/') || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new EvidenceWriteException("A finite normalized relative input/payload path is required.");
    }

    private static void Unique(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new EvidenceWriteException("Duplicate installed profile property.");
                Unique(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Unique(item);
    }

    internal sealed record PayloadFile(string Path, long Bytes, string Sha256);
    internal sealed record ProfileData(string Schema, string Tool, string Version, string PackageVersion, string SourceCommit, string Format,
        string InstallationRoot, string ExecutableRelativePath, string PackagePath, string PackageSha256,
        PayloadFile[] Payload, string DotNetHostPath, string DotNetHostSha256, string SdkVersion,
        string GitExecutablePath, string GitExecutableSha256, string Framework, string Runner, string Collector, string ConfigurationRelativePath, string ConfigurationSha256,
        Dictionary<string, string> Environment, string[] Limitations, bool Fixture);
}
