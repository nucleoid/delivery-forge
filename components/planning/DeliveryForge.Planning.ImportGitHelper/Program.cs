using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Finite, explicitly selected TEST wrapper around real Git. It produces only
// observational markers/receipts, never reader, completion or policy authority.
var started = Stopwatch.GetTimestamp();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
using var wholeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
TimeSpan? cleanupBegun = null;
TimeSpan WholeRemaining()
{
    var left = TimeSpan.FromSeconds(30) - Stopwatch.GetElapsedTime(started);
    if (left <= TimeSpan.Zero) throw new TimeoutException("Original whole helper allowance expired.");
    return left;
}
TimeSpan CleanupRemaining()
{
    var now = Stopwatch.GetElapsedTime(started);
    var end = TimeSpan.FromSeconds(30);
    if (cleanupBegun is { } begun && begun + TimeSpan.FromSeconds(5) < end) end = begun + TimeSpan.FromSeconds(5);
    var left = end - now;
    if (left <= TimeSpan.Zero) throw new TimeoutException("Original helper cleanup reserve expired.");
    return left;
}
TimeSpan Remaining()
{
    var left = TimeSpan.FromSeconds(25) - Stopwatch.GetElapsedTime(started);
    if (left <= TimeSpan.Zero) throw new TimeoutException("Original helper work allowance expired.");
    deadline.Token.ThrowIfCancellationRequested(); return left;
}
var home = AppContext.BaseDirectory;
var configPath = Path.Combine(home, "git-helper.json");
if (new FileInfo(configPath).Length > 16 * 1024) throw new IOException("Fixture configuration exceeds16KiB.");
var config = JsonSerializer.Deserialize<Configuration>(await File.ReadAllBytesAsync(configPath, deadline.Token))
    ?? throw new IOException("Fixture configuration missing.");
if (!Path.IsPathFullyQualified(config.Git) || !Path.IsPathFullyQualified(config.Repository) || config.Nonce.Length != 32)
    throw new IOException("Exact fixture paths/nonce required.");
foreach (var path in new[] { home, config.Repository, config.Git })
    for (FileSystemInfo? entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path); entry is not null;
         entry = entry is DirectoryInfo directory ? directory.Parent : ((FileInfo)entry).Directory)
        if (entry.LinkTarget is not null || entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Fixture path is redirected.");
void NewJson(string path, object value, bool cleanup = false)
{
    void Check() { _ = cleanup ? CleanupRemaining() : Remaining(); }
    Check(); var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
    if (bytes.Length > 64 * 1024) throw new IOException("Fixture observation exceeds64KiB.");
    var staged = path + ".pending-" + Guid.NewGuid().ToString("N");
    Check();
    using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    { output.Write(bytes); output.Flush(true); }
    Check(); File.Move(staged, path, overwrite: false); Check();
}
async Task<byte[]> Read(Stream stream, int quota, bool originalDrain = false)
{
    using var output = new MemoryStream(); var buffer = new byte[8192];
    while (true)
    {
        _ = originalDrain ? WholeRemaining() : Remaining();
        var read = await stream.ReadAsync(buffer, originalDrain ? wholeDeadline.Token : deadline.Token);
        _ = originalDrain ? WholeRemaining() : Remaining();
        if (read == 0) return output.ToArray();
        if (output.Length + read > quota) throw new IOException("Original helper capture quota exceeded.");
        output.Write(buffer, 0, read);
    }
}
if (args.Length == 2 && args[0] == "--fixture-filter" && args[1] == config.Nonce)
{
    NewJson(Path.Combine(home, "filter-entry-" + Guid.NewGuid().ToString("N") + ".json"), new { config.Nonce, kind = "actual-clean-filter-entry" });
    if (!File.Exists(Path.Combine(home, "filter-entered.json")))
        NewJson(Path.Combine(home, "filter-entered.json"), new { config.Nonce, kind = "actual-clean-filter-entry" });
    var input = await Read(Console.OpenStandardInput(), 8 * 1024 * 1024);
    await Console.OpenStandardOutput().WriteAsync(input, deadline.Token); _ = Remaining(); return 0;
}
var id = Guid.NewGuid().ToString("N");
NewJson(Path.Combine(home, "invocation-" + id + "-entered.json"), new { config.Nonce, arguments = args, kind = "selected-real-git-wrapper" });
var originalConfigPath = Path.Combine(config.Repository, ".git", "config");
byte[]? originalConfig = null;
Process? original = null; Task<byte[]>? stdout = null; Task<byte[]>? stderr = null; Task? exit = null;
Exception? primary = null; var secondary = new List<Exception>(); var childExit = -1;
byte[]? stdoutBytes = null; byte[]? stderrBytes = null;
string? originalBlob = null, externalBlob = null, alternateFile = null, originalBlobSha = null;
bool blobMoved = false, externalRoutingConsumed = false;
byte[]? ownedAlternateBytes = null;
bool alternateCreated = false;
var cleanupTasks = new List<Task>();
try
{
    if (config.Alternate && args.Length >= 4 && args[^4] == "cat-file" && args[^3] == "blob" && args[^2] == "--end-of-options" && args[^1] == config.BlobId &&
        !File.Exists(Path.Combine(home, "alternate-entered.json")))
    {
        if (config.BlobId is null || config.BlobId.Length != 40 || !config.BlobId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            config.ObjectDirectory is null || !Path.IsPathFullyQualified(config.ObjectDirectory) || config.StoredSha256 is null)
            throw new IOException("Exact owned alternate stimulus is unavailable.");
        var objectRoot = Path.Combine(config.Repository, ".git", "objects");
        foreach (var path in new[] { objectRoot, config.ObjectDirectory })
            for (DirectoryInfo? entry = new(path); entry is not null; entry = entry.Parent)
                if (!entry.Exists || entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Owned object directory is missing or redirected.");
        originalBlob = Path.Combine(objectRoot, config.BlobId[..2], config.BlobId[2..]);
        externalBlob = Path.Combine(config.ObjectDirectory, config.BlobId[..2], config.BlobId[2..]);
        alternateFile = Path.Combine(objectRoot, "info", "alternates");
        if (Path.Exists(alternateFile) || Path.Exists(externalBlob)) throw new IOException("Owned alternate stimulus collision.");
        if (new FileInfo(originalBlob).Length > 8 * 1024 * 1024) throw new IOException("Actual loose blob exceeds8MiB.");
        originalBlobSha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(originalBlob, deadline.Token)));
        if (originalBlobSha != config.StoredSha256) throw new IOException("Actual original loose blob bytes differ.");
        var phaseStartedTicks = Stopwatch.GetTimestamp();
        var left = Remaining();
        var phaseCap = left < TimeSpan.FromSeconds(10) ? left : TimeSpan.FromSeconds(10);
        var phaseDeadlineTicks = checked(phaseStartedTicks + (long)(phaseCap.TotalSeconds * Stopwatch.Frequency));
        NewJson(Path.Combine(home, "alternate-entered.json"), new
        {
            config.Nonce,
            arguments = args,
            originalBlobSha,
            phaseProtocol = "alternate-cat-file-v1",
            invocationId = id,
            phaseStartedTicks,
            phaseDeadlineTicks,
            frequency = Stopwatch.Frequency
        });
        while (true)
        {
            _ = Remaining(); if (Stopwatch.GetTimestamp() >= phaseDeadlineTicks) throw new TimeoutException("Alternate barrier expired.");
            var released = File.Exists(Path.Combine(home, "alternate-release.json"));
            _ = Remaining(); if (Stopwatch.GetTimestamp() >= phaseDeadlineTicks) throw new TimeoutException("Late alternate release refused.");
            if (released) break; await Task.Delay(20, deadline.Token);
        }
        using var release = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(home, "alternate-release.json"), deadline.Token));
        _ = Remaining(); if (Stopwatch.GetTimestamp() >= phaseDeadlineTicks || release.RootElement.GetProperty("nonce").GetString() != config.Nonce)
            throw new IOException("Alternate release window/nonce differs.");
        Directory.CreateDirectory(Path.GetDirectoryName(externalBlob)!);
        _ = Remaining(); File.Move(originalBlob, externalBlob, overwrite: false); blobMoved = true; _ = Remaining();
        if (File.Exists(originalBlob) || Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(externalBlob, deadline.Token))) != originalBlobSha)
            throw new IOException("Actual owned blob move differs.");
        Directory.CreateDirectory(Path.GetDirectoryName(alternateFile)!);
        ownedAlternateBytes = Encoding.UTF8.GetBytes(config.ObjectDirectory.Replace('\\', '/') + "\n");
        using (var output = new FileStream(alternateFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { alternateCreated = true; output.Write(ownedAlternateBytes); output.Flush(true); }
        _ = Remaining();
    }
    if (config.Race && args.Contains("status", StringComparer.Ordinal) && !File.Exists(Path.Combine(home, "race-entered.json")))
    {
        originalConfig = await File.ReadAllBytesAsync(originalConfigPath, deadline.Token);
        NewJson(Path.Combine(home, "race-entered.json"), new
        {
            config.Nonce,
            arguments = args,
            configSha256 = Convert.ToHexStringLower(SHA256.HashData(originalConfig))
        });
        var barrierLeft = Remaining();
        var barrierEnd = Stopwatch.GetElapsedTime(started) + (barrierLeft < TimeSpan.FromSeconds(10) ? barrierLeft : TimeSpan.FromSeconds(10));
        while (true)
        {
            _ = Remaining(); if (Stopwatch.GetElapsedTime(started) >= barrierEnd) throw new TimeoutException("Original finite fixture release barrier expired.");
            var released = File.Exists(Path.Combine(home, "race-release.json"));
            _ = Remaining(); if (Stopwatch.GetElapsedTime(started) >= barrierEnd) throw new TimeoutException("Late fixture release refused.");
            if (released) break;
            await Task.Delay(20, deadline.Token);
        }
        _ = Remaining();
        using var release = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(home, "race-release.json"), deadline.Token));
        _ = Remaining(); if (Stopwatch.GetElapsedTime(started) >= barrierEnd) throw new TimeoutException("Late fixture release read refused.");
        if (release.RootElement.GetProperty("nonce").GetString() != config.Nonce) throw new IOException("Fixture release nonce differs.");
        var executable = Environment.ProcessPath ?? throw new IOException("Original helper apphost path unavailable.");
        if (executable.Contains('"') || executable.Contains('\n')) throw new IOException("Fixture quoting path unsupported.");
        var command = "\"" + executable.Replace("\\", "/", StringComparison.Ordinal) + "\" --fixture-filter " + config.Nonce;
        // A real local driver is installed only AFTER owner preflight and only
        // for one REAL status call; it is restored in independent cleanup.
        var injected = Encoding.UTF8.GetBytes("\n[filter \"owned-test\"]\n\tclean = " + command.Replace("\"", "\\\"", StringComparison.Ordinal) + "\n\trequired = true\n");
        await File.WriteAllBytesAsync(originalConfigPath, originalConfig.Concat(injected).ToArray(), deadline.Token);
        var tracked = Path.Combine(config.Repository, "tracked.txt");
        var actual = await File.ReadAllBytesAsync(tracked, deadline.Token);
        await File.WriteAllBytesAsync(tracked, actual, deadline.Token);
        File.SetLastWriteTimeUtc(tracked, DateTime.UtcNow.AddSeconds(2)); _ = Remaining();
    }
    var info = new ProcessStartInfo(config.Git)
    {
        WorkingDirectory = config.Repository,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in args) info.ArgumentList.Add(argument);
    original = new Process { StartInfo = info }; // Original object registered before Start.
    _ = Remaining(); if (!original.Start()) throw new IOException("Actual selected Git failed to start.");
    stdout = Read(original.StandardOutput.BaseStream, 16 * 1024 * 1024, originalDrain: true);
    stderr = Read(original.StandardError.BaseStream, 16 * 1024 * 1024, originalDrain: true);
    exit = original.WaitForExitAsync(CancellationToken.None);
    foreach (var task in new Task[] { stdout, stderr, exit })
        _ = task.ContinueWith(done => _ = done.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    await Task.WhenAll(stdout, stderr, exit).WaitAsync(Remaining(), deadline.Token); _ = Remaining();
    childExit = original.ExitCode;
    stdoutBytes = await stdout; stderrBytes = await stderr;
    if (blobMoved)
    {
        var actualObjects = Environment.GetEnvironmentVariable("GIT_OBJECT_DIRECTORY");
        externalRoutingConsumed = actualObjects == Path.Combine(config.Repository, ".git", "objects") &&
            !File.Exists(originalBlob) && File.Exists(externalBlob) && childExit == 0;
        NewJson(Path.Combine(home, "alternate-observed.json"), new
        {
            config.Nonce,
            childExit,
            actualObjects,
            externalRoutingConsumed,
            stdoutLength = stdoutBytes.LongLength,
            stdoutSha256 = Convert.ToHexStringLower(SHA256.HashData(stdoutBytes)),
            originalBlobSha,
            externalBlobSha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(externalBlob!, deadline.Token)))
        });
    }
    await Console.OpenStandardOutput().WriteAsync(stdoutBytes, deadline.Token);
    await Console.OpenStandardError().WriteAsync(stderrBytes, deadline.Token); _ = Remaining();
}
catch (Exception failure) { primary = failure; }
finally
{
    cleanupBegun ??= Stopwatch.GetElapsedTime(started);
    // 25s work + up to5s cleanup, clipped to the SAME original30s endpoint.
    async Task Attempt(Func<Task> action)
    {
        try
        {
            _ = CleanupRemaining(); var actual = action(); cleanupTasks.Add(actual);
            _ = actual.ContinueWith(done => _ = done.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await actual.WaitAsync(CleanupRemaining()); _ = CleanupRemaining();
        }
        catch (Exception failure) { secondary.Add(failure); }
    }
    if (originalConfig is not null)
        await Attempt(() => File.WriteAllBytesAsync(originalConfigPath, originalConfig, CancellationToken.None));
    if (blobMoved && originalBlob is not null && externalBlob is not null)
        await Attempt(async () =>
        {
            var external = new FileInfo(externalBlob);
            if (!external.Exists || external.LinkTarget is not null || (external.Attributes & FileAttributes.ReparsePoint) != 0 ||
                external.Length > 8 * 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(externalBlob, CancellationToken.None))) != originalBlobSha ||
                Path.Exists(originalBlob)) throw new IOException("Owned loose object changed; no replacement restoration.");
            File.Move(externalBlob, originalBlob, overwrite: false);
            if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(originalBlob, CancellationToken.None))) != originalBlobSha)
                throw new IOException("Original owned blob restoration differs."); blobMoved = false;
        });
    if (alternateCreated && alternateFile is not null)
        await Attempt(async () =>
        {
            var entry = new FileInfo(alternateFile);
            if (!entry.Exists || entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                entry.Length > 4096 || !(await File.ReadAllBytesAsync(alternateFile, CancellationToken.None)).SequenceEqual(ownedAlternateBytes!))
                throw new IOException("Own alternate entry changed; retained rather than deleting another entry.");
            File.Delete(alternateFile); alternateCreated = false;
        });
    if (original is not null)
    {
        await Attempt(() => { if (!original.HasExited) original.Kill(entireProcessTree: false); return Task.CompletedTask; });
        await Attempt(() => { exit ??= original.WaitForExitAsync(CancellationToken.None); return exit; });
        if (stdout is not null) await Attempt(async () => { _ = await stdout; });
        if (stderr is not null) await Attempt(async () => { _ = await stderr; });
    }
    try
    {
        NewJson(Path.Combine(home, "invocation-" + id + "-result.json"), new
        {
            config.Nonce,
            arguments = args,
            childExit,
            originalExit = original is not null && exit?.IsCompletedSuccessfully == true && original.HasExited,
            stdoutCompleted = stdout?.IsCompletedSuccessfully == true,
            stderrCompleted = stderr?.IsCompletedSuccessfully == true,
            cleanupTasksSettled = cleanupTasks.All(task => task.IsCompleted),
            stdoutLength = stdoutBytes?.LongLength,
            stderrLength = stderrBytes?.LongLength,
            stdoutSha256 = stdoutBytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(stdoutBytes)),
            stderrSha256 = stderrBytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(stderrBytes)),
            elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
            primary = primary?.ToString(),
            secondary = secondary.Select(error => error.ToString()).ToArray()
        }, cleanup: true);
    }
    catch (Exception failure) { secondary.Add(failure); }
    if (original is not null && exit?.IsCompletedSuccessfully == true && stdout?.IsCompletedSuccessfully == true && stderr?.IsCompletedSuccessfully == true)
        original.Dispose(); // No pending original handle is disposed as closure.
}
if (primary is not null || secondary.Count != 0)
{
    try
    {
        _ = CleanupRemaining();
        var errorOutput = Console.Error.WriteLineAsync(new AggregateException("Actual helper primary/independent secondary observations", primary is null ? secondary : new[] { primary }.Concat(secondary)).ToString());
        cleanupTasks.Add(errorOutput);
        _ = errorOutput.ContinueWith(done => _ = done.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await errorOutput.WaitAsync(CleanupRemaining()); _ = CleanupRemaining();
    }
    catch (Exception failure) { secondary.Add(failure); }
    return 47;
}
_ = CleanupRemaining();
return childExit;

internal sealed record Configuration(string Git, string Repository, string Nonce, bool Race,
    bool Alternate = false, string? BlobId = null, string? ObjectDirectory = null, string? StoredSha256 = null);
