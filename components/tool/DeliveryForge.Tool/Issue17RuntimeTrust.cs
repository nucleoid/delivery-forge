using DeliveryForge.Evidence;
using DeliveryForge.Tool.Claude;

namespace DeliveryForge.Tool;

// Root-selection DATA. It is not a capability and cannot be passed to a port.
// The accepted containing root below must bind it to its actual original first.
public sealed record Issue17HostInput(string Path, long? Bytes, string? Sha256);
public sealed record Issue17RuntimeTrustSelection(string Kind, AdmissionFile OwnerDecision,
    IReadOnlyList<Issue17HostInput> HostInputs);

// Ordinary complete file/environment binding only. No native or issuer authority.
internal sealed class Issue17HostInputs
{
    readonly ToolLifetime original;
    readonly Issue17HostInput[] inputs;
    readonly KeyValuePair<string, string?>[] environment;
    static StringComparer Paths => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static readonly string[] EnvironmentNames = ["HOME", "USERPROFILE", "CODEX_HOME", "CLAUDE_CONFIG_DIR",
        "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL", "CODEX_APP_SERVER_CHATGPT_BASE_URL", .. ClaudeIndependentReviewData.ProviderOverrides];
    Issue17HostInputs(ToolLifetime original, Issue17HostInput[] inputs, KeyValuePair<string, string?>[] environment)
    { this.original = original; this.inputs = inputs; this.environment = environment; }
    internal static Issue17HostInputs Capture(ToolLifetime original, IReadOnlyList<Issue17HostInput> selected)
    {
        original.Check();
        if (selected is null || selected.Count is < 2 or > 16) throw new ToolUsageException("Finite complete issue17 host-input selection required.");
        var names = new HashSet<string>(Paths); long bytes = 0;
        var copy = new Issue17HostInput[selected.Count];
        for (var i = 0; i < selected.Count; i++)
        {
            original.Check(); var row = selected[i] ?? throw new ToolUsageException("Missing selected host input.");
            if (!Path.IsPathFullyQualified(row.Path) || !names.Add(Path.GetFullPath(row.Path)) ||
                (row.Bytes is null) != (row.Sha256 is null) || row.Bytes is < 1 or > 65536 ||
                row.Sha256 is { } hash && (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c))))
                throw new ToolUsageException("Unique absolute finite host-file presence/absence pins required.");
            bytes += row.Bytes ?? 0; if (bytes > 1048576) throw new ToolUsageException("Selected host inputs exceed their complete finite profile.");
            copy[i] = row with { Path = Path.GetFullPath(row.Path) };
        }
        var env = EnvironmentNames.Select(name => new KeyValuePair<string, string?>(name, Environment.GetEnvironmentVariable(name))).ToArray();
        var result = new Issue17HostInputs(original, copy, env); result.Check(); return result;
    }
    internal bool Contains(string path) => inputs.Any(row => Paths.Equals(row.Path, Path.GetFullPath(path)));
    // Compare the already completed read against this original's immutable
    // selected tuple. This performs no file read and consumes no new credit.
    internal void RequireSelectedBytes(ToolLifetime actual, string path, byte[]? current)
    {
        original.Check();
        if (!ReferenceEquals(original, actual)) throw new ToolUsageException("Selected host input cannot cross original owners.");
        var full = Path.GetFullPath(path);
        var selected = inputs.SingleOrDefault(row => Paths.Equals(row.Path, full));
        if (selected is null || (current is null
            ? selected.Bytes is not null || selected.Sha256 is not null
            : selected.Bytes != current.LongLength || selected.Sha256 != BoundedFiles.Sha(current)))
            throw new ToolUsageException("Actual reviewer settings differ from the independently selected complete input.");
        original.Check();
    }
    internal void RequirePath(string path)
    {
        if (!Contains(path))
            throw new ToolUsageException("Actual host configuration source was not selected by the accepted containing root.");
    }
    internal void Separate(params string[] protectedRoots)
    {
        foreach (var root in protectedRoots) foreach (var row in inputs) BoundedFiles.Separate(root, row.Path);
    }
    internal void Check()
    {
        original.Check();
        try
        {
            foreach (var item in environment)
                if (!string.Equals(Environment.GetEnvironmentVariable(item.Key), item.Value, StringComparison.Ordinal))
                    throw new ToolUsageException("Selected issue17 host environment changed; original admission is revoked.");
            foreach (var row in inputs)
            {
                original.Check(); BoundedFiles.Plain(row.Path);
                FileAttributes? attributes;
                try { attributes = File.GetAttributes(row.Path); }
                catch (FileNotFoundException) { attributes = null; }
                catch (DirectoryNotFoundException) { attributes = null; }
                if (row.Bytes is null)
                {
                    if (attributes is not null) throw new ToolUsageException("An absent selected host input appeared; original admission is revoked.");
                }
                else
                {
                    if (attributes is null || (attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new ToolUsageException("Selected plain host input disappeared or redirected.");
                    _ = InstalledHostAdmission.ReadExact(new(row.Path, row.Bytes.Value, row.Sha256!), original, 65536);
                }
            }
            original.Check();
        }
        catch (Exception primary)
        {
            // Fail sticky under this existing owner; never refresh WORK or retry.
            try { original.BeginFailureCleanup(); }
            catch (Exception cleanup) { throw new AggregateException("Host-input refusal and same-original cleanup admission failure.", primary, cleanup); }
            throw;
        }
    }
}

public sealed partial class InstalledHostAdmission
{
    internal RuntimeTrust? TrustedRuntime { get; private set; }
    // Only the root-accepted containing admission can construct this binding.
    // It grants no review capability, receipt, lease or accepted worker identity.
    internal sealed class RuntimeTrust
    {
        readonly ToolLifetime original;
        readonly TrustedConfiguration configuration;
        readonly AdmissionFile root, configurationPin, ownerDecision;
        readonly Issue17HostInputs inputs;
        readonly StandaloneHostSelection configurationHost;
        readonly BatchWindowsImages? images;
        string? boundWorktree, boundResult; LinuxScopeSelection? boundLinuxScope; int checks;
        static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        // Existing RPC64 + bind/start/wait5 + reviewer entry/VerifyImage7.
        internal const int MaximumChecks = 76;
        RuntimeTrust(ToolLifetime original, TrustedConfiguration configuration, AdmissionFile root,
            AdmissionFile configurationPin, AdmissionFile ownerDecision, Issue17HostInputs inputs, StandaloneHostSelection host, BatchWindowsImages? images)
        { this.original = original; this.configuration = configuration; this.root = root; this.configurationPin = configurationPin; this.ownerDecision = ownerDecision; this.inputs = inputs; configurationHost = host; this.images = images; }
        internal bool Matches(ToolLifetime actual, IndependentClaudeReviewSelection reviewer) =>
            ReferenceEquals(original, actual) && ReferenceEquals(configuration.InstalledReview?.IndependentReviewer, reviewer);
        internal bool MatchesLinuxScope(ToolLifetime actual, IndependentClaudeReviewSelection reviewer, LinuxScopeSelection? scope) =>
            OperatingSystem.IsLinux() && boundWorktree is not null && boundLinuxScope is not null &&
            ReferenceEquals(boundLinuxScope, scope) && Matches(actual, reviewer);
        internal void Check(ToolLifetime actual)
        {
            if (!ReferenceEquals(original, actual)) throw new ToolUsageException("Owner-trusted runtime cannot cross original owners.");
            original.Check();
            try
            {
                if (Interlocked.Increment(ref checks) > MaximumChecks) throw new ToolUsageException("Finite selected runtime-trust check profile exhausted before file reads.");
                Codex.CodexRuntimeRoute.RequireSubscriptionEnvironment(Environment.GetEnvironmentVariable);
                ClaudeIndependentReviewData.RequireSubscriptionEnvironment(Environment.GetEnvironmentVariable);
                inputs.Check();
                _ = ReadExact(root, original, 1048576);
                _ = ReadExact(configurationPin, original, 65536);
                _ = ReadExact(ownerDecision, original, 65536);
                if (boundWorktree is not null) RefuseRepositoryConfiguration(boundWorktree);
                RefuseRepositoryConfiguration(configuration.RepositoryRoot);
            }
            catch (Exception primary)
            {
                try { original.BeginFailureCleanup(); }
                catch (Exception cleanup) { throw new AggregateException("Root trust drift and same-original cleanup admission failure.", primary, cleanup); }
                throw;
            }
        }
        internal void BindWorker(ToolLifetime actual, Codex.CodexAppServerSelection image, string worktree, string resultDirectory, LinuxScopeSelection? scope = null)
        {
            Check(actual);
            if (configuration.Adapter != "codex.cli" || image.Executable != configurationHost.Executable || image.Bytes != configurationHost.Bytes || image.Sha256 != configurationHost.Sha256 || image.Version != configurationHost.Version)
                throw new ToolUsageException("Owner trust is bound to the exact root-selected GPT worker image.");
            if (boundWorktree is not null && (boundWorktree != worktree || boundResult != resultDirectory))
                throw new ToolUsageException("Owner runtime trust cannot be rebound to another worker.");
            if (OperatingSystem.IsLinux() && (scope is null || boundLinuxScope is not null && !ReferenceEquals(boundLinuxScope, scope)))
                throw new ToolUsageException("Linux owner trust requires the same concrete worker scope; no scope adoption or rebind.");
            inputs.Separate(configuration.RepositoryRoot, worktree, resultDirectory);
            // Repository/ancestor Codex configuration is not part of this host-only
            // trust decision. Refuse it rather than silently importing new policy.
            RefuseRepositoryConfiguration(worktree);
            RefuseRepositoryConfiguration(configuration.RepositoryRoot);
            boundWorktree = worktree; boundResult = resultDirectory;
            if (OperatingSystem.IsLinux()) boundLinuxScope = scope;
            Check(actual);
        }
        internal WindowsImageUse RetainWorkerImage(ToolLifetime actual, AdmissionFile pin, CancellationToken token)
        {
            if (!ReferenceEquals(original, actual) || boundWorktree is null || pin.Path != configurationHost.Executable ||
                pin.Bytes != configurationHost.Bytes || pin.Sha256 != configurationHost.Sha256)
                throw new ToolUsageException("Worker image use differs from its node-bound original selection.");
            original.Check(); return images?.Borrow(pin, actual, token) ?? WindowsImageUse.Open(pin, actual, token);
        }
        internal WindowsImageUse RetainReviewerImage(ToolLifetime actual, IndependentClaudeReviewSelection reviewer, CancellationToken token)
        {
            if (!Matches(actual, reviewer)) throw new ToolUsageException("Reviewer image use differs from its node-bound original selection.");
            original.Check(); return images?.Borrow(reviewer.Image, actual, token) ?? WindowsImageUse.Open(reviewer.Image, actual, token);
        }
        internal void RevokeRoute() => original.BeginFailureCleanup();
        internal void CheckReviewer(ToolLifetime actual, IndependentClaudeReviewSelection reviewer)
        {
            if (!Matches(actual, reviewer)) throw new ToolUsageException("Independent reviewer trust differs from its accepted original selection.");
            Check(actual);
        }
        internal void RequireSelectedReviewerSettings(ToolLifetime actual, IndependentClaudeReviewSelection reviewer, string path, byte[]? current)
        {
            original.Check();
            var expected = Path.Combine(ClaudeReviewerUserSettings.ActualDirectory(), "settings.json");
            if (!Matches(actual, reviewer) || !PathComparer.Equals(Path.GetFullPath(path), Path.GetFullPath(expected)))
                throw new ToolUsageException("Reviewer settings must belong to the same selected original and reviewer.");
            inputs.RequireSelectedBytes(actual, path, current);
            original.Check();
        }
        void RefuseRepositoryConfiguration(string directory)
        {
            var current = Path.GetFullPath(directory);
            for (var count = 0; ; count++)
            {
                if (count >= 128) throw new ToolUsageException("Repository configuration ancestry exceeds the finite host-only profile.");
                var candidate = Path.Combine(current, ".codex", "config.toml"); BoundedFiles.Plain(candidate);
                try { _ = File.GetAttributes(candidate); if (!inputs.Contains(candidate)) throw new ToolUsageException("Repository/ancestor Codex configuration is outside the selected host-only trust profile."); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                var parent = Path.GetDirectoryName(current); if (parent is null) return; current = parent;
            }
        }
        // Refusal-only preflight over selected DATA. It creates no runtime trust.
        internal static void RequireSelectedProfile(InstalledHostRootAcceptance accepted, TrustedConfiguration config, string platform)
        {
            if (accepted.RuntimeTrust is null) return;
            if (platform is not ("windows" or "linux") || accepted.Platform != platform || accepted.Adapter != "codex.cli" || config.Adapter != "codex.cli" ||
                accepted.RuntimeTrust.Kind != "Issue17OwnerTrustedPersonalHost-v1" || config.InstalledReview?.IndependentReviewer is null)
                throw new ToolUsageException("The scoped issue17 trust profile requires the selected Windows/Linux GPT worker and independent Claude reviewer.");
        }
        internal readonly record struct ReadRequirement(int Operations, long Bytes);
        internal static ReadRequirement ReadCapacity(InstalledHostRootAcceptance accepted, AdmissionFile rootPin, AdmissionFile configPin, TrustedConfiguration config)
        {
            RequireSelectedProfile(accepted, config, accepted.Platform);
            if (accepted.RuntimeTrust is not { } trust || config.InstalledReview?.IndependentReviewer is null)
                throw new ToolUsageException("Exact selected runtime-trust reviewer profile required for read admission.");
            if (trust.OwnerDecision is null || accepted.ToolAssembly is null || accepted.Host is null || trust.HostInputs is null || trust.HostInputs.Count is < 2 or > 16 || accepted.Artifacts is null || accepted.Artifacts.Count is < 1 or > 32)
                throw new ToolUsageException("Finite complete runtime-trust read selection required.");
            var operations = 0; var bytes = 0L;
            void Read(long length, long maximum)
            {
                if (length is <= 0 || length > maximum) throw new ToolUsageException("Invalid declared runtime-trust input extent.");
                operations = checked(operations + 2); bytes = checked(bytes + length + 1);
            }
            var selectedBytes = 0L;
            foreach (var row in trust.HostInputs)
            {
                if (row is null || (row.Bytes is null) != (row.Sha256 is null) || row.Bytes is < 1 or > 65536)
                    throw new ToolUsageException("Invalid selected host-file extent.");
                selectedBytes = checked(selectedBytes + (row.Bytes ?? 0));
            }
            if (selectedBytes > 1048576) throw new ToolUsageException("Selected host inputs exceed their complete finite profile.");
            // Selected trust-read subset only: future root/image/input admission,
            // bounded trust rechecks and these settings/config reads. This does
            // not pre-admit the full reviewer/pipeline or its Git/receipt effects.
            Read(rootPin.Bytes, 1048576); Read(accepted.ToolAssembly.Bytes, 16777216);
            foreach (var artifact in accepted.Artifacts)
            {
                if (artifact is null) throw new ToolUsageException("Missing declared conformance artifact.");
                Read(artifact.Bytes, 1048576);
            }
            Read(trust.OwnerDecision.Bytes, 65536); Read(configPin.Bytes, 65536); Read(configPin.Bytes, 65536);
            foreach (var row in trust.HostInputs) if (row.Bytes is long present) Read(present, 65536);
            for (var check = 0; check < MaximumChecks; check++)
            {
                Read(rootPin.Bytes, 1048576); Read(configPin.Bytes, 65536); Read(trust.OwnerDecision.Bytes, 65536);
                foreach (var row in trust.HostInputs) if (row.Bytes is long present) Read(present, 65536);
            }
            // Six VerifyImage checks plus the original settings capture.
            for (var settings = 0; settings < 7; settings++) Read(65536, 65536);
            // The reviewer directory binding and three protected-config callbacks.
            for (var configuration = 0; configuration < 4; configuration++) Read(configPin.Bytes, 65536);
            var imageReads = ImageReadCapacity(accepted, config);
            operations = checked(operations + imageReads.Operations); bytes = checked(bytes + imageReads.Bytes);
            operations = checked(operations + 4); // bounded worker/reviewer trace writes
            return new(operations, bytes);
        }
        internal static ReadRequirement ImageReadCapacity(InstalledHostRootAcceptance accepted, TrustedConfiguration config)
        {
            RequireSelectedProfile(accepted, config, accepted.Platform);
            var worker = accepted.Host.Bytes; var reviewer = config.InstalledReview!.IndependentReviewer!.Image.Bytes;
            if (worker is <= 0 or > BoundedFiles.MaximumNativeImageBytes || reviewer is <= 0 or > BoundedFiles.MaximumNativeImageBytes)
                throw new ToolUsageException("Invalid declared runtime-trust input extent.");
            if (accepted.Platform == "linux")
            {
                if (worker < 64 || worker > OwnedDuplexProcess.MaximumExactLinuxImageBytes ||
                    reviewer < 64 || reviewer > OwnedDuplexProcess.MaximumExactLinuxImageBytes)
                    throw new ToolUsageException("Selected Linux trust images exceed the existing exact-image kernel profile.");
                // Two worker pathname hashes + one gate copy + one sealed-fd hash;
                // six reviewer pathname hashes + three copies + three sealed hashes.
                // These remain real cumulative debits, not a cache or new bucket.
                return new(20, checked(4 * (worker + 1) + 12 * (reviewer + 1)));
            }
            return new(6, checked(2 * (worker + 1) + reviewer + 1));
        }
        internal static void RequireReadCapacity(InstalledHostRootAcceptance accepted, AdmissionFile rootPin, AdmissionFile configPin,
            TrustedConfiguration config, ToolLifetime original, BatchWindowsImages? images = null)
        {
            original.Check();
            if (accepted.Platform == "linux" && images is not null) throw new ToolUsageException("Windows held-image borrowing cannot admit a Linux original.");
            var plan = ReadCapacity(accepted, rootPin, configPin, config);
            if (images is null) BoundedFiles.RequireReadCapacity(original, plan.Operations, plan.Bytes);
            else images.RequireNodeCapacity(original, new(accepted.Host.Executable, accepted.Host.Bytes, accepted.Host.Sha256),
                config.InstalledReview!.IndependentReviewer!.Image, plan, ImageReadCapacity(accepted, config));
        }
        internal static RuntimeTrust Bind(InstalledHostAdmission containing, AdmissionFile configPin, BatchWindowsImages? images = null)
        {
            // This concrete object has no public constructor. Its root/config/
            // original fields are set only after all existing conformance cases.
            var accepted = containing.actual; var root = containing.actualRoot;
            var config = containing.actualConfiguration; var original = containing.actualOriginal;
            var selected = accepted.RuntimeTrust ?? throw new ToolUsageException("No explicit issue17 owner trust selection.");
            RequireSelectedProfile(accepted, config, OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported");
            if (OperatingSystem.IsLinux() && images is not null || selected.Kind != "Issue17OwnerTrustedPersonalHost-v1" || config.Adapter != "codex.cli" ||
                config.InstalledReview?.IndependentReviewer is not { } reviewer)
                throw new ToolUsageException("The scoped issue17 trust profile requires the selected Windows/Linux GPT worker and independent Claude reviewer.");
            _ = ReadExact(selected.OwnerDecision, original, 65536); _ = ReadExact(configPin, original, 65536);
            var parsed = TrustedConfiguration.LoadSelected(configPin.Path, original, configPin);
            if (!System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(config, BoundedFiles.Json).AsSpan().SequenceEqual(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(parsed, BoundedFiles.Json)))
                throw new ToolUsageException("Actual configuration object differs from its complete selected bytes.");
            var inputs = Issue17HostInputs.Capture(original, selected.HostInputs);
            var explicitHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (explicitHome is null)
                foreach (var name in new[] { "HOME", "USERPROFILE" })
                    if (Environment.GetEnvironmentVariable(name) is { } inherited && (!Path.IsPathFullyQualified(inherited) ||
                        !PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(inherited)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(user)))))
                        throw new ToolUsageException("Ambiguous Codex home selection is outside the owner-trusted profile.");
            var home = explicitHome ?? Path.Combine(user, ".codex");
            if (!Path.IsPathFullyQualified(home)) throw new ToolUsageException("Absolute selected Codex home required; no config override is introduced.");
            inputs.RequirePath(Path.Combine(home, "config.toml"));
            inputs.RequirePath(Path.Combine(ClaudeReviewerUserSettings.ActualDirectory(), "settings.json"));
            inputs.Separate(config.RepositoryRoot, config.InstalledReview.ArtifactRoot);
            foreach (var pin in new[] { root, configPin, selected.OwnerDecision })
                foreach (var protectedRoot in new[] { config.RepositoryRoot, config.InstalledReview.ArtifactRoot }) BoundedFiles.Separate(protectedRoot, pin.Path);
            var result = new RuntimeTrust(original, config, root, configPin, selected.OwnerDecision, inputs, accepted.Host, images);
            result.CheckReviewer(original, reviewer); return result;
        }
    }
}
