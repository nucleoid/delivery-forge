using System.Reflection;
using System.Text.Json;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Scheduling;
using DeliveryForge.Tool.Claude;
using DeliveryForge.Tool.Codex;

namespace DeliveryForge.Tool.Tests;

// Private-state acceptance-boundary DATA controls. No native process is started,
// no accepted receipt is fabricated, and no installed-host authority is issued.
public sealed class ForeignHostWaitTests
{
    [Theory(Timeout = 30000)]
    [InlineData("codex.cli")]
    [InlineData("claude-code.cli")]
    public async Task Foreign_wait_does_not_stop_usage_or_consume_the_legitimate_pending_wait(string adapter)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var slots = new ResourceSlots(4, 2, 4096, 1, 1, 0);
        var budget = ResourceBudget.Measure(slots, new("wait-boundary-DATA", true, slots, TimeSpan.FromMinutes(10), 100000, 100));
        var usage = ReportedModelUsage.Bind(budget, 100000, 100000).Register();
        usage.EnterModelWrite(); usage.Observe(10, 2);
        var before = usage.Evidence();
        var stops = 0;
        var watcher = usage.WatchAsync(() => { Interlocked.Increment(ref stops); return Task.CompletedTask; });
        var profile = new AgentAdapterProfile(adapter, "boundary-DATA", [new(AgentCapability.Launch,
            AgentCapabilityStatus.Unverified, "Managed wait-boundary DATA only", "No native authority")]);
        IHostToolSessionPort port = adapter == "codex.cli"
            ? new CodexAppServerPort(new("unopened-DATA", 1, new string('a', 64), "boundary-DATA"), new WorktreeManager(), profile)
            : new ClaudeStreamPort(new("unopened-DATA", 1, new string('a', 64), "boundary-DATA"), new WorktreeManager(), profile);
        ((IOriginalBoundHostPort)port).BindOriginal(original);
        var binding = new AgentRunBinding(adapter, "boundary-DATA", "private-run-DATA", new string('b', 64), "private-session-DATA", "private-task-DATA");
        Set(port, "binding", binding); Set(port, "usage", usage); Set(port, "usageWatch", watcher);
        var codexTerminal = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task terminal;
        Action<Exception> failTerminal;
        if (port is CodexAppServerPort)
        {
            Set(port, "terminalWait", codexTerminal); terminal = codexTerminal.Task;
            failTerminal = failure => codexTerminal.TrySetException(failure);
        }
        else
        {
            var claudeTerminal = Assert.IsType<TaskCompletionSource<bool>>(Field(port, "settled").GetValue(port));
            terminal = claudeTerminal.Task; failTerminal = failure => claudeTerminal.TrySetException(failure);
        }
        var terminalFailure = new IOException("Original terminal DATA failure after legitimate wait entry.");
        Task<AgentCompletionReceipt>? legitimate = null;
        try
        {
            await Assert.ThrowsAsync<WorkerEnvelopeException>(() => port.WaitAsync(
                binding with { RuntimeTaskIdentity = "foreign-task-DATA" }, original.Remaining, original.Token));
            Assert.Equal(before, usage.Evidence()); Assert.Null(usage.Failure);
            Assert.Equal(0, Volatile.Read(ref stops)); Assert.False(watcher.IsCompleted);
            Assert.False(terminal.IsCompleted); Assert.Empty(Tasks(port)); Assert.False(Get<bool>(port, "shutdown"));
            Assert.Null(Process(port)); original.Check();

            var exactWait = port.WaitAsync(binding, original.Remaining, original.Token); legitimate = exactWait;
            Assert.False(exactWait.IsCompleted); Assert.Same(terminal, Assert.Single(Tasks(port)));
            Assert.Equal(before, usage.Evidence()); Assert.Equal(0, Volatile.Read(ref stops));
            // A failure of the exact pending completion still enters the existing
            // usage failure boundary. Foreign rejection must not weaken it.
            failTerminal(terminalFailure);
            Assert.Same(terminalFailure, await Assert.ThrowsAsync<IOException>(() => exactWait));
            await watcher.WaitAsync(original.Remaining, original.Token);
            Assert.Equal(1, Volatile.Read(ref stops)); Assert.NotNull(usage.Failure);
            Assert.True(usage.Evidence().Stopped); Assert.Null(Process(port));
        }
        finally
        {
            if (legitimate is not null)
            {
                if (!terminal.IsCompleted) failTerminal(terminalFailure);
                try { await legitimate; } catch (IOException) { }
            }
            await usage.JoinAsync(watcher);
        }
    }

    static FieldInfo Field(object port, string name) => port.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Required existing private port field missing: " + name);
    static void Set(object port, string name, object value) => Field(port, name).SetValue(port, value);
    static T Get<T>(object port, string name) => (T)Field(port, name).GetValue(port)!;
    static IReadOnlyList<Task> Tasks(IHostToolSessionPort port) => port switch
    {
        CodexAppServerPort codex => codex.OriginalTasks,
        ClaudeStreamPort claude => claude.OriginalTasks,
        _ => throw new InvalidOperationException("Closed test adapter selection required.")
    };
    static OwnedDuplexProcess? Process(IHostToolSessionPort port) => port switch
    {
        CodexAppServerPort codex => codex.OriginalProcess,
        ClaudeStreamPort claude => claude.OriginalProcess,
        _ => throw new InvalidOperationException("Closed test adapter selection required.")
    };
}
