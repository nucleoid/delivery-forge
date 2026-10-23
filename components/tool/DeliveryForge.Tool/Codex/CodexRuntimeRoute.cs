namespace DeliveryForge.Tool.Codex;

// Actual response/environment DATA checks only. No model, endpoint or login is changed.
internal static class CodexRuntimeRoute
{
    internal static void RequireSubscriptionEnvironment(Func<string, string?> read)
    {
        foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL" })
            if (!string.IsNullOrEmpty(read(name))) throw new ToolUsageException("The owner-trusted first-party route refuses a credential/endpoint override: " + name);
    }
    internal static void RequireObserved(bool chatgptAccount, string? provider, string? model)
    {
        if (!chatgptAccount || provider != "openai" || model is not { Length: > 4 and <= 256 } ||
            model.Any(char.IsControl) || !model.StartsWith("gpt-", StringComparison.Ordinal))
            throw new IOException("Actual existing ChatGPT subscription, openai provider and GPT worker identity are required before turn dispatch.");
    }
}
