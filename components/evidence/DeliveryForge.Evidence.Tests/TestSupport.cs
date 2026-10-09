using DeliveryForge.Evidence.Adapters;
using System.Text.Json;

namespace DeliveryForge.Evidence.Tests;

internal static class TestEvidence
{
    public static string Identity(char value) => $"sha256:{new string(value, 64)}";
    public static string Commit(char value) => new(value, 40);

    public static PolicyCandidate PolicyCandidate(bool fixture = false) =>
        new(Identity('1'), Identity('2'), Commit('a'), fixture, 0m, ["build", "test", "coverage"]);

    public static ResolvedEvidencePolicy ResolvedPolicy()
    {
        var candidate = PolicyCandidate();
        return EvidencePolicy.Resolve(candidate,
            new PolicyAuthority(PolicyAuthorityKind.ProtectedGitBase, candidate.SourceRevision, candidate.ContentIdentity));
    }

    public static DotNetRunEvidence DotNetRun(
        bool compiled = true, int discovered = 4, int executed = 4, bool coverage = true,
        IReadOnlyList<string>? expected = null, IReadOnlyList<string>? observed = null) =>
        new("10.0.401", 0, compiled, discovered, executed, 4, 0, 0, coverage,
            expected ?? ["A.Tests"], observed ?? ["A.Tests"], false);

    public static string CrapReport(bool completed, string decision, int unknown, string schema = "1.2") =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = schema,
            toolVersion = "1.2.0",
            complexityRulesetVersion = "callables-v1",
            evaluation = new
            {
                invocationMode = "check",
                policy = new { threshold = 30, allowMissingCoverage = false, comparisonOperator = "gt" },
                scope = new { logicalWorkspaceRoot = ".", sources = new[] { "A.cs" } },
                contexts = Array.Empty<object>(),
                checks = Array.Empty<object>(),
                metrics = Array.Empty<object>(),
                findings = Array.Empty<object>(),
                coverage = new { methods = 1, known = 1 - unknown, unknown, reasons = new { } },
                artifacts = Array.Empty<object>(),
                decision = new { completed, policyDecision = decision, reason = "fixture" }
            },
            run = new
            {
                invocationId = "00000000-0000-0000-0000-000000000001",
                startedAt = "2026-10-09T00:00:00Z",
                finishedAt = "2026-10-09T00:00:01Z",
                durationMilliseconds = 1000,
                commands = Array.Empty<object>(),
                diagnosticLocations = Array.Empty<object>(),
                artifacts = Array.Empty<object>(),
                cancellation = new { cancelled = false, timedOut = false, reason = (string?)null },
                status = "completed",
                exitCode = decision == "fail" ? 1 : 0
            }
        });

    public static RepositoryIdentity Repository() => new(Commit('a'), Commit('b'), Commit('c'), true);
    public static CommandResult CommandResult() => new(
        0, "stdout", "stderr", false, false,
        DateTimeOffset.Parse("2026-10-09T00:00:00Z"), DateTimeOffset.Parse("2026-10-09T00:00:01Z"));
    public static GateRequest GateRequest(string output) => new(
        "test", Identity('2'), Identity('5'), Identity('6'), Repository(),
        new CommandInvocation("dotnet", ["test", "DeliveryForge.slnx", "--no-restore"], "/repo"),
        "full", TimeSpan.FromSeconds(30), output,
        _ => NormalizedEvidence.Pass("complete test evidence", true));
}

internal sealed class StubCommandExecutor(CommandResult result) : ICommandExecutor
{
    public Task<CommandResult> ExecuteAsync(
        CommandInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(result);
}

internal sealed class SequenceRepositoryIdentityReader(params RepositoryIdentity[] identities) : IRepositoryIdentityReader
{
    private int _index;
    public Task<RepositoryIdentity> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(identities[Math.Min(_index++, identities.Length - 1)]);
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "delivery-forge-evidence-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, true);
    }
}
