using System.Text.Json;
namespace DeliveryForge.Tool;
public static class Program {
    static readonly List<Exception> retainedFailures=[];
    internal static void RetainFailure(Exception actual){lock(retainedFailures){if(retainedFailures.Count<16)retainedFailures.Add(actual);}}
    public static IReadOnlyList<Exception> ActualFailures {get {lock(retainedFailures)return retainedFailures.ToArray();}}
    public static Task<int> Main(string[] args) {
        // Only exact diagnostics bypass the actual owned standalone lifetime.
        if(args.Length==1&&(args[0]=="--help"||args[0]=="--version")||args.Length==2&&CommandLine.Modes.Contains(args[0],StringComparer.Ordinal)&&(args[1]=="--help"||args[1]=="--version"))return ExecuteAsync(args,new ToolApplication(),Console.Out,Console.Error);
        return StandaloneEntrypoint.RunAsync(args,Console.Out,Console.Error);
    }
    public static async Task<int> ExecuteAsync(string[] args,ToolApplication actualApplication,TextWriter output,TextWriter errors,CancellationToken token=default) {
        try {
            var command=CommandLine.Parse(args);
            if(command.Has("help")){output.WriteLine(CommandLine.Help());return 0;}
            if(command.Has("version")){output.WriteLine(JsonSerializer.Serialize(new { tool="delivery-forge",version="0.1.0",contracts="1.0.0",workerEnvelope=DeliveryForge.Execution.WorkerEnvelope.SchemaVersion,platforms=new[]{"linux","windows"},liveCertification="INCOMPLETE" }));return 0;}
            var result=await actualApplication.ExecuteAsync(command,token);
            output.WriteLine(command.Has("json")?JsonSerializer.Serialize(result,BoundedFiles.Json):result.Outcome+": "+result.Message);
            return result.ExitCode;
        } catch(Exception error) when(error is ToolUsageException or IOException or ArgumentException or JsonException or DeliveryForge.Planning.PlanningException or DeliveryForge.Contracts.Validation.ContractValidationException or OperationCanceledException or TimeoutException or DeliveryForge.Contracts.Serialization.ContractJsonException or DeliveryForge.Execution.WorkerEnvelopeException or AggregateException) {
            lock(retainedFailures){if(retainedFailures.Count<16)retainedFailures.Add(error);}var report=ToolFailureReport.Create(error);errors.WriteLine(JsonSerializer.Serialize(report,BoundedFiles.Json));return 1;
        }
    }
}
