using System.Text;
using DeliveryForge.Evidence;
namespace DeliveryForge.Tool;

public sealed partial class ToolApplication
{
    async Task<ToolResult> SetupAsync(ToolCommand command, CancellationToken token)
    {
        if (command.Has("dry-run") || command.Has("config") && (command.Has("repo") || command.Has("repository") || command.Has("git") || command.Has("reference")) ||
            !command.Has("config") && (string.IsNullOrWhiteSpace(command.Get("repo")) || string.IsNullOrWhiteSpace(command.Get("repository"))))
            throw new ToolUsageException("setup requires either repository selection or an existing template and never creates files during dry-run.");
        var life = actualOriginal ?? new ToolLifetime(TimeSpan.FromSeconds(120), token);
        using var local = actualOriginal is null ? life : null;
        life.Check();
        var state = command.Get("state-root") ?? AutomaticSetup.DefaultStateRoot();
        AutomaticSetupResult selected;
        if (command.Get("config") is string templatePath)
        {
            var template = TrustedConfiguration.LoadSelected(templatePath, life, actualConfigurationPin);
            if (TimeSpan.FromSeconds(template.OperationSeconds) < life.WholeLimit) life.Restrict(TimeSpan.FromSeconds(template.OperationSeconds));
            selected = AutomaticSetup.Rebase(template, state, life);
        }
        else
        {
            var repo = BoundedFiles.Plain(command.Get("repo") ?? throw new ToolUsageException("Setup repository required."), true);
            var repository = command.Get("repository") ?? throw new ToolUsageException("Setup requires resolved repository identity.");
            var git = ResolveSetupGit(command.Get("git"));
            var reference = command.Get("reference");
            var repositoryIdentitySource = "ExplicitSelectionUnverified";
            if (reference is null)
            {
                // Pure setup with an explicit branch works on either platform.
                // Automatic branch discovery uses the existing actual Windows
                // owner. Linux requires an independently selected scope template.
                if (!OperatingSystem.IsWindows()) return ToolResult.Incomplete("SETUP_REFERENCE_REQUIRED",
                    "Select the current exact branch for pure setup, or reuse the installed Linux scope template; no unscoped Git process was launched.");
                var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                    .Where(row => row.Key is string key && !key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(row => (string)row.Key, row => (string)row.Value!, StringComparer.OrdinalIgnoreCase);
                environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = "NUL";
                environment["GIT_TERMINAL_PROMPT"] = "0"; environment["GIT_OPTIONAL_LOCKS"] = "0";
                var executor = new ProcessCommandExecutor();
                var budget = new OwnedWorkPhaseBudget(life);
                async Task<BoundedCommandCaptureResult> Capture(string kind, params string[] arguments)
                {
                    var observed = await Observe(kind, () => executor.ExecuteBoundedCaptureAsync(
                        new(git, new[] { "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "protocol.allow=never", "-C", repo }.Concat(arguments).ToArray(), repo, environment) { EnvironmentIsComplete = true },
                        life.Remaining < TimeSpan.FromSeconds(30) ? life.Remaining : TimeSpan.FromSeconds(30), 8192, life.Token, budget), life).ConfigureAwait(false);
                    if (!observed.RootExitObserved || !observed.StandardOutputDrainCompleted || !observed.StandardErrorDrainCompleted || !observed.Command.OwnedProcessQuiescent)
                    { life.RetainUnknown(); throw new IOException("Actual setup Git process or I/O remains unresolved."); }
                    return observed;
                }
                var capture = await Capture("ActualSetupBranch", "symbolic-ref", "--quiet", "HEAD").ConfigureAwait(false);
                if (capture.Command.ExitCode != 0 || capture.CaptureBoundExceeded || capture.Command.TimedOut || capture.Command.Cancelled)
                    return ToolResult.Incomplete("SETUP_BRANCH_REQUIRED", "Current checkout must have an exact branch reference; no default branch was guessed.");
                reference = new UTF8Encoding(false, true).GetString(capture.StandardOutputBytes).TrimEnd('\r', '\n');
                var origin = await Capture("ActualSetupOrigin", "remote", "get-url", "origin").ConfigureAwait(false);
                if (origin.Command.ExitCode != 0 || origin.CaptureBoundExceeded || origin.Command.TimedOut || origin.Command.Cancelled)
                    return ToolResult.Incomplete("SETUP_REPOSITORY_IDENTITY_REQUIRED", "Current checkout origin must identify the requested GitHub repository; no setup files were created.");
                string remote;
                try { remote = new UTF8Encoding(false, true).GetString(origin.StandardOutputBytes).TrimEnd('\r', '\n'); }
                catch (DecoderFallbackException)
                { return ToolResult.Incomplete("SETUP_REPOSITORY_IDENTITY_REQUIRED", "Current checkout origin must identify the requested GitHub repository; no setup files were created."); }
                if (!TryGitHubRepository(remote, out var actualRepository))
                    return ToolResult.Incomplete("SETUP_REPOSITORY_IDENTITY_REQUIRED", "Current checkout origin must identify the requested GitHub repository; no setup files were created.");
                if (!string.Equals(actualRepository, repository, StringComparison.OrdinalIgnoreCase))
                    return ToolResult.Incomplete("SETUP_REPOSITORY_MISMATCH", "Requested GitHub repository differs from the current checkout origin; no setup files were created.");
                repositoryIdentitySource = "CheckoutOriginVerified";
            }
            selected = AutomaticSetup.PreparePlan(repository, repo, reference, git, state, life) with { RepositoryIdentitySource = repositoryIdentitySource };
        }
        life.Check();
        return new("COMPLETE", "SETUP_PREPARED", "Fresh isolated paths and pinned configuration prepared; no worker, lease or runtime capability was created.", selected);
    }

    // Projection only: no URL is exported in setup output, including URLs that
    // contain a username or credential. Unsupported remotes remain unverified.
    internal static bool TryGitHubRepository(string remote, out string repository)
    {
        repository = string.Empty;
        if (string.IsNullOrEmpty(remote) || remote.Length > 8192 || remote.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || remote.Contains('%') || remote.Contains('\\') || remote.Split('/').Any(part => part is "." or "..")) return false;
        string path;
        const string scpPrefix = "git@github.com:";
        if (remote.StartsWith(scpPrefix, StringComparison.OrdinalIgnoreCase)) path = remote[scpPrefix.Length..];
        else
        {
            if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !(uri.Scheme == "https" || uri.Scheme == "ssh" && uri.UserInfo == "git")) return false;
            path = uri.AbsolutePath.StartsWith('/') ? uri.AbsolutePath[1..] : uri.AbsolutePath;
        }
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        if (!System.Text.RegularExpressions.Regex.IsMatch(path, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            path.Split('/').Any(part => part is "." or "..")) return false;
        repository = path;
        return true;
    }

    internal static string ResolveSetupGit(string? selected)
    {
        if (selected is not null)
        {
            var exact = BoundedFiles.Plain(selected);
            if (!File.Exists(exact)) throw new ToolUsageException("Selected Git executable is absent.");
            return exact;
        }
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = raw.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return BoundedFiles.Plain(candidate);
        }
        throw new ToolUsageException("Git is unavailable in absolute PATH entries; no alternate executable was guessed.");
    }
}
