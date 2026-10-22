using System.Diagnostics;
using System.Text.Json;

namespace DeliveryForge.Evidence;

internal sealed record GateClaimAcquisition(
    GateClaimLease? Lease,
    string? UnavailableReason,
    string? InterruptedClaimPath);

internal sealed class GateClaimLease : IAsyncDisposable
{
    internal const int MaximumClaimBytes = 4096;
    private const int ClaimVersion = 1;
    private const int MaximumPendingClaimsInspected = 64;
    private static readonly TimeSpan PendingClaimRecoveryAge = TimeSpan.FromMinutes(10);
    private readonly string path;
    private readonly string nonce;
    private FileStream? stream;

    private GateClaimLease(string path, string nonce, FileStream stream)
    {
        this.path = path;
        this.nonce = nonce;
        this.stream = stream;
    }

    internal static GateClaimAcquisition Acquire(string path, Action<string>? afterInspectionForTest = null)
    {
        RecoverAbandonedPendingClaims(path);
        string? interruptedClaimPath = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            try
            {
                var claim = CurrentClaim(nonce);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(claim);
                if (bytes.Length > MaximumClaimBytes)
                    throw new EvidenceWriteException("Gate claim metadata exceeds its bounded representation.");
                var temporary = path + ".pending-" + nonce;
                try
                {
                    using (var pending = new FileStream(
                               temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                               MaximumClaimBytes, FileOptions.WriteThrough))
                    {
                        pending.Write(bytes);
                        pending.Flush(flushToDisk: true);
                    }
                    // Same-directory rename publishes only a complete durable claim and fails
                    // atomically when another owner already holds the canonical name.
                    File.Move(temporary, path);
                    var created = new FileStream(
                        path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                        MaximumClaimBytes, FileOptions.WriteThrough);
                    if (!OperatingSystem.IsMacOS()) created.Lock(0, 1);
                    return new(new(path, nonce, created), null, interruptedClaimPath);
                }
                catch
                {
                    try { File.Delete(temporary); } catch (IOException) { }
                    TryDeleteOwned(path, nonce);
                    throw;
                }
            }
            catch (IOException)
            {
                var inspection = Inspect(path);
                if (inspection.State == ClaimOwnerState.Live)
                    return new(null, "A live process owns the immutable gate claim.", null);
                if (inspection.State == ClaimOwnerState.Ambiguous)
                    return new(null, $"Gate claim ownership is ambiguous: {inspection.Reason}", null);

                var interrupted = path + ".interrupted-" + Guid.NewGuid().ToString("N");
                afterInspectionForTest?.Invoke(path);
                try
                {
                    File.Move(path, interrupted);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                var displaced = Inspect(interrupted);
                if (displaced.State != ClaimOwnerState.Stale ||
                    !string.Equals(displaced.Nonce, inspection.Nonce, StringComparison.Ordinal))
                {
                    try
                    {
                        File.Move(interrupted, path);
                    }
                    catch (IOException)
                    {
                        // Preserve the displaced claim for diagnosis if a concurrent owner won.
                    }
                    return new(null,
                        "Gate claim changed identity during stale takeover; ownership is ambiguous.", null);
                }

                // The stale name was atomically displaced. The bounded outer loop acquires the
                // canonical name before reporting interruption so only one recovery writer proceeds.
                interruptedClaimPath ??= interrupted;
            }
        }
        return new(null, "Gate claim changed concurrently and could not be acquired deterministically.", null);
    }

    private static void RecoverAbandonedPendingClaims(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        var cutoff = DateTime.UtcNow - PendingClaimRecoveryAge;
        try
        {
            foreach (var pending in Directory.EnumerateFiles(
                         directory, Path.GetFileName(path) + ".pending-*", SearchOption.TopDirectoryOnly)
                     .Take(MaximumPendingClaimsInspected))
            {
                try
                {
                    var information = new FileInfo(pending);
                    if (!information.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                        information.LastWriteTimeUtc <= cutoff)
                        File.Delete(pending);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A concurrent acquisition may still own a fresh pending file. Recovery is
                    // best-effort and never weakens exclusion on the canonical claim name.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Failure to scavenge an unpublished file cannot affect canonical claim ownership.
        }
    }

    internal static byte[] CreatePayloadForProcess(int processId)
    {
        var claim = ClaimForProcess(processId, Guid.NewGuid().ToString("N"));
        return JsonSerializer.SerializeToUtf8Bytes(claim);
    }

    public ValueTask DisposeAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            try { stream?.Unlock(0, 1); } catch (IOException) { }
        }
        stream?.Dispose();
        stream = null;
        TryDeleteOwned(path, nonce);
        return ValueTask.CompletedTask;
    }

    private static ClaimInspection Inspect(string path)
    {
        try
        {
            var information = new FileInfo(path);
            if (!information.Exists)
                return new(ClaimOwnerState.Stale, "claim disappeared", null);
            if (information.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return new(ClaimOwnerState.Ambiguous, "claim is a reparse point", null);
            if (information.Length <= 0 || information.Length > MaximumClaimBytes)
                return InspectMalformedLiveness(path, information, "claim is empty or oversized");
            var bytes = File.ReadAllBytes(path);
            var claim = JsonSerializer.Deserialize<ClaimDocument>(bytes);
            if (claim is null || claim.Version != ClaimVersion || claim.ProcessId <= 0 ||
                string.IsNullOrWhiteSpace(claim.BootId) || string.IsNullOrWhiteSpace(claim.ProcessStartToken) ||
                string.IsNullOrWhiteSpace(claim.Nonce))
                return InspectMalformedLiveness(path, information, "claim metadata is malformed");
            var currentBoot = BootId();
            if (!string.Equals(currentBoot, claim.BootId, StringComparison.Ordinal))
                return new(ClaimOwnerState.Stale, "claim belongs to an earlier boot", claim.Nonce);
            var currentStart = TryProcessStartToken(claim.ProcessId, out var missing);
            if (missing)
                return new(ClaimOwnerState.Stale, "claim owner process exited", claim.Nonce);
            if (currentStart is null)
                return new(ClaimOwnerState.Ambiguous, "claim owner process identity cannot be verified", claim.Nonce);
            return string.Equals(currentStart, claim.ProcessStartToken, StringComparison.Ordinal)
                ? new(ClaimOwnerState.Live, "claim owner is live", claim.Nonce)
                : new(ClaimOwnerState.Stale, "claim PID was reused", claim.Nonce);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or EvidenceWriteException)
        {
            return new(ClaimOwnerState.Ambiguous, exception.Message, null);
        }
    }

    private static ClaimInspection InspectMalformedLiveness(string path, FileInfo information, string reason)
    {
        if (OperatingSystem.IsMacOS())
            return new(ClaimOwnerState.Ambiguous,
                $"{reason}; advisory-lock recovery is unavailable on macOS", null);
        try
        {
            using var candidate = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            candidate.Lock(0, 1);
            candidate.Unlock(0, 1);
            var identity = $"malformed:{information.Length}:{information.LastWriteTimeUtc.Ticks}";
            return new(ClaimOwnerState.Stale, $"{reason}; no live advisory-lock owner exists", identity);
        }
        catch (IOException)
        {
            return new(ClaimOwnerState.Live, $"{reason}; an advisory-lock owner is live", null);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new(ClaimOwnerState.Ambiguous, $"{reason}; liveness cannot be checked: {exception.Message}", null);
        }
    }

    private static ClaimDocument CurrentClaim(string nonce) =>
        ClaimForProcess(Environment.ProcessId, nonce);

    private static ClaimDocument ClaimForProcess(int processId, string nonce)
    {
        var token = TryProcessStartToken(processId, out var missing);
        if (missing || token is null)
            throw new EvidenceWriteException("Gate claim process identity is unavailable.");
        return new(ClaimVersion, processId, BootId(), token, nonce, DateTimeOffset.UtcNow);
    }

    private static string BootId()
    {
        if (OperatingSystem.IsLinux())
        {
            const string path = "/proc/sys/kernel/random/boot_id";
            try
            {
                var bootId = File.ReadAllText(path).Trim();
                if (Guid.TryParse(bootId, out _))
                    return bootId;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new EvidenceWriteException($"Linux boot identity is unavailable: {exception.Message}");
            }
            throw new EvidenceWriteException("Linux boot identity is malformed.");
        }
        // On non-Linux hosts the exact OS process start timestamp below is the ownership
        // discriminator; this stable marker prevents wall-clock approximation drift.
        return "process-start-authoritative:" + Environment.MachineName;
    }

    private static string? TryProcessStartToken(int processId, out bool missing)
    {
        missing = false;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{processId}/stat");
                var close = stat.LastIndexOf(')');
                if (close < 0 || close + 2 >= stat.Length)
                    return null;
                var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > 19 ? fields[19] : null;
            }
            catch (DirectoryNotFoundException)
            {
                missing = true;
                return null;
            }
            catch (FileNotFoundException)
            {
                missing = true;
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            missing = true;
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void TryDeleteOwned(string path, string nonce)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumClaimBytes)
                return;
            var claim = JsonSerializer.Deserialize<ClaimDocument>(File.ReadAllBytes(path));
            if (claim?.Nonce == nonce)
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A failed release remains a durable stale claim; the next run performs takeover.
        }
    }

    private sealed record ClaimDocument(
        int Version,
        int ProcessId,
        string BootId,
        string ProcessStartToken,
        string Nonce,
        DateTimeOffset AcquiredAt);

    private sealed record ClaimInspection(ClaimOwnerState State, string Reason, string? Nonce);
    private enum ClaimOwnerState { Live, Stale, Ambiguous }
}
