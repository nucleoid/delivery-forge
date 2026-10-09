using System.Security.Cryptography;
using System.Xml.Linq;
using DeliveryForge.Evidence.Adapters;

namespace DeliveryForge.Evidence;

internal sealed record EvidenceExecutionSnapshot(
    string? TrxPath,
    string? TrxIdentity,
    IReadOnlyDictionary<string, string> OutputIdentities);

internal static class EvidenceAdapterDispatcher
{
    public static EvidenceExecutionSnapshot CaptureBefore(GateRequest request)
    {
        var trx = request.Adapter == EvidenceAdapterKind.DotNet ? ResolveRequestedTrx(request.Invocation) : null;
        return new EvidenceExecutionSnapshot(
            trx,
            trx is not null && File.Exists(trx) ? HashFile(trx) : null,
            request.Adapter == EvidenceAdapterKind.DotNet
                ? CaptureOutputs(request.Invocation.WorkingDirectory)
                : new Dictionary<string, string>());
    }

    public static NormalizedEvidence Normalize(
        GateRequest request,
        CommandResult execution,
        EvidenceExecutionSnapshot snapshot) =>
        request.Adapter switch
        {
            EvidenceAdapterKind.DotNet => NormalizeDotNet(request, execution, snapshot),
            EvidenceAdapterKind.Crap4CSharp =>
                new Crap4CSharpAdapter().Normalize(System.Text.Encoding.UTF8.GetBytes(execution.StandardOutput), request.Capability),
            EvidenceAdapterKind.Mutate4CSharp =>
                new Mutate4CSharpAdapter().Normalize(System.Text.Encoding.UTF8.GetBytes(execution.StandardOutput), request.Capability),
            _ => NormalizedEvidence.Error("No sealed evidence adapter is selected for this gate.")
        };

    public static void AppendBoundArtifacts(
        GateRequest request,
        EvidenceExecutionSnapshot snapshot,
        string gateDirectory,
        IDictionary<string, string> artifactHashes)
    {
        if (request.Adapter != EvidenceAdapterKind.DotNet || snapshot.TrxPath is null || !File.Exists(snapshot.TrxPath))
            return;

        var destination = Path.Combine(gateDirectory, "evidence.trx");
        using (var source = new FileStream(snapshot.TrxPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(target);
            target.Flush(true);
        }
        artifactHashes["evidence.trx"] = HashFile(destination);
    }

    private static NormalizedEvidence NormalizeDotNet(
        GateRequest request,
        CommandResult execution,
        EvidenceExecutionSnapshot snapshot)
    {
        if (!string.Equals(request.Capability.Tool, "dotnet", StringComparison.Ordinal) ||
            !request.Capability.Supported)
            return NormalizedEvidence.Incomplete("The detected .NET SDK capability is unavailable or mismatched.");

        var projects = request.Invocation.Arguments
            .Where(argument => argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(argument => Path.GetFileNameWithoutExtension(argument)!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (projects.Length == 0)
            return NormalizedEvidence.Incomplete("The bound .NET invocation has no explicit required test project set.");

        var trx = snapshot.TrxPath;
        if (trx is null)
            return NormalizedEvidence.Incomplete("The bound .NET invocation did not declare an exact TRX artifact.");
        if (!File.Exists(trx))
            return NormalizedEvidence.Incomplete("The bound .NET invocation produced no exact TRX artifact.");
        var trxIdentity = HashFile(trx);
        if (string.Equals(trxIdentity, snapshot.TrxIdentity, StringComparison.Ordinal) ||
            File.GetLastWriteTimeUtc(trx) < execution.StartedAt.UtcDateTime.AddSeconds(-1))
            return NormalizedEvidence.Error("The bound TRX artifact is stale or was not produced by this gate invocation.");

        try
        {
            var counters = XDocument.Load(trx).Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Counters");
            if (counters is null)
                return NormalizedEvidence.Error("The bound TRX artifact has no counters.");

            var total = ReadCounter(counters, "total");
            var executed = ReadCounter(counters, "executed");
            var passed = ReadCounter(counters, "passed");
            var failed = ReadCounter(counters, "failed");
            var skipped = Math.Max(0, total - executed);
            var noBuild = request.Invocation.Arguments.Any(argument => argument == "--no-build");
            var compilerOutput = execution.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.Contains(" -> ", StringComparison.Ordinal) && line.Contains(".dll", StringComparison.OrdinalIgnoreCase));
            var freshOutput = CaptureOutputs(request.Invocation.WorkingDirectory).Any(pair =>
                !snapshot.OutputIdentities.TryGetValue(pair.Key, out var previous) ||
                !string.Equals(previous, pair.Value, StringComparison.Ordinal));
            var coverageComplete = !string.Equals(request.GateId, "coverage", StringComparison.Ordinal) ||
                Directory.EnumerateFiles(request.Invocation.WorkingDirectory, "coverage.*.xml", SearchOption.AllDirectories).Any();

            return new DotNetAdapter().Normalize(new DotNetRunEvidence(
                request.Capability.Version,
                execution.ExitCode ?? -1,
                !noBuild && compilerOutput && freshOutput,
                total,
                executed,
                passed,
                failed,
                skipped,
                coverageComplete,
                projects,
                projects,
                execution.TimedOut));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            return NormalizedEvidence.Error($"The bound TRX artifact is unreadable or malformed: {exception.Message}");
        }
    }

    private static string? ResolveRequestedTrx(CommandInvocation invocation)
    {
        var logger = invocation.Arguments.FirstOrDefault(argument => argument.Contains("LogFileName=", StringComparison.Ordinal));
        var name = logger?.Split("LogFileName=", 2, StringSplitOptions.None)[1];
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var exact = Path.GetFullPath(Path.IsPathFullyQualified(name)
            ? name
            : Path.Combine(invocation.WorkingDirectory, name));
        var root = Path.GetFullPath(invocation.WorkingDirectory);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return exact.StartsWith(prefix, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal)
            ? exact
            : null;
    }

    private static IReadOnlyDictionary<string, string> CaptureOutputs(string root) =>
        Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
            .Where(path => path.Split(Path.DirectorySeparatorChar).Contains("bin", StringComparer.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetFullPath(path), HashFile,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static string HashFile(string path) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))}";

    private static int ReadCounter(XElement counters, string name) =>
        int.Parse(counters.Attribute(name)?.Value ?? throw new FormatException($"TRX counter '{name}' is missing."),
            System.Globalization.CultureInfo.InvariantCulture);
}
