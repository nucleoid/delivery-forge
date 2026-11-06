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
            if (reference is null)
            {
                // Pure setup with an explicit branch works on either platform.
                // Automatic branch discovery uses the existing actual Windows
                // owner. Linux requires an independently selected scope template.
                if (!OperatingSystem.IsWindows()) return ToolResult.Incomplete("SETUP_REFERENCE_REQUIRED",
                    "Select the current exact branch for pure setup, or reuse the installed Linux scope template; no unscoped Git process was launched.");
                var capture = await Observe("ActualSetupBranch", () => new ProcessCommandExecutor().ExecuteBoundedCaptureAsync(
                    new(git, ["--no-optional-locks", "-C", repo, "symbolic-ref", "--quiet", "HEAD"], repo),
                    life.Remaining < TimeSpan.FromSeconds(30) ? life.Remaining : TimeSpan.FromSeconds(30), 8192, life.Token, life), life).ConfigureAwait(false);
                if (!capture.RootExitObserved || !capture.StandardOutputDrainCompleted || !capture.StandardErrorDrainCompleted || !capture.Command.OwnedProcessQuiescent)
                { life.RetainUnknown(); throw new IOException("Actual setup Git process or I/O remains unresolved."); }
                if (capture.Command.ExitCode != 0 || capture.CaptureBoundExceeded || capture.Command.TimedOut || capture.Command.Cancelled)
                    return ToolResult.Incomplete("SETUP_BRANCH_REQUIRED", "Current checkout must have an exact branch reference; no default branch was guessed.");
                reference = new UTF8Encoding(false, true).GetString(capture.StandardOutputBytes).TrimEnd('\r', '\n');
            }
            selected = AutomaticSetup.PreparePlan(repository, repo, reference, git, state, life);
        }
        life.Check();
        return new("COMPLETE", "SETUP_PREPARED", "Fresh isolated paths and pinned configuration prepared; no worker, lease or runtime capability was created.", selected);
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
