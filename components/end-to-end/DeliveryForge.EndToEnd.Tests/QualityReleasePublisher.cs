namespace DeliveryForge.EndToEnd.Tests;

// Fixture-only publication; the callback observes the real staging writer before close, never authority.
internal static class QualityReleasePublisher
{
    internal static async Task PublishAsync(OwnedQualityInterruptionRuntime runtime, string ownedDirectory, string nonce,
        Func<string, CancellationToken, Task>? beforeClose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException("Release requires its exact fresh nonce.", nameof(nonce));
        ownedDirectory = Path.GetFullPath(ownedDirectory);
        RequirePlainOwned(runtime, ownedDirectory);
        if (!Directory.Exists(ownedDirectory)) throw new IOException("Owned release directory is absent.");
        var final = Path.Combine(ownedDirectory, "release-" + nonce);
        RequireAbsent(runtime, final);
        var staging = Path.Combine(ownedDirectory, "release-staging-" + nonce + "-" + Guid.NewGuid().ToString("N"));
        RequireAbsent(runtime, staging);
        await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(nonce), cancellationToken);
            await stream.FlushAsync(cancellationToken); stream.Flush(true);
            if (beforeClose is not null) await beforeClose(staging, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        RequirePlainOwned(runtime, staging);
        if (await File.ReadAllTextAsync(staging, cancellationToken) != nonce) throw new IOException("Completed release staging bytes changed.");
        RequireAbsent(runtime, final);
        File.Move(staging, final, overwrite: false);
        RequirePlainOwned(runtime, final);
        if (await File.ReadAllTextAsync(final, cancellationToken) != nonce) throw new IOException("Published release bytes changed.");
    }

    private static void RequireAbsent(OwnedQualityInterruptionRuntime runtime, string path)
    {
        RequirePlainOwned(runtime, path);
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new IOException("Release entry already exists; no overwrite is authorized.");
    }
    private static void RequirePlainOwned(OwnedQualityInterruptionRuntime runtime, string path)
    {
        runtime.EnsureOwner();
        path = Path.GetFullPath(path);
        if (!path.StartsWith(runtime.Repository + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Release publication escaped its live owned runtime.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Release path is redirected."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (current == runtime.Root) break;
        }
    }
}
