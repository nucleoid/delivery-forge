using System.Security.Cryptography;
using System.Text.Json;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Execution;

public sealed record FrozenWorkerPlan(
    string RunId,
    string PlanIdentity,
    string BaseCommit,
    string Repository,
    string ParentCheckout,
    string Worktree,
    string ResultDirectory,
    string WriterToken,
    IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<string> Exclusions);

public static class WorkerEnvelope
{
    public const string SchemaVersion = "1.0.0";
    public const string SupportedHostCapability = "sessions_spawn";

    public static AgentRequest PrepareWorker(FrozenWorkerPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Require(plan.RunId, nameof(plan.RunId));
        Require(plan.PlanIdentity, nameof(plan.PlanIdentity));
        RequireGitObject(plan.BaseCommit, nameof(plan.BaseCommit));
        Require(plan.Repository, nameof(plan.Repository));
        Require(plan.WriterToken, nameof(plan.WriterToken));
        if (!Path.IsPathFullyQualified(plan.Worktree) || !Path.IsPathFullyQualified(plan.ResultDirectory))
        {
            throw new WorkerEnvelopeException("Worktree and result directory must be absolute local paths.");
        }

        var allowed = plan.AllowedPaths.Select(NormalizeRelative).Distinct(StringComparer.Ordinal).Order().ToArray();
        var exclusions = plan.Exclusions.Select(NormalizeRelative).Distinct(StringComparer.Ordinal).Order().ToArray();
        if (allowed.Length == 0) throw new WorkerEnvelopeException("At least one allowed path is required.");
        var request = new AgentRequest(SchemaVersion, plan.RunId, "", plan.PlanIdentity, plan.BaseCommit, plan.Repository,
            Path.GetFullPath(plan.Worktree), Path.GetFullPath(plan.ResultDirectory), plan.WriterToken,
            allowed, exclusions);
        return request with { RequestIdentity = ComputeRequestIdentity(request) };
    }

    public static string ComputeRequestIdentity(AgentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = request with { RequestIdentity = "" };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }

    public static void ValidateAccepted(AgentRequest request, AgentAcceptedReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(receipt);
        if (request.SchemaVersion != SchemaVersion || receipt.SchemaVersion != SchemaVersion ||
            request.RequestIdentity != ComputeRequestIdentity(request) ||
            request.RunId != receipt.RunId || request.RequestIdentity != receipt.RequestIdentity ||
            request.PlanIdentity != receipt.PlanIdentity || request.BaseCommit != receipt.BaseCommit ||
            !SamePath(request.Worktree, receipt.Worktree) || receipt.HostCapability != SupportedHostCapability ||
            string.IsNullOrWhiteSpace(receipt.HostRunId) || string.IsNullOrWhiteSpace(receipt.ChildIdentity))
        {
            throw new WorkerEnvelopeException("Accepted receipt does not bind the supported host run to the immutable prepared request.");
        }
    }

    public static void ValidateCompletion(AgentRequest request, AgentAcceptedReceipt accepted, AgentCompletionReceipt completion)
    {
        ValidateAccepted(request, accepted);
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.SchemaVersion != SchemaVersion || accepted.RunId != completion.RunId ||
            accepted.RequestIdentity != completion.RequestIdentity || accepted.HostRunId != completion.HostRunId ||
            accepted.ChildIdentity != completion.ChildIdentity || !SamePath(request.Worktree, completion.Worktree) ||
            !string.Equals(completion.Outcome, "success", StringComparison.Ordinal) ||
            completion.CompletedAt < accepted.AcceptedAt)
        {
            throw new WorkerEnvelopeException("Completion receipt does not bind a successful result from the accepted host worker.");
        }

        foreach (var artifact in completion.ArtifactPaths)
        {
            if (!Path.IsPathFullyQualified(artifact) || !IsWithin(artifact, request.ResultDirectory) || !File.Exists(artifact))
                throw new WorkerEnvelopeException("Completion artifacts must be existing files inside the immutable request result directory.");
        }

        RequireGitObject(completion.HeadCommit, nameof(completion.HeadCommit));
        RequireGitObject(completion.TreeId, nameof(completion.TreeId));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathFullyQualified(relative);
    }

    private static string NormalizeRelative(string value)
    {
        Require(value, "path");
        if (Path.IsPathFullyQualified(value)) throw new WorkerEnvelopeException("Allowed and excluded paths must be repository-relative.");
        var normalized = value.Replace('\\', '/').Trim('/');
        if (normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new WorkerEnvelopeException($"Path '{value}' is not a normalized repository-relative path.");
        return normalized;
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new WorkerEnvelopeException($"{name} is required.");
    }

    private static void RequireGitObject(string value, string name)
    {
        if (value.Length is not (40 or 64) || value.Any(character => !Uri.IsHexDigit(character)) || value != value.ToLowerInvariant())
            throw new WorkerEnvelopeException($"{name} must be a lowercase Git object id.");
    }
}

public sealed class WorkerEnvelopeException(string message) : Exception(message);
