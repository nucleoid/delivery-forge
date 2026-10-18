using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Git;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Tool;
namespace DeliveryForge.Tool.Tests;
// Genuine installed CLI calls, NOT a fixture host, fabricated supported profile,
// model assertion or publication issuer. Root must explicitly admit these costly
// real calls and prepare fresh isolated roots before selecting either theory.
// Missing bindings fail; no environment/capability skip produces a PASS.
public sealed class InstalledNativeHostConformanceTests
{
    public sealed record Fixture(string Kind, string Adapter, StandaloneHostSelection Host, string Git, string ParentCheckout, string Worktree, string ResultDirectory, string BaseCommit, string Repository, string CaseKind, AdmissionFile PlanBundle, PlanImportExpectations Expectations, string MutableReference, string RelativePath, string ExpectedText, AdmissionFile? LinuxScopeProfile);
    sealed class Frame : IAsyncDisposable
    {
        static readonly List<Frame> Unknown = [];
        internal readonly ToolLifetime Original = new(TimeSpan.FromSeconds(600), TestContext.Current.CancellationToken);
        internal readonly List<Task> ActualCalls = []; internal Fixture Selected = null!; internal IHostToolSessionPort Port = null!; internal WorktreeManager Git = null!; internal ProcessCommandExecutor Executor = null!; internal WriterLease? Lease; internal AgentRequest Request = null!;
        internal DuplexRetirement? Retirement; internal bool LeaseCloseEntered, LeaseClosed, DispositionEntered; internal Exception? Failure, Post;
        internal OwnedDuplexProcess? Native => Port switch { DeliveryForge.Tool.Codex.CodexAppServerPort p => p.OriginalProcess, DeliveryForge.Tool.Claude.ClaudeStreamPort p => p.OriginalProcess, DeliveryForge.Tool.Pi.PiRpcPort p => p.OriginalProcess, _ => null };
        internal async Task<T> Call<T>(Func<Task<T>> invoke) { Original.Check(); var actual = invoke(); ActualCalls.Add(actual); try { var result = await actual.WaitAsync(Original.Remaining, TestContext.Current.CancellationToken); Original.Check(); return result; } catch (Exception error) { Failure ??= error; throw; } }
        internal static Frame Prepare(string adapter, bool stop)
        {
            var frame = new Frame(); lock (Unknown) Unknown.Add(frame);
            var fixtureKey = "DELIVERY_FORGE_NATIVE_CONFORMANCE_" + (adapter == "codex.cli" ? "CODEX" : adapter == "claude-code.cli" ? "CLAUDE" : "PI") + (stop ? "_STOP" : "_COMPLETE");
            var path = Environment.GetEnvironmentVariable(fixtureKey) ?? throw new IOException("Fresh Root-admitted real native conformance fixture required; no model/CLI fallback.");
            var rawFixture = BoundedFiles.Read(path, frame.Original, 65536); var expectedFixtureHash = Environment.GetEnvironmentVariable(fixtureKey + "_SHA256") ?? throw new IOException("Independent complete fixture hash required."); Assert.Equal(expectedFixtureHash, BoundedFiles.Sha(rawFixture)); _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(rawFixture); frame.Original.Check(); frame.Selected = JsonSerializer.Deserialize<Fixture>(rawFixture, BoundedFiles.Json) ?? throw new IOException("Actual probe fixture null."); var f = frame.Selected;
            if (f.Kind != "RootPreparedNativeConformanceFixture-v1" || f.CaseKind != (stop ? "stop" : "completion") || f.PlanBundle is null || f.Expectations is null || f.Adapter != adapter || f.Host is null || string.IsNullOrWhiteSpace(f.Host.Version) || f.Host.Version == "unverified" || f.Host.Version.Length > 256 || f.RelativePath is null || f.RelativePath.Length is < 1 or > 256 || f.RelativePath.Contains('\\') || Path.IsPathFullyQualified(f.RelativePath) || f.RelativePath.Split('/').Any(v => v is "" or "." or "..") || f.ExpectedText is null || f.ExpectedText.Length is < 1 or > 4096 || f.ExpectedText.Contains('\0')) throw new IOException("Exact finite independently prepared real-host task fixture required.");
            BoundedFiles.Plain(f.ParentCheckout, true); BoundedFiles.Plain(f.Worktree, true); BoundedFiles.Plain(f.ResultDirectory, true); BoundedFiles.Separate(f.ParentCheckout, f.Worktree); BoundedFiles.Separate(f.Worktree, f.ResultDirectory);
            var image = BoundedFiles.Read(f.Host.Executable, frame.Original, 268435456); Assert.Equal(f.Host.Bytes, image.LongLength); Assert.Equal(f.Host.Sha256, BoundedFiles.Sha(image));
            LinuxScopeSelection? scope = null; if (OperatingSystem.IsLinux())
            {
                var pin = f.LinuxScopeProfile ?? throw new IOException("Actual independently prepared Linux profile required."); var raw = BoundedFiles.Read(pin.Path, frame.Original, 65536); Assert.Equal(pin.Bytes, raw.LongLength); Assert.Equal(pin.Sha256, BoundedFiles.Sha(raw)); scope = JsonSerializer.Deserialize<LinuxScopeSelection>(raw, BoundedFiles.Json) ?? throw new IOException("Null actual Linux selection.");
            }
            var executor = OperatingSystem.IsLinux() ? new ProcessCommandExecutor(scope!, frame.Original) : new ProcessCommandExecutor(); frame.Executor = executor; frame.Git = new(f.Git, executor, frame.Original);
            // UNVERIFIED remains UNVERIFIED throughout this probe. Canonical
            // owner acceptance MUST refuse it until independent actual audit.
            var profile = new AgentAdapterProfile(adapter, f.Host.Version, new[] { AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Stop, AgentCapability.EvidenceExport }.Select(c => new AgentCapabilityEvidence(c, AgentCapabilityStatus.Unverified, "actual installed transport UNDER TEST", "source-defined genuine probe, not conformance authority")).ToArray());
            frame.Port = adapter switch
            {
                "codex.cli" => new DeliveryForge.Tool.Codex.CodexAppServerPort(new(f.Host.Executable, f.Host.Bytes, f.Host.Sha256, f.Host.Version), frame.Git, profile, scope),
                "claude-code.cli" => new DeliveryForge.Tool.Claude.ClaudeStreamPort(new(f.Host.Executable, f.Host.Bytes, f.Host.Sha256, f.Host.Version), frame.Git, profile, scope),
                "pi.cli" => new DeliveryForge.Tool.Pi.PiRpcPort(new(f.Host.Executable, f.Host.Bytes, f.Host.Sha256, f.Host.Version), frame.Git, profile, scope),
                _ => throw new IOException("No arbitrary protocol dispatch.")
            };
            ((IOriginalBoundHostPort)frame.Port).BindOriginal(frame.Original); return frame;
        }
        internal async Task<AgentAcceptedReceipt> Start(bool stopProbe)
        {
            var f = Selected; var before = await Call(() => Git.ObserveAsync(f.Worktree, TestContext.Current.CancellationToken)); Assert.Equal(f.BaseCommit, before.HeadCommit); Assert.True(before.IsClean); Original.Check();
            var planBytes = BoundedFiles.Read(f.PlanBundle.Path, Original, 524288); Assert.Equal(f.PlanBundle.Bytes, planBytes.LongLength); Assert.Equal(f.PlanBundle.Sha256, BoundedFiles.Sha(planBytes));
            var importLimit = Original.Remaining < TimeSpan.FromSeconds(120) ? Original.Remaining : TimeSpan.FromSeconds(120);
            var imported = await Call(() => PlanImporter.ImportAndReconcileOwnedAsync(planBytes, new(f.ParentCheckout, f.MutableReference, f.Git), f.Expectations, importLimit, Executor, Original, cancellationToken: TestContext.Current.CancellationToken));
            var plan = imported.Plan ?? throw new IOException("Actual conformance rich-plan import refused."); Assert.True(imported.DownstreamReady); Assert.Equal(f.BaseCommit, plan.BaseCommit); Assert.Equal(f.Repository, plan.Repository);
            using var material = JsonDocument.Parse(plan.CanonicalBytes); Assert.Contains(material.RootElement.GetProperty("plan").GetProperty("changeMap").EnumerateArray(), row => row.GetProperty("path").GetString() == f.RelativePath);
            var run = "probe-" + Guid.NewGuid().ToString("N"); var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            Lease = Git.AcquireWriter(f.Worktree, run, token); Original.Check();
            var assignment = new FrozenWorkerPlan(run, plan.ContractIdentity, f.BaseCommit, f.Repository, f.ParentCheckout, f.Worktree, f.ResultDirectory, token, [f.RelativePath], [], f.Adapter, f.Host.Version, [AgentCapability.Launch, AgentCapability.CompletionEvidence]); Request = WorkerEnvelope.PrepareWorker(assignment);
            var instructions = HostInstructionContext.FromFrozenPlan(plan, assignment);
            ((IHostInstructionPort)Port).BindInstructions(instructions); var accepted = await Call(() => Port.StartAsync(Request, Original.Remaining, TestContext.Current.CancellationToken));
            Assert.Equal(Request.RequestIdentity, accepted.RequestIdentity); Assert.Equal(Request.RunId, accepted.RunId); Assert.Equal(Request.PlanIdentity, accepted.PlanIdentity); Assert.Equal(Request.BaseCommit, accepted.BaseCommit); Assert.Equal(Request.Worktree, accepted.Worktree); Assert.Equal(Request.AdapterId, accepted.AdapterId); Assert.Equal(Request.AdapterVersion, accepted.AdapterVersion); Assert.False(string.IsNullOrWhiteSpace(accepted.RuntimeRunIdentity)); Assert.False(string.IsNullOrWhiteSpace(accepted.RuntimeTaskIdentity)); Assert.All(accepted.Capabilities, c => Assert.Equal(AgentCapabilityStatus.Unverified, c.Status));
            Assert.Throws<AgentCapabilityIncompleteException>(() => WorkerEnvelope.ValidateAccepted(Request, accepted)); return accepted;
        }
        internal static AgentRunBinding Binding(AgentAcceptedReceipt a) => new(a.AdapterId, a.AdapterVersion, a.RunId, a.RequestIdentity, a.RuntimeRunIdentity, a.RuntimeTaskIdentity);
        public async ValueTask DisposeAsync()
        {
            if (DispositionEntered) throw new IOException("Original native conformance disposition already entered; no retry."); DispositionEntered = true;
            try
            {
                if (Native is not null) { var actual = Native.RetireAsync(Retirement is null); ActualCalls.Add(actual); Retirement = await actual.WaitAsync(Original.CleanupRemaining); }
                if (Retirement is { KnownClosed: false } || ActualCalls.Any(t => !t.IsCompleted)) throw new IOException("Actual native conformance remains UNKNOWN; original graph retained, no passing cleanup.");
                if (Lease is not null) { _ = Original.CleanupRemaining; LeaseCloseEntered = true; Lease.Dispose(); LeaseClosed = true; _ = Original.CleanupRemaining; }
                Original.Dispose(); lock (Unknown) Unknown.Remove(this);
            }
            catch (Exception error) { Post = error; throw; } // original frame remains strongly retained, no retry/recursive cleanup.
        }
    }
    [Theory(Timeout = 700000)]
    [InlineData("codex.cli")]
    [InlineData("claude-code.cli")]
    [InlineData("pi.cli")]
    public async Task Actual_installed_native_task_accepts_exact_assignment_commits_then_refuses_foreign_completion(string adapter)
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity(); await using var frame = Frame.Prepare(adapter, false); var f = frame.Selected; var accepted = await frame.Start(false); var binding = Frame.Binding(accepted);
        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => frame.Port.WaitAsync(binding with { RuntimeTaskIdentity = binding.RuntimeTaskIdentity + "-foreign" }, frame.Original.Remaining, TestContext.Current.CancellationToken));
        var completion = await frame.Call(() => frame.Port.WaitAsync(binding, frame.Original.Remaining, TestContext.Current.CancellationToken)); Assert.Equal(binding.RuntimeRunIdentity, completion.RuntimeRunIdentity); Assert.Equal(binding.RuntimeTaskIdentity, completion.RuntimeTaskIdentity); Assert.Equal(binding.RequestIdentity, completion.RequestIdentity); Assert.Equal("success", completion.Outcome); Assert.NotEqual(f.BaseCommit, completion.HeadCommit);
        var native = frame.Native ?? throw new IOException("Actual original process reference missing."); var sameRetirement = native.RetireAsync(false); frame.Retirement = await sameRetirement.WaitAsync(frame.Original.Remaining, TestContext.Current.CancellationToken); HostTransportRetirement.RequireSuccessful(frame.Retirement);
        var after = await frame.Call(() => frame.Git.ObserveAsync(f.Worktree, TestContext.Current.CancellationToken)); Assert.True(after.IsClean); Assert.Equal(after.HeadCommit, completion.HeadCommit); Assert.Equal(after.TreeId, completion.TreeId); Assert.Equal(Encoding.UTF8.GetBytes(f.ExpectedText), BoundedFiles.Read(Path.Combine(f.Worktree, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)), frame.Original));
        Assert.NotEmpty(completion.ArtifactPaths); foreach (var artifact in completion.ArtifactPaths) { var bytes = BoundedFiles.Read(artifact, frame.Original); Assert.DoesNotContain(frame.Request.WriterToken, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal); }
        // Actual native/protocol/Git observations only. This probe never writes
        // RootActualInstalledHostConformanceAcceptance or publishes authority.
    }
    [Theory(Timeout = 700000)]
    [InlineData("codex.cli")]
    [InlineData("claude-code.cli")]
    [InlineData("pi.cli")]
    public async Task Actual_installed_live_session_stops_only_through_its_same_original_group(string adapter)
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity(); await using var frame = Frame.Prepare(adapter, true); var accepted = await frame.Start(true); var binding = Frame.Binding(accepted);
        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => frame.Port.ControlAsync(binding with { RuntimeRunIdentity = binding.RuntimeRunIdentity + "-foreign" }, AgentControlAction.Stop, frame.Original.Remaining, TestContext.Current.CancellationToken));
        var native = frame.Native ?? throw new IOException("Actual native original missing."); var live = native.ObserveActualNative(); Assert.True(live.Known); Assert.False(live.RootExited); Assert.True(live.ActiveProcesses > 0);
        _ = frame.Original.CleanupRemaining; var actual = frame.Port.ControlAsync(binding, AgentControlAction.Stop, frame.Original.CleanupRemaining, CancellationToken.None); frame.ActualCalls.Add(actual); var control = await actual.WaitAsync(frame.Original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.Equal(binding, control.Binding); Assert.Equal(AgentActivityState.Quiescent, control.Activity);
        var retired = native.RetireAsync(true); frame.Retirement = await retired.WaitAsync(frame.Original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.True(frame.Retirement.KnownClosed); Assert.True(frame.Retirement.RootExited); Assert.Equal(0u, frame.Retirement.ActiveProcesses); Assert.True(frame.Retirement.DispositionReturned); _ = frame.Original.CleanupRemaining;
        // A stop attempt is not a successful worker commit. Native kill/actual
        // protocol-control outcomes remain separately recorded by the port.
    }
}
