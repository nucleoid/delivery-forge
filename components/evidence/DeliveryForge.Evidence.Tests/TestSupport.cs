using DeliveryForge.Evidence.Adapters;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeliveryForge.Evidence.Tests;

internal static class TestEvidence
{
    public static string Identity(char value) => $"sha256:{new string(value, 64)}";
    public static string Commit(char value) => new(value, 40);
    public static DateTimeOffset Time(int seconds) =>
        DateTimeOffset.Parse("2026-10-09T00:00:00Z").AddSeconds(seconds);

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
    public static GateRequest GateRequest(string output)
    {
        var executable = System.IO.Path.Combine(output, "fixture-dotnet");
        File.WriteAllText(executable, "fixture executable");
        var identity = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable)))}";
        return new GateRequest(
            "test", ResolvedPolicy(),
            new ToolCapability("dotnet", "10.0.401", "trx-v1", true, true, [], executable, identity, ["test"]),
            EvidenceAdapterKind.DotNet,
            Identity('6'), Repository(),
            new CommandInvocation(executable, ["test", "DeliveryForge.Tests.csproj", "--no-restore"], output),
            "full", TimeSpan.FromSeconds(30), output);
    }
}

internal sealed class QueueCommandExecutor(params CommandResult[] results) : ICommandExecutor
{
    private readonly Queue<CommandResult> _results = new(results);

    public Task<CommandResult> ExecuteAsync(
        CommandInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult(_results.Dequeue());
}

internal sealed class MutatingCommandExecutor(string executable, CommandResult result) : ICommandExecutor
{
    public Task<CommandResult> ExecuteAsync(
        CommandInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        File.AppendAllText(executable, "changed");
        return Task.FromResult(result);
    }
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

internal sealed class ThrowingRepositoryIdentityReader : IRepositoryIdentityReader
{
    public Task<RepositoryIdentity> ReadAsync(CancellationToken cancellationToken) =>
        throw new EvidenceRepositoryException("fixture repository failure");
}

internal sealed class FirstThenThrowRepositoryIdentityReader(RepositoryIdentity identity)
    : IRepositoryIdentityReader
{
    private bool _read;

    public Task<RepositoryIdentity> ReadAsync(CancellationToken cancellationToken)
    {
        if (_read)
            throw new EvidenceRepositoryException("fixture repository failure");

        _read = true;
        return Task.FromResult(identity);
    }
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
        if (!Directory.Exists(Path))
            return;

        var pending = new Stack<string>();
        var directories = new List<string>();
        pending.Push(Path);
        directories.Add(Path);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (attributes.HasFlag(FileAttributes.Directory))
                        Directory.Delete(entry);
                    else
                        File.Delete(entry);
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                    directories.Add(entry);
                }
                else
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
            }
        }

        foreach (var directory in directories.OrderByDescending(value => value.Length))
            File.SetAttributes(directory, FileAttributes.Normal);
        Directory.Delete(Path, true);
    }
}
