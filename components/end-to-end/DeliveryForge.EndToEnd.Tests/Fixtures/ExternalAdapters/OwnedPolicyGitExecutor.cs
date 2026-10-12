using DeliveryForge.Evidence;

namespace DeliveryForge.EndToEnd.Tests;

// Real command executor restricted to the owned Git fixture. No tool output or exit is fabricated.
internal sealed class OwnedPolicyGitExecutor(OwnedScenarioRun run) : ICommandExecutor
{
    private readonly ProcessCommandExecutor _actual = new();
    private readonly string _git = FindGit();

    public async Task<CommandResult> ExecuteAsync(CommandInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (invocation.FileName != "git" || Path.GetFullPath(invocation.WorkingDirectory) != Path.GetFullPath(run.Checkout))
            throw new InvalidOperationException("Policy boundary may invoke only actual Git in this owned repository.");
        var gitDirectory = Path.Combine(run.Checkout, ".git");
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        var environment = new Dictionary<string, string>
        {
            ["GIT_DIR"] = gitDirectory,
            ["GIT_WORK_TREE"] = run.Checkout,
            ["GIT_COMMON_DIR"] = gitDirectory,
            ["GIT_OBJECT_DIRECTORY"] = Path.Combine(gitDirectory, "objects"),
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = "",
            ["GIT_INDEX_FILE"] = Path.Combine(gitDirectory, "index"),
            ["GIT_CONFIG_COUNT"] = "0",
            ["GIT_CONFIG_PARAMETERS"] = "",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_SYSTEM"] = nullDevice,
            ["GIT_CONFIG_GLOBAL"] = nullDevice,
            ["GIT_CONFIG"] = Path.Combine(gitDirectory, "config"),
            ["GIT_NO_REPLACE_OBJECTS"] = "1",
            ["GIT_GRAFT_FILE"] = nullDevice,
            ["GIT_NO_LAZY_FETCH"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0"
        };
        var actual = new CommandInvocation(_git,
            ["-c", "core.hooksPath=" + nullDevice, "-c", "core.fsmonitor=false", .. invocation.Arguments], run.Checkout, environment);
        var result = await _actual.ExecuteAsync(actual, timeout < TimeSpan.FromSeconds(15) ? timeout : TimeSpan.FromSeconds(15), cancellationToken);
        await run.WriteNewJsonAsync("policy-git-command-" + Guid.NewGuid().ToString("N") + ".json", new { fixture = true, actual, result }, CancellationToken.None);
        if (!result.OwnedProcessQuiescent)
        {
            run.RetainArtifacts("Actual protected-policy Git command remains physically uncertain.");
            throw new InvalidOperationException("Actual policy Git process did not settle; evidence is nonpass.");
        }
        return result;
    }

    private static string FindGit()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Actual absolute Git fixture executable is unavailable.");
    }
}
