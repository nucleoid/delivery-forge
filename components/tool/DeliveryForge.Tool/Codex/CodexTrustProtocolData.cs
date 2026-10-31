using System.Text.Json;
namespace DeliveryForge.Tool.Codex;

// Trust-mode DATA boundary only. Preserve bounded wire identities needed by the
// real protocol, never arbitrary notification/request/error/config payload text.
internal static class CodexTrustProtocolData
{
    static string Identity(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String ||
            field.GetString() is not { Length: > 0 and <= 4096 } text || text.Any(char.IsControl))
            throw new IOException("Missing or malformed bounded trust-protocol identity.");
        return text;
    }
    static JsonElement Object(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Object)
            throw new IOException("Missing or malformed trust-protocol object.");
        return field;
    }
    internal static JsonElement Response(JsonElement message, string method)
    {
        if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out var number) || number is < 1 or > 64)
            throw new IOException("Uncorrelated trust-protocol response.");
        if (message.TryGetProperty("error", out var error))
        {
            int? code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var rawCode) &&
                rawCode.ValueKind == JsonValueKind.Number && rawCode.TryGetInt32(out var actual) ? actual : null;
            return JsonSerializer.SerializeToElement(new { id = number, error = new { code, message = "Actual trust-mode RPC was refused; payload omitted." } });
        }
        if (method == "config/read") return CodexConfigReadData.Redact(message, number);
        if (method == "account/read") return CodexAccountReadData.Redact(message);
        if (method is "initialize" or "turn/interrupt")
            return JsonSerializer.SerializeToElement(new { id = number, result = new { } });
        var result = Object(message, "result");
        if (method == "thread/start")
        {
            var thread = Object(result, "thread"); var threadId = Identity(thread, "id");
            string? model = null; string? provider = null;
            if (result.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String &&
                m.GetString() is { Length: > 4 and <= 256 } value && value.StartsWith("gpt-", StringComparison.Ordinal) && !value.Any(char.IsControl)) model = value;
            if (result.TryGetProperty("modelProvider", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() == "openai") provider = "openai";
            return JsonSerializer.SerializeToElement(new { id = number, result = new { thread = new { id = threadId }, model, modelProvider = provider } });
        }
        if (method == "turn/start")
        {
            var turnId = Identity(Object(result, "turn"), "id");
            return JsonSerializer.SerializeToElement(new { id = number, result = new { turn = new { id = turnId } } });
        }
        throw new IOException("Unsupported trust-protocol RPC; no response payload retained.");
    }
    internal static JsonElement Notification(JsonElement message)
    {
        if (message.GetProperty("method").GetString() == "thread/tokenUsage/updated") return CodexReportedUsage.Projection(message);
        if (message.GetProperty("method").GetString() != "turn/completed")
            return JsonSerializer.SerializeToElement(new { method = "trust/other-notification", @params = new { payloadOmitted = true } });
        var parameters = Object(message, "params"); var threadId = Identity(parameters, "threadId");
        var turn = Object(parameters, "turn"); var turnId = Identity(turn, "id"); var status = Identity(turn, "status");
        if (status is not ("completed" or "interrupted" or "failed")) throw new IOException("Invalid terminal Codex status.");
        return JsonSerializer.SerializeToElement(new { method = "turn/completed", @params = new { threadId, turn = new { id = turnId, status } } });
    }
    internal static JsonElement ServerRequestReceipt() => JsonSerializer.SerializeToElement(new
    { method = "trust/unsupported-server-request", @params = new { payloadOmitted = true } });
    // Called only for the pump's already-correlated response, BEFORE projection.
    // These text matches are hints, never an OS/account/model/resource verdict.
    internal static CodexRefusalFacts? Refusal(JsonElement message, long expectedId)
    {
        if (expectedId is < 1 or > 64 || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var actualId) || actualId != expectedId ||
            !message.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return null;
        string? hash = null; int? bytes = null, osCode = null; var exceeded = false; var signals = CodexRefusalSignal.None;
        if (error.TryGetProperty("message", out var raw) && raw.ValueKind == JsonValueKind.String && raw.GetString() is { } text)
        {
            // Diagnostic-only bound; an oversized message does not change the
            // existing protocol admission, exception, cleanup or UNKNOWN state.
            exceeded = text.Length > 4096;
            if (!exceeded)
            {
                var utf8 = System.Text.Encoding.UTF8.GetBytes(text); bytes = utf8.Length;
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(utf8)).ToLowerInvariant();
                bool Has(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
                if (Has("os error") || Has("permission denied")) signals |= CodexRefusalSignal.OsError;
                if (Has("unauthorized") || Has("authentication") || Has("not logged in")) signals |= CodexRefusalSignal.Authentication;
                if (Has("model not found") || Has("model is not supported") || Has("model does not exist")) signals |= CodexRefusalSignal.Model;
                if (Has("resource temporarily unavailable") || Has("too many open files") || Has("out of memory") || Has("rate limit")) signals |= CodexRefusalSignal.Resource;
                if (Has("read-only") || Has("read only")) signals |= CodexRefusalSignal.ReadOnly;
                if (Has("sqlite") || Has("database")) signals |= CodexRefusalSignal.Database;
                if (Has("no such file or directory") || Has("path does not exist") || Has("file not found")) signals |= CodexRefusalSignal.PathMissing;
                const string prefix = "os error ";
                var start = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (start >= 0 && text.IndexOf(prefix, start + prefix.Length, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (start == 0 || char.IsWhiteSpace(text[start - 1]) || text[start - 1] is '(' or '[' or '{'))
                {
                    start += prefix.Length; var end = start;
                    while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
                    if (end > start && end - start <= 5 &&
                        (end == text.Length || char.IsWhiteSpace(text[end]) || text[end] is ')' or ']' or '}' or ',' or ';' or ':') &&
                        int.TryParse(text.AsSpan(start, end - start), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number <= 65535)
                        osCode = number;
                }
            }
        }
        return new(hash, bytes, true, exceeded, error.TryGetProperty("data", out _), signals.ToString(), true, osCode);
    }
    internal static string ErrorMessage(JsonElement error)
    {
        int? code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var raw) &&
            raw.ValueKind == JsonValueKind.Number && raw.TryGetInt32(out var value) ? value : null;
        return "Actual trust-mode app-server RPC refused; code=" + (code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable");
    }
}
