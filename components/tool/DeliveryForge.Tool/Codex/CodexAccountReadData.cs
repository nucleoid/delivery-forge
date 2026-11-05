using System.Text.Json;
namespace DeliveryForge.Tool.Codex;

internal static class CodexAccountReadData
{
    // Official schema: https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/src/protocol/v2/account.rs
    // GetAccountParams uses camelCase refreshToken=false; Account is tagged "chatgpt".
    // Retain only the route type in traces/TCS results, never account PII/tokens.
    internal static JsonElement Redact(JsonElement message)
    {
        var id = message.GetProperty("id").Clone();
        if (message.TryGetProperty("error", out _))
            return JsonSerializer.SerializeToElement(new { id, error = new { code = -32000, message = "Optional account provenance unavailable." } });
        string? type = null;
        if (message.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("account", out var account) &&
            account.ValueKind == JsonValueKind.Object && account.TryGetProperty("type", out var value) && value.ValueKind == JsonValueKind.String)
        {
            var observed = value.GetString(); if (observed is "chatgpt" or "apiKey" or "amazonBedrock") type = observed;
        }
        return JsonSerializer.SerializeToElement(new { id, result = new { account = type is null ? null : new { type } } });
    }
}
