using System.Text;
using System.Text.Json;

namespace DeliveryForge.Tool;

public sealed record AutomaticSetupResult(string ConfigurationPath, AdmissionFile Configuration, AdmissionFile Root,
    string RepositoryKey, string RunId, string RunDirectory, StandaloneRootSelection Selection)
{
    // Setup produces selections, never an installed-host acceptance or owner.
    public bool RuntimeCertified => false;
    public string RepositoryIdentitySource { get; init; } = "ExplicitSelectionUnverified";
    public AdmissionFile? ProfileRegistration { get; init; }
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
        var selected = template?.StandaloneRoot ?? throw new ToolUsageException("Existing setup template requires its exact standalone root selection.");
        return Prepare(template, ReadOrdinaryTemplateRoot(template, selected.Path, original), stateRoot, original);
    }

    // The actual parent plan-selection helper may narrow a selected profile,
    // never substitute another root or add writable paths to its allowlist.
    internal static AutomaticSetupResult RebaseSelected(TrustedConfiguration template, StandaloneRootSelection selectedRoot, string stateRoot, ToolLifetime original)
    {
        var pin = template.StandaloneRoot ?? throw new ToolUsageException("Exact original setup root selection required.");
        var prior = ReadOrdinaryTemplateRoot(template, pin.Path, original);
        if (selectedRoot?.WorkerPaths?.AllowedPaths is not { Count: > 0 and <= 128 } allowed ||
            prior.WorkerPaths.AllowedPaths is not { Count: > 0 and <= 128 } permitted ||
            selectedRoot.WorkerPaths.Exclusions is null || prior.WorkerPaths.Exclusions is null)
            throw new ToolUsageException("Selected plan setup requires an existing finite owner allowlist and exclusion protection.");
        var trusted = permitted.Select(NormalizeSetupScope).ToArray();
        var narrowed = allowed.Select(NormalizeSetupScope).ToArray();
        var exclusions = PlanSetupSelection.ConstrainExclusions(selectedRoot.WorkerPaths.Exclusions, prior.WorkerPaths.Exclusions);
        var excluded = exclusions.Select(NormalizeSetupScope).ToArray();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Within(string path, string prefix) => string.Equals(path, prefix, comparison) || path.StartsWith(prefix + "/", comparison);
        if (narrowed.Distinct(StringComparer.Ordinal).Count() != narrowed.Length ||
            OperatingSystem.IsWindows() && narrowed.Distinct(StringComparer.OrdinalIgnoreCase).Count() != narrowed.Length ||
            narrowed.Any(path => !trusted.Any(prefix => Within(path, prefix)) || excluded.Any(prefix => Within(path, prefix) || Within(prefix, path))))
            throw new ToolUsageException("Selected plan setup cannot widen the registered worker scope or overlap its exclusions.");
        var unchanged = selectedRoot with
        {
            WorkerPaths = selectedRoot.WorkerPaths with
            {
                AllowedPaths = prior.WorkerPaths.AllowedPaths,
                Exclusions = prior.WorkerPaths.Exclusions
            }
        };
        if (!JsonSerializer.SerializeToUtf8Bytes(unchanged, BoundedFiles.Json).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(prior, BoundedFiles.Json)))
            throw new ToolUsageException("Selected plan setup cannot replace other registered root selections.");
        original.Check();
        return Prepare(template, selectedRoot, stateRoot, original);
    }

    static string NormalizeSetupScope(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path != path.Trim())
            throw new ToolUsageException("Finite normalized repository-relative profile paths required.");
        var normalized = path.Replace('\\', '/');
        if (normalized.Any(character => char.IsControl(character) || character is ':' or '*' or '?' or '[' or ']' or '<' or '>' or '\"' or '|') ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new ToolUsageException("Profile path prefixes cannot contain traversal, globs or absolute/ambiguous components.");
        PlanSetupSelection.RefuseWindowsAliases(normalized);
        return normalized;
    }

    internal static StandaloneRootSelection ReadOrdinaryTemplateRoot(TrustedConfiguration template, string configurationPath, ToolLifetime original)
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
        StandaloneEntrypoint.SeparateSelection(template, configurationPath, root);
        return root;
    }

    static AutomaticSetupResult Prepare(TrustedConfiguration template, StandaloneRootSelection? prior,
        string stateRoot, ToolLifetime original)
    {
        original.Check();
        if (string.IsNullOrWhiteSpace(template.Repository) || template.Repository.Length > 256 ||
            !System.Text.RegularExpressions.Regex.IsMatch(template.Repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            template.Repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ||
            template.Repository.Split('/').Any(part => part is "." or "..") ||
            string.IsNullOrWhiteSpace(template.MutableReference) || !template.MutableReference.StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new ToolUsageException("Setup requires the resolved repository identity and exact branch reference.");
        _ = TrustedConfiguration.Ceiling(template.MaximumCeiling);
        if (template.OperationSeconds is < 1 or > 1200) throw new ToolUsageException("Setup cannot increase the existing operation ceiling.");
        var repository = Path.TrimEndingDirectorySeparator(BoundedFiles.Plain(template.RepositoryRoot, true));
        if (!Directory.Exists(repository)) throw new ToolUsageException("Setup requires an existing resolved repository directory.");
        var git = BoundedFiles.Plain(template.GitExecutable);
        var state = BoundedFiles.Plain(stateRoot, true);
        var key = RepositoryKey(template.Repository, repository);
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
        if (OperatingSystem.IsWindows())
        {
            // Packaged Windows may virtualize only the newly created child,
            // even when its existing LocalAppData ancestor resolves unchanged.
            // Retain this same fresh anchor; select its actual physical spelling
            // before emitting any root/configuration pins or checkpoint paths.
            run = BoundedFiles.Plain(DeliveryForge.Execution.Git.WorktreeManager.WindowsFinalPath(run), true);
            control = BoundedFiles.Plain(Path.Combine(run, "control"), true);
            configurationPath = Path.Combine(control, "configuration.json");
            rootPath = Path.Combine(control, "root.json");
            unadmittedPath = Path.Combine(control, "host-unadmitted.json");
            worker = worker with { Worktree = Path.Combine(run, "worktree"), ResultDirectory = Path.Combine(run, "results") };
            selection = selection with
            {
                StoreRoot = Path.Combine(run, "store"), WorkerPaths = worker,
                InstalledHostAdmission = prior?.InstalledHostAdmission ?? Pin(unadmittedPath, unadmitted)
            };
            rootBytes = Encode(selection); rootPin = Pin(rootPath, rootBytes);
            if (coordination is not null) coordination = coordination with { CheckpointRoot = Path.Combine(run, "checkpoints") };
            configuration = configuration with { StandaloneRoot = rootPin, Coordination = coordination };
            configurationBytes = Encode(configuration);
            // Recheck actual topology before any DATA file/checkpoint creation.
            BoundedFiles.Separate(repository, run);
            StandaloneEntrypoint.SeparateSelection(configuration, configurationPath, selection);
            original.Check();
        }
        if (coordination is not null)
        {
            // The existing parent requires a prepared empty checkpoint
            // directory. Only this fresh run receives one; never adopt or
            // clear the previous template's checkpoint namespace.
            var checkpoints = BoundedFiles.Plain(coordination.CheckpointRoot, true);
            if (Directory.Exists(checkpoints) || File.Exists(checkpoints))
                throw new IOException("New checkpoint directory already exists; no previous state is adopted.");
            original.Check();
            Directory.CreateDirectory(checkpoints);
            original.Check();
            _ = BoundedFiles.Plain(checkpoints, true);
            if (Directory.EnumerateFileSystemEntries(checkpoints).Any())
                throw new IOException("New checkpoint directory must be empty before setup returns.");
            original.Check();
        }
        if (prior is null) BoundedFiles.WriteNew(unadmittedPath, unadmitted, original);
        BoundedFiles.WriteNew(rootPath, rootBytes, original);
        BoundedFiles.WriteNew(configurationPath, configurationBytes, original);
        return new(configurationPath, Pin(configurationPath, configurationBytes), rootPin, key, runId, run, selection);
    }

    internal static string RepositoryKey(string repositoryIdentity, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryIdentity) || repositoryIdentity.Length > 256 ||
            !System.Text.RegularExpressions.Regex.IsMatch(repositoryIdentity, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            repositoryIdentity.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ||
            repositoryIdentity.Split('/').Any(part => part is "." or ".."))
            throw new ToolUsageException("Setup requires the resolved repository identity.");
        // GitHub identity case and supported local checkout aliases must select
        // one registration key. Keep old experimental keys/attempts untouched.
        if (OperatingSystem.IsWindows() && repositoryRoot.StartsWith(@"\\", StringComparison.Ordinal) &&
            !repositoryRoot.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new ToolUsageException("Automatic repository profiles require a local Windows drive checkout; UNC paths are unsupported.");
        var repository = Path.TrimEndingDirectorySeparator(BoundedFiles.Plain(repositoryRoot, true));
        if (!Directory.Exists(repository)) throw new ToolUsageException("Repository key requires an existing checkout directory.");
        if (OperatingSystem.IsWindows())
        {
            repository = Path.TrimEndingDirectorySeparator(BoundedFiles.Plain(
                DeliveryForge.Execution.Git.WorktreeManager.WindowsFinalPath(repository), true));
            if (repository.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ToolUsageException("Automatic repository profiles require a local Windows drive checkout; UNC paths are unsupported.");
        }
        var pathIdentity = OperatingSystem.IsWindows() ? repository.ToUpperInvariant() : repository;
        return BoundedFiles.Sha(Encoding.UTF8.GetBytes(repositoryIdentity.ToUpperInvariant() + "\n" + pathIdentity));
    }

    static byte[] Encode<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, BoundedFiles.Json);
        if (bytes.Length > 65536) throw new ToolUsageException("Setup selection exceeds the existing configuration/root byte bound.");
        return bytes;
    }
    static AdmissionFile Pin(string path, byte[] bytes) => new(path, bytes.LongLength, BoundedFiles.Sha(bytes));
}
