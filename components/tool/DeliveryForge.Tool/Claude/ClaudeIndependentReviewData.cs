using System.Globalization;
using System.Text.Json;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Review;

namespace DeliveryForge.Tool.Claude;

// Parsed native DATA only. These helpers cannot issue capabilities or receipts.
internal static class ClaudeIndependentReviewData
{
    // Closed session-only restrictions retain the selected user source and
    // configured model/auth; no settings file or global configuration is written.
    internal const string SessionSafetySettings = "{\"disableAllHooks\":true,\"permissions\":{\"defaultMode\":\"dontAsk\"}}";
    internal static string[] IsolatedObservationArguments(string[] arguments)
    {
        if (!arguments.SequenceEqual(new[] { "--version" }) && !arguments.SequenceEqual(new[] { "auth", "status" }))
            throw new ToolUsageException("Only the existing read-only reviewer observations are supported.");
        return ["--safe-mode", "--setting-sources", "user", "--settings", SessionSafetySettings, .. arguments];
    }
    internal static string[] IsolatedReviewArguments(decimal maximumCost) =>
        ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--replay-user-messages", "--safe-mode", "--setting-sources", "user", "--settings", SessionSafetySettings, "--no-session-persistence", "--strict-mcp-config", "--tools", "", "--permission-mode", "dontAsk", "--max-budget-usd", maximumCost.ToString(CultureInfo.InvariantCulture)];

    // Validate the ACTUAL argv before birth when selected user settings use
    // the private personal-host profile. This is no capability or file effect.
    internal static void RequirePinnedSettingsIsolation(IReadOnlyList<string> arguments, bool observation)
    {
        if (observation)
        {
            if (arguments.SequenceEqual(new[] { "--safe-mode", "--setting-sources", "user", "--settings", SessionSafetySettings, "--version" }) ||
                arguments.SequenceEqual(new[] { "--safe-mode", "--setting-sources", "user", "--settings", SessionSafetySettings, "auth", "status" })) return;
        }
        else
        {
            string[] prefix = ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--replay-user-messages", "--safe-mode", "--setting-sources", "user", "--settings", SessionSafetySettings, "--no-session-persistence", "--strict-mcp-config", "--tools", "", "--permission-mode", "dontAsk", "--max-budget-usd"];
            if (arguments.Count == prefix.Length + 1 && arguments.Take(prefix.Length).SequenceEqual(prefix) &&
                decimal.TryParse(arguments[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var cost) && cost > 0) return;
        }
        throw new ToolUsageException("Selected reviewer settings require the exact isolated native invocation.");
    }

    // Compare the actual explicit child overrides, not ambient/profile DATA.
    // Compaction is disabled for the complete bounded review rather than
    // assuming any model/context-dependent percentage is universally safe.
    internal static void RequirePinnedSettingsReviewEnvironment(IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null || environment.Count != 4 ||
            !environment.TryGetValue("DOTNET_PROCESSOR_COUNT", out var processors) || processors != "2" ||
            !environment.TryGetValue("DOTNET_GCHeapHardLimit", out var heap) || heap != "0x100000000" ||
            !environment.TryGetValue("CLAUDE_CODE_SDK_READS_SESSION_STATE", out var session) || session != "1" ||
            !environment.TryGetValue("DISABLE_COMPACT", out var compact) || compact != "1")
            throw new ToolUsageException("Selected reviewer settings require the exact bounded noncompacting process environment.");
    }

    internal static readonly string[] ProviderOverrides = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "CLAUDE_CODE_SIMPLE"];
    internal static void RequireSubscriptionEnvironment(Func<string, string?> read)
    {
        foreach (var name in ProviderOverrides)
            if (read(name) is { Length: > 0 } value &&
                (name is "ANTHROPIC_API_KEY" or "ANTHROPIC_AUTH_TOKEN" or "ANTHROPIC_BASE_URL" || value is not ("0" or "false" or "False")))
                throw new ToolUsageException("First-party subscription route is not assured while an API/provider override is present: " + name);
    }
    internal static void RequireSubscription(byte[] bytes)
    {
        _ = CanonicalJson.Canonicalize(bytes); using var doc = JsonDocument.Parse(bytes); var value = doc.RootElement;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("loggedIn", out var logged) || logged.ValueKind != JsonValueKind.True ||
            !value.TryGetProperty("authMethod", out var method) || method.ValueKind != JsonValueKind.String || method.GetString() != "claude.ai" ||
            !value.TryGetProperty("apiProvider", out var provider) || provider.ValueKind != JsonValueKind.String || provider.GetString() != "firstParty")
            throw new ToolUsageException("Actual reviewer must use its existing first-party Claude subscription login.");
    }
    internal static string RequireModel(JsonElement init)
    {
        var model = ClaudeStreamState.Text(init, "model");
        if (model.Length > 256 || !model.StartsWith("claude-", StringComparison.Ordinal) || model.Any(char.IsControl))
            throw new IOException("Actual first-party reviewer model family is unavailable or ambiguous.");
        return model;
    }
    internal static (string RawVerdict, string Verdict, IReadOnlyList<ReviewFinding> Findings) ParseReport(string text)
    {
        if (text.Length is < 1 or > 32768) throw new IOException("Complete reviewer report exceeds its finite profile.");
        var bytes = System.Text.Encoding.UTF8.GetBytes(text); _ = CanonicalJson.Canonicalize(bytes);
        using var doc = JsonDocument.Parse(bytes); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("verdict" or "findings")))
            throw new IOException("Reviewer must return the complete strict report object.");
        var raw = ClaudeStreamState.Text(root, "verdict");
        var verdict = raw switch { "ship" => "PASS", "needs-attention" or "do-not-ship" => "FAIL", _ => throw new IOException("Unknown actual reviewer verdict.") };
        var findings = ClaudeStreamState.Member(root, "findings");
        if (findings.ValueKind != JsonValueKind.Array || findings.GetArrayLength() > 128) throw new IOException("Complete finite findings array required.");
        var result = new List<ReviewFinding>();
        foreach (var row in findings.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Any(p => p.Name is not ("code" or "severity" or "message" or "location"))) throw new IOException("Unknown reviewer finding member.");
            var severity = ClaudeStreamState.Text(row, "severity");
            if (severity is not ("critical" or "high" or "medium" or "low")) throw new IOException("Unknown reviewer severity.");
            result.Add(new(ClaudeStreamState.Text(row, "code"), severity, ClaudeStreamState.Text(row, "message"), ClaudeStreamState.Text(row, "location")));
        }
        // Structurally valid contradictory DATA must survive assignment/retention.
        // Every caller receives a non-PASS verdict for findings of ANY severity;
        // the original raw verdict remains unchanged for provenance matching.
        if (raw == "ship" && result.Count != 0) verdict = "FAIL";
        return (raw, verdict, result);
    }
    internal static (long Tokens, decimal Cost) Usage(JsonElement result)
    {
        var usage = ClaudeStreamState.Member(result, "usage"); long tokens = 0;
        if (usage.ValueKind != JsonValueKind.Object) throw new IOException("Complete actual reviewer token usage is missing.");
        foreach (var name in new[] { "input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" })
        {
            if (!usage.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0) throw new IOException("Complete actual reviewer token usage is missing.");
            try { tokens = checked(tokens + count); } catch (OverflowException failure) { throw new IOException("Complete actual reviewer token usage exceeds its finite representation.", failure); }
        }
        if (!result.TryGetProperty("total_cost_usd", out var cost) || cost.ValueKind != JsonValueKind.Number || !cost.TryGetDecimal(out var amount) || amount < 0)
            throw new IOException("Actual reviewer cost observation is missing.");
        return (tokens, amount);
    }
}
