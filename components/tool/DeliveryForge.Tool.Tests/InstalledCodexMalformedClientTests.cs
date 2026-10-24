using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Codex;

namespace DeliveryForge.Tool.Tests;

// Genuine installed server refusal of malformed CLIENT input, not fabricated
// server output, a Port pump-corruption proof, or a conformance authority issuer.
[Trait("Category", "InstalledLocalRealAcceptance")]
public sealed class InstalledCodexMalformedClientTests
{
    public static bool IsWindowsOrLinux => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    sealed class Probe : IAsyncDisposable
    {
        static readonly List<Probe> Retained = [];
        internal readonly ToolLifetime Original = new(TimeSpan.FromSeconds(600), TestContext.Current.CancellationToken);
        internal readonly List<Task> Calls = [];
        internal readonly TaskCompletionSource<(int Code, int ParsedBytes, string ParsedSha256)> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InstalledNativeHostConformanceTests.Fixture Fixture = null!;
        internal LinuxScopeSelection? LinuxScope;
        internal LinuxScopeImage? ExactImage;
        internal OwnedDuplexProcess? Native;
        internal CodexJsonlProtocol? Protocol;
        internal DuplexRetirement? Retirement;
        internal Exception? Primary;
        internal bool DispositionEntered;
        internal static Probe Prepare()
        {
            var probe = new Probe(); lock (Retained) Retained.Add(probe);
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new IOException("This actual installed control requires Windows Job or selected Linux original scope ownership.");
            const string key = "DELIVERY_FORGE_NATIVE_CONFORMANCE_CODEX_STOP";
            var fixturePath = Environment.GetEnvironmentVariable(key) ?? throw new IOException("Fresh Root-admitted Codex stop fixture required; no supported fixture fallback.");
            var bytes = BoundedFiles.Read(fixturePath, probe.Original, 65536);
            Assert.Equal(Environment.GetEnvironmentVariable(key + "_SHA256") ?? throw new IOException("Exact fixture SHA required."), BoundedFiles.Sha(bytes));
            _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(bytes);
            probe.Fixture = JsonSerializer.Deserialize<InstalledNativeHostConformanceTests.Fixture>(bytes, BoundedFiles.Json) ?? throw new IOException("Null fixture.");
            var f = probe.Fixture;
            if (f.Kind != "RootAdmittedNativeConformanceProvisioningFixture-v2" || f.Adapter != "codex.cli" || f.CaseKind != "stop" || f.ObserveRuntimeTrustRoute != OperatingSystem.IsWindows() || f.Host is null || string.IsNullOrWhiteSpace(f.Host.Version) || f.Host.Version is "unverified" || f.Host.Version.Length > 256)
                throw new IOException("Exact fresh platform-selected Codex UNVERIFIED fixture required.");
            BoundedFiles.Plain(f.ParentCheckout, true); BoundedFiles.Plain(f.ResultDirectory, true); BoundedFiles.Separate(f.ParentCheckout, f.ResultDirectory);
            if (OperatingSystem.IsLinux())
            {
                var pin = f.LinuxScopeProfile ?? throw new IOException("Actual independently prepared Linux profile required.");
                var raw = BoundedFiles.Read(pin.Path, probe.Original, 65536);
                Assert.Equal(pin.Bytes, raw.LongLength); Assert.Equal(pin.Sha256, BoundedFiles.Sha(raw));
                _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(raw);
                probe.LinuxScope = JsonSerializer.Deserialize<LinuxScopeSelection>(raw, BoundedFiles.Json) ?? throw new IOException("Null actual Linux selection.");
                probe.ExactImage = new(f.Host.Executable, f.Host.Bytes, f.Host.Sha256);
                BoundedFiles.ReserveExactLinuxImageReads(new(f.Host.Executable, f.Host.Bytes, f.Host.Sha256), probe.Original);
            }
            BoundedFiles.VerifyNativeImage(new(f.Host.Executable, f.Host.Bytes, f.Host.Sha256), probe.Original);
            return probe;
        }
        internal async Task ReadOriginal()
        {
            try
            {
                using var response = await Protocol!.ReadAsync(Original.Token) ?? throw new IOException("Server returned EOF instead of actual invalid-request refusal.");
                var value = response.RootElement; CodexJsonlProtocol.RequireEnvelope(value);
                Assert.False(value.TryGetProperty("method", out _)); Assert.False(value.TryGetProperty("result", out _));
                Assert.Equal(1L, value.GetProperty("id").GetInt64());
                var error = value.GetProperty("error"); Assert.Equal(JsonValueKind.Object, error.ValueKind);
                var code = error.GetProperty("code").GetInt32(); Assert.True(code < 0, "Actual server must return a negative numeric error code.");
                // Hash/extent describe parsed JSON serialization, not raw JSONL.
                // No vendor message, configuration, account or prompt is exported.
                var parsed = JsonSerializer.SerializeToUtf8Bytes(value);
                Reply.TrySetResult((code, parsed.Length, BoundedFiles.Sha(parsed)));
                // This exclusive consumer is terminal now. Original retirement
                // closes stdin and owns any remaining stdout drain with its actual
                // WORK/cleanup phase; no second WORK-only protocol read races Stop.
            }
            catch (Exception error) { Reply.TrySetException(error); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (DispositionEntered) throw new IOException("Original malformed-client disposition entered twice.");
            DispositionEntered = true;
            try
            {
                if (Native is not null)
                {
                    var actual = Native.RetireAsync(true); Calls.Add(actual);
                    Retirement = await actual.WaitAsync(Original.CleanupRemaining, TestContext.Current.CancellationToken);
                    var observed = new List<Exception>();
                    foreach (var call in Calls.Distinct())
                    {
                        try { await call.WaitAsync(Original.CleanupRemaining); }
                        catch (Exception error) { if (!call.IsCompleted) throw; observed.Add(error); }
                    }
                    if (!Retirement.KnownClosed || !Retirement.DispositionReturned || Native.HasUnknownIo || Calls.Any(call => !call.IsCompleted))
                        throw new IOException("Actual malformed-client original remains UNKNOWN; retain owner.");
                    // All actual entered calls are terminal before releasing this
                    // retained owner. Preserve their faults as disposition evidence.
                    if (observed.Count != 0) throw new AggregateException("Original malformed-client calls failed after physical disposition.", observed);
                }
                Original.Dispose(); lock (Retained) Retained.Remove(this);
            }
            catch (Exception error)
            {
                if (Primary is not null && !ReferenceEquals(Primary, error)) throw new AggregateException("Actual malformed-client primary and original disposition.", Primary, error);
                throw;
            }
        }
    }

    [Fact(Timeout = 700000, Skip = "Actual Windows Job or selected Linux original scope required.", SkipUnless = nameof(IsWindowsOrLinux))]
    public async Task Actual_installed_Codex_refuses_malformed_client_initialize_then_same_original_retires()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        await using var probe = Probe.Prepare();
        try
        {
            var f = probe.Fixture;
            var environment = new Dictionary<string, string> { ["DOTNET_PROCESSOR_COUNT"] = "2", ["DOTNET_GCHeapHardLimit"] = "0x100000000", ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1", ["MSBUILDDISABLENODEREUSE"] = "1" };
            probe.Native = OwnedDuplexProcess.Open(new(f.Host.Executable, ["app-server"], f.ParentCheckout, environment), probe.Original, probe.LinuxScope, probe.ExactImage);
            if (probe.Native.StartupFailure is Exception startup) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startup).Throw();
            if (OperatingSystem.IsLinux()) Assert.True(probe.Native.HasCreationOwnedExactLinuxImage);
            probe.Protocol = new(probe.Native.Input, probe.Native.Output, probe.Original);
            var reader = probe.ReadOriginal(); probe.Calls.Add(reader); probe.Native.AttachOriginalOutputConsumer(reader);
            var startupLimit = probe.Original.Remaining < TimeSpan.FromSeconds(120) ? probe.Original.Remaining : TimeSpan.FromSeconds(120);
            using var phase = CancellationTokenSource.CreateLinkedTokenSource(probe.Original.Token, TestContext.Current.CancellationToken); phase.CancelAfter(startupLimit);
            // Deliberately malformed params shape; no initialized/account/thread/
            // turn/config request, model operation, or protocol-stream substitution.
            var write = probe.Protocol.WriteAsync(new { id = 1L, method = "initialize", @params = 17 }, phase.Token); probe.Calls.Add(write);
            await write.WaitAsync(startupLimit, phase.Token);
            var reply = await probe.Reply.Task.WaitAsync(probe.Original.Remaining < startupLimit ? probe.Original.Remaining : startupLimit, phase.Token);
            probe.Original.Check();
            // Successful disposal is requested while original WORK is still open.
            // If the genuine server does not exit cleanly after stdin EOF, actual
            // retirement/failure semantics win; no synthetic clean-exit fallback.
            var retired = probe.Native.RetireAsync(false); probe.Calls.Add(retired);
            probe.Retirement = await retired.WaitAsync(probe.Original.Remaining, TestContext.Current.CancellationToken);
            Assert.Same(retired, probe.Native.OriginalRetirementTask);
            Assert.True(probe.Retirement.KnownClosed); Assert.True(probe.Retirement.RootExited); Assert.Equal(0u, probe.Retirement.ActiveProcesses);
            Assert.True(probe.Retirement.OutputEof); Assert.True(probe.Retirement.ErrorEof); Assert.True(probe.Retirement.InputCloseReturned); Assert.True(probe.Retirement.DispositionReturned); Assert.Empty(probe.Retirement.Failures);
            Assert.False(probe.Native.HasUnknownIo); Assert.False(probe.Protocol.Unknown);
            Assert.All(probe.Calls, call => Assert.True(call.IsCompletedSuccessfully)); Assert.All(probe.Protocol.EnteredTasks, call => Assert.True(call.IsCompletedSuccessfully));
            probe.Original.Check(); Directory.CreateDirectory(f.ResultDirectory); probe.Original.Check();
            var summary = JsonSerializer.SerializeToUtf8Bytes(new { Kind = "ActualInstalledCodexMalformedClientRefusal-v1", f.Host, Version = f.Host.Version, SentMethod = "initialize", SentId = 1, ParamsKind = "Number", ReplyId = 1, ErrorCode = reply.Code, reply.ParsedBytes, reply.ParsedSha256, Retirement = probe.Retirement, MalformedClientInput = true, MalformedServerOutput = false, PortPumpCorruptionProven = false, RuntimeCertified = false, AuthorityIssued = false }, BoundedFiles.Json);
            BoundedFiles.WriteNew(Path.Combine(f.ResultDirectory, "codex-malformed-client-refusal.json"), summary, probe.Original);
        }
        catch (Exception error) { probe.Primary = error; throw; }
    }
}
