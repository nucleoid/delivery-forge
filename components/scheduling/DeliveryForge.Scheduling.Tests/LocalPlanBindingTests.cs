using DeliveryForge.Evidence;
using DeliveryForge.Planning;
using Xunit;

namespace DeliveryForge.Scheduling.Tests;

internal sealed class PlanningRepository : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "delivery-forge-local-scheduling-" + Guid.NewGuid().ToString("N"));
    public string Checkout => Path.Combine(Root, "repository");
    public string GitExecutable { get; } = FindGit();
    public GitRepositoryContextReader Reader => new(GitExecutable);
    private bool _cleanupSafe = true;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Checkout);
        await GitAsync(Checkout, "init", "-q", "-b", "main");
        await CommitAsync("base");
    }

    public async Task<string> CommitAsync(string text)
    {
        await File.WriteAllTextAsync(Path.Combine(Checkout, "README.md"), text, TestContext.Current.CancellationToken);
        await GitAsync(Checkout, "add", "README.md");
        await GitAsync(Checkout, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-q", "-m", text);
        return await GitAsync(Checkout, "rev-parse", "HEAD");
    }

    public async Task<FrozenPlan> FreezeAsync()
    {
        var context = await Reader.ReadAsync(Checkout, "refs/heads/main", TestContext.Current.CancellationToken);
        var file = await Reader.ReadFileAsync(context, "README.md", TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var request = new PlanningRequest("nucleoid/delivery-forge", "#9", "implement", "Exercise local single scheduling",
            ["README.md"], ["excluded"], ["Exact identities and bounded execution"], "implement");
        EvidenceItem[] evidence = [EvidenceItem.FromRepositoryFile(file, now, [])];
        var draft = new PlanDraft(request, context, evidence, [new("README.md", "document", "edit document")], [new("#9", [], "root")],
            [new("test", new("dotnet", [PlanCommandArgument.Literal("test")]), "PASS")],
            new("additive", "none", "none", "none", "none", "none", "receipt", "revert", []), [], IntakePlanner.Assess(request, evidence));
        return PlanFreezer.Freeze(draft, "fixture-revision", now);
    }

    public async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var result = await new ProcessCommandExecutor().ExecuteAsync(new(GitExecutable, arguments, directory), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (!result.OwnedProcessQuiescent) _cleanupSafe = false;
        Assert.True(result.OwnedProcessQuiescent && result.ExitCode == 0 && !result.Cancelled && !result.TimedOut, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private static string FindGit() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "git.exe" : "git")).First(File.Exists);

    public void Dispose()
    {
        if (!_cleanupSafe || !Directory.Exists(Root)) return;
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(Root, "*", enumeration)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(Root, recursive: true);
    }
}

public sealed class LocalPlanBindingTests
{
    [Fact]
    public async Task Same_git_facts_in_another_checkout_do_not_rebind_a_frozen_plan()
    {
        using var repository = new PlanningRepository();
        await repository.InitializeAsync();
        var plan = await repository.FreezeAsync();
        var clone = Path.Combine(repository.Root, "clone");
        await repository.GitAsync(repository.Root, "clone", "-q", "--no-hardlinks", repository.Checkout, clone);
        var context = await repository.Reader.ReadAsync(clone, "refs/heads/main", TestContext.Current.CancellationToken);
        Assert.Equal(plan.BaseCommit, context.Commit);
        Assert.Equal(plan.BaseTree, context.Tree);
        Assert.False(plan.ReconcileBase(context).DownstreamReady);
        var original = await repository.Reader.ReadAsync(repository.Checkout, "refs/heads/main", TestContext.Current.CancellationToken);
        Assert.True(plan.ReconcileBase(original).DownstreamReady);
        Assert.DoesNotContain(repository.Root, System.Text.Encoding.UTF8.GetString(plan.CanonicalBytes), StringComparison.OrdinalIgnoreCase);
    }
}
