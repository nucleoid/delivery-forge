using DeliveryForge.Planning;

namespace DeliveryForge.EndToEnd.CrashDriver;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var originalStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var input = DriverInput.Parse(args);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        if (input.Mode == "child") { await EarlyDriverComposition.ChildAsync(input, originalStarted, lifetime.Token); return; }
        // This is genuine repository evidence only; it does not import a FrozenPlan/worker/ticket from bytes.
        var observed = await new GitRepositoryContextReader().ReadAsync(Path.Combine(input.RuntimeRoot, "repository"), "refs/heads/main", lifetime.Token);
        if (observed.Commit != input.RuntimeBaseCommit) throw new InvalidOperationException("Exact fixture repository base is not current.");
        await EarlyDriverComposition.WriteJson(Path.Combine(input.ArtifactRoot, "driver-" + input.Mode + "-preflight.json"), new
        { fixture = true, input.Stage, input.Nonce, observed.Commit, observed.Tree, observed.HeadCommit, mode = input.Mode,
            processIdDiagnosticOnly = Environment.ProcessId, limitation = "Driver stdout/stderr inherit outer SDK capture; this file is owned diagnostic data only." }, lifetime.Token);
        await EarlyDriverComposition.RunAsync(input, observed, lifetime.Token);
    }
}
internal sealed record DriverInput(string Mode, string Stage, string RuntimeRoot, string ArtifactRoot,
    string Nonce, string RuntimeBaseCommit, string IntegratedBaseCommit, int Seed)
{
    internal static readonly IReadOnlyList<string> AllBoundaries = Array.AsReadOnly(new[]
    {
        "plan-frozen", "worker-prepared", "host-accepted", "owned-command-started", "command-completed",
        "gate-receipt-written", "review-registration", "review-receipt", "scheduler-checkpoint",
        "publication-intent", "publication-readback", "cleanup-receipt"
    });
    internal static DriverInput Parse(string[] args)
    {
        if (args.Length != 8 || args[0] is not ("barrier" or "recover" or "release-control" or "child") || !AllBoundaries.Contains(args[1]) ||
            !Path.IsPathFullyQualified(args[2]) || !Path.IsPathFullyQualified(args[3]) || !Guid.TryParseExact(args[4], "N", out _) ||
            !IsCommit(args[5]) || !IsCommit(args[6]) || !int.TryParse(args[7], out var seed) || seed != 11019)
            throw new ArgumentException("Strict crash-fixture protocol requires mode/stage/absolute roots/nonce/exact bases/fixed seed.");
        var runtime = Path.GetFullPath(args[2]); var artifact = Path.GetFullPath(args[3]);
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "delivery-forge-e2e")) + Path.DirectorySeparatorChar;
        if (!runtime.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(runtime), "N", out _) || runtime == artifact)
            throw new IOException("Driver runtime is not the exact owned Temp GUID leaf.");
        foreach (var path in new[] { runtime, artifact })
            for (DirectoryInfo? ancestor = new(path); ancestor is not null; ancestor = ancestor.Parent)
                if (!ancestor.Exists || (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Driver root/ancestor is absent or redirected.");
        return new(args[0], args[1], runtime, artifact, args[4], args[5], args[6], seed);
    }
    private static bool IsCommit(string value) => value.Length == 40 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
