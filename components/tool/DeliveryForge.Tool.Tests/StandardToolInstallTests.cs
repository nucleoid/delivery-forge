using System.Text;
using DeliveryForge.Evidence;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Packaging;
namespace DeliveryForge.Tool.Tests;

public sealed class StandardToolInstallTests
{
    [Fact]
    public async Task Genuine_local_tool_install_shim_uninstall_reinstall_on_this_OS()
    {
        var package = Environment.GetEnvironmentVariable("DELIVERY_FORGE_TOOL_PACKAGE") ?? throw new InvalidOperationException("Actual fresh Tool package is required.");
        var sha = Environment.GetEnvironmentVariable("DELIVERY_FORGE_TOOL_PACKAGE_SHA256") ?? throw new InvalidOperationException("Actual fresh package pin is required.");
        var sdk = Environment.GetEnvironmentVariable("DELIVERY_FORGE_DOTNET_HOST") ?? throw new InvalidOperationException("Actual SDK host is required.");
        using var affinity = new OwnedDuplexProcessTests.Affinity(); using var life = new ToolLifetime(TimeSpan.FromSeconds(240), TestContext.Current.CancellationToken); var archive = BoundedFiles.Read(package, life, 16 * 1024 * 1024); Assert.Equal(sha, BoundedFiles.Sha(archive)); var hostPin = BoundedFiles.Sha(BoundedFiles.Read(sdk, life));
        var root = Path.Combine(Path.GetTempPath(), "delivery-forge-local-tool-" + Guid.NewGuid().ToString("N")); life.Check(); Directory.CreateDirectory(root); life.Check();
        var feed = Path.Combine(root, "feed"); Directory.CreateDirectory(feed); BoundedFiles.WriteNew(Path.Combine(feed, "DeliveryForge.Tool.0.1.0.nupkg"), archive, life);
        var config = Path.Combine(root, "NuGet.Config"); var xml = "<configuration><packageSources><clear/><add key=\"owned-package-only\" value=\"" + System.Security.SecurityElement.Escape(feed) + "\"/></packageSources></configuration>"; BoundedFiles.WriteNew(config, Encoding.UTF8.GetBytes(xml), life);
        var prefix = Path.Combine(root, "tool path with spaces"); var user = Path.Combine(root, "user.txt"); BoundedFiles.WriteNew(user, "preserve"u8, life);
        var executor = ActualOrdinaryCapture.Select(life); var environment = new Dictionary<string, string> { ["DOTNET_ROOT"] = Path.GetDirectoryName(sdk) ?? throw new IOException("SDK root absent."), ["DOTNET_ROOT_X64"] = Path.GetDirectoryName(sdk) ?? throw new IOException("SDK root absent."), ["DOTNET_PROCESSOR_COUNT"] = "2", ["DOTNET_GCHeapHardLimit"] = "0x100000000", ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1", ["MSBUILDDISABLENODEREUSE"] = "1" };
        async Task<string> Run(string image, string[] args, int exit)
        {
            life.Check(); Assert.Equal(hostPin, BoundedFiles.Sha(BoundedFiles.Read(sdk, life)));
            var actual = await executor.ExecuteBoundedCaptureAsync(new(image, args, root, environment), life.Remaining, 1048576, life.Token, new OwnedWorkPhaseBudget(life));
            ActualOrdinaryCapture.Require(actual); Assert.True(actual.RootExitObserved && actual.StandardOutputDrainCompleted && actual.StandardErrorDrainCompleted && actual.Command.OwnedProcessQuiescent); Assert.False(actual.CaptureBoundExceeded); Assert.False(actual.Command.TimedOut); Assert.False(actual.Command.Cancelled); Assert.Equal(exit, actual.Command.ExitCode); life.Check(); return new UTF8Encoding(false, true).GetString(actual.StandardOutputBytes);
        }
        async Task Install() { Assert.False(Directory.Exists(prefix)); await Run(sdk, ["tool", "install", "DeliveryForge.Tool", "--version", "0.1.0", "--tool-path", prefix, "--configfile", config, "--disable-parallel", "--no-cache"], 0); }
        async Task ObserveAndRemove()
        {
            var shim = Path.Combine(prefix, OperatingSystem.IsWindows() ? "delivery-forge.exe" : "delivery-forge"); Assert.True(File.Exists(shim)); Assert.Contains("workerEnvelope", await Run(shim, ["--version"], 0)); Assert.Contains("INCOMPLETE", await Run(shim, ["capabilities", "--json"], 2));
            // Snapshot ALL actual SDK-owned outputs before uninstall. Refuse any
            // drift; do not infer tool-path ownership from a filename alone.
            var pins = Snapshot(prefix, life); Assert.NotEmpty(pins); foreach (var pin in pins) { var bytes = BoundedFiles.Read(pin.Path, life); Assert.Equal(pin.Bytes, bytes.LongLength); Assert.Equal(pin.Sha256, BoundedFiles.Sha(bytes)); }
            await Run(sdk, ["tool", "uninstall", "DeliveryForge.Tool", "--tool-path", prefix], 0);
            Assert.False(File.Exists(shim)); Assert.Equal("preserve", File.ReadAllText(user));
            // SDK may retain empty scaffolding. Preserve it and use a separate
            // fresh sibling for the reinstall proof instead of recursive cleanup.
        }
        await Install(); await ObserveAndRemove(); prefix = Path.Combine(root, "reinstall path with spaces"); await Install(); await ObserveAndRemove();
        Assert.Equal(sha, BoundedFiles.Sha(BoundedFiles.Read(package, life, 16 * 1024 * 1024))); Assert.Equal("preserve", File.ReadAllText(user));
        // Both OS jobs must execute this actual SDK route. It proves ordinary
        // installation mechanics, not host discovery/live worker/reviewer quality.
    }
    static IReadOnlyList<FilePin> Snapshot(string prefix, ToolLifetime life)
    {
        var rows = new List<FilePin>(); var stack = new Stack<string>(); stack.Push(BoundedFiles.Plain(prefix, true)); var count = 0; long total = 0;
        while (stack.Count != 0) foreach (var path in Directory.EnumerateFileSystemEntries(stack.Pop()))
        {
            life.Check(); if (++count > 4096) throw new IOException("Actual local-tool topology exceeds finite profile."); var attrs = File.GetAttributes(path); if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException("Actual local-tool reparse path refused."); if ((attrs & FileAttributes.Directory) != 0) { stack.Push(path); continue; }
            var size = new FileInfo(path).Length; life.Check(); if (size < 0 || size > 16 * 1024 * 1024 || total > 256L * 1024 * 1024 - size) throw new IOException("Actual local-tool byte profile exceeded."); var data = BoundedFiles.Read(path, life); if (data.LongLength != size) throw new IOException("Actual local-tool size drift."); total += size; if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) PublicContentScanner.RequireJsonSafe(data); else PublicContentScanner.RequireSafe(data); rows.Add(new(path, size, BoundedFiles.Sha(data)));
        }
        return rows;
    }
}
