using System.Text.Json;
using DeliveryForge.Tool.Codex;
namespace DeliveryForge.Tool.Tests;

public sealed class CodexAccountReadDataTests
{
    [Fact]
    public void Native_account_route_projection_retains_no_account_or_credential_details()
    {
        using var response = JsonDocument.Parse("{\"id\":4,\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":\"private@example.invalid\",\"planType\":\"pro\"},\"requiresOpenaiAuth\":true,\"extraSecret\":\"must-not-persist\"}}");
        var safe = CodexAccountReadData.Redact(response.RootElement);
        Assert.Equal("chatgpt", safe.GetProperty("result").GetProperty("account").GetProperty("type").GetString());
        Assert.DoesNotContain("private@example.invalid", safe.GetRawText()); Assert.DoesNotContain("must-not-persist", safe.GetRawText());
    }
    [Theory]
    [InlineData("{\"id\":4,\"result\":{\"account\":null}}")]
    [InlineData("{\"id\":4,\"result\":{\"account\":{\"type\":\"caller-claimed-family\"}}}")]
    public void Missing_or_unknown_account_route_stays_unavailable(string json)
    {
        using var response = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, CodexAccountReadData.Redact(response.RootElement).GetProperty("result").GetProperty("account").ValueKind);
    }
}
