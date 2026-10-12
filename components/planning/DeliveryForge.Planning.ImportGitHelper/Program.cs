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
var cleanupTasks = new List<Task>();
try
{
    if (config.Race && args.Contains("status", StringComparer.Ordinal) && !File.Exists(Path.Combine(home, "race-entered.json")))
    {
        originalConfig = await File.ReadAllBytesAsync(originalConfigPath, deadline.Token);
        NewJson(Path.Combine(home, "race-entered.json"), new { config.Nonce, arguments = args,
            configSha256 = Convert.ToHexStringLower(SHA256.HashData(originalConfig)) });
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
    var info = new ProcessStartInfo(config.Git) { WorkingDirectory = config.Repository, UseShellExecute = false,
        CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
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
    if (original is not null)
    {
        await Attempt(() => { if (!original.HasExited) original.Kill(entireProcessTree: false); return Task.CompletedTask; });
        await Attempt(() => { exit ??= original.WaitForExitAsync(CancellationToken.None); return exit; });
        if (stdout is not null) await Attempt(async () => { _ = await stdout; });
        if (stderr is not null) await Attempt(async () => { _ = await stderr; });
    }
    try
    {
        NewJson(Path.Combine(home, "invocation-" + id + "-result.json"), new { config.Nonce, arguments = args, childExit,
            originalExit = original is not null && exit?.IsCompletedSuccessfully == true && original.HasExited,
            stdoutCompleted = stdout?.IsCompletedSuccessfully == true, stderrCompleted = stderr?.IsCompletedSuccessfully == true,
            cleanupTasksSettled = cleanupTasks.All(task => task.IsCompleted),
            stdoutLength = stdoutBytes?.LongLength, stderrLength = stderrBytes?.LongLength,
            stdoutSha256 = stdoutBytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(stdoutBytes)),
            stderrSha256 = stderrBytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(stderrBytes)),
            elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
            primary = primary?.ToString(), secondary = secondary.Select(error => error.ToString()).ToArray() }, cleanup: true);
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

internal sealed record Configuration(string Git, string Repository, string Nonce, bool Race);
