using System.Security.Cryptography;
using System.Text;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Publication.GitHost;

namespace DeliveryForge.Publication.Tests;

public sealed class GitHubCliAdapterTests
{
    private static PublicationTarget Target => new("fixture/forge-disposable-10", "refs/heads/work", "refs/heads/main");
    private static GitHubReadConfiguration Configuration()
    {
        var executable = Environment.ProcessPath!;
        return new(executable, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
            AppContext.BaseDirectory, "2.80.0", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
    private static ParentDisposableGitHubAuthority Parent() => ParentDisposableGitHubAuthority.CreateForExplicitUserAuthorization(
        "fixture-only-user-grant", Target, "forge-disposable-", AuthorizationCeiling.Merge, DateTimeOffset.UtcNow.AddMinutes(5));
    private static GitHostMutation Mutation(PublicationAction action = PublicationAction.MergeNormal) => new(
        new("cli-key", "fixture-run", "fixture-revision", action, Target, PublicationFixture.Commit('a'),
            PublicationFixture.Commit('b'), PublicationFixture.Commit('c'), PublicationIntentState.Sent, CreatedAt: DateTimeOffset.UtcNow),
        PublicationFixture.Commit('a'));

    [Fact]
    public async Task Configured_readonly_probe_observes_exact_cli_version_and_existing_auth()
    {
        var runner = new CliFixtureRunner();
        var capability = await new GitHubCliAdapter(Configuration(), runner).ProbeAsync(TestContext.Current.CancellationToken);
        Assert.True(capability.Supported, capability.Reason);
        Assert.Equal("2.80.0", capability.Version);
        Assert.Equal(0, runner.Mutations);
    }
    [Fact]
    public async Task Readonly_adapter_reads_exact_refs_pr_head_required_checks_and_protection()
    {
        var runner = new CliFixtureRunner();
        var observation = await new GitHubCliAdapter(Configuration(), runner).ReadAsync(Target, "cli-key", TestContext.Current.CancellationToken);
        Assert.Equal(PublicationFixture.Commit('b'), observation.SourceHead);
        Assert.Equal(PublicationFixture.Commit('a'), observation.TargetHead);
        Assert.Equal(PublicationFixture.Commit('b'), observation.PrHead);
        Assert.Equal(GitHostChecks.Passing, observation.Checks);
        Assert.Equal(GitHostProtection.RequiredAndObserved, observation.Protection);
        Assert.Equal(0, runner.Mutations);
    }
    [Fact]
    public async Task Readonly_configuration_cannot_perform_any_mutation()
    {
        var runner = new CliFixtureRunner();
        var adapter = new GitHubCliAdapter(Configuration(), runner);
        await Assert.ThrowsAsync<PublicationUnavailableException>(() => adapter.MergeNormalAsync(Mutation(), TestContext.Current.CancellationToken));
        Assert.Equal(0, runner.Mutations);
    }
    [Fact]
    public async Task Authorized_normal_merge_uses_exact_head_guard_without_bypass_or_queue()
    {
        var runner = new CliFixtureRunner();
        var parent = Parent();
        var adapter = new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant());
        var result = await adapter.MergeNormalAsync(Mutation(), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Confirmed, result.Outcome);
        var call = Assert.Single(runner.Calls.Where(call => call.Count > 1 && call[0] == "pr" && call[1] == "merge"));
        Assert.Contains("--merge", call);
        Assert.Contains("--match-head-commit", call);
        Assert.Contains(PublicationFixture.Commit('b'), call);
        Assert.DoesNotContain("--admin", call);
        Assert.DoesNotContain("--auto", call);
        Assert.DoesNotContain("--squash", call);
        Assert.DoesNotContain("--rebase", call);
    }
    [Fact]
    public async Task Authorized_pr_creation_names_remote_head_and_never_pushes_or_forks()
    {
        var runner = new CliFixtureRunner { PrExists = false };
        var parent = Parent();
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .CreateOrUpdatePrAsync(Mutation(PublicationAction.CreateOrUpdatePr), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Confirmed, result.Outcome);
        var call = Assert.Single(runner.Calls.Where(call => call.Count > 1 && call[0] == "pr" && call[1] == "create"));
        Assert.Contains("--head", call);
        Assert.Contains("--repo", call);
        Assert.Contains("--base", call);
        Assert.DoesNotContain("--dry-run", call);
    }
    [Fact]
    public async Task Revoked_or_borrowed_disposable_grant_never_calls_the_cli_mutator()
    {
        var runner = new CliFixtureRunner();
        var parent = Parent();
        var grant = parent.IssueGrant();
        parent.Revoke(grant);
        var adapter = new GitHubCliAdapter(Configuration(), runner, parent, grant);
        await Assert.ThrowsAsync<PublicationUnavailableException>(() => adapter.MergeNormalAsync(Mutation(), TestContext.Current.CancellationToken));
        var borrowed = new GitHubCliAdapter(Configuration(), runner, Parent(), parent.IssueGrant());
        await Assert.ThrowsAsync<PublicationUnavailableException>(() => borrowed.MergeNormalAsync(Mutation(), TestContext.Current.CancellationToken));
        Assert.Equal(0, runner.Mutations);
    }
    [Fact]
    public async Task Missing_required_check_or_merge_queue_blocks_normal_merge()
    {
        var runner = new CliFixtureRunner { MergeQueue = true };
        var parent = Parent();
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Rejected, result.Outcome);
        Assert.Equal(0, runner.Mutations);
    }

    private sealed class CliFixtureRunner : IGitHubCliExecutor
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public bool PrExists { get; set; } = true;
        public bool MergeQueue { get; set; }
        public int Mutations => Calls.Count(call => call.Count > 1 && call[0] == "pr" && call[1] is "merge" or "create" or "edit");
        public Task<GitHubCliResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToArray());
            var endpoint = arguments.Count > 1 ? arguments[1] : "";
            var body = arguments[0] == "--version" ? "gh version 2.80.0 (2025-10-01)" : arguments[0] == "auth" ? "Logged in" :
                arguments[0] == "pr" ? "https://github.com/fixture/forge-disposable-10/pull/10" :
                endpoint.Contains("git/ref/heads/work", StringComparison.Ordinal) ? "{\"object\":{\"sha\":\"" + PublicationFixture.Commit('b') + "\"}}" :
                endpoint.Contains("git/ref/heads/main", StringComparison.Ordinal) ? "{\"object\":{\"sha\":\"" + PublicationFixture.Commit('a') + "\"}}" :
                endpoint.Contains("/pulls", StringComparison.Ordinal) ? PrJson() :
                endpoint.EndsWith("/protection", StringComparison.Ordinal) ? "{\"required_status_checks\":{\"contexts\":[\"ci\"]},\"required_pull_request_reviews\":{}}" :
                endpoint.Contains("/rules/branches/", StringComparison.Ordinal) ? MergeQueue ? "[{\"type\":\"merge_queue\"}]" : "[]" :
                endpoint.Contains("/check-runs", StringComparison.Ordinal) ? "{\"total_count\":0,\"check_runs\":[]}" :
                endpoint.EndsWith("/status", StringComparison.Ordinal) ? "{\"state\":\"success\",\"statuses\":[{\"context\":\"ci\",\"state\":\"success\"}]}" : "{}";
            return Task.FromResult(new GitHubCliResult(0, body, ""));
        }
        private string PrJson()
        {
            if (!PrExists) return "[]";
            var marker = "delivery-forge-publication:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("cli-key")));
            return $$"""
                [{ "number":10, "html_url":"https://github.com/fixture/forge-disposable-10/pull/10",
                   "head":{"sha":"{{PublicationFixture.Commit('b')}}","ref":"work","repo":{"full_name":"fixture/forge-disposable-10"}},
                   "base":{"ref":"main","sha":"{{PublicationFixture.Commit('a')}}"},"body":"<!-- {{marker}} -->",
                   "merged_at":null,"merge_commit_sha":null }]
                """;
        }
    }
}
