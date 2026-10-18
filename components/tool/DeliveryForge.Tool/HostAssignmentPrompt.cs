using System.Text.Json;
using DeliveryForge.Execution.Host;
namespace DeliveryForge.Tool;

public static class HostAssignmentPrompt
{
    public static string Create(AgentRequest actual, HostInstructionContext instructions)
    {
        instructions.Require(actual);
        // The model receives concrete parent-selected work, never WriterToken.
        var text = "Implement ONLY the selected task and acceptance instructions inside this immutable assignment's allowed paths. Preserve exclusions and ceiling. Do not publish, merge, release, deploy, alter authentication or fabricate evidence. Completion requires actual owner revalidation.\n" + JsonSerializer.Serialize(new { actual.SchemaVersion, actual.RunId, actual.RequestIdentity, actual.PlanIdentity, actual.BaseCommit, actual.Repository, actual.Worktree, actual.ResultDirectory, actual.AllowedPaths, actual.Exclusions, actual.AuthorizationCeiling, instructions.FrozenBundleIdentity, Instructions = instructions.Project(actual) }, BoundedFiles.Json);
        if (text.Contains(actual.WriterToken, StringComparison.Ordinal)) throw new IOException("Private writer material appeared in selected instructions; refuse before dispatch.");
        if (text.Length > 32768) throw new IOException("Complete actual assignment prompt exceeds finite profile."); return text;
    }
    public static object CodexTurnStart(AgentRequest actual, HostInstructionContext instructions, string threadId) => new { threadId, input = new[] { new { type = "text", text = Create(actual, instructions) } } };
    public static object PiPrompt(AgentRequest actual, HostInstructionContext instructions, string id) => new { id, type = "prompt", message = Create(actual, instructions) };
}
