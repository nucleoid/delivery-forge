using System.Text.Json;

namespace DeliveryForge.Evidence;

/// <summary>
/// Executable developer example for the supported advisory composition. The Evidence project
/// remains a library normally; build this entry point into an isolated output with OutputType
/// and StartupObject, then execute that output.
/// </summary>
public static class AdvisoryAntiSlopExample
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("""
                Advisory anti-slop composition example (never production authority)

                dotnet build components/evidence/DeliveryForge.Evidence -c Release --no-restore \
                  -o /absolute/example-host -p:OutputType=Exe \
                  -p:StartupObject=DeliveryForge.Evidence.AdvisoryAntiSlopExample
                dotnet /absolute/example-host/DeliveryForge.Evidence.dll \
                  --repository /absolute/repository --base <protected-base-sha> \
                  --bundle /absolute/components/evidence/anti-slop --node /absolute/node \
                  --output /absolute/output [--effect] [--disabled] \
                  [--ignore path ...] --input path [--input path ...]
                """);
            return 0;
        }

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--effect" or "--disabled")
            {
                flags.Add(argument);
                continue;
            }
            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 == args.Length)
                throw new ArgumentException($"Expected --name value, received '{argument}'.");
            if (!values.TryGetValue(argument, out var collected))
                values[argument] = collected = [];
            collected.Add(args[++index]);
        }

        string One(string name) => values.TryGetValue(name, out var items) && items.Count == 1
            ? items[0]
            : throw new ArgumentException($"Exactly one {name} value is required.");
        IReadOnlyList<string> Many(string name) => values.TryGetValue(name, out var items) ? items : [];

        var report = await AdvisoryAntiSlopComposition.RunAsync(new AdvisoryAntiSlopOptions(
            One("--repository"),
            One("--base"),
            One("--bundle"),
            One("--node"),
            One("--output"),
            Many("--input"),
            new HashSet<string>(Many("--ignore"), StringComparer.Ordinal),
            Enabled: !flags.Contains("--disabled"),
            EnableEffectRules: flags.Contains("--effect"))).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            authority = "advisory",
            report.ProductionAuthorityActivated,
            selections = report.Selections,
            executions = report.Executions.Select(execution => new
            {
                execution.Selection.Id,
                execution.Selection.Enabled,
                execution.Selection.Inputs,
                execution.Selection.EnableEffectRules,
                execution.Disposition,
                outcome = execution.Result?.Outcome.ToString(),
                reason = execution.Result?.Reason,
                pluginRawOutcome = execution.Result?.Plugin.PluginRawOutcome,
                receiptPath = execution.Result?.ReceiptPath,
                artifacts = execution.Result?.ArtifactHashes
            })
        }, new JsonSerializerOptions { WriteIndented = true }));

        return report.Executions.Any(execution =>
            execution.Result?.Outcome is GateOutcome.Fail or GateOutcome.Error or GateOutcome.Incomplete) ? 2 : 0;
    }
}
