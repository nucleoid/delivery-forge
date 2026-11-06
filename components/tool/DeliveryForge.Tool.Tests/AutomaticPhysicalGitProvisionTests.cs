using System.Text;
using DeliveryForge.Evidence;
using DeliveryForge.Execution.Git;

namespace DeliveryForge.Tool.Tests;

// Local owned Git reproduction only: no model, Continuum, production owner,
// accepted worker, runtime trust, quality, publication or grant is constructed.
public sealed class AutomaticPhysicalGitProvisionTests
{
    public static bool Windows => OperatingSystem.IsWindows();

    [Fact(Timeout = 120000, SkipUnless = nameof(Windows), Skip = "Actual Windows creation-owned Git and packaged path selection required.")]
    public async Task Actual_default_state_anchor_provisions_a_fresh_git_worktree_and_observes_clean_base()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        using var original = new ToolLifetime(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), "forge-auto-physical-git-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "parent");
        TestContext.Current.TestOutputHelper!.WriteLine("Retained local Git reproduction: " + root);
        Directory.CreateDirectory(repository);
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, "git.exe")).First(File.Exists);
        var executor = new ProcessCommandExecutor();
        var current = true;
        // TEST-ONLY local admission: exercises the same original Git plumbing,
        // but deliberately has no remote lease/dispatch-authority interpretation.
        var parent = new ParentEffectAdmission(new object(), run => current && run == "local-repro", _ => current = false, original: original);
        async Task<string> Git(params string[] arguments)
        {
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(row => !((string)row.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(row => (string)row.Key, row => row.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_CONFIG_GLOBAL"] = "NUL";
            environment["GIT_CONFIG_SYSTEM"] = "NUL"; environment["GIT_TERMINAL_PROMPT"] = "0";
            var actual = parent.EnterAsync("local-repro", () => executor.ExecuteBoundedCaptureAsync(
                new(git, ["-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false", "-c", "commit.gpgSign=false", .. arguments], repository, environment)
                { EnvironmentIsComplete = true }, TimeSpan.FromSeconds(15), 1048576, original.Token, new OwnedWorkPhaseBudget(original)));
            var capture = await actual.WaitAsync(original.Remaining, original.Token);
            Assert.True(capture.Command.ExitCode == 0, "Local fixture Git failed: " + Encoding.UTF8.GetString(capture.StandardErrorBytes));
            Assert.False(capture.Command.TimedOut); Assert.False(capture.Command.Cancelled); Assert.False(capture.CaptureBoundExceeded);
            Assert.True(capture.Command.OwnedProcessQuiescent); Assert.True(capture.RootExitObserved);
            Assert.True(capture.StandardOutputDrainCompleted); Assert.True(capture.StandardErrorDrainCompleted);
            Assert.True(capture.Command.LifecycleDiagnostics?.OwnedTree is { CreationOwned: true, SettlementObserved: true, ActiveProcesses: 0 });
            return Encoding.UTF8.GetString(capture.StandardOutputBytes).Trim();
        }
        try
        {
            await Git("init", "-q", "-b", "main");
            Directory.CreateDirectory(Path.Combine(repository, "quality"));
            File.WriteAllText(Path.Combine(repository, "control.txt"), "original local Git fixture\n");
            File.WriteAllText(Path.Combine(repository, "quality", "review-policy.json"), "{\"fixture\":\"review-only-DATA\"}\n");
            File.WriteAllText(Path.Combine(repository, "quality", "evidence-policy.json"), "{\"fixture\":\"evidence-only-DATA\"}\n");
            await Git("add", "--", "control.txt", "quality/review-policy.json", "quality/evidence-policy.json");
            await Git("-c", "user.name=LocalGitReproduction", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", "original local fixture");
            var head = await Git("rev-parse", "HEAD");
            var selected = AutomaticSetup.PreparePlan("fixture/local-git-provision", repository, "refs/heads/main", git,
                AutomaticSetup.DefaultStateRoot(), original);
            TestContext.Current.TestOutputHelper!.WriteLine("Retained selected physical run: " + selected.RunDirectory);
            Assert.Equal(WorktreeManager.WindowsFinalPath(selected.RunDirectory), selected.RunDirectory);
            var state = WorktreeManager.WindowsFinalPath(AutomaticSetup.DefaultStateRoot());
            Assert.Equal(Path.Combine(state, "worktrees", selected.RunId, "worktree"), selected.Selection.WorkerPaths.Worktree);
            Assert.True(Encoding.UTF8.GetByteCount(Path.Combine(selected.Selection.WorkerPaths.Worktree, ".git")) <= 220);
            Assert.False(Directory.Exists(selected.Selection.WorkerPaths.Worktree));
            var manager = new WorktreeManager(git, executor, original).WithParentAdmission(parent, "local-repro");
            await manager.PrepareAsync(repository, selected.Selection.WorkerPaths.Worktree, head, original.Token);
            var observed = await manager.ObserveAsync(selected.Selection.WorkerPaths.Worktree, original.Token);
            Assert.True(observed.IsClean); Assert.Equal(head, observed.HeadCommit);
            Assert.Equal("original local Git fixture\n", File.ReadAllText(Path.Combine(selected.Selection.WorkerPaths.Worktree, "control.txt")));
            Assert.True(File.Exists(Path.Combine(selected.Selection.WorkerPaths.Worktree, "quality", "review-policy.json")));
            Assert.True(File.Exists(Path.Combine(selected.Selection.WorkerPaths.Worktree, "quality", "evidence-policy.json")));
            Assert.False(Directory.Exists(selected.Selection.WorkerPaths.ResultDirectory));
            Assert.False(original.HasExplicitUnknown);
            await Task.WhenAll(parent.OriginalObservers).WaitAsync(original.Remaining, original.Token);
            Assert.True(parent.AllKnownSettled);
        }
        catch (Exception error)
        {
            TestContext.Current.TestOutputHelper!.WriteLine(error.ToString());
            throw; // Preserve the actual exception type/message/stack for triage.
        }
        // Preserve every fixture/partial worktree on success and failure.
    }
}
