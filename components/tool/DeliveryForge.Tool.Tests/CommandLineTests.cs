using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("plan", "--repo", "/repo", "--intent-file", "/intent")]
    [InlineData("implement", "--plan", "/plan", "--repo", "/repo")]
    [InlineData("pr", "--run-dir", "/run", "--dry-run", null)]
    [InlineData("merge", "--run-dir", "/run", null, null)]
    [InlineData("resume", "--run-dir", "/run", "--ceiling", "implement")]
    [InlineData("capabilities", "--profile", "local", null, null)]
    [InlineData("quality-baseline", "--plan", "/plan", "--config", "/config")]
    public void Exact_modes_are_parsed(string a, string b, string c, string? d, string? e)
    {
        var command = CommandLine.Parse(new[] { a, b, c, d, e }.Where(x => x is not null).Select(x => x!).ToArray()); Assert.NotNull(command.Mode);
    }
    [Theory]
    [InlineData("")]
    [InlineData("deploy")]
    [InlineData("release")]
    [InlineData("execute-replay")]
    [InlineData("plan --repo /repo")]
    [InlineData("plan --repo /repo --issue a/b#1 --intent-file /intent")]
    [InlineData("implement --plan /plan --issue a/b#1")]
    [InlineData("pr --run-dir /run --repo /repo")]
    [InlineData("merge --run-dir /run --admin")]
    [InlineData("resume --run-dir /run --ceiling deploy")]
    [InlineData("capabilities --profile invented")]
    [InlineData("--dry-run")]
    [InlineData("--help --version")]
    [InlineData("implement --plan /one --plan /two")]
    [InlineData("plan --repo=/repo --intent-file /intent")]
    [InlineData("quality-baseline")]
    [InlineData("quality-baseline --plan /plan --run-dir /saved")]
    [InlineData("quality-baseline --plan /plan --ceiling implement")]
    public void Invalid_authority_or_ambiguous_grammar_refuses_before_effect(string input) => Assert.Throws<ToolUsageException>(() => CommandLine.Parse(input.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    [Fact] public void Help_is_generated_from_same_option_table() { var help = CommandLine.Help(); foreach (var row in CommandLine.Definitions) Assert.Contains("--" + row.Name, help); Assert.DoesNotContain("--authorize", help); }
    [Fact] public async Task Capabilities_reports_unavailable_without_config_or_host_probe() { var r = await new ToolApplication().ExecuteAsync(CommandLine.Parse(["capabilities"]), TestContext.Current.CancellationToken); Assert.Equal("INCOMPLETE", r.Outcome); Assert.False(r.RuntimeCertified); }
    [Fact] public async Task No_config_cannot_create_an_implementation_owner() { var r = await new ToolApplication().ExecuteAsync(CommandLine.Parse(["implement", "--plan", "/not-read"]), TestContext.Current.CancellationToken); Assert.Equal("TRUSTED_CONFIG_REQUIRED", r.Code); }
}
