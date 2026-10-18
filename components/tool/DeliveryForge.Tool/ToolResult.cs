namespace DeliveryForge.Tool;

public sealed record ToolResult(string Outcome, string Code, string Message, object? Data = null, bool RuntimeCertified = false)
{
    public int ExitCode => Outcome switch { "COMPLETE" => 0, "DRY_RUN" => 0, "INCOMPLETE" => 2, "BLOCKED" => 3, _ => 1 };
    public static ToolResult Incomplete(string code, string message, object? data = null) => new("INCOMPLETE", code, message, data);
}
