namespace DeliveryForge.Tool;

public enum ToolMode { Plan, Implement, Pr, Merge, Resume, Capabilities, QualityBaseline }
public sealed class ToolUsageException(string message) : Exception(message);
public sealed record ToolCommand(ToolMode? Mode, IReadOnlyDictionary<string, string?> Options)
{
    public bool Has(string name) => Options.ContainsKey(name);
    public string? Get(string name) => Options.GetValueOrDefault(name);
}
public sealed record OptionDefinition(string Name, bool TakesValue, string[] Modes);
public static class CommandLine
{
    private static readonly string[] ModeNames = ["plan", "implement", "pr", "merge", "resume", "capabilities", "quality-baseline"];
    private static readonly OptionDefinition[] Table = [
        new("help",false,ModeNames),new("version",false,ModeNames),new("json",false,ModeNames),new("dry-run",false,ModeNames),new("config",true,ModeNames),
        new("repo",true,["plan","implement"]),new("issue",true,["plan"]),new("intent-file",true,["plan"]),new("plan-out",true,["plan"]),
        new("plan",true,["implement","quality-baseline"]),new("run-dir",true,["implement","pr","merge","resume"]),new("result-dir",true,["implement"]),
        new("replay-out",true,["plan","implement","pr","merge","resume"]),new("ceiling",true,["resume"]),new("profile",true,["capabilities"]),
        new("title-file",true,["pr"]),new("body-file",true,["pr"])
    ];
    public static IReadOnlyList<string> Modes => Array.AsReadOnly(ModeNames);
    public static IReadOnlyList<OptionDefinition> Definitions => Table.Select(x => new OptionDefinition(x.Name, x.TakesValue, x.Modes.ToArray())).ToArray();
    public static ToolCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count is 0 or > 64) throw new ToolUsageException("Select one explicit mode, or --help/--version.");
        var at = 0; ToolMode? mode = null; string? spelling = null;
        if (!args[0].StartsWith("--", StringComparison.Ordinal))
        {
            spelling = args[0]; var index = Array.IndexOf(ModeNames, spelling);
            if (index < 0) throw new ToolUsageException("Unknown mode; release/deploy and replay execution are not supported.");
            mode = (ToolMode)index; at = 1;
        }
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (at < args.Count)
        {
            var token = args[at++];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length > 8192) throw new ToolUsageException("Expected an exact --option; positional arguments and -- are refused.");
            var name = token[2..]; var definition = Table.SingleOrDefault(x => x.Name == name) ?? throw new ToolUsageException("Unknown option: " + name);
            if (values.ContainsKey(name)) throw new ToolUsageException("Repeated option: " + name);
            if (spelling is not null && !definition.Modes.Contains(spelling, StringComparer.Ordinal)) throw new ToolUsageException("Option is not allowed for this mode: " + name);
            string? value = null;
            if (definition.TakesValue)
            {
                if (at == args.Count || args[at].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[at]) || args[at].Length > 8192 || args[at].Any(char.IsControl)) throw new ToolUsageException("Missing or invalid option value: " + name);
                value = args[at++];
            }
            values.Add(name, value);
        }
        if (values.ContainsKey("help") || values.ContainsKey("version"))
        {
            if (values.Count != 1) throw new ToolUsageException("Help/version cannot be combined with execution options.");
            return new(mode, values);
        }
        if (mode is null) throw new ToolUsageException("An explicit mode is required.");
        bool Has(string n) => values.ContainsKey(n);
        switch (mode)
        {
            case ToolMode.Plan: if (!Has("repo") || Has("issue") == Has("intent-file")) throw new ToolUsageException("plan requires --repo and exactly one --issue/--intent-file."); break;
            case ToolMode.Implement: case ToolMode.QualityBaseline: if (!Has("plan")) throw new ToolUsageException("This mode requires --plan."); break;
            case ToolMode.Pr: case ToolMode.Merge: case ToolMode.Resume: if (!Has("run-dir")) throw new ToolUsageException("This mode requires --run-dir."); break;
        }
        if (Has("profile") && values["profile"] is not ("local" or "production")) throw new ToolUsageException("Profile must be local or production.");
        if (Has("ceiling") && values["ceiling"] is not ("plan" or "implement" or "pr" or "merge")) throw new ToolUsageException("Invalid ceiling.");
        return new(mode, values);
    }
    public static string Help() => "delivery-forge <plan|implement|pr|merge|resume|capabilities|quality-baseline> [options]\n" +
        "plan --repo ROOT (--issue OWNER/REPO#N | --intent-file FILE) [--plan-out FILE]\n" +
        "implement --plan RICH_BUNDLE [--repo EXACT_ROOT] [--run-dir DIR] [--result-dir DIR]\n" +
        "quality-baseline --plan RICH_BUNDLE --config TRUSTED_CONFIG (real gates only; candidate remains unapproved)\n" +
        "pr|merge|resume --run-dir DIR; resume [--ceiling plan|implement|pr|merge]\n" +
        "capabilities [--profile local|production]\n" +
        string.Join("\n", Table.Select(x => "--" + x.Name + (x.TakesValue ? " VALUE" : "") + " (" + string.Join(",", x.Modes) + ")")) + "\nNo implicit publication, replay execution, release or deploy. Unsupported live profiles remain INCOMPLETE.\n";
}
