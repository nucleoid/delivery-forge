using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution.Git;
using DeliveryForge.Tool;
using DeliveryForge.Tool.Codex;

namespace DeliveryForge.Tool.Tests;

// Genuine installed server refusal of malformed CLIENT input, not fabricated
// server output, a Port pump-corruption proof, or a conformance authority issuer.
[Trait("Category", "InstalledLocalRealAcceptance")]
public sealed class InstalledCodexMalformedClientTests
{
    public static bool IsWindowsOrLinux => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    public static bool IsLinux => OperatingSystem.IsLinux();
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
        internal WriterLease? WorkspaceWriter;
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
                if (WorkspaceWriter is not null) { _ = Original.CleanupRemaining; WorkspaceWriter.Dispose(); _ = Original.CleanupRemaining; }
                Original.Dispose(); lock (Retained) Retained.Remove(this);
            }
            catch (Exception error)
            {
                if (Primary is not null && !ReferenceEquals(Primary, error)) throw new AggregateException("Actual malformed-client primary and original disposition.", Primary, error);
                throw;
            }
        }
    }

    [Fact(Timeout = 240000, Skip = "Actual selected Linux exact-image startup fixture required.", SkipUnless = nameof(IsLinux))]
    public async Task Actual_installed_Codex_workspace_thread_start_reports_safe_refusal_then_same_original_retires_without_turn()
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity();
        await using var probe = Probe.Prepare();
        try
        {
            var f = probe.Fixture;
            if (probe.LinuxScope?.RestrictiveChildPolicy != "nnp-user1000-ancestor-v3" || f.LinuxOwnerTrustedHelper is not null || f.LinuxDangerFullAccessOnce)
                throw new IOException("Exact restrictive non-helper Linux startup selection required.");
            // Root must admit a NEW absent destination and its current project
            // trust entries. Never capture/adopt the previous failed writer.
            if (Path.Exists(f.Worktree) || Path.Exists(f.ResultDirectory)) throw new IOException("Fresh startup worktree/result required.");
            var run = "startup-" + Guid.NewGuid().ToString("N"); var current = true;
            var parent = new ParentEffectAdmission(new object(), actual => current && actual == run, _ => current = false);
            var executor = new ProcessCommandExecutor(StandaloneOwnerComposition.OrdinaryLinuxScope(probe.LinuxScope), probe.Original);
            var git = new WorktreeManager(f.Git, executor, probe.Original).WithParentAdmission(parent, run);
            var prepare = git.PrepareAsync(f.ParentCheckout, f.Worktree, f.BaseCommit, probe.Original.Token); probe.Calls.Add(prepare); await prepare;
            probe.WorkspaceWriter = git.AcquireWriter(f.Worktree, run, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            var binding = git.RequireOriginalBinding(f.Worktree, probe.Original, run);
            var enable = binding.EnablePrivateObjectWritesAsync(probe.Original.Token); probe.Calls.Add(enable); await enable;
            var environment = new Dictionary<string, string> { ["DOTNET_PROCESSOR_COUNT"] = "2", ["DOTNET_GCHeapHardLimit"] = "0x100000000", ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1", ["MSBUILDDISABLENODEREUSE"] = "1", ["GIT_OBJECT_DIRECTORY"] = binding.ObjectsPath, ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = binding.CommonObjectsPath };
            probe.Native = OwnedDuplexProcess.Open(new(f.Host.Executable, ["app-server"], f.Worktree, environment), probe.Original, probe.LinuxScope, probe.ExactImage);
            binding.AttachPrivateObjectWriter(probe.Native);
            if (probe.Native.StartupFailure is Exception startup) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startup).Throw();
            Assert.True(probe.Native.HasCreationOwnedExactLinuxImage);
            Assert.Equal(probe.LinuxScope.ExactImageBacking, probe.Native.CreationOwnedExactLinuxImageBacking);
            probe.Protocol = new(probe.Native.Input, probe.Native.Output, probe.Original);
            var clip = probe.Original.Remaining < TimeSpan.FromSeconds(120) ? probe.Original.Remaining : TimeSpan.FromSeconds(120);
            using var phase = CancellationTokenSource.CreateLinkedTokenSource(probe.Original.Token, TestContext.Current.CancellationToken); phase.CancelAfter(clip);
            var refused = false; var threadCreated = false; string? refusedMethod = null; long? refusedId = null; int? errorCode = null; CodexRefusalFacts? refusal = null;
            async Task<JsonElement> Rpc(long id, string method, object parameters)
            {
                var write = probe.Protocol.WriteAsync(new { id, method, @params = parameters }, phase.Token); probe.Calls.Add(write); await write;
                while (true)
                {
                    using var document = await probe.Protocol.ReadAsync(probe.Original.Token) ?? throw new IOException("Actual startup server returned EOF.");
                    var message = document.RootElement; CodexJsonlProtocol.RequireEnvelope(message);
                    if (message.TryGetProperty("method", out _))
                    {
                        if (message.TryGetProperty("id", out var serverId))
                        {
                            var decline = probe.Protocol.WriteAsync(new { id = serverId.Clone(), error = new { code = -32601, message = "Unsupported client request; no approval or capability granted." } }, phase.Token); probe.Calls.Add(decline); await decline;
                        }
                        continue;
                    }
                    Assert.Equal(id, message.GetProperty("id").GetInt64());
                    if (message.TryGetProperty("error", out var error))
                    {
                        refused = true; refusedMethod = method; refusedId = id; refusal = CodexTrustProtocolData.Refusal(message, id);
                        errorCode = error.TryGetProperty("code", out var code) && code.TryGetInt32(out var number) ? number : null;
                    }
                    else if (method == "thread/start")
                    {
                        var result = message.GetProperty("result"); Assert.Equal(f.Worktree, result.GetProperty("cwd").GetString());
                        var policy = result.GetProperty("sandbox"); Assert.Equal("workspaceWrite", policy.GetProperty("type").GetString()); Assert.False(policy.GetProperty("networkAccess").GetBoolean());
                        Assert.All(policy.GetProperty("writableRoots").EnumerateArray(), root => Assert.Contains(root.GetString(), new[] { binding.Worktree, binding.GitDirectory }));
                        threadCreated = true;
                    }
                    return CodexTrustProtocolData.Response(message, method);
                }
            }
            async Task StartupOnly()
            {
                _ = await Rpc(1, "initialize", new { clientInfo = new { name = "delivery_forge", title = "Delivery Forge", version = "0.1.0" }, capabilities = new { experimentalApi = false } });
                if (refused) return;
                var initialized = probe.Protocol.WriteAsync(new { method = "initialized", @params = new { } }, phase.Token); probe.Calls.Add(initialized); await initialized;
                var config = await Rpc(2, "config/read", new { cwd = f.Worktree, includeLayers = true }); if (refused) return;
                _ = CodexConfigReadData.RequireSupported(config.GetProperty("result"));
                var account = await Rpc(3, "account/read", new { refreshToken = false }); if (refused) return;
                Assert.Equal("chatgpt", account.GetProperty("result").GetProperty("account").GetProperty("type").GetString());
                _ = await Rpc(4, "thread/start", new { cwd = f.Worktree, sandbox = "workspace-write" });
                // Deliberately no turn/start, task text or forward host owner.
            }
            var actual = StartupOnly(); probe.Calls.Add(actual); probe.Native.AttachOriginalOutputConsumer(actual);
            await actual.WaitAsync(clip, phase.Token); probe.Original.Check();
            var retire = probe.Native.RetireAsync(false); probe.Calls.Add(retire); probe.Retirement = await retire.WaitAsync(probe.Original.Remaining, TestContext.Current.CancellationToken);
            Assert.Same(retire, probe.Native.OriginalRetirementTask); Assert.True(probe.Retirement.KnownClosed); Assert.False(probe.Native.HasUnknownIo);
            Assert.All(probe.Calls, call => Assert.True(call.IsCompletedSuccessfully)); Assert.All(probe.Protocol.EnteredTasks, call => Assert.True(call.IsCompletedSuccessfully));
            var summary = JsonSerializer.SerializeToUtf8Bytes(new { Kind = "ActualInstalledCodexWorkspaceStartupOnly-v1", f.Host, f.Worktree, Refused = refused, RefusedMethod = refusedMethod, RefusedId = refusedId, ErrorCode = errorCode, Refusal = refusal, ThreadCreated = threadCreated, TurnStartSent = false, ModelPromptSent = false, Retirement = CodexStartupCleanupFacts.Observe(retire, probe.Native.HasUnknownIo, null), GeneralConformanceAccepted = false, RuntimeCertified = false, AuthorityIssued = false }, BoundedFiles.Json);
            probe.Original.Check(); Directory.CreateDirectory(f.ResultDirectory); BoundedFiles.WriteNew(Path.Combine(f.ResultDirectory, "codex-workspace-startup-only.json"), summary, probe.Original);
        }
        catch (Exception error) { probe.Primary = error; throw; }
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
