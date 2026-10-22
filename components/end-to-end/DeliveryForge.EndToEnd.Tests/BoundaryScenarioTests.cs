using System.Security.Cryptography;
using DeliveryForge.Evidence;
using Xunit;

namespace DeliveryForge.EndToEnd.Tests;

public sealed class BoundaryScenarioTests
{
    private static ScenarioOptions Options => new(ScenarioProfile.LocalContract, [],
        new(TimeSpan.FromSeconds(90), 2, 4096, 1000, 0m), 11017);

    [Fact(Timeout = 120_000)]
    public async Task Actual_interrupted_command_retains_deadline_and_quiescence_receipt()
    {
        var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await new ScenarioRunner(run).RunAsync("recovery.interrupted-command", Options, TestContext.Current.CancellationToken);
            if (result.Outcome != GateOutcome.Incomplete)
                await WriteInterruptedCommandDiagnosticAsync(run, result, TestContext.Current.CancellationToken);
            Assert.Equal(GateOutcome.Incomplete, result.Outcome);
            Assert.True(result.Invariants.TryGetValue("real-command-deadline-observed", out var observed) && observed,
                "A generic incomplete result is not an observed real command timeout.");
            Assert.True(result.Invariants["command-owned-quiescent"]);
            Assert.False(result.FullRealProfileCertified);
            var records = await run.Store.RecoverAsync(run.RunId, TestContext.Current.CancellationToken);
            var command = Assert.Single(records.Records, record => record.Kind == "scenario-command");
            var execution = command.Payload.GetProperty("execution");
            Assert.True(execution.GetProperty("timedOut").GetBoolean());
            Assert.False(execution.GetProperty("cancelled").GetBoolean());
            Assert.True(execution.GetProperty("ownedProcessQuiescent").GetBoolean());
            Assert.Contains(command.Sha256, result.ReceiptIds);
            run.MarkAssertionsPassed();
        }
        finally { await run.DisposeAsync(); }
        Assert.True(Directory.Exists(run.Root), "Observed non-pass command evidence must survive a passing safety assertion.");
    }

    [Fact(Timeout = 120_000)]
    public async Task Corrupt_immutable_record_is_rejected_and_kept_with_independent_diagnostic()
    {
        var run = await OwnedScenarioRun.CreateAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await new ScenarioRunner(run).RunAsync("recovery.immutable-record-corruption", Options, TestContext.Current.CancellationToken);
            Assert.Equal(GateOutcome.Error, result.Outcome);
            Assert.True(result.Invariants["immutable-corruption-rejected"]);
            var records = await run.Store.RecoverAsync(run.RunId, TestContext.Current.CancellationToken);
            var diagnostic = Assert.Single(records.Records, record => record.Kind == "scenario-corruption-observation");
            var path = diagnostic.Payload.GetProperty("corruptRecordPath").GetString()!;
            Assert.StartsWith(run.Root + Path.DirectorySeparatorChar, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            var observed = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
            Assert.NotEqual(diagnostic.Payload.GetProperty("expectedSha256").GetString(), observed);
            Assert.Equal(diagnostic.Payload.GetProperty("observedSha256").GetString(), observed);
            Assert.Contains(diagnostic.Sha256, result.ReceiptIds);
            Assert.False(result.FullRealProfileCertified);
            run.MarkAssertionsPassed();
        }
        finally { await run.DisposeAsync(); }
        Assert.True(Directory.Exists(run.Root), "Corrupt bytes must not be erased by a passing rejection assertion.");
    }
    // Reads only the selected immutable receipt; never repairs current.json or adopts a saved path.
    private static async Task<(System.Text.Json.JsonElement Payload, string Sha256)> ReadInterruptedCommandReceiptAsync(
        OwnedScenarioRun run, ScenarioResult result, CancellationToken cancellationToken)
    {
        if (result.ReceiptIds.Count != 1) throw new InvalidDataException("Interrupted-command receipt selection is not singular.");
        var hash = result.ReceiptIds[0];
        if (hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("Interrupted-command receipt identity is invalid.");
        var directory = Path.Combine(run.Store.RootPath, "runs", run.Store.GetRunNamespaceIdentity(run.RunId));
        static void RequirePlainAncestors(string entry)
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(entry)); current is not null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Diagnostic receipt path traverses a reparse entry.");
        }
        RequirePlainAncestors(directory);
        cancellationToken.ThrowIfCancellationRequested();
        var paths = Directory.EnumerateFiles(directory, "*-sha256-*.json").Take(4097).ToArray();
        if (paths.Length > 4096) throw new InvalidDataException("Diagnostic receipt inventory exceeds the existing store extent.");
        var selected = paths.Single(candidate => Path.GetFileName(candidate).EndsWith("-sha256-" + hash + ".json", StringComparison.Ordinal));
        var name = Path.GetFileName(selected);
        if (name.Length != 89 || name[12..] != "-sha256-" + hash + ".json" ||
            name[..12].Any(character => character is < '0' or > '9') ||
            !long.TryParse(name[..12], out var sequence) || sequence < 1)
            throw new InvalidDataException("Diagnostic receipt filename is invalid.");
        if ((File.GetAttributes(selected) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Diagnostic receipt is a reparse entry.");
        byte[] bytes;
        using (var stream = new FileStream(selected, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            if (stream.Length is <= 0 or > 16 * 1024 * 1024)
                throw new InvalidDataException("Diagnostic receipt exceeds the existing per-record extent.");
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Diagnostic receipt grew during read.");
        }
        RequirePlainAncestors(directory);
        if ((File.GetAttributes(selected) & FileAttributes.ReparsePoint) != 0 ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash)
            throw new InvalidDataException("Diagnostic receipt identity changed or does not match its content.");
        using var document = System.Text.Json.JsonDocument.Parse(bytes, new System.Text.Json.JsonDocumentOptions { MaxDepth = 64 });
        var record = document.RootElement;
        if (record.GetProperty("runId").GetString() != run.RunId || record.GetProperty("sequence").GetInt64() != sequence ||
            record.GetProperty("kind").GetString() != "scenario-command" ||
            record.GetProperty("payload").ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new InvalidDataException("Diagnostic receipt does not match the selected scenario namespace.");
        return (record.GetProperty("payload").Clone(), hash);
    }

    // Only failed fixture observations. No stdout, environment, owner token or native handles.
    private static async Task WriteInterruptedCommandDiagnosticAsync(OwnedScenarioRun run,
        ScenarioResult result, CancellationToken cancellationToken)
    {
        var output = TestContext.Current.TestOutputHelper;
        try
        {
            output?.WriteLine("Retained interrupted-command fixture: " + run.Root[..Math.Min(run.Root.Length, 4096)]);
            var command = await ReadInterruptedCommandReceiptAsync(run, result, cancellationToken);
            var execution = command.Payload.GetProperty("execution");
            System.Text.Json.JsonElement lifecycle = default, startup = default, stdout = default, stderr = default, tree = default;
            execution.TryGetProperty("lifecycleDiagnostics", out lifecycle);
            if (lifecycle.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                lifecycle.TryGetProperty("startup", out startup);
                lifecycle.TryGetProperty("ownedTree", out tree);
                lifecycle.TryGetProperty("standardOutput", out stdout);
                lifecycle.TryGetProperty("standardError", out stderr);
            }
            static object? Field(System.Text.Json.JsonElement value, string name)
            {
                if (value.ValueKind != System.Text.Json.JsonValueKind.Object || !value.TryGetProperty(name, out var field)) return null;
                return field.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Number => field.GetRawText().Length <= 64 ? field.GetRawText() : "<number exceeds diagnostic extent>",
                    System.Text.Json.JsonValueKind.String => field.GetString() is { } text ? text[..Math.Min(text.Length, 512)] : null,
                    _ => null
                };
            }
            var observation = System.Text.Json.JsonSerializer.Serialize(new
            {
                scenario = "recovery.interrupted-command", outcome = result.Outcome.ToString(),
                run.RunId, receiptSha256 = command.Sha256,
                exitCode = Field(execution, "exitCode"), timedOut = Field(execution, "timedOut"),
                cancelled = Field(execution, "cancelled"), ownedProcessQuiescent = Field(execution, "ownedProcessQuiescent"),
                startedAt = Field(execution, "startedAt"), completedAt = Field(execution, "completedAt"),
                killAttempted = Field(lifecycle, "killAttempted"), killSucceeded = Field(lifecycle, "killSucceeded"),
                killExceptionType = Field(lifecycle, "killExceptionType"), killExceptionDetail = Field(lifecycle, "killExceptionDetail"),
                exitWaitCompleted = Field(lifecycle, "exitWaitCompleted"), rootExited = Field(lifecycle, "rootExited"),
                drainWaitCompleted = Field(lifecycle, "drainWaitCompleted"), cleanupWaitMilliseconds = Field(lifecycle, "cleanupWaitMilliseconds"),
                stdoutCompleted = Field(stdout, "completedSuccessfully"), stdoutFaulted = Field(stdout, "faulted"), stdoutCancelled = Field(stdout, "cancelled"),
                stderrCompleted = Field(stderr, "completedSuccessfully"), stderrFaulted = Field(stderr, "faulted"), stderrCancelled = Field(stderr, "cancelled"),
                creationOwned = Field(tree, "creationOwned"), settlementObserved = Field(tree, "settlementObserved"),
                activeProcesses = Field(tree, "activeProcesses"), nativeFailure = Field(lifecycle, "nativeFailure"),
                startupCreation = Field(startup, "creation"), startupRootHandle = Field(startup, "rootHandle"),
                startupJobAssignment = Field(startup, "jobAssignment"), startupResume = Field(startup, "resume"),
                startupReaderEndpoints = Field(startup, "readerEndpoints"), startupStdoutDrain = Field(startup, "standardOutputDrain"),
                startupStderrDrain = Field(startup, "standardErrorDrain"), startupLastStage = Field(startup, "lastStage"),
                startupFailureStage = Field(startup, "failureStage"), originalElapsedSeconds = Field(startup, "originalElapsedSeconds"),
                originalTotalSeconds = Field(startup, "originalTotalSeconds"), originalWorkSeconds = Field(startup, "originalWorkSeconds"),
                startupReason = Field(startup, "reason")
            });
            output?.WriteLine(observation.Length <= 16 * 1024 ? observation : observation[..(16 * 1024)] + " <diagnostic truncated>");
        }
        catch (Exception diagnosticFailure)
        {
            // Keep the original outcome assertion as the failure, even if this optional read fails.
            try { output?.WriteLine("Interrupted-command diagnostic unavailable: " + diagnosticFailure.GetType().FullName); }
            catch (Exception) { /* Optional output must not replace the original outcome assertion. */ }
        }
    }

}
