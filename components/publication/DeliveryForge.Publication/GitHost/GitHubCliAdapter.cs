using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeliveryForge.Contracts.Models;

namespace DeliveryForge.Publication.GitHost;

/// <summary>Read-only by default. Mutations require an ephemeral parent grant for one disposable target.</summary>
public sealed class GitHubCliAdapter : IGitHostAdapter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefLocks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Task> UnsettledCommands = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(ParentDisposableGitHubAuthority Parent, string Resource, string Key), PublicationIntent> Attempts = new();
    private readonly GitHubReadConfiguration? configuration;
    private readonly IGitHubCliExecutor? executor;
    private readonly ParentDisposableGitHubAuthority? parent;
    private readonly ParentDisposableGitHubGrant? grant;

    public GitHubCliAdapter() { }
    public GitHubCliAdapter(GitHubReadConfiguration configuration, IGitHubCliExecutor executor,
        ParentDisposableGitHubAuthority? parent = null, ParentDisposableGitHubGrant? grant = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(executor);
        ProcessGitHubCliExecutor.ValidateConfiguration(configuration);
        this.configuration = configuration;
        this.executor = executor;
        this.parent = parent;
        this.grant = grant;
    }
    public PublicationRealm Realm => PublicationRealm.Production;

    public async Task<GitHubCliCapability> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (configuration is null || executor is null) return new(false, "", "GitHub CLI configuration is unavailable.");
        try
        {
            var version = await Command(["--version"], configuration.ReadTimeout, cancellationToken).ConfigureAwait(false);
            var parsed = Regex.Match(version.StandardOutput, @"^gh version ([0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.CultureInvariant);
            if (!Complete(version) || !parsed.Success || parsed.Groups[1].Value != configuration.ExpectedVersion)
                return new(false, parsed.Success ? parsed.Groups[1].Value : "", "Observed CLI version does not match the configured pin.");
            var auth = await Command(["auth", "status", "--active", "--hostname", "github.com"], configuration.ReadTimeout, cancellationToken).ConfigureAwait(false);
            return new(Complete(auth), parsed.Groups[1].Value, Complete(auth) ? "Existing first-party CLI authentication observed." : "Existing CLI authentication is unavailable.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(false, "", "CLI capability observation is incomplete; credentials/configuration are unchanged."); }
    }

    public async Task<GitHostObservation> ReadAsync(PublicationTarget target, string idempotencyKey, CancellationToken cancellationToken) =>
        (await ReadSnapshot(target, idempotencyKey, cancellationToken).ConfigureAwait(false)).Observation;
    public Task<GitHostMutationResult> CreateOrUpdatePrAsync(GitHostMutation mutation, CancellationToken cancellationToken) =>
        Mutate(mutation, PublicationAction.CreateOrUpdatePr, cancellationToken);
    public Task<GitHostMutationResult> MergeNormalAsync(GitHostMutation mutation, CancellationToken cancellationToken) =>
        Mutate(mutation, PublicationAction.MergeNormal, cancellationToken);

    private async Task<GitHostMutationResult> Mutate(GitHostMutation mutation, PublicationAction action, CancellationToken cancellationToken)
    {
        EnsureGrant(mutation, action);
        var intent = mutation.Intent;
        var resource = intent.Target.Repository.ToLowerInvariant() + "\0" + intent.Target.TargetRef;
        var mutex = RefLocks.GetOrAdd(resource, _ => new SemaphoreSlim(1, 1));
        var entered = false;
        var dispatched = false;
        Task<GitHubCliResult>? pendingCommand = null;
        try
        {
            entered = await mutex.WaitAsync(configuration!.ActionTimeout, cancellationToken).ConfigureAwait(false);
            if (!entered) return new(GitHostMutationOutcome.Uncertain, null);
            if (UnsettledCommands.TryGetValue(resource, out var unsettled))
            {
                if (!unsettled.IsCompleted) return new(GitHostMutationOutcome.Uncertain, null);
                ProcessGitHubCliExecutor.ObserveLate(unsettled);
                UnsettledCommands.TryRemove(resource, out _);
            }
            var snapshot = await ReadSnapshot(intent.Target, intent.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            var observed = snapshot.Observation;
            var attemptKey = (parent!, resource, intent.IdempotencyKey);
            if (Attempts.TryGetValue(attemptKey, out var prior) && !SameBinding(prior, intent))
                return new(GitHostMutationOutcome.Rejected, null);
            if (Completed(snapshot, intent)) return new(GitHostMutationOutcome.Confirmed, observed.ResultId);
            if (!Exact(snapshot, mutation, action)) return new(GitHostMutationOutcome.Rejected, null);
            if (prior is not null)
            {
                // Any previously dispatched command must be read back, never blindly resubmitted.
                return new(GitHostMutationOutcome.Uncertain, null);
            }
            IReadOnlyList<string> arguments;
            if (action == PublicationAction.MergeNormal)
                arguments = ["pr", "merge", snapshot.Number!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--repo", intent.Target.Repository, "--merge", "--match-head-commit", intent.HeadCommit];
            else
            {
                var body = Marker(intent.IdempotencyKey) + "\n" + ParentMarker();
                if (snapshot.Number is not null)
                {
                    if (!snapshot.Body.Contains(ParentMarker(), StringComparison.Ordinal) || snapshot.State != "open")
                        return new(GitHostMutationOutcome.Rejected, null);
                    body = snapshot.Body + "\n" + body;
                    arguments = ["pr", "edit", snapshot.Number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "--repo", intent.Target.Repository, "--body", body];
                }
                else
                    arguments = ["pr", "create", "--repo", intent.Target.Repository, "--head", Branch(intent.Target.SourceRef),
                        "--base", Branch(intent.Target.TargetRef), "--title", "Delivery Forge disposable publication", "--body", body];
            }
            EnsureGrant(mutation, action);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Attempts.TryAdd(attemptKey, intent)) return new(GitHostMutationOutcome.Uncertain, null);
            dispatched = true;
            using var actionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, grant!.Revocation);
            var actionBudget = parent!.Remaining < configuration.ActionTimeout ? parent.Remaining : configuration.ActionTimeout;
            actionDeadline.CancelAfter(actionBudget > TimeSpan.Zero ? actionBudget : TimeSpan.Zero);
            if (!ProcessGitHubCliExecutor.PinMatches(configuration)) throw Unavailable();
            pendingCommand = executor!.ExecuteAsync(arguments, actionDeadline.Token);
            GitHubCliResult response;
            try { response = await pendingCommand.WaitAsync(configuration.ActionTimeout, cancellationToken).ConfigureAwait(false); }
            catch { actionDeadline.Cancel(); throw; }
            // Non-zero exit can follow a committed effect; it never authorizes retry.
            var readback = await ReadSnapshot(intent.Target, intent.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            return Completed(readback, intent)
                ? new(GitHostMutationOutcome.Confirmed, readback.Observation.ResultId)
                : new(GitHostMutationOutcome.Uncertain, Complete(response) ? readback.Observation.ResultId : null);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (dispatched && pendingCommand is not null && !pendingCommand.IsCompleted)
            {
                UnsettledCommands[resource] = pendingCommand;
                ProcessGitHubCliExecutor.ObserveLate(pendingCommand);
            }
            return new(dispatched ? GitHostMutationOutcome.Uncertain : GitHostMutationOutcome.Rejected, null);
        }
        finally { if (entered) mutex.Release(); }
    }

    private async Task<Snapshot> ReadSnapshot(PublicationTarget target, string key, CancellationToken cancellationToken)
    {
        if (configuration is null || executor is null) throw Unavailable();
        ValidateTarget(target);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(configuration.ReadTimeout);
        var token = deadline.Token;
        if (!(await ProbeAsync(token).ConfigureAwait(false)).Supported) throw Unavailable();
        var repository = "repos/" + target.Repository;
        using var source = await Get(repository + "/git/ref/heads/" + Uri.EscapeDataString(Branch(target.SourceRef)), token).ConfigureAwait(false);
        using var destination = await Get(repository + "/git/ref/heads/" + Uri.EscapeDataString(Branch(target.TargetRef)), token).ConfigureAwait(false);
        var sourceHead = source.RootElement.GetProperty("object").GetProperty("sha").GetString()!;
        var targetHead = destination.RootElement.GetProperty("object").GetProperty("sha").GetString()!;
        if (!GitId(sourceHead) || !GitId(targetHead)) throw Unavailable();
        var owner = target.Repository.Split('/')[0];
        using var collection = await Get(repository + "/pulls?state=all&head=" + Uri.EscapeDataString(owner + ":" + Branch(target.SourceRef)) +
            "&base=" + Uri.EscapeDataString(Branch(target.TargetRef)) + "&per_page=100", token).ConfigureAwait(false);
        if (collection.RootElement.ValueKind != JsonValueKind.Array || collection.RootElement.GetArrayLength() >= 100) throw Unavailable();
        var candidates = collection.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
        var matching = candidates.Where(item => item.GetProperty("body").GetString()?.Contains(Marker(key), StringComparison.Ordinal) == true).ToArray();
        var open = candidates.Where(item => item.GetProperty("state").GetString() == "open").ToArray();
        if (matching.Length > 1 || matching.Length == 0 && open.Length > 1) throw Unavailable();
        int? number = matching.Length == 1 ? matching[0].GetProperty("number").GetInt32() : open.Length == 1 ? open[0].GetProperty("number").GetInt32() : null;
        string? prHead = null, resultId = null, mergedHead = null, state = null;
        var body = "";
        var autoMerge = false;
        if (number is not null)
        {
            using var pull = await Get(repository + "/pulls/" + number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), token).ConfigureAwait(false);
            var p = pull.RootElement;
            if (p.GetProperty("head").GetProperty("ref").GetString() != Branch(target.SourceRef) ||
                p.GetProperty("base").GetProperty("ref").GetString() != Branch(target.TargetRef) ||
                !string.Equals(p.GetProperty("head").GetProperty("repo").GetProperty("full_name").GetString(), target.Repository, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(p.GetProperty("base").GetProperty("repo").GetProperty("full_name").GetString(), target.Repository, StringComparison.OrdinalIgnoreCase)) throw Unavailable();
            prHead = p.GetProperty("head").GetProperty("sha").GetString();
            body = p.GetProperty("body").GetString() ?? "";
            state = p.GetProperty("state").GetString();
            resultId = p.GetProperty("html_url").GetString();
            if (number.Value <= 0 || !ResultUrl(resultId, target, number.Value) || !GitId(prHead)) throw Unavailable();
            autoMerge = p.GetProperty("auto_merge").ValueKind != JsonValueKind.Null;
            if (p.GetProperty("merged_at").ValueKind != JsonValueKind.Null && GitId(p.GetProperty("merge_commit_sha").GetString())) mergedHead = prHead;
        }
        var protection = GitHostProtection.Unknown;
        var checks = GitHostChecks.Unknown;
        var mergeQueue = false;
        try
        {
            using var rules = await Get(repository + "/rules/branches/" + Uri.EscapeDataString(Branch(target.TargetRef)), token).ConfigureAwait(false);
            if (rules.RootElement.ValueKind != JsonValueKind.Array) throw Unavailable();
            mergeQueue = rules.RootElement.EnumerateArray().Any(rule => rule.GetProperty("type").GetString() is "merge_queue" or "required_linear_history");
            using var protectedBranch = await Get(repository + "/branches/" + Uri.EscapeDataString(Branch(target.TargetRef)) + "/protection", token).ConfigureAwait(false);
            var r = protectedBranch.RootElement;
            if (r.TryGetProperty("required_status_checks", out var required) && required.ValueKind == JsonValueKind.Object &&
                r.TryGetProperty("required_pull_request_reviews", out var reviews) && reviews.ValueKind == JsonValueKind.Object)
            {
                var contexts = required.GetProperty("contexts").EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
                if (required.TryGetProperty("checks", out var requiredChecks))
                    foreach (var item in requiredChecks.EnumerateArray()) contexts.Add(item.GetProperty("context").GetString()!);
                if (contexts.Count > 0 && contexts.All(x => !string.IsNullOrWhiteSpace(x)))
                {
                    protection = GitHostProtection.RequiredAndObserved;
                    using var status = await Get(repository + "/commits/" + sourceHead + "/status", token).ConfigureAwait(false);
                    using var runs = await Get(repository + "/commits/" + sourceHead + "/check-runs?filter=latest&per_page=100", token).ConfigureAwait(false);
                    var checkRuns = runs.RootElement.GetProperty("check_runs");
                    var complete = runs.RootElement.GetProperty("total_count").GetInt32() == checkRuns.GetArrayLength();
                    var passed = new HashSet<string>(StringComparer.Ordinal);
                    var seenStatuses = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var s in status.RootElement.GetProperty("statuses").EnumerateArray())
                    {
                        var name = s.GetProperty("context").GetString()!;
                        if (seenStatuses.Add(name) && s.GetProperty("state").GetString() == "success") passed.Add(name);
                    }
                    foreach (var check in checkRuns.EnumerateArray())
                        if (check.GetProperty("status").GetString() == "completed" && check.GetProperty("conclusion").GetString() == "success")
                            passed.Add(check.GetProperty("name").GetString()!);
                    checks = complete && contexts.IsSubsetOf(passed) && status.RootElement.GetProperty("state").GetString() == "success"
                        ? GitHostChecks.Passing : GitHostChecks.Pending;
                }
                else protection = GitHostProtection.Missing;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { protection = GitHostProtection.Unknown; checks = GitHostChecks.Unknown; }
        var observation = new GitHostObservation(target, sourceHead, targetHead, prHead, checks, protection,
            body.Contains(Marker(key), StringComparison.Ordinal) ? key : null, resultId, mergedHead);
        token.ThrowIfCancellationRequested();
        return new(observation, number, body, state, mergeQueue || autoMerge);
    }

    private async Task<JsonDocument> Get(string endpoint, CancellationToken cancellationToken)
    {
        var result = await Command(["api", endpoint, "--hostname", "github.com", "--method", "GET", "-H", "Accept: application/vnd.github+json",
            "-H", "X-GitHub-Api-Version:2026-03-10"], configuration!.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (!Complete(result)) throw Unavailable();
        return JsonDocument.Parse(result.StandardOutput);
    }
    private async Task<GitHubCliResult> Command(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (configuration is null || executor is null || !ProcessGitHubCliExecutor.PinMatches(configuration)) throw Unavailable();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var task = executor.ExecuteAsync(args, deadline.Token);
        try { return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch { deadline.Cancel(); ProcessGitHubCliExecutor.ObserveLate(task); throw; }
    }
    private void EnsureGrant(GitHostMutation mutation, PublicationAction action)
    {
        if (configuration is null || executor is null || parent is null || grant is null || !parent.Owns(grant) ||
            !PublicationPreflight.SameTarget(parent.Target, mutation.Intent.Target) || mutation.Intent.Action != action ||
            mutation.Intent.State != PublicationIntentState.Sent ||
            string.IsNullOrWhiteSpace(mutation.Intent.IdempotencyKey) || mutation.Intent.IdempotencyKey.Length > 256 ||
            mutation.Intent.IdempotencyKey.Any(char.IsControl) || !GitId(mutation.Intent.BaseCommit) ||
            !GitId(mutation.Intent.HeadCommit) || !GitId(mutation.Intent.TreeId) || !GitId(mutation.ExpectedTargetHead) ||
            (action == PublicationAction.MergeNormal && parent.Ceiling != AuthorizationCeiling.Merge)) throw Unavailable();
        ValidateTarget(mutation.Intent.Target);
    }
    private static bool Exact(Snapshot s, GitHostMutation m, PublicationAction action) =>
        s.Observation.SourceHead == m.Intent.HeadCommit && s.Observation.TargetHead == m.ExpectedTargetHead &&
        (s.Observation.PrHead is null || s.Observation.PrHead == m.Intent.HeadCommit) && !s.QueueOrAutoMerge &&
        (action != PublicationAction.MergeNormal || s.Number is not null && s.State == "open" &&
            s.Observation.PrHead == m.Intent.HeadCommit && s.Observation.Checks == GitHostChecks.Passing &&
            s.Observation.Protection == GitHostProtection.RequiredAndObserved);
    private static bool Completed(Snapshot s, PublicationIntent i) => s.Observation.SourceHead == i.HeadCommit &&
        s.Observation.PrHead == i.HeadCommit && s.Observation.PublishedIdempotencyKey == i.IdempotencyKey &&
        s.Observation.ResultId is not null && (i.Action != PublicationAction.MergeNormal || s.Observation.MergedHead == i.HeadCommit);
    private static bool SameBinding(PublicationIntent a, PublicationIntent b) => a.RunIdentity == b.RunIdentity &&
        a.RevisionIdentity == b.RevisionIdentity && a.Action == b.Action && a.BaseCommit == b.BaseCommit &&
        a.HeadCommit == b.HeadCommit && a.TreeId == b.TreeId && PublicationPreflight.SameTarget(a.Target, b.Target);
    private static bool Complete(GitHubCliResult r) => r.ExitCode == 0 && !r.TimedOut && !r.ResidualProcessUncertain;
    private static string Marker(string key) => "<!-- delivery-forge-publication:" + Digest(key) + " -->";
    private string ParentMarker() => "<!-- delivery-forge-disposable-parent:" + Digest(parent!.AuthorizationReference) + " -->";
    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Branch(string reference) => reference["refs/heads/".Length..];
    private static void ValidateTarget(PublicationTarget t)
    {
        if (!Regex.IsMatch(t.Repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) ||
            t.SourceRef == t.TargetRef || new[] { t.SourceRef, t.TargetRef }.Any(reference =>
                !reference.StartsWith("refs/heads/", StringComparison.Ordinal) || reference.Length <= 11 ||
                reference.Contains("..", StringComparison.Ordinal) || reference.Contains("//", StringComparison.Ordinal) ||
                reference.EndsWith(".lock", StringComparison.Ordinal) || !Regex.IsMatch(reference, @"^[A-Za-z0-9_./-]+$", RegexOptions.CultureInvariant)))
            throw new ArgumentException("Explicit single-repository branch refs are required.");
    }
    private static bool GitId(string? value) => value is not null && Regex.IsMatch(value, @"^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant);
    private static bool ResultUrl(string? result, PublicationTarget target, int number) => Uri.TryCreate(result, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "github.com" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        string.Equals(uri.AbsolutePath, "/" + target.Repository + "/pull/" + number, StringComparison.OrdinalIgnoreCase);
    private sealed record Snapshot(GitHostObservation Observation, int? Number, string Body, string? State, bool QueueOrAutoMerge);
    private static PublicationUnavailableException Unavailable() => new("GitHub observation or current disposable-target parent authorization is unavailable.");
}
