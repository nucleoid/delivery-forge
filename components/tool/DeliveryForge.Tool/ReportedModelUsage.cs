using System.Runtime.CompilerServices;
using System.Text.Json;
using DeliveryForge.Scheduling;

namespace DeliveryForge.Tool;

// Reported spend, not provider-side reservation or an absolute token ceiling.
// A budget OBJECT identifies the containing run; node/session lifetimes cannot reset it.
internal sealed class ReportedModelUsage
{
    internal sealed record UsageSnapshot(long InputTokens, long OutputTokens, long NodeTokens, long AggregateTokens,
        long SessionStopAt, long NodeStopAt, long AggregateStopAt, bool UsageObserved, bool FinalUsageKnown, bool StopThresholdReached, bool Stopped);
    static readonly ConditionalWeakTable<ResourceBudget, ReportedModelUsage> Runs = new();
    readonly object gate = new();
    readonly long maximum;
    readonly TaskCompletionSource<Exception> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long consumed;
    int registrations;
    Exception? failure;
    ReportedModelUsage(long maximum) => this.maximum = maximum;
    internal static UsageNode Bind(ResourceBudget? budget, long policyMaximum, long nodeMaximum)
    {
        if (budget is null) return new(new(0), 0);
        var limit = Math.Min(100000, policyMaximum);
        if (limit <= 0 || nodeMaximum <= 0 || nodeMaximum > policyMaximum) throw new ToolUsageException("Finite existing production token policy required.");
        var run = Runs.GetValue(budget, _ => new(limit));
        lock (run.gate)
        {
            if (run.maximum != limit) throw new ToolUsageException("Same run cannot change its reported token threshold.");
            run.Check(); return new(run, Math.Min(nodeMaximum, limit));
        }
    }
    void Check()
    {
        if (maximum <= 0) throw new ReportedUsageException("Production model dispatch requires the existing finite scheduling policy.");
        if (failure is not null) throw new ReportedUsageException("Containing run reported usage stopped; no further forward work.", failure);
    }
    void Fail(Exception error) { failure ??= error; stopped.TrySetResult(failure); }
    internal sealed class UsageNode
    {
        readonly ReportedModelUsage run;
        readonly long maximum;
        long consumed;
        internal UsageNode(ReportedModelUsage run, long maximum) { this.run = run; this.maximum = maximum; }
        internal void Check() { lock (run.gate) run.Check(); }
        internal UsageSession Register(long sessionMaximum = long.MaxValue)
        {
            lock (run.gate)
            {
                run.Check();
                if (++run.registrations > 4096 || maximum <= 0 || sessionMaximum <= 0) throw new ReportedUsageException("Finite containing-run model session inventory required.");
                return new(this, Math.Min(maximum, sessionMaximum));
            }
        }
        internal sealed class UsageSession(UsageNode node, long maximum)
        {
            long input, output;
            bool seen, final;
            readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Exception? Failure { get { lock (node.run.gate) return node.run.failure; } }
            internal void Check() => node.Check();
            internal void Refuse()
            {
                lock (node.run.gate) node.run.Fail(new ReportedUsageException("Missing, malformed, foreign or regressing reported model usage; completion refused."));
            }
            internal void Observe(long actualInput, long actualOutput, bool isFinal = false)
            {
                lock (node.run.gate)
                {
                    // Continue charging late/in-flight reports after the stop latch.
                    try
                    {
                        if (actualInput < input || actualOutput < output || actualInput < 0 || actualOutput < 0)
                            throw new ReportedUsageException("Reported model usage is not a fresh monotone cumulative stream.");
                        var total = checked(actualInput + actualOutput);
                        var delta = checked(total - checked(input + output));
                        var nextNode = checked(node.consumed + delta); var nextRun = checked(node.run.consumed + delta);
                        node.consumed = nextNode; node.run.consumed = nextRun;
                        input = actualInput; output = actualOutput; seen = true; final |= isFinal;
                        if (total >= maximum || node.consumed >= node.maximum || node.run.consumed >= node.run.maximum)
                            node.run.Fail(new ReportedUsageException("Reported model usage stop threshold reached; in-flight overshoot may already exist."));
                    }
                    catch (Exception error) when (error is OverflowException or ReportedUsageException) { node.run.Fail(new ReportedUsageException("Invalid or exhausted reported model usage.", error)); }
                }
            }
            internal void RequireFinal()
            {
                lock (node.run.gate)
                {
                    if (!seen || !final) node.run.Fail(new ReportedUsageException("Final correlated model usage is required; missing usage is not zero."));
                    node.run.Check();
                }
            }
            internal void MarkFinal()
            {
                lock (node.run.gate) { if (final) Refuse(); else final = true; }
            }
            internal async Task WatchAsync(Func<Task> stop)
            {
                // TCS continuations are asynchronous: no cancellation callbacks or
                // owner retirement run on the output-pump observation stack.
                if (await Task.WhenAny(node.run.stopped.Task, finished.Task).ConfigureAwait(false) == node.run.stopped.Task)
                    await stop().ConfigureAwait(false);
            }
            internal void Finish() => finished.TrySetResult();
            internal async Task JoinAsync(Task? watcher, Exception? primary = null)
            {
                Finish();
                if (watcher is null) return;
                try { await watcher.ConfigureAwait(false); }
                catch (Exception cleanup) when (primary is not null) { throw new AggregateException("Reported usage failure and actual stop failure.", primary, cleanup); }
            }
            internal T Accept<T>(Func<T> receipt)
            {
                lock (node.run.gate) { RequireFinal(); var value = receipt(); RequireFinal(); return value; }
            }
            internal UsageSnapshot Evidence()
            {
                lock (node.run.gate) return new(input, output, node.consumed, node.run.consumed,
                    maximum, node.maximum, node.run.maximum, seen, seen && final,
                    seen && (checked(input + output) >= maximum || node.consumed >= node.maximum || node.run.consumed >= node.run.maximum), node.run.failure is not null);
            }
            internal (long Input, long Output, long Aggregate) Snapshot() { lock (node.run.gate) return (input, output, node.run.consumed); }
        }
    }
}

internal sealed class ReportedUsageException(string message, Exception? inner = null) : IOException(message, inner);

internal static class UsageNumbers
{
    internal static long Number(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count(p => p.Name == key) != 1 || !value.TryGetProperty(key, out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt64(out var result) || result < 0)
            throw new ReportedUsageException("Reported token count must be a nonnegative Int64.");
        return result;
    }
    internal static string Id(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count(p => p.Name == key) != 1 || !value.TryGetProperty(key, out var n) || n.ValueKind != JsonValueKind.String || n.GetString() is not { Length: > 0 and <= 4096 } result || result.Any(char.IsControl))
            throw new ReportedUsageException("Reported usage requires bounded actual wire identity.");
        return result;
    }
    internal static JsonElement Object(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count(p => p.Name == key) != 1 || !value.TryGetProperty(key, out var result) || result.ValueKind != JsonValueKind.Object)
            throw new ReportedUsageException("Reported usage requires one numeric object.");
        return result;
    }
}

internal sealed class CodexReportedUsage(ReportedModelUsage.UsageNode.UsageSession session)
{
    readonly object gate = new();
    string? thread, turn, provisionalTurn;
    bool turnEntered;
    internal void BindThread(string actual) { lock (gate) { if (thread is not null) session.Refuse(); else thread = actual; } }
    internal void BeginTurn() { lock (gate) { session.Check(); if (thread is null || turnEntered) { session.Refuse(); session.Check(); } turnEntered = true; } }
    internal void BindTurn(string actual) { lock (gate) { if (!turnEntered || turn is not null || provisionalTurn is not null && provisionalTurn != actual) session.Refuse(); else turn = actual; } }
    void Correlate(JsonElement value)
    {
        if (!turnEntered || thread is null || UsageNumbers.Id(value, "threadId") != thread) throw new ReportedUsageException("Foreign reported usage thread.");
        var actual = UsageNumbers.Id(value, "turnId");
        if (turn is not null ? actual != turn : provisionalTurn is not null && actual != provisionalTurn) throw new ReportedUsageException("Foreign reported usage turn.");
        provisionalTurn ??= actual;
    }
    internal void Observe(JsonElement parameters)
    {
        try
        {
            lock (gate)
            {
                Correlate(parameters); var u = UsageNumbers.Object(UsageNumbers.Object(parameters, "tokenUsage"), "total");
                var input = UsageNumbers.Number(u, "inputTokens"); var output = UsageNumbers.Number(u, "outputTokens");
                if (UsageNumbers.Number(u, "cachedInputTokens") > input || UsageNumbers.Number(u, "reasoningOutputTokens") > output || UsageNumbers.Number(u, "totalTokens") != checked(input + output)) throw new ReportedUsageException("Inconsistent cumulative Codex token counts.");
                session.Observe(input, output); // Cached input/reasoning are subsets, not extra spend.
            }
        }
        catch (Exception error) when (error is ReportedUsageException or InvalidOperationException or KeyNotFoundException or OverflowException) { session.Refuse(); }
    }
    internal void Terminal(JsonElement parameters)
    {
        try
        {
            lock (gate)
            {
                var actual = UsageNumbers.Object(parameters, "turn");
                Correlate(JsonSerializer.SerializeToElement(new { threadId = UsageNumbers.Id(parameters, "threadId"), turnId = UsageNumbers.Id(actual, "id") }));
                session.MarkFinal();
            }
        }
        catch (Exception error) when (error is ReportedUsageException or InvalidOperationException or KeyNotFoundException) { session.Refuse(); }
    }
    internal static JsonElement Projection(JsonElement message)
    {
        try
        {
            var p = UsageNumbers.Object(message, "params"); var u = UsageNumbers.Object(UsageNumbers.Object(p, "tokenUsage"), "total");
            return JsonSerializer.SerializeToElement(new { method = "thread/tokenUsage/updated", @params = new { threadId = UsageNumbers.Id(p, "threadId"), turnId = UsageNumbers.Id(p, "turnId"), tokenUsage = new { total = new { inputTokens = UsageNumbers.Number(u, "inputTokens"), outputTokens = UsageNumbers.Number(u, "outputTokens"), cachedInputTokens = UsageNumbers.Number(u, "cachedInputTokens"), reasoningOutputTokens = UsageNumbers.Number(u, "reasoningOutputTokens"), totalTokens = UsageNumbers.Number(u, "totalTokens") } } } });
        }
        catch (ReportedUsageException) { return JsonSerializer.SerializeToElement(new { method = "thread/tokenUsage/updated", @params = new { usageUnavailable = true } }); }
    }
}

internal sealed class ClaudeReportedUsage(ReportedModelUsage.UsageNode.UsageSession session)
{
    readonly Dictionary<string, Counts> messages = new(StringComparer.Ordinal);
    string? active;
    Counts? finalCounts;
    readonly record struct Counts(long Input, long CacheCreate, long CacheRead, long Output)
    {
        internal long AllInput => checked(Input + checked(CacheCreate + CacheRead));
    }
    internal void Observe(JsonElement row, string actualSession)
    {
        try
        {
            var type = UsageNumbers.Id(row, "type");
            if (type is not ("assistant" or "stream_event" or "result")) return;
            if (UsageNumbers.Id(row, "session_id") != actualSession) throw new ReportedUsageException("Foreign reported Claude session.");
            if (type == "result")
            {
                if (finalCounts is not null) throw new ReportedUsageException("Duplicate final Claude usage.");
                var u = UsageNumbers.Object(row, "usage");
                var actual = new Counts(UsageNumbers.Number(u, "input_tokens"), UsageNumbers.Number(u, "cache_creation_input_tokens"), UsageNumbers.Number(u, "cache_read_input_tokens"), UsageNumbers.Number(u, "output_tokens"));
                var sum = Sum();
                if (Regresses(actual, sum)) throw new ReportedUsageException("Final Claude usage regresses observed messages.");
                finalCounts = actual; session.Observe(actual.AllInput, actual.Output, true); return;
            }
            if (type == "assistant")
            {
                var message = UsageNumbers.Object(row, "message");
                if (message.TryGetProperty("usage", out _)) Update(UsageNumbers.Id(message, "id"), UsageNumbers.Object(message, "usage"));
                return;
            }
            var e = UsageNumbers.Object(row, "event"); var kind = UsageNumbers.Id(e, "type");
            if (kind == "message_start")
            {
                if (active is not null) throw new ReportedUsageException("Overlapping Claude message usage.");
                var message = UsageNumbers.Object(e, "message"); active = UsageNumbers.Id(message, "id"); Update(active, UsageNumbers.Object(message, "usage"));
            }
            else if (kind == "message_delta" && e.TryGetProperty("usage", out _)) Update(active ?? throw new ReportedUsageException("Unbound Claude message usage."), UsageNumbers.Object(e, "usage"));
            else if (kind == "message_stop") { if (active is null) throw new ReportedUsageException("Unbound Claude message stop."); active = null; }
            else if (e.TryGetProperty("usage", out _)) throw new ReportedUsageException("Unsupported reported usage event.");
        }
        catch (Exception error) when (error is ReportedUsageException or InvalidOperationException or KeyNotFoundException or OverflowException) { session.Refuse(); }
    }
    void Update(string id, JsonElement usage)
    {
        if (!messages.TryGetValue(id, out var prior) && messages.Count >= 4096) throw new ReportedUsageException("Finite reported message inventory exceeded.");
        if (!new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens" }.Any(name => usage.TryGetProperty(name, out _))) throw new ReportedUsageException("Numeric message usage is missing.");
        long Part(string name, long previous) => usage.TryGetProperty(name, out _) ? UsageNumbers.Number(usage, name) : previous;
        var next = new Counts(Part("input_tokens", prior.Input), Part("cache_creation_input_tokens", prior.CacheCreate), Part("cache_read_input_tokens", prior.CacheRead), Part("output_tokens", prior.Output));
        if (Regresses(next, prior)) throw new ReportedUsageException("Claude message usage regressed.");
        messages[id] = next; var sum = Sum();
        // Final CLI result is cumulative across messages. It replaces, never
        // adds to, intermediate totals. Unexpected late spend remains charged.
        session.Observe(Math.Max(sum.AllInput, finalCounts?.AllInput ?? 0), Math.Max(sum.Output, finalCounts?.Output ?? 0));
        if (finalCounts is { } actual && Regresses(actual, sum)) session.Refuse();
    }
    static bool Regresses(Counts next, Counts prior) => next.Input < prior.Input || next.CacheCreate < prior.CacheCreate || next.CacheRead < prior.CacheRead || next.Output < prior.Output;
    Counts Sum()
    {
        long input = 0, create = 0, read = 0, output = 0;
        foreach (var c in messages.Values) { input = checked(input + c.Input); create = checked(create + c.CacheCreate); read = checked(read + c.CacheRead); output = checked(output + c.Output); }
        return new(input, create, read, output);
    }
}
