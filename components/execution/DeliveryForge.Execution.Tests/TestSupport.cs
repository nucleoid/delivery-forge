using System.Diagnostics;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Execution.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"delivery-forge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed class GitFixture : IAsyncDisposable
{
    private GitFixture(TemporaryDirectory directory, string head) { Directory = directory; Head = head; }
    private TemporaryDirectory Directory { get; }
    public string Path => Directory.Path;
    public string Head { get; }

    public async Task<(string Head, string Tree)> CommitAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
        await RunAsync(Path, cancellationToken, "add", "--", relativePath);
        await RunAsync(Path, cancellationToken, "commit", "-m", $"change {relativePath}");
        return await RevisionAsync(cancellationToken);
    }

    public async Task<(string Head, string Tree)> RevisionAsync(CancellationToken cancellationToken)
    {
        var head = (await RunAsync(Path, cancellationToken, "rev-parse", "HEAD^{commit}")).Trim();
        var tree = (await RunAsync(Path, cancellationToken, "rev-parse", "HEAD^{tree}")).Trim();
        return (head, tree);
    }

    public static async Task<GitFixture> CreateAsync(CancellationToken cancellationToken)
    {
        var directory = new TemporaryDirectory();
        await RunAsync(directory.Path, cancellationToken, "init");
        await RunAsync(directory.Path, cancellationToken, "config", "user.email", "test@example.invalid");
        await RunAsync(directory.Path, cancellationToken, "config", "user.name", "Delivery Forge Test");
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "tracked.txt"), "initial", cancellationToken);
        await RunAsync(directory.Path, cancellationToken, "add", "tracked.txt");
        await RunAsync(directory.Path, cancellationToken, "commit", "-m", "initial");
        var head = (await RunAsync(directory.Path, cancellationToken, "rev-parse", "HEAD")).Trim();
        return new GitFixture(directory, head);
    }

    public static async Task<string> RunAsync(string cwd, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output;
    }

    public ValueTask DisposeAsync() { Directory.Dispose(); return ValueTask.CompletedTask; }
}

internal sealed class ProvenTestAgentControl : IAgentControlPort
{
    public Task<AgentControlResult> ControlAsync(
        AgentRunBinding binding,
        AgentControlAction action,
        TimeSpan deadline,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentControlResult(binding, action,
            action == AgentControlAction.Resume ? AgentActivityState.Live : AgentActivityState.Quiescent, true,
            $"test-proof:{binding.AdapterId}:{binding.RuntimeTaskIdentity}:{action}", ""));
}

internal sealed class FixedTestAgentControl(
    Func<AgentRunBinding, AgentControlAction, AgentControlResult> resultFactory) : IAgentControlPort
{
    public Task<AgentControlResult> ControlAsync(
        AgentRunBinding binding,
        AgentControlAction action,
        TimeSpan deadline,
        CancellationToken cancellationToken = default) => Task.FromResult(resultFactory(binding, action));
}

internal sealed class ThrowingExecutionFactSource : IExecutionFactSource
{
    public Task<ExecutionOwnedFacts> ObserveAsync(
        AgentRequest request,
        DeliveryForge.Execution.Git.WorktreeObservation worktree,
        CancellationToken cancellationToken = default) =>
        throw new WorkerEnvelopeException("fact observation failed after control");
}

internal sealed class MutableExecutionFactSource : IExecutionFactSource
{
    public string AuthorizationCeiling { get; set; } = "implement";

    public Task<ExecutionOwnedFacts> ObserveAsync(
        AgentRequest request,
        DeliveryForge.Execution.Git.WorktreeObservation worktree,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ExecutionOwnedFacts(
            WorkerEnvelope.ComputeRequestIdentity(request), request.PlanIdentity, request.BaseCommit,
            AuthorizationCeiling, []));
}
