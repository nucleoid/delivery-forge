using System.Text;
using System.Text.Json;

namespace DeliveryForge.Tool;

public sealed record AutomaticSetupResult(string ConfigurationPath, AdmissionFile Configuration, AdmissionFile Root,
    string RepositoryKey, string RunId, string RunDirectory, StandaloneRootSelection Selection)
{
    // Setup produces selections, never an installed-host acceptance or owner.
    public bool RuntimeCertified => false;
}

/// <summary>Materializes ordinary path defaults as explicit independently pinned DATA.</summary>
public static class AutomaticSetup
{
    public static string DefaultStateRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!Path.IsPathFullyQualified(local)) throw new ToolUsageException("A complete local application data directory is required.");
            return BoundedFiles.Plain(Path.Combine(local, "DeliveryForge"), true);
        }
        if (OperatingSystem.IsLinux())
        {
            if (Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state)
            {
                if (!Path.IsPathFullyQualified(state)) throw new ToolUsageException("XDG_STATE_HOME must be absolute when selected.");
                return BoundedFiles.Plain(Path.Combine(state, "delivery-forge"), true);
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Path.IsPathFullyQualified(home)) throw new ToolUsageException("A complete user profile directory is required.");
            return BoundedFiles.Plain(Path.Combine(home, ".local", "state", "delivery-forge"), true);
        }
        throw new PlatformNotSupportedException("Automatic setup supports Windows and Linux.");
    }

    public static AutomaticSetupResult PreparePlan(string repository, string repositoryRoot, string mutableReference,
        string gitExecutable, string stateRoot, ToolLifetime original)
    {
        var configuration = new TrustedConfiguration(repository, repositoryRoot, mutableReference, gitExecutable);
        return Prepare(configuration, null, stateRoot, original);
    }

    /// <summary>Preserves every selected policy and ceiling, replacing only per-run storage paths.</summary>
    public static AutomaticSetupResult Rebase(TrustedConfiguration template, string stateRoot, ToolLifetime original)
    {
        ArgumentNullException.ThrowIfNull(template);
        original.Check();
        if (template.ProductionBatch is not null || template.HistoricalQuality is not null || template.LocalRealGolden is not null)
            throw new ToolUsageException("Automatic setup supports ordinary single-run templates; batch, historical-quality and fixture selections require their own explicit preparation.");
        var selected = template.StandaloneRoot ?? throw new ToolUsageException("Existing setup template requires its exact standalone root selection.");
        var raw = InstalledHostAdmission.ReadExact(selected, original, 65536);
        _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(raw);
        var root = JsonSerializer.Deserialize<StandaloneRootSelection>(raw, BoundedFiles.Json)
            ?? throw new ToolUsageException("Existing setup root is null.");
        if (root.Kind != "DeliveryForgeStandaloneRoot-v1" || root.WorkerPaths is null || root.InstalledHostAdmission is null)
            throw new ToolUsageException("Existing setup root shape is unsupported.");
        // The selected root path is itself an independently selected control
        // input. Validate the old topology before allocating another one;
        // rebasing must not quietly repair an invalid input selection.
        StandaloneEntrypoint.SeparateSelection(template, selected.Path, root);
        return Prepare(template, root, stateRoot, original);
    }

    static AutomaticSetupResult Prepare(TrustedConfiguration template, StandaloneRootSelection? prior,
        string stateRoot, ToolLifetime original)
    {
        original.Check();
        if (string.IsNullOrWhiteSpace(template.Repository) || template.Repository.Length > 256 ||
            !System.Text.RegularExpressions.Regex.IsMatch(template.Repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            template.Repository.Split('/').Any(part => part is "." or "..") ||
            string.IsNullOrWhiteSpace(template.MutableReference) || !template.MutableReference.StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new ToolUsageException("Setup requires the resolved repository identity and exact branch reference.");
        _ = TrustedConfiguration.Ceiling(template.MaximumCeiling);
        if (template.OperationSeconds is < 1 or > 1200) throw new ToolUsageException("Setup cannot increase the existing operation ceiling.");
        var repository = Path.TrimEndingDirectorySeparator(BoundedFiles.Plain(template.RepositoryRoot, true));
        if (!Directory.Exists(repository)) throw new ToolUsageException("Setup requires an existing resolved repository directory.");
        var git = BoundedFiles.Plain(template.GitExecutable);
        var state = BoundedFiles.Plain(stateRoot, true);
        var pathIdentity = OperatingSystem.IsWindows() ? repository.ToUpperInvariant() : repository;
        var key = BoundedFiles.Sha(Encoding.UTF8.GetBytes(template.Repository + "\n" + pathIdentity));
        var runId = "run-" + Guid.NewGuid().ToString("N");
        var run = BoundedFiles.Plain(Path.Combine(state, "repos", key, "runs", runId), true);
        var control = Path.Combine(run, "control");
        var configurationPath = Path.Combine(control, "configuration.json");
        var rootPath = Path.Combine(control, "root.json");
        var unadmittedPath = Path.Combine(control, "host-unadmitted.json");
        var unadmitted = JsonSerializer.SerializeToUtf8Bytes(new { Kind = "DeliveryForgeHostNotAdmitted-v1", Reason = "PLAN_ONLY", RuntimeCertified = false }, BoundedFiles.Json);
        var worker = prior?.WorkerPaths is { } previousWorker
            ? previousWorker with { Worktree = Path.Combine(run, "worktree"), ResultDirectory = Path.Combine(run, "results") }
            : new LocalWorkerPaths(Path.Combine(run, "worktree"), Path.Combine(run, "results"), [], []);
        var selection = new StandaloneRootSelection("DeliveryForgeStandaloneRoot-v1", Path.Combine(run, "store"), worker,
            prior?.InstalledHostAdmission ?? Pin(unadmittedPath, unadmitted), prior?.LinuxScopeProfile);
        var rootBytes = Encode(selection);
        var rootPin = Pin(rootPath, rootBytes);
        var coordination = template.Coordination is { } existingCoordination
            ? existingCoordination with { CheckpointRoot = Path.Combine(run, "checkpoints") }
            : null;
        var configuration = template with { RepositoryRoot = repository, GitExecutable = git, StandaloneRoot = rootPin, Coordination = coordination };
        var configurationBytes = Encode(configuration);
        // Validate every selected output before creating any directory or file.
        // Existing admission files remain DATA; execution verifies them itself.
        BoundedFiles.Separate(repository, run);
        StandaloneEntrypoint.SeparateSelection(configuration, configurationPath, selection);
        original.Check();
        if (Directory.Exists(run) || File.Exists(run)) throw new IOException("New setup run already exists; no previous run is adopted.");
        Directory.CreateDirectory(control);
        original.Check();
        _ = BoundedFiles.Plain(control, true);
        if (prior is null) BoundedFiles.WriteNew(unadmittedPath, unadmitted, original);
        BoundedFiles.WriteNew(rootPath, rootBytes, original);
        BoundedFiles.WriteNew(configurationPath, configurationBytes, original);
        return new(configurationPath, Pin(configurationPath, configurationBytes), rootPin, key, runId, run, selection);
    }

    static byte[] Encode<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, BoundedFiles.Json);
        if (bytes.Length > 65536) throw new ToolUsageException("Setup selection exceeds the existing configuration/root byte bound.");
        return bytes;
    }
    static AdmissionFile Pin(string path, byte[] bytes) => new(path, bytes.LongLength, BoundedFiles.Sha(bytes));
}
