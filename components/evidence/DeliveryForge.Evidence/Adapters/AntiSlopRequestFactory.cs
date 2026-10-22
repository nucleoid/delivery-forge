using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace DeliveryForge.Evidence.Adapters;

/// <summary>Code-owned construction of the complete canonical anti-slop request.</summary>
public static class AntiSlopRequestFactory
{
    public static GateRequest Create(
        string gateId,
        ResolvedEvidencePolicy policy,
        ToolCapability capability,
        RepositoryIdentity expectedRepository,
        string trustedWorkingDirectory,
        string bundleRoot,
        string nodePath,
        IReadOnlyList<string> inputs,
        string scope,
        TimeSpan timeout,
        string outputDirectory,
        bool enableEffectRules = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(expectedRepository);
        var workingRoot = Path.GetFullPath(trustedWorkingDirectory);
        var bundle = Path.GetFullPath(bundleRoot);
        var node = Path.GetFullPath(nodePath);
        var selectedInputs = DeterministicEvaluationPluginRegistry.NormalizeInputs(inputs);
        AntiSlopManifestPolicy.ValidateInputs(selectedInputs);
        long inputBytes = 0;
        foreach (var input in selectedInputs)
        {
            inputBytes = checked(inputBytes + Encoding.UTF8.GetByteCount(input) + 1L);
            if (inputBytes > AntiSlopManifestPolicy.MaximumManifestBytes)
                throw new EvaluationPluginException(
                    $"Anti-slop input paths exceed the {AntiSlopManifestPolicy.MaximumManifestBytes}-byte request composition ceiling.");
        }
        var config = Path.Combine(bundle, enableEffectRules ? "oxlint.effect.config.ts" : "oxlint.config.ts");
        var oxlint = Path.Combine(bundle, "node_modules", "oxlint", "bin", "oxlint");
        var direct = new List<string>
        {
            oxlint, "--config", config, "--format", "json", "--no-ignore", "--disable-nested-config"
        };
        direct.AddRange(selectedInputs);

        IReadOnlyList<string> arguments;
        if (direct.Sum(value => Encoding.UTF8.GetByteCount(value) + 1) <=
            AntiSlopEvaluationPlugin.MaximumDirectArgumentBytes)
        {
            arguments = direct.AsReadOnly();
        }
        else
        {
            var manifestRoot = Path.Combine(Path.GetFullPath(outputDirectory), "anti-slop-manifests");
            Directory.CreateDirectory(manifestRoot);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(selectedInputs);
            if (manifestBytes.Length > AntiSlopManifestPolicy.MaximumManifestBytes)
                throw new EvaluationPluginException(
                    $"Canonical anti-slop manifest exceeds the {AntiSlopManifestPolicy.MaximumManifestBytes}-byte composition ceiling.");
            var manifestIdentity = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(manifestBytes));
            var manifest = Path.Combine(manifestRoot, manifestIdentity + ".json");
            PublishManifest(manifest, manifestBytes);
            arguments =
            [
                Path.Combine(bundle, "run-oxlint-batches.mjs"), "--oxlint", oxlint,
                "--config", config, "--inputs", manifest
            ];
        }

        var invocation = new CommandInvocation(node, arguments, workingRoot,
            new Dictionary<string, string>(StringComparer.Ordinal), InheritEnvironment: false);
        return new GateRequest(
            gateId, policy, capability, EvidenceAdapterKind.External,
            "unavailable",
            expectedRepository, invocation, scope, timeout, Path.GetFullPath(outputDirectory),
            new EvaluationPluginSelection(
                AntiSlopEvaluationPlugin.CreateBundled(bundle, node).Id,
                Enabled: true,
                selectedInputs,
                enableEffectRules));
    }

    private static void PublishManifest(string manifest, ReadOnlySpan<byte> manifestBytes)
    {
        if (HasExpectedBytes(manifest, manifestBytes))
        {
            FlushManifestDirectory(manifest);
            return;
        }

        var temporary = Path.Combine(
            Path.GetDirectoryName(manifest)!,
            $".{Path.GetFileName(manifest)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(manifestBytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, manifest, overwrite: false);
            }
            catch (IOException) when (File.Exists(manifest))
            {
                if (HasExpectedBytes(manifest, manifestBytes))
                {
                    FlushManifestDirectory(manifest);
                    return;
                }

                // A crashed predecessor may have left a truncated final file. Replace it
                // atomically with the fully flushed same-directory temporary file.
                File.Move(temporary, manifest, overwrite: true);
            }

            if (!HasExpectedBytes(manifest, manifestBytes))
                throw new EvaluationPluginException(
                    "Canonical anti-slop manifest publication did not preserve the expected bytes.");
            FlushManifestDirectory(manifest);
        }
        catch (EvaluationPluginException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           NotSupportedException or PathTooLongException)
        {
            throw new EvaluationPluginException(
                $"Canonical anti-slop manifest publication failed recoverably: {exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // The uniquely named temporary is never authoritative and a later retry
                // remains safe even when cleanup is interrupted.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool HasExpectedBytes(string path, ReadOnlySpan<byte> expected)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length != expected.Length)
                return false;
            var actual = new byte[expected.Length];
            stream.ReadExactly(actual);
            return actual.AsSpan().SequenceEqual(expected);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void FlushManifestDirectory(string manifest)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var descriptor = NativeMethods.Open(
            Path.GetDirectoryName(manifest)!, NativeMethods.OpenReadOnly | NativeMethods.OpenDirectory);
        if (descriptor < 0)
            throw DirectoryFlushFailure("open");
        try
        {
            if (NativeMethods.Flush(descriptor) != 0 && Marshal.GetLastPInvokeError() is not (22 or 95))
                throw DirectoryFlushFailure("fsync");
        }
        finally
        {
            _ = NativeMethods.Close(descriptor);
        }
    }

    private static EvaluationPluginException DirectoryFlushFailure(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new EvaluationPluginException(
            $"Canonical anti-slop manifest directory durability {operation} failed recoverably: " +
            new System.ComponentModel.Win32Exception(error).Message);
    }

    private static class NativeMethods
    {
        internal const int OpenReadOnly = 0;
        internal const int OpenDirectory = 0x10000;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open(string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int Flush(int descriptor);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static extern int Close(int descriptor);
    }
}
