using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
    private static ParentDisposableGitHubAuthority Parent(PublicationTarget? target = null) => ParentDisposableGitHubAuthority.CreateForExplicitUserAuthorization(
        "fixture-only-user-grant", target ?? Target, "forge-disposable-", AuthorizationCeiling.Merge, DateTimeOffset.UtcNow.AddMinutes(5));
    private static GitHostMutation Mutation(PublicationAction action = PublicationAction.MergeNormal,
        string key = "cli-key", PublicationTarget? target = null) => new(
        new(key, "fixture-run", "fixture-revision", action, target ?? Target, PublicationFixture.Commit('a'),
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
        var call = Assert.Single(runner.Calls, call => call.Count > 1 && call[0] == "pr" && call[1] == "merge");
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
        var call = Assert.Single(runner.Calls, call => call.Count > 1 && call[0] == "pr" && call[1] == "create");
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

    [Theory]
    [InlineData("head")]
    [InlineData("checks")]
    [InlineData("protection")]
    public async Task Exact_remote_drift_or_unknown_evidence_blocks_cli_send(string fault)
    {
        var runner = new CliFixtureRunner { Fault = fault };
        var parent = Parent();
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Rejected, result.Outcome);
        Assert.Equal(0, runner.Mutations);
    }

    [Fact(Timeout = 10_000)]
    public async Task Ignored_action_cancellation_returns_uncertain_and_cannot_start_another_action()
    {
        var runner = new CliFixtureRunner { BlockMutation = true };
        var parent = Parent();
        var configuration = Configuration() with { ActionTimeout = TimeSpan.FromMilliseconds(25) };
        var adapter = new GitHubCliAdapter(configuration, runner, parent, parent.IssueGrant());
        try
        {
            var result = await adapter.MergeNormalAsync(Mutation(), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(GitHostMutationOutcome.Uncertain, result.Outcome);
            var replay = await adapter.MergeNormalAsync(Mutation(), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(GitHostMutationOutcome.Uncertain, replay.Outcome);
            Assert.Equal(1, runner.Mutations);
        }
        finally
        {
            runner.ReleaseMutation.TrySetResult();
            if (runner.MutationTask is not null)
                await runner.MutationTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Cli_failure_after_possible_action_remains_uncertain_without_blind_retry()
    {
        var runner = new CliFixtureRunner { FailMutation = true };
        var parent = Parent();
        var adapter = new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant());
        var first = await adapter.MergeNormalAsync(Mutation(), TestContext.Current.CancellationToken);
        var replay = await adapter.MergeNormalAsync(Mutation(), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Uncertain, first.Outcome);
        Assert.Equal(GitHostMutationOutcome.Uncertain, replay.Outcome);
        Assert.Equal(1, runner.Mutations);
    }

    [Fact(Timeout = 10_000)]
    public async Task Local_process_capture_is_complete_and_bounded_without_invoking_github()
    {
        var executable = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ComSpec")! : "/bin/sh";
        var configuration = Configuration() with
        {
            ExecutablePath = executable,
            ExecutableIdentity = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable)))
        };
        var arguments = OperatingSystem.IsWindows() ? new[] { "/d", "/c", "echo fixture-output" } : new[] { "-c", "printf fixture-output" };
        var result = await new ProcessGitHubCliExecutor(configuration).ExecuteAsync(arguments, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("fixture-output", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(result.ResidualProcessUncertain);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_residual_process_result_fences_ref_across_new_key_and_parent(bool newParent)
    {
        var target = Target with { Repository = "fixture/forge-disposable-residual-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, ResidualMutation = true };
        var parent = Parent(target);
        var first = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Uncertain, first.Outcome);
        Assert.Equal(1, runner.Mutations);
        var nextParent = newParent ? Parent(target) : parent;
        var second = await new GitHubCliAdapter(Configuration(), runner, nextParent, nextParent.IssueGrant())
            .MergeNormalAsync(Mutation(key: "another-key", target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Uncertain, second.Outcome);
        Assert.Equal(1, runner.Mutations);
    }

    [Fact]
    public async Task Fully_quiescent_uncertain_result_does_not_invent_a_physical_residual_barrier()
    {
        var target = Target with { Repository = "fixture/forge-disposable-quiescent-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, FailMutation = true };
        var parent = Parent(target);
        var first = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Uncertain, first.Outcome);
        runner.FailMutation = false;
        runner.PublicationKey = "next-quiescent-key";
        var nextParent = Parent(target);
        var second = await new GitHubCliAdapter(Configuration(), runner, nextParent, nextParent.IssueGrant())
            .MergeNormalAsync(Mutation(key: runner.PublicationKey, target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Confirmed, second.Outcome);
        Assert.Equal(2, runner.Mutations);
    }

    [Theory]
    [InlineData("wrong-app")]
    [InlineData("absent-required-app")]
    [InlineData("required-failing-wrong-passing")]
    [InlineData("missing-observed-app")]
    [InlineData("missing-required-app")]
    public async Task App_specific_required_check_cannot_be_satisfied_by_name_only(string shape)
    {
        var target = Target with { Repository = "fixture/forge-disposable-app-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, AppShape = shape };
        var parent = Parent(target);
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Rejected, result.Outcome);
        Assert.Equal(0, runner.Mutations);
    }

    [Theory]
    [InlineData("correct-app")]
    [InlineData("explicit-any-app")]
    [InlineData("legacy")]
    public async Task Correct_or_explicitly_unrestricted_required_check_is_a_positive_control(string shape)
    {
        var target = Target with { Repository = "fixture/forge-disposable-app-control-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, AppShape = shape };
        var parent = Parent(target);
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Confirmed, result.Outcome);
        Assert.Equal(1, runner.Mutations);
    }

    [Theory]
    [InlineData("legacy-failed")]
    [InlineData("legacy-pending")]
    [InlineData("legacy-cancelled")]
    [InlineData("legacy-wrong-head")]
    public async Task Legacy_success_status_cannot_mask_same_name_unacceptable_check_run(string shape)
    {
        var target = Target with { Repository = "fixture/forge-disposable-conjunction-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, AppShape = shape };
        var parent = Parent(target);
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Rejected, result.Outcome);
        Assert.Equal(0, runner.Mutations);
    }

    [Theory]
    [InlineData("legacy-both-success")]
    [InlineData("legacy-check-only")]
    public async Task Legacy_successful_same_name_conjunction_or_check_only_is_a_positive_control(string shape)
    {
        var target = Target with { Repository = "fixture/forge-disposable-conjunction-control-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, AppShape = shape };
        var parent = Parent(target);
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Confirmed, result.Outcome);
        Assert.Equal(1, runner.Mutations);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("wrong-integration")]
    [InlineData("multiple-layers-missing")]
    [InlineData("tail-queue")]
    [InlineData("tail-check")]
    [InlineData("unbounded-pages")]
    [InlineData("invalid-shape")]
    [InlineData("unsupported-type")]
    [InlineData("truncated")]
    public async Task Applicable_rules_must_be_complete_and_every_required_integration_must_pass(string shape)
    {
        var target = Target with { Repository = "fixture/forge-disposable-rules-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, RulesShape = shape };
        var parent = Parent(target);
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Rejected, result.Outcome);
        Assert.Equal(0, runner.Mutations);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("multiple-layers-pass")]
    public async Task Complete_supported_ruleset_requirements_are_a_positive_control(string shape)
    {
        var target = Target with { Repository = "fixture/forge-disposable-rules-control-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, RulesShape = shape };
        var parent = Parent(target);
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Confirmed, result.Outcome);
        Assert.Equal(1, runner.Mutations);
    }

    [Theory]
    [InlineData("correct-app")]
    [InlineData("explicit-any-app")]
    [InlineData("legacy-check-only")]
    public async Task Required_successful_check_runs_need_no_unrelated_commit_status(string shape)
    {
        var target = Target with { Repository = "fixture/forge-disposable-empty-status-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, AppShape = shape, EmptyStatuses = true };
        var parent = Parent(target);
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Confirmed, result.Outcome);
        Assert.Equal(1, runner.Mutations);
    }

    [Theory]
    [InlineData("correct-app", "failure")]
    [InlineData("correct-app", "pending")]
    [InlineData("explicit-any-app", "failure")]
    [InlineData("explicit-any-app", "pending")]
    [InlineData("legacy-both-success", "failure")]
    [InlineData("legacy-both-success", "pending")]
    public async Task Passing_check_does_not_mask_required_same_name_status(string shape, string state)
    {
        var target = Target with { Repository = "fixture/forge-disposable-status-conjunction-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, AppShape = shape, SameNameStatusState = state };
        var parent = Parent(target);
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Rejected, result.Outcome);
        Assert.Equal(0, runner.Mutations);
    }

    [Theory]
    [InlineData("workflow_dispatch")]
    [InlineData("missing-run")]
    [InlineData("foreign-repository")]
    [InlineData("wrong-head")]
    [InlineData("wrong-suite")]
    [InlineData("missing-job")]
    [InlineData("wrong-check-association")]
    [InlineData("missing-suite")]
    [InlineData("incomplete-runs")]
    [InlineData("incomplete-jobs")]
    public async Task Actions_ruleset_check_requires_exact_eligible_run_and_job_provenance(string shape)
    {
        var target = Target with { Repository = "fixture/forge-disposable-actions-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, RulesShape = "complete", ActionsShape = shape };
        var parent = Parent(target);
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Rejected, result.Outcome);
        Assert.Equal(0, runner.Mutations);
    }

    [Theory]
    [InlineData("push")]
    [InlineData("pull_request")]
    [InlineData("pull_request_review")]
    [InlineData("pull_request_target")]
    [InlineData("deployment")]
    [InlineData("deployment_status")]
    [InlineData("external-app")]
    public async Task Exact_eligible_workflow_or_external_app_check_is_a_positive_control(string shape)
    {
        var target = Target with { Repository = "fixture/forge-disposable-actions-control-" + Guid.NewGuid().ToString("N") };
        var runner = new CliFixtureRunner { RepositoryTarget = target, RulesShape = "complete", ActionsShape = shape };
        var parent = Parent(target);
        var result = await new GitHubCliAdapter(Configuration(), runner, parent, parent.IssueGrant())
            .MergeNormalAsync(Mutation(target: target), TestContext.Current.CancellationToken);
        Assert.Equal(GitHostMutationOutcome.Confirmed, result.Outcome);
        Assert.Equal(1, runner.Mutations);
    }

    private sealed class CliFixtureRunner : IGitHubCliExecutor
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public bool PrExists { get; set; } = true;
        public bool MergeQueue { get; set; }
        public string? Fault { get; set; }
        public bool BlockMutation { get; set; }
        public bool FailMutation { get; set; }
        public bool ResidualMutation { get; set; }
        public PublicationTarget RepositoryTarget { get; set; } = Target;
        public string PublicationKey { get; set; } = "cli-key";
        public string? AppShape { get; set; }
        public string? RulesShape { get; set; }
        public bool EmptyStatuses { get; set; }
        public string? SameNameStatusState { get; set; }
        public string? ActionsShape { get; set; }
        public TaskCompletionSource ReleaseMutation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GitHubCliResult>? MutationTask { get; private set; }
        private bool merged;
        public int Mutations => Calls.Count(call => call.Count > 1 && call[0] == "pr" && call[1] is "merge" or "create" or "edit");
        public Task<GitHubCliResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var task = ExecuteCoreAsync(arguments, cancellationToken);
            if (arguments.Count > 1 && arguments[0] == "pr" && arguments[1] is "merge" or "create" or "edit") MutationTask = task;
            return task;
        }
        private async Task<GitHubCliResult> ExecuteCoreAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToArray());
            if (arguments.Count > 1 && arguments[0] == "pr" && arguments[1] is "merge" or "create" or "edit")
            {
                if (BlockMutation) await ReleaseMutation.Task;
                if (FailMutation) return new(1, "", "fixture lost result");
                if (ResidualMutation) return new(null, "", "fixture incomplete physical cleanup", ResidualProcessUncertain: true);
                if (arguments[1] == "merge") merged = true;
                if (arguments[1] == "create") PrExists = true;
            }
            var endpoint = arguments.Count > 1 ? arguments[1] : "";
            var body = arguments[0] == "--version" ? "gh version 2.80.0 (2025-10-01)" : arguments[0] == "auth" ? "Logged in" :
                arguments[0] == "pr" ? "https://github.com/" + RepositoryTarget.Repository + "/pull/10" :
                endpoint.Contains("git/ref/heads/work", StringComparison.Ordinal) ? "{\"object\":{\"sha\":\"" + PublicationFixture.Commit('b') + "\"}}" :
                endpoint.Contains("git/ref/heads/main", StringComparison.Ordinal) ? "{\"object\":{\"sha\":\"" + PublicationFixture.Commit(merged ? 'd' : 'a') + "\"}}" :
                endpoint.Contains("/pulls/", StringComparison.Ordinal) ? PrDocument().ToJsonString() :
                endpoint.Contains("/pulls", StringComparison.Ordinal) ? PrJson() :
                endpoint.EndsWith("/protection", StringComparison.Ordinal) ? "{\"required_status_checks\":{\"contexts\":[\"ci\"]},\"required_pull_request_reviews\":{}}" :
                endpoint.Contains("/rules/branches/", StringComparison.Ordinal) ? MergeQueue ? "[{\"type\":\"merge_queue\"}]" : "[]" :
                endpoint.Contains("/check-runs", StringComparison.Ordinal) ? "{\"total_count\":0,\"check_runs\":[]}" :
                endpoint.Contains("/status", StringComparison.Ordinal) ? "{\"state\":\"success\",\"total_count\":1,\"statuses\":[{\"context\":\"ci\",\"state\":\"success\"}]}" : "{}";
            if (Fault == "head" && endpoint.Contains("git/ref/heads/work", StringComparison.Ordinal))
                body = "{\"object\":{\"sha\":\"" + PublicationFixture.Commit('9') + "\"}}";
            if (Fault == "checks" && endpoint.Contains("/status", StringComparison.Ordinal))
                body = "{\"state\":\"pending\",\"total_count\":1,\"statuses\":[{\"context\":\"ci\",\"state\":\"pending\"}]}";
            if (Fault == "protection" && endpoint.EndsWith("/protection", StringComparison.Ordinal)) body = "{}";
            if (AppShape is not null)
            {
                if (endpoint.EndsWith("/protection", StringComparison.Ordinal)) body = AppProtection();
                if (endpoint.Contains("/check-runs", StringComparison.Ordinal)) body = AppRuns();
                if (endpoint.Contains("/status", StringComparison.Ordinal))
                {
                    var context = AppShape is "absent-required-app" or "legacy" ||
                        AppShape.StartsWith("legacy-", StringComparison.Ordinal) && AppShape != "legacy-check-only" ? "ci" : "unrelated";
                    body = new JsonObject
                    {
                        ["state"] = "success",
                        ["total_count"] = 1,
                        ["statuses"] = new JsonArray(new JsonObject
                        { ["context"] = context, ["state"] = "success" })
                    }.ToJsonString();
                }
            }
            if (RulesShape is not null)
            {
                if (endpoint.Contains("/rules/branches/", StringComparison.Ordinal))
                {
                    if (RulesShape == "truncated") return new(null, "[", "fixture truncated rules", TimedOut: true);
                    body = RulePage(endpoint);
                }
                if (endpoint.Contains("/check-runs", StringComparison.Ordinal)) body = RuleRuns();
            }
            if ((EmptyStatuses || AppShape == "legacy-check-only") && endpoint.Contains("/status", StringComparison.Ordinal))
                body = "{\"state\":\"pending\",\"total_count\":0,\"statuses\":[]}";
            if (SameNameStatusState is not null && endpoint.Contains("/status", StringComparison.Ordinal))
                body = new JsonObject
                {
                    ["state"] = SameNameStatusState,
                    ["total_count"] = 1,
                    ["statuses"] = new JsonArray(new JsonObject { ["context"] = "ci", ["state"] = SameNameStatusState })
                }.ToJsonString();
            if (ActionsShape is not null)
            {
                if (endpoint.Contains("/actions/runs/", StringComparison.Ordinal) && endpoint.Contains("/jobs", StringComparison.Ordinal)) body = ActionsJobs();
                else if (endpoint.Contains("/actions/runs", StringComparison.Ordinal)) body = ActionsRuns();
            }
            return new(0, body, "");
        }
        private string RulePage(string endpoint)
        {
            JsonObject Required(string context, int integration) => new()
            {
                ["type"] = "required_status_checks",
                ["ruleset_id"] = integration,
                ["ruleset_source_type"] = "Repository",
                ["ruleset_source"] = RepositoryTarget.Repository,
                ["parameters"] = new JsonObject
                {
                    ["strict_required_status_checks_policy"] = true,
                    ["required_status_checks"] = new JsonArray(new JsonObject { ["context"] = context, ["integration_id"] = integration })
                }
            };
            if (RulesShape == "invalid-shape") return "[{}]";
            if (RulesShape == "unsupported-type") return "[{\"type\":\"future_opaque_protection\"}]";
            var rules = new JsonArray();
            if (RulesShape is "tail-queue" or "tail-check" or "unbounded-pages")
            {
                var explicitPage = endpoint.Contains("per_page=100", StringComparison.Ordinal);
                var secondPage = endpoint.Contains("page=2", StringComparison.Ordinal);
                if (!secondPage || RulesShape == "unbounded-pages")
                    for (var index = 0; index < (explicitPage ? 100 : 30); index++) rules.Add(Required("security", 123));
                else if (RulesShape == "tail-queue") rules.Add(new JsonObject { ["type"] = "merge_queue" });
                else rules.Add(Required("tail-security", 789));
            }
            else
            {
                rules.Add(Required("security", ActionsShape is not null and not "external-app" ? 15368 : 123));
                if (RulesShape is "multiple-layers-missing" or "multiple-layers-pass") rules.Add(Required("audit", 456));
            }
            return rules.ToJsonString();
        }
        private string RuleRuns()
        {
            JsonObject Run(string name, int app, string conclusion) => new()
            {
                ["id"] = app,
                ["name"] = name,
                ["head_sha"] = PublicationFixture.Commit('b'),
                ["status"] = "completed",
                ["conclusion"] = conclusion,
                ["app"] = new JsonObject { ["id"] = app }
            };
            var runs = new JsonArray();
            if (ActionsShape is not null)
            {
                var action = Run("security", ActionsShape == "external-app" ? 123 : 15368, "success");
                action["id"] = 500;
                action["url"] = "https://api.github.com/repos/" + RepositoryTarget.Repository + "/check-runs/500";
                action["app"]!["slug"] = ActionsShape == "external-app" ? "external-provider" : "github-actions";
                if (ActionsShape != "missing-suite") action["check_suite"] = new JsonObject { ["id"] = 900 };
                runs.Add(action);
                return new JsonObject { ["total_count"] = 1, ["check_runs"] = runs }.ToJsonString();
            }
            if (RulesShape != "missing") runs.Add(Run("security", RulesShape == "wrong-integration" ? 999 : 123,
                RulesShape == "failed" ? "failure" : "success"));
            if (RulesShape == "multiple-layers-pass") runs.Add(Run("audit", 456, "success"));
            return new JsonObject { ["total_count"] = runs.Count, ["check_runs"] = runs }.ToJsonString();
        }
        private string ActionsRuns()
        {
            var runs = new JsonArray();
            if (ActionsShape != "missing-run") runs.Add(new JsonObject
            {
                ["id"] = 700,
                ["check_suite_id"] = ActionsShape == "wrong-suite" ? 901 : 900,
                ["head_sha"] = PublicationFixture.Commit(ActionsShape == "wrong-head" ? '9' : 'b'),
                ["event"] = ActionsShape is "workflow_dispatch" or "push" or "pull_request" or "pull_request_review" or
                    "pull_request_target" or "deployment" or "deployment_status" ? ActionsShape : "pull_request",
                ["status"] = "completed",
                ["conclusion"] = "success",
                ["run_attempt"] = 1,
                ["repository"] = new JsonObject { ["full_name"] = ActionsShape == "foreign-repository" ? "foreign/other" : RepositoryTarget.Repository },
                ["url"] = "https://api.github.com/repos/" + RepositoryTarget.Repository + "/actions/runs/700"
            });
            return new JsonObject { ["total_count"] = ActionsShape == "incomplete-runs" ? runs.Count + 1 : runs.Count, ["workflow_runs"] = runs }.ToJsonString();
        }
        private string ActionsJobs()
        {
            var jobs = new JsonArray();
            if (ActionsShape != "missing-job") jobs.Add(new JsonObject
            {
                ["id"] = 500,
                ["run_id"] = 700,
                ["head_sha"] = PublicationFixture.Commit('b'),
                ["name"] = "security",
                ["status"] = "completed",
                ["conclusion"] = "success",
                ["check_run_url"] = "https://api.github.com/repos/" + RepositoryTarget.Repository + "/check-runs/" +
                    (ActionsShape == "wrong-check-association" ? "501" : "500"),
                ["run_url"] = "https://api.github.com/repos/" + RepositoryTarget.Repository + "/actions/runs/700"
            });
            return new JsonObject { ["total_count"] = ActionsShape == "incomplete-jobs" ? jobs.Count + 1 : jobs.Count, ["jobs"] = jobs }.ToJsonString();
        }
        private string AppProtection()
        {
            var required = new JsonObject { ["contexts"] = new JsonArray("ci") };
            if (AppShape != "legacy" && AppShape?.StartsWith("legacy-", StringComparison.Ordinal) != true)
            {
                var check = new JsonObject { ["context"] = "ci" };
                if (AppShape != "missing-required-app") check["app_id"] = AppShape == "explicit-any-app" ? -1 : 123;
                required["checks"] = new JsonArray(check);
            }
            return new JsonObject { ["required_status_checks"] = required, ["required_pull_request_reviews"] = new JsonObject() }.ToJsonString();
        }
        private string AppRuns()
        {
            JsonObject Run(int? app, string conclusion)
            {
                var run = new JsonObject
                {
                    ["id"] = app ?? 1,
                    ["name"] = "ci",
                    ["head_sha"] = PublicationFixture.Commit('b'),
                    ["status"] = "completed",
                    ["conclusion"] = conclusion
                };
                if (app is not null) run["app"] = new JsonObject { ["id"] = app.Value };
                return run;
            }
            var runs = new JsonArray();
            if (AppShape?.StartsWith("legacy-", StringComparison.Ordinal) == true)
            {
                var run = Run(123, AppShape == "legacy-failed" ? "failure" : AppShape == "legacy-cancelled" ? "cancelled" : "success");
                if (AppShape == "legacy-pending") { run["status"] = "in_progress"; run["conclusion"] = null; }
                if (AppShape == "legacy-wrong-head") run["head_sha"] = PublicationFixture.Commit('9');
                runs.Add(run);
            }
            else if (AppShape is not ("absent-required-app" or "legacy"))
            {
                if (AppShape == "required-failing-wrong-passing") runs.Add(Run(123, "failure"));
                runs.Add(Run(AppShape == "correct-app" ? 123 : AppShape == "missing-observed-app" ? null : 999, "success"));
            }
            return new JsonObject { ["total_count"] = runs.Count, ["check_runs"] = runs }.ToJsonString();
        }
        private string PrJson()
        {
            if (!PrExists) return "[]";
            return new JsonArray(PrDocument()).ToJsonString();
        }
        private JsonObject PrDocument()
        {
            var marker = "delivery-forge-publication:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PublicationKey)));
            return new JsonObject
            {
                ["number"] = 10,
                ["html_url"] = "https://github.com/" + RepositoryTarget.Repository + "/pull/10",
                ["head"] = new JsonObject
                {
                    ["sha"] = PublicationFixture.Commit('b'),
                    ["ref"] = "work",
                    ["repo"] = new JsonObject { ["full_name"] = RepositoryTarget.Repository }
                },
                ["base"] = new JsonObject
                {
                    ["sha"] = PublicationFixture.Commit('a'),
                    ["ref"] = "main",
                    ["repo"] = new JsonObject { ["full_name"] = RepositoryTarget.Repository }
                },
                ["body"] = "<!-- " + marker + " -->",
                ["state"] = merged ? "closed" : "open",
                ["auto_merge"] = null,
                ["merged_at"] = merged ? "2026-10-11T00:00:01Z" : null,
                ["merge_commit_sha"] = merged ? PublicationFixture.Commit('d') : null
            };
        }
    }
}
