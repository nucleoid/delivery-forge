using System.Security.Cryptography;
using System.Runtime.InteropServices;
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
    public static bool IsLinux => OperatingSystem.IsLinux();
    [Theory]
    [InlineData(null)]
    [InlineData("nnp-user1000-ancestor-v3")]
    public void Linux_scope_selection_preserves_legacy_shape_and_optional_policy(string? policy)
    {
        var image = new LinuxScopeImage("/unexecuted-DATA", 1, "unexecuted-DATA");
        var selection = new LinuxScopeSelection(image, image, "manager-DATA", "kernel-DATA", image, [], image)
        {
            RestrictiveChildPolicy = policy
        };
        var (systemdRun, systemdLibrary, manager, kernel, gate, closure, receipt) = selection;
        Assert.Same(image, systemdRun); Assert.Same(image, systemdLibrary);
        Assert.Equal("manager-DATA", manager); Assert.Equal("kernel-DATA", kernel);
        Assert.Same(image, gate); Assert.Empty(closure); Assert.Same(image, receipt);
        var roundTrip = JsonSerializer.Deserialize<LinuxScopeSelection>(JsonSerializer.Serialize(selection, BoundedFiles.Json), BoundedFiles.Json);
        Assert.NotNull(roundTrip); Assert.Equal(policy, roundTrip.RestrictiveChildPolicy);
        var legacy = new { selection.SystemdRun, selection.SystemdLibrary, selection.ManagerVersion, selection.KernelRelease, selection.BirthGate, selection.BirthGateClosure, selection.GateBuildReceipt };
        var legacyRoundTrip = JsonSerializer.Deserialize<LinuxScopeSelection>(JsonSerializer.Serialize(legacy, BoundedFiles.Json), BoundedFiles.Json);
        Assert.NotNull(legacyRoundTrip); Assert.Null(legacyRoundTrip.RestrictiveChildPolicy);
    }

    [Theory(Timeout = 30000, Skip = "Actual no-follow statx guard requires Linux.", SkipUnless = nameof(IsLinux))]
    [InlineData("absent")]
    [InlineData("helper")]
    [InlineData("policy")]
    [InlineData("dangling-helper")]
    [InlineData("dangling-policy")]
    public void Non_helper_guard_refuses_actual_ambient_entries_without_following_links(string entry)
    {
        using var original = new ToolLifetime(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var root = Directory.CreateTempSubdirectory("forge-helper-absence-").FullName;
        var helper = Path.Combine(root, "helper"); var policy = Path.Combine(root, "policy");
        var selected = entry.Contains("policy", StringComparison.Ordinal) ? policy : helper;
        if (entry.StartsWith("dangling-", StringComparison.Ordinal)) File.CreateSymbolicLink(selected, Path.Combine(root, "missing-target"));
        else if (entry != "absent") File.WriteAllText(selected, "retained guard DATA");
        if (entry == "absent") HelperGuard.RequireAbsent(original, helper, policy);
        else
        {
            var refusal = Assert.Throws<IOException>(() => HelperGuard.RequireAbsent(original, helper, policy));
            Assert.Contains("requires absent helper and ancestor policy", refusal.Message);
        }
        // Retain entries/links on success and failure; no installed root mutation.
    }
    public sealed record Fixture(string Kind, string Adapter, StandaloneHostSelection Host, string Git, string ParentCheckout, string Worktree, string ResultDirectory, string BaseCommit, string Repository, string CaseKind, AdmissionFile PlanBundle, PlanImportExpectations Expectations, string MutableReference, string RelativePath, string ExpectedText, AdmissionFile? LinuxScopeProfile, bool ObserveRuntimeTrustRoute = false, OwnerTrustedLinuxHelper? LinuxOwnerTrustedHelper = null, bool LinuxDangerFullAccessOnce = false);
    // Explicit conformance-only DATA selection; never a RuntimeTrust/native authority issuer.
    public sealed record OwnerTrustedLinuxHelper(AdmissionFile OwnerDecision, AdmissionFile CompletedReceipt, string PreparedBootId, AdmissionFile FinalDisposition);
    sealed record HelperDecision(string Kind, bool Approved, string VmId, string HelperPath, long HelperBytes, string HelperSha256, string TrustBoundary, string RestrictiveChildPolicy, string OfflineDecisionSha256);
    sealed record HelperFinal(string Kind, string Status, string VmId, string RootUuid, string Nonce, string Operation, string BootId, string DecisionSha256, string? CompletedSha256, long HelperBytes, string HelperSha256, bool InstalledRootUnmounted, bool SeedUnmounted, bool Clean, int Errors, bool NoRuntimeAuthority);
    sealed record HelperProvision(string Kind, string VmId, string HelperPath, long HelperBytes, string HelperSha256, uint Uid, uint Gid, int Mode, ulong Inode, uint Major, uint Minor, uint Links);
    sealed record HelperCompleted(string Kind, string VmId, string RootUuid, string Nonce, string InstallerBootId, HelperProvision HelperProvision, string PolicyPath, string PolicySha256, long PolicyBytes, bool NoRuntimeAuthority, long ReadBytesWithReserve, long WriteBytesBeforeReceipt, int OperationsBeforeReceipt);
    sealed class HelperGuard(ToolLifetime original)
    {
        const string PathName = "/codex-code-mode-host", Vm = "6baeba02-2e64-4b62-a051-4bb82f57a913";
        const string Hash = "6b4de7a1a69656779c88b6e249ab976cbbc9a43e1edf65e6ec9927feb26f996b";
        const string RootUuid = "336b7308-380b-4f03-8a00-a46b12910576", ProvisionNonce = "3ebae3b812d1490daae436b81a17f137";
        const string ProvisionDirectory = "/var/lib/delivery-forge-route-a-" + ProvisionNonce;
        const string PolicyPath = "/etc/systemd/system/user-1000.slice.d/90-delivery-forge-conformance.conf";
        const string PolicyHash = "e8d9d5317a40bd34453241e81f7e0c3f035d79bb8a1d5d0d1b27bbb20ee07c2a";
        const long Extent = 74277776;
        HelperProvision? provision; Statx selected, selectedPolicy; bool captured, policyCaptured;
        FileStream? bootStream; bool bootCloseEntered, bootClosed;
        T ReadSelected<T>(AdmissionFile pin) where T : class
        {
            if (pin is null || pin.Bytes is < 1 or > 65536) throw new IOException("Finite explicit helper policy/raw completed receipt pin required.");
            var bytes = BoundedFiles.Read(pin.Path, original, 65536);
            if (bytes.LongLength != pin.Bytes || BoundedFiles.Sha(bytes) != pin.Sha256) throw new IOException("Complete selected helper DATA pin changed.");
            return JsonSerializer.Deserialize<T>(bytes, BoundedFiles.Json) ?? throw new IOException("Null selected helper DATA.");
        }
        Statx Observe(string path)
        {
            original.Check(); var returned = statx(-100, path, 0x100, 0x7ff, out var row); original.Check();
            if (returned != 0 || (row.Mask & 0x3df) != 0x3df) throw new IOException("Complete actual no-follow helper/policy metadata unavailable.");
            return row;
        }
        internal static void RequireAbsent(ToolLifetime original, string helperPath, string policyPath)
        {
            // Two no-follow observations only: existing files, dangling links
            // and unexpected lookup errors all refuse before any image birth.
            foreach (var path in new[] { helperPath, policyPath })
            {
                original.Check();
                var returned = statx(-100, path, 0x100, 0x7ff, out _);
                var error = Marshal.GetLastPInvokeError();
                original.Check();
                if (returned != -1 || error != 2) throw new IOException("Non-helper Linux fixture requires absent helper and ancestor policy; rollback is required before this fixture.");
            }
        }
        internal static void RequireAmbientAbsent(ToolLifetime original) => RequireAbsent(original, PathName, PolicyPath);
        static void RootDirectory(Statx row)
        {
            if ((row.Mode & 0xf000) != 0x4000 || row.Uid != 0 || row.Gid != 0 || (row.Mode & 0x12) != 0) throw new IOException("Root-owned non-writable selected ancestor required.");
        }
        void CheckProvisionDirectory()
        {
            foreach (var path in new[] { "/", "/var", "/var/lib", ProvisionDirectory }) RootDirectory(Observe(path));
            var found = new HashSet<string>(StringComparer.Ordinal);
            original.Check(); using var entries = Directory.EnumerateFileSystemEntries(ProvisionDirectory).GetEnumerator(); original.Check();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                original.Check(); var more = entries.MoveNext(); original.Check();
                if (!more)
                {
                    if (!found.SetEquals(new[] { "prepared.json", "completed.json" })) throw new IOException("Actual successful provision receipt directory is incomplete.");
                    return;
                }
                var name = Path.GetFileName(entries.Current);
                if (name is not ("prepared.json" or "completed.json") || !found.Add(name)) throw new IOException("Failed, partial or extra provision attempt evidence requires Root review before conformance.");
            }
            throw new IOException("Finite successful provision receipt directory exceeded.");
        }
        internal void Prepare(Fixture fixture)
        {
            var selectedHelper = fixture.LinuxOwnerTrustedHelper ?? throw new IOException("Explicit owner-trusted Linux helper selection required; no ambient helper admission.");
            var decision = ReadSelected<HelperDecision>(selectedHelper.OwnerDecision);
            if (decision.Kind != "RouteALinuxCodexHelperOwnerDecision-v1" || !decision.Approved || decision.VmId != Vm || decision.HelperPath != PathName || decision.HelperBytes != Extent || decision.HelperSha256 != Hash || decision.TrustBoundary != "root-owned-path-not-sealed" || decision.RestrictiveChildPolicy != "nnp-user1000-ancestor-v3" || decision.OfflineDecisionSha256 is not { Length: 64 }) throw new IOException("Exact owner-approved helper trust boundary required.");
            if (selectedHelper.CompletedReceipt.Path != ProvisionDirectory + "/completed.json") throw new IOException("Select the raw actual provision receipt, never an unbound projection.");
            CheckProvisionDirectory();
            var terminal = ReadSelected<HelperFinal>(selectedHelper.FinalDisposition);
            if (terminal.Kind != "RouteAOfflineFinal-v8" || terminal.Status != "PASS" || terminal.VmId != Vm || terminal.RootUuid != RootUuid || terminal.Nonce != ProvisionNonce || terminal.Operation != "provision" || !Guid.TryParseExact(terminal.BootId, "D", out _) || terminal.DecisionSha256 != decision.OfflineDecisionSha256 || terminal.CompletedSha256 != selectedHelper.CompletedReceipt.Sha256 || terminal.HelperBytes != Extent || terminal.HelperSha256 != Hash || !terminal.InstalledRootUnmounted || !terminal.SeedUnmounted || !terminal.Clean || terminal.Errors != 0 || !terminal.NoRuntimeAuthority) throw new IOException("Selected successful post-unmount original final DATA required; completed file alone is insufficient.");
            var completed = ReadSelected<HelperCompleted>(selectedHelper.CompletedReceipt);
            if (completed.InstallerBootId != terminal.BootId || completed.Kind != "ActualOfflineHelperAncestorProvision-v1" || completed.VmId != Vm || completed.RootUuid != RootUuid || completed.Nonce != ProvisionNonce || !Guid.TryParseExact(completed.InstallerBootId, "D", out _) || completed.PolicyPath != PolicyPath || completed.PolicyBytes != 88 || completed.PolicySha256 != PolicyHash || !completed.NoRuntimeAuthority) throw new IOException("Exact raw completed provision/nonce/root/policy binding required.");
            provision = completed.HelperProvision ?? throw new IOException("Raw completed receipt lacks its actual helper provision.");
            if (provision.Kind != "ActualRootOwnedLinuxCodexHelperProvision-v1" || provision.VmId != Vm || provision.HelperPath != PathName || provision.HelperBytes != Extent || provision.HelperSha256 != Hash || provision.Uid != 0 || provision.Gid != 0 || provision.Mode != 365 || provision.Inode == 0 || provision.Links != 2) throw new IOException("Exact selected actual helper provision DATA required.");
            if (!Guid.TryParseExact(selectedHelper.PreparedBootId, "D", out _)) throw new IOException("Fresh actual prepared guest boot required.");
            var buffer = new byte[65]; var count = 0;
            Exception? primary = null, close = null, post = null;
            try
            {
                original.Check(); bootStream = new("/proc/sys/kernel/random/boot_id", FileMode.Open, FileAccess.Read, FileShare.Read); original.Check();
                while (count < buffer.Length) { original.Check(); var n = bootStream.Read(buffer, count, buffer.Length - count); original.Check(); if (n == 0) break; count += n; }
                if (count != 37 || Encoding.ASCII.GetString(buffer, 0, count) != selectedHelper.PreparedBootId + "\n") throw new IOException("Actual current guest boot differs from prepared helper selection.");
            }
            catch (Exception error) { primary = error; }
            if (bootStream is not null)
            {
                // First-close is attempted despite a primary failure and never retried.
                try { bootCloseEntered = true; bootStream.Dispose(); bootClosed = true; }
                catch (Exception error) { close = error; }
            }
            try { original.Check(); } catch (Exception error) { post = error; }
            var failures = new[] { primary, close, post }.OfType<Exception>().ToArray();
            if (failures.Length > 1) throw new AggregateException("Original boot observation, first-close and post-check failures.", failures);
            if (failures.Length == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            CheckPolicy(); CheckMetadata();
            // Exactly ONE prelaunch helper pass; successful completion adds the
            // second pass below, both on the SAME original file credit.
            BoundedFiles.VerifyNativeImage(new(PathName, Extent, Hash), original);
            CheckMetadata();
        }
        void CheckPolicyMetadata()
        {
            foreach (var path in new[] { "/", "/etc", "/etc/systemd", "/etc/systemd/system", "/etc/systemd/system/user-1000.slice.d" }) RootDirectory(Observe(path));
            var actual = Observe(PolicyPath);
            if ((actual.Mode & 0xf000) != 0x8000 || actual.Uid != 0 || actual.Gid != 0 || (actual.Mode & 0x12) != 0 || actual.Size != 88 || actual.Links != 2) throw new IOException("Actual selected root-owned ancestor policy differs.");
            if (policyCaptured && (actual.Inode != selectedPolicy.Inode || actual.Major != selectedPolicy.Major || actual.Minor != selectedPolicy.Minor || actual.MTimeSeconds != selectedPolicy.MTimeSeconds || actual.MTimeNanos != selectedPolicy.MTimeNanos || actual.CTimeSeconds != selectedPolicy.CTimeSeconds || actual.CTimeNanos != selectedPolicy.CTimeNanos)) throw new IOException("Actual same-boot selected ancestor policy identity changed.");
            selectedPolicy = actual; policyCaptured = true;
        }
        void CheckPolicy()
        {
            CheckPolicyMetadata(); var bytes = BoundedFiles.Read(PolicyPath, original, 88);
            if (bytes.Length != 88 || BoundedFiles.Sha(bytes) != PolicyHash) throw new IOException("Actual complete selected ancestor policy hash differs.");
            CheckPolicyMetadata();
        }
        internal void VerifyAfterRun()
        {
            CheckProvisionDirectory(); CheckPolicy();
            CheckMetadata(); BoundedFiles.VerifyNativeImage(new(PathName, Extent, Hash), original); CheckMetadata();
        }
        internal void CheckMetadata()
        {
            if (provision is null || bootCloseEntered && !bootClosed) throw new IOException("Original helper preparation/first-close incomplete.");
            RootDirectory(Observe("/")); var actual = Observe(PathName);
            if ((actual.Mode & 0xf000) != 0x8000 || (actual.Mode & 0xfff) != 365 || actual.Uid != 0 || actual.Gid != 0 || actual.Size != Extent || actual.Links != 2 || actual.Inode != provision.Inode) throw new IOException("Actual root-owned helper identity/permissions differ.");
            // Raw receipt Major/Minor describe the rescue mount. Capture actual
            // installed-boot device identity here, then require SAME-boot stability.
            if (captured && (actual.Inode != selected.Inode || actual.Size != selected.Size || actual.Major != selected.Major || actual.Minor != selected.Minor || actual.MTimeSeconds != selected.MTimeSeconds || actual.MTimeNanos != selected.MTimeNanos || actual.CTimeSeconds != selected.CTimeSeconds || actual.CTimeNanos != selected.CTimeNanos)) throw new IOException("Original root-owned helper metadata changed before birth or after run.");
            selected = actual; captured = true; original.Check();
        }
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        struct Statx
        {
            [FieldOffset(0)] public uint Mask; [FieldOffset(16)] public uint Links;
            [FieldOffset(20)] public uint Uid; [FieldOffset(24)] public uint Gid; [FieldOffset(28)] public ushort Mode;
            [FieldOffset(32)] public ulong Inode; [FieldOffset(40)] public ulong Size;
            [FieldOffset(96)] public long CTimeSeconds; [FieldOffset(104)] public uint CTimeNanos;
            [FieldOffset(112)] public long MTimeSeconds; [FieldOffset(120)] public uint MTimeNanos;
            [FieldOffset(136)] public uint Major; [FieldOffset(140)] public uint Minor;
        }
        [DllImport("libc", SetLastError = true)] static extern int statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx value);
    }
    sealed class Frame : IAsyncDisposable
    {
        static readonly List<Frame> Unknown = [];
        internal readonly string RunId = "probe-" + Guid.NewGuid().ToString("N");
        internal bool BindingCurrent = true;
        HelperGuard? helper; LinuxScopeSelection? helperScope;
        internal WorktreeManager ProvisioningGit = null!;
        internal ParentEffectAdmission Parent = null!;
        internal readonly ToolLifetime Original = new(TimeSpan.FromSeconds(600), TestContext.Current.CancellationToken);
        internal readonly List<Task> ActualCalls = []; internal Fixture Selected = null!; internal IHostToolSessionPort Port = null!; internal WorktreeManager Git = null!; internal ProcessCommandExecutor Executor = null!; internal WriterLease? Lease; internal AgentRequest Request = null!;
        internal DuplexRetirement? Retirement; internal bool LeaseCloseEntered, LeaseClosed, DispositionEntered, AcceptanceAssertionsReturned; internal Exception? Failure, Post; internal NativeFailureSnapshotWriter? FailureSnapshot;
        internal Task<AgentAcceptedReceipt>? StartupCall; internal CancellationTokenSource? StartupPhase;
        internal long StartupPhaseStarted; internal TimeSpan StartupPhaseLimit; internal bool StartupPhaseCloseEntered, StartupPhaseClosed;
        internal OwnedDuplexProcess? Native => Port switch { DeliveryForge.Tool.Codex.CodexAppServerPort p => p.OriginalProcess, DeliveryForge.Tool.Claude.ClaudeStreamPort p => p.OriginalProcess, DeliveryForge.Tool.Pi.PiRpcPort p => p.OriginalProcess, _ => null };
        internal async Task<T> Call<T>(Func<Task<T>> invoke) { Original.Check(); var actual = invoke(); ActualCalls.Add(actual); try { var result = await actual.WaitAsync(Original.Remaining, TestContext.Current.CancellationToken); Original.Check(); return result; } catch (Exception error) { Failure ??= error; throw; } }
        internal static Frame Prepare(string adapter, bool stop)
        {
            var frame = new Frame(); lock (Unknown) Unknown.Add(frame);
            var fixtureKey = "DELIVERY_FORGE_NATIVE_CONFORMANCE_" + (adapter == "codex.cli" ? "CODEX" : adapter == "claude-code.cli" ? "CLAUDE" : "PI") + (stop ? "_STOP" : "_COMPLETE");
            var path = Environment.GetEnvironmentVariable(fixtureKey) ?? throw new IOException("Fresh Root-admitted real native conformance fixture required; no model/CLI fallback.");
            var rawFixture = BoundedFiles.Read(path, frame.Original, 65536); var expectedFixtureHash = Environment.GetEnvironmentVariable(fixtureKey + "_SHA256") ?? throw new IOException("Independent complete fixture hash required."); Assert.Equal(expectedFixtureHash, BoundedFiles.Sha(rawFixture)); _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(rawFixture); frame.Original.Check(); frame.Selected = JsonSerializer.Deserialize<Fixture>(rawFixture, BoundedFiles.Json) ?? throw new IOException("Actual probe fixture null."); var f = frame.Selected;
            if (f.Kind != "RootAdmittedNativeConformanceProvisioningFixture-v2" || f.CaseKind != (stop ? "stop" : "completion") || f.PlanBundle is null || f.Expectations is null || f.Adapter != adapter || f.Host is null || string.IsNullOrWhiteSpace(f.Host.Version) || f.Host.Version == "unverified" || f.Host.Version.Length > 256 || f.RelativePath is null || f.RelativePath.Length is < 1 or > 256 || f.RelativePath.Contains('\\') || Path.IsPathFullyQualified(f.RelativePath) || f.RelativePath.Split('/').Any(v => v is "" or "." or "..") || f.ExpectedText is null || f.ExpectedText.Length is < 1 or > 4096 || f.ExpectedText.Contains('\0')) throw new IOException("Exact finite independently prepared real-host task fixture required.");
            // Once is Root-procedural: select one fresh original/nonce; no fixture token DB or replay grant.
            if (f.LinuxDangerFullAccessOnce && (!OperatingSystem.IsLinux() || adapter != "codex.cli" || stop || f.CaseKind != "completion" || f.ObserveRuntimeTrustRoute || f.LinuxOwnerTrustedHelper is null))
                throw new IOException("One explicitly selected helper-bound Linux Codex completion fixture required for danger-full-access; no production or stop-route selection.");
            if (f.ObserveRuntimeTrustRoute && (adapter != "codex.cli" || !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() ||
                OperatingSystem.IsLinux() && f.LinuxScopeProfile is null))
                throw new IOException("Trust-route conformance selects only the existing Windows/Linux Codex UNVERIFIED producer with an actual Linux scope; no authority is granted.");
            if (f.LinuxOwnerTrustedHelper is not null && !stop && f.CaseKind == "completion" && OperatingSystem.IsLinux() && adapter == "codex.cli" && f.Host.Version == "0.163.0-alpha.10.2")
            {
                if (f.Host.Bytes != 296885960 || f.Host.Sha256 != "70ea33b7094a4c20c007d09b83bb50e33debb9bbf99bf066709f9eb85bad524a") throw new IOException("Exact measured RouteA primary image required.");
                frame.helper = new(frame.Original);
            }
            else if (f.LinuxOwnerTrustedHelper is not null) throw new IOException("Helper trust selection is only the explicit measured Linux Codex fixture.");
            if (OperatingSystem.IsLinux() && frame.helper is null) HelperGuard.RequireAmbientAbsent(frame.Original);
            BoundedFiles.Plain(f.ParentCheckout, true); BoundedFiles.Plain(f.Worktree, true); BoundedFiles.Plain(f.ResultDirectory, true); BoundedFiles.Separate(f.ParentCheckout, f.Worktree); BoundedFiles.Separate(f.Worktree, f.ResultDirectory);
            if (Directory.Exists(f.Worktree) || File.Exists(f.Worktree))
                throw new IOException("Native conformance v2 requires the exact independently admitted absent destination; existing metadata cannot be adopted.");
            BoundedFiles.VerifyNativeImage(new(f.Host.Executable, f.Host.Bytes, f.Host.Sha256), frame.Original);
            LinuxScopeSelection? scope = null; if (OperatingSystem.IsLinux())
            {
                var pin = f.LinuxScopeProfile ?? throw new IOException("Actual independently prepared Linux profile required."); var raw = BoundedFiles.Read(pin.Path, frame.Original, 65536); Assert.Equal(pin.Bytes, raw.LongLength); Assert.Equal(pin.Sha256, BoundedFiles.Sha(raw)); scope = JsonSerializer.Deserialize<LinuxScopeSelection>(raw, BoundedFiles.Json) ?? throw new IOException("Null actual Linux selection.");
            }
            if (frame.helper is not null && scope?.RestrictiveChildPolicy != "nnp-user1000-ancestor-v3") throw new IOException("Explicit helper selection requires the creator-observed unprivileged child policy.");
            frame.helper?.Prepare(f); frame.helperScope = frame.helper is null ? null : scope;
            var executor = OperatingSystem.IsLinux() ? new ProcessCommandExecutor(scope!, frame.Original) : new ProcessCommandExecutor(); frame.Executor = executor; frame.Git = new(f.Git, executor, frame.Original);
            // A private finite test parent owns actual provisioning/captures;
            // it issues no production host/profile or coordination authority.
            frame.Parent = new(new object(), run => frame.BindingCurrent && run == frame.RunId, _ => frame.BindingCurrent = false);
            frame.ProvisioningGit = frame.Git.WithParentAdmission(frame.Parent, frame.RunId);
            // UNVERIFIED remains UNVERIFIED throughout this probe. Canonical
            // owner acceptance MUST refuse it until independent actual audit.
            var profile = new AgentAdapterProfile(adapter, f.Host.Version, new[] { AgentCapability.Launch, AgentCapability.CompletionEvidence, AgentCapability.Stop, AgentCapability.EvidenceExport }.Select(c => new AgentCapabilityEvidence(c, AgentCapabilityStatus.Unverified, "actual installed transport UNDER TEST", "source-defined genuine probe, not conformance authority")).ToArray());
            frame.Port = adapter switch
            {
                "codex.cli" => new DeliveryForge.Tool.Codex.CodexAppServerPort(new(f.Host.Executable, f.Host.Bytes, f.Host.Sha256, f.Host.Version), frame.Git, profile, scope, null, f.ObserveRuntimeTrustRoute, f.LinuxDangerFullAccessOnce),
                "claude-code.cli" => new DeliveryForge.Tool.Claude.ClaudeStreamPort(new(f.Host.Executable, f.Host.Bytes, f.Host.Sha256, f.Host.Version), frame.Git, profile, scope),
                "pi.cli" => new DeliveryForge.Tool.Pi.PiRpcPort(new(f.Host.Executable, f.Host.Bytes, f.Host.Sha256, f.Host.Version), frame.Git, profile, scope),
                _ => throw new IOException("No arbitrary protocol dispatch.")
            };
            ((IOriginalBoundHostPort)frame.Port).BindOriginal(frame.Original); return frame;
        }
        internal async Task<AgentAcceptedReceipt> Start(bool stopProbe)
        {
            var f = Selected; Original.Check();
            var planBytes = BoundedFiles.Read(f.PlanBundle.Path, Original, 524288); Assert.Equal(f.PlanBundle.Bytes, planBytes.LongLength); Assert.Equal(f.PlanBundle.Sha256, BoundedFiles.Sha(planBytes));
            var importLimit = Original.Remaining < TimeSpan.FromSeconds(120) ? Original.Remaining : TimeSpan.FromSeconds(120);
            var imported = await Call(() => PlanImporter.ImportAndReconcileOwnedAsync(planBytes, new(f.ParentCheckout, f.MutableReference, f.Git), f.Expectations, importLimit, Executor, Original, cancellationToken: TestContext.Current.CancellationToken));
            var plan = imported.Plan ?? throw new IOException("Actual conformance rich-plan import refused."); Assert.True(imported.DownstreamReady); Assert.Equal(f.BaseCommit, plan.BaseCommit); Assert.Equal(f.Repository, plan.Repository);
            using var material = JsonDocument.Parse(plan.CanonicalBytes); Assert.Contains(material.RootElement.GetProperty("plan").GetProperty("changeMap").EnumerateArray(), row => row.GetProperty("path").GetString() == f.RelativePath);
            // Preserve the selected task/source/destination. Never rename or
            // replace a precreated fixture worktree to manufacture provisioning.
            await Call(async () => { await ProvisioningGit.PrepareAsync(f.ParentCheckout, f.Worktree, f.BaseCommit, TestContext.Current.CancellationToken); return true; });
            var before = await Call(() => Git.ObserveAsync(f.Worktree, TestContext.Current.CancellationToken));
            Assert.Equal(f.BaseCommit, before.HeadCommit); Assert.True(before.IsClean); Original.Check();
            var run = RunId; var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            Lease = ProvisioningGit.AcquireWriter(f.Worktree, run, token); Original.Check();
            var assignment = new FrozenWorkerPlan(run, plan.ContractIdentity, f.BaseCommit, f.Repository, f.ParentCheckout, f.Worktree, f.ResultDirectory, token, [f.RelativePath], [], f.Adapter, f.Host.Version, [AgentCapability.Launch, AgentCapability.CompletionEvidence]); Request = WorkerEnvelope.PrepareWorker(assignment);
            var instructions = HostInstructionContext.FromFrozenPlan(plan, assignment);
            ((IHostInstructionPort)Port).BindInstructions(instructions);
            // A narrower startup phase consumes the SAME600s original. Its
            // cancellation reaches only this Start call; the actual port pump
            // remains owned by Original.Token and settles through its own owner.
            Original.Check(); StartupPhaseLimit = Original.Remaining < TimeSpan.FromSeconds(120) ? Original.Remaining : TimeSpan.FromSeconds(120);
            StartupPhaseStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            StartupPhase = CancellationTokenSource.CreateLinkedTokenSource(Original.Token, TestContext.Current.CancellationToken); StartupPhase.CancelAfter(StartupPhaseLimit);
            AgentAcceptedReceipt accepted;
            try
            {
                helper?.CheckMetadata();
                StartupCall = Port.StartAsync(Request, StartupPhaseLimit, StartupPhase.Token); ActualCalls.Add(StartupCall);
                accepted = await StartupCall.WaitAsync(StartupPhaseLimit, StartupPhase.Token);
                Original.Check(); StartupPhase.Token.ThrowIfCancellationRequested();
                if (System.Diagnostics.Stopwatch.GetElapsedTime(StartupPhaseStarted) >= StartupPhaseLimit) throw new TimeoutException("Actual conformance startup phase exceeded its original bounded allowance.");
            }
            catch (Exception error) { Failure ??= error; throw; }
            if (helper is not null && Native is not { HasCreationOwnedExactLinuxImage: true }) throw new IOException("Selected Codex main did not return its actual checked exact-image release.");
            Assert.Equal(Request.RequestIdentity, accepted.RequestIdentity); Assert.Equal(Request.RunId, accepted.RunId); Assert.Equal(Request.PlanIdentity, accepted.PlanIdentity); Assert.Equal(Request.BaseCommit, accepted.BaseCommit); Assert.Equal(Request.Worktree, accepted.Worktree); Assert.Equal(Request.AdapterId, accepted.AdapterId); Assert.Equal(Request.AdapterVersion, accepted.AdapterVersion); Assert.False(string.IsNullOrWhiteSpace(accepted.RuntimeRunIdentity)); Assert.False(string.IsNullOrWhiteSpace(accepted.RuntimeTaskIdentity)); Assert.All(accepted.Capabilities, c => Assert.Equal(AgentCapabilityStatus.Unverified, c.Status));
            Assert.Throws<AgentCapabilityIncompleteException>(() => WorkerEnvelope.ValidateAccepted(Request, accepted)); return accepted;
        }
        internal async Task<WorktreeObservation> ObserveAfterRun()
        {
            // This is the existing post-retirement Git observation. Its actual
            // selected ordinary scopes re-read the ancestor in the existing
            // kernel ledger; no additional process or adoption is introduced.
            if (helper is not null) Executor.RequireSelectedLinuxCaptureOwner(Original, helperScope ?? throw new IOException("Selected original helper scope missing."));
            var after = await Call(() => Git.ObserveAsync(Selected.Worktree, TestContext.Current.CancellationToken));
            helper?.VerifyAfterRun(); return after;
        }
        internal static AgentRunBinding Binding(AgentAcceptedReceipt a) => new(a.AdapterId, a.AdapterVersion, a.RunId, a.RequestIdentity, a.RuntimeRunIdentity, a.RuntimeTaskIdentity);
        internal void CaptureFailureAfterNativeSettlement()
        {
            _ = Original.CleanupRemaining;
            if (FailureSnapshot is not null) throw new IOException("Original snapshot attempt remains retained; no replacement or second artifact attempt.");
            var native = Native ?? throw new IOException("No retained actual native owner for failure snapshot.");
            if (Retirement is not { KnownClosed: true, RootExited: true, ActiveProcesses: 0, DispositionReturned: true } || native.HasUnknownIo) throw new IOException("No failure snapshot before actual same-original native settlement.");
            var stderr = native.StandardError; _ = Original.CleanupRemaining;
            if (stderr.Length > 1048576) throw new IOException("Original stderr profile exceeded.");
            // Retain raw bytes only in the original private owner. Arbitrary
            // stderr, account responses and prompts cannot be made safe by a
            // regex redaction claim. Export exact hash/extent and fixed signals.
            var text = Encoding.UTF8.GetString(stderr); var signals = new List<string>();
            foreach (var signal in new[] { "not initialized", "already initialized", "failed to initialize sqlite", "error loading", "permission denied", "connection refused", "timed out", "unauthorized", "authentication", "invalid request", "error", "Resource temporarily unavailable", "failed to spawn", "out of memory", "failed to create", "Operation not permitted", "Read-only file system" })
                if (text.Contains(signal, StringComparison.OrdinalIgnoreCase)) signals.Add(signal);
            var io = native.ActualIo; var first = io.Take(128).Select(x => new { x.Kind, x.Entered, x.InvocationReturned, TaskStatus = x.ActualTask?.Status, PrimaryType = x.Primary?.GetType().FullName, PostType = x.Post?.GetType().FullName, x.Unknown }).ToArray();
            var value = new
            {
                Kind = "ActualNativeHostFailureSnapshot-v1",
                Selected.Adapter,
                Selected.CaseKind,
                OriginalStarted = Original.OriginalStarted,
                Frequency = System.Diagnostics.Stopwatch.Frequency,
                ElapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - Original.OriginalStarted,
                StartupPhaseStarted,
                StartupPhaseLimitTicks = StartupPhaseLimit.Ticks,
                StartupPhaseTaskStatus = StartupCall?.Status,
                StartupPhaseCancellationRequested = StartupPhase?.IsCancellationRequested,
                PrimaryType = Failure?.GetType().FullName,
                PostType = Post?.GetType().FullName,
                Startup = Port is DeliveryForge.Tool.Codex.CodexAppServerPort codex ? codex.SnapshotStartupDiagnostics() : null,
                StandardError = new { Bytes = stderr.Length, Sha256 = BoundedFiles.Sha(stderr), Signals = signals, TextExported = false },
                Native = new { Retirement.KnownClosed, Retirement.RootExited, Retirement.ActiveProcesses, Retirement.OutputEof, Retirement.ErrorEof, Retirement.InputCloseReturned, Retirement.DispositionReturned, Retirement.ExitCode, FailureCount = Retirement.Failures.Count },
                ActualIoCount = io.Count,
                ActualIo = first,
                IoMetadataBoundExceeded = io.Count > first.Length,
                FrameTasks = ActualCalls.Take(128).Select(t => t.Status).ToArray(),
                FrameTaskCount = ActualCalls.Count,
                Accepted = false,
                RuntimeCertified = false,
                RawAccountOrPromptExported = false
            };
            _ = Original.CleanupRemaining; var bytes = JsonSerializer.SerializeToUtf8Bytes(value, BoundedFiles.Json); _ = Original.CleanupRemaining;
            FailureSnapshot = new(Original); FailureSnapshot.Write(Path.Combine(Selected.ResultDirectory, "native-host-failure.json"), bytes); _ = Original.CleanupRemaining;
        }
        public async ValueTask DisposeAsync()
        {
            if (DispositionEntered) throw new IOException("Original native conformance disposition already entered; no retry."); DispositionEntered = true;
            Exception? snapshotFailure = null;
            try
            {
                if (Native is not null) { var actual = Native.RetireAsync(Retirement is null); ActualCalls.Add(actual); Retirement = await actual.WaitAsync(Original.CleanupRemaining); }
                // An outer phase timeout never substitutes for the actual Start
                // task. After native retirement, observe that original task's
                // real terminal result within the SAME cleanup endpoint.
                if (StartupCall is not null)
                {
                    try { await StartupCall.WaitAsync(Original.CleanupRemaining); }
                    catch (Exception error) { if (!StartupCall.IsCompleted) throw; Failure ??= error; }
                }
                if (!AcceptanceAssertionsReturned)
                {
                    if (FailureSnapshot is { Unknown: true }) throw new IOException("Original snapshot acquisition or close remains UNKNOWN; original graph retained.");
                    if (FailureSnapshot is not null)
                        snapshotFailure = new IOException("Original snapshot attempt remains retained; no replacement or second artifact attempt.");
                    else if (Native is not null)
                    {
                        try { CaptureFailureAfterNativeSettlement(); }
                        catch (Exception error) when (FailureSnapshot is { Unknown: false }) { snapshotFailure = error; }
                    }
                }
                if (Retirement is { KnownClosed: false }
                    || Native is { } retainedNative && (Retirement is not { KnownClosed: true, RootExited: true, ActiveProcesses: 0, DispositionReturned: true } || retainedNative.HasUnknownIo)
                    || ActualCalls.Any(t => !t.IsCompleted)) throw new IOException("Actual native conformance remains UNKNOWN; original graph retained, no passing cleanup.");
                if (StartupPhase is not null) { _ = Original.CleanupRemaining; StartupPhaseCloseEntered = true; StartupPhase.Dispose(); StartupPhaseClosed = true; _ = Original.CleanupRemaining; }
                if (Lease is not null) { _ = Original.CleanupRemaining; LeaseCloseEntered = true; Lease.Dispose(); LeaseClosed = true; _ = Original.CleanupRemaining; }
                Original.Dispose(); lock (Unknown) Unknown.Remove(this);
                if (snapshotFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(snapshotFailure).Throw();
            }
            catch (Exception error)
            {
                Post = snapshotFailure is not null && !ReferenceEquals(snapshotFailure, error)
                    ? new AggregateException("Original snapshot and same-original disposition failures.", snapshotFailure, error) : error;
                if (Failure is not null && !ReferenceEquals(Failure, Post)) throw new AggregateException("Actual native primary and same-original failure disposition.", Failure, Post);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Post).Throw();
                throw;
            } // Unknown/incomplete disposition remains retained; settled owners can still report a snapshot failure. No retry.
        }
    }
    [Fact(Timeout = 30000)]
    public void Failure_snapshot_retains_completed_first_writer_without_second_artifact_attempt()
    {
        var frame = new Frame();
        var root = Directory.CreateTempSubdirectory("forge-native-first-snapshot-").FullName;
        var path = Path.Combine(root, "codex-contract-stop-disposition.json");
        var bytes = Encoding.UTF8.GetBytes("{\"Kind\":\"TestCompletedFirstSnapshot-v1\"}");
        try
        {
            var first = new NativeFailureSnapshotWriter(frame.Original);
            frame.FailureSnapshot = first;
            first.Write(path, bytes);
            Assert.True(first.AttemptEntered); Assert.True(first.AcquisitionReturned);
            Assert.True(first.WriteReturned); Assert.True(first.FlushReturned);
            Assert.True(first.CloseReturned); Assert.True(first.PostReturned); Assert.False(first.Unknown);
            // Exercise the real capture boundary after a fully settled first
            // writer. No native owner is needed: a second attempt must refuse
            // before touching any native snapshot dependency or new artifact.
            var refusal = Assert.Throws<IOException>(frame.CaptureFailureAfterNativeSettlement);
            Assert.Equal("Original snapshot attempt remains retained; no replacement or second artifact attempt.", refusal.Message);
            Assert.Same(first, frame.FailureSnapshot);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(Path.Combine(root, "native-host-failure.json")));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally { frame.Original.Dispose(); }
        // Retain this tiny test-owned artifact, including on assertion failure.
    }
    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_preserves_first_snapshot_and_primary_but_releases_known_managed_owners(bool existingSnapshotRefused)
    {
        var frame = new Frame();
        var root = Directory.CreateTempSubdirectory("forge-native-snapshot-dispose-").FullName;
        var path = Path.Combine(root, "codex-contract-stop-disposition.json");
        var writerPath = Path.Combine(root, "original-writer.lock");
        var bytes = Encoding.UTF8.GetBytes("{\"Kind\":\"TestOriginalSnapshotBeforeDisposition\"}");
        var stream = new FileStream(writerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        frame.Lease = new WriterLease(writerPath, stream, frame.RunId, "test-owned-writer");
        frame.StartupPhase = CancellationTokenSource.CreateLinkedTokenSource(frame.Original.Token);
        try
        {
            var first = new NativeFailureSnapshotWriter(frame.Original);
            frame.FailureSnapshot = first;
            Exception primary;
            if (existingSnapshotRefused)
            {
                File.WriteAllBytes(path, bytes);
                primary = Assert.Throws<IOException>(() => first.Write(path, bytes));
                Assert.False(first.AcquisitionEntered);
            }
            else
            {
                first.Write(path, bytes);
                Assert.True(first.CloseReturned); Assert.True(first.PostReturned);
                primary = Assert.Throws<FileNotFoundException>(() => File.ReadAllBytes(Path.Combine(root, "missing-original-phase-input")));
            }
            Assert.False(first.Unknown);
            frame.Failure = primary;
            frame.StartupCall = Task.FromException<AgentAcceptedReceipt>(primary);
            frame.ActualCalls.Add(frame.StartupCall);
            // Release-only control: no Native or retirement proof is fabricated.
            // Real first-writer/lease streams and CTS exercise DisposeAsync itself.
            Assert.Null(frame.Native); Assert.Null(frame.Retirement);
            var failure = await Assert.ThrowsAsync<AggregateException>(async () => await frame.DisposeAsync());
            Assert.Equal(2, failure.InnerExceptions.Count);
            Assert.Same(primary, failure.InnerExceptions[0]);
            var refusal = Assert.IsType<IOException>(failure.InnerExceptions[1]);
            Assert.Equal("Original snapshot attempt remains retained; no replacement or second artifact attempt.", refusal.Message);
            Assert.Same(refusal, frame.Post); Assert.Same(first, frame.FailureSnapshot);
            Assert.True(frame.DispositionEntered); Assert.True(frame.StartupPhaseCloseEntered); Assert.True(frame.StartupPhaseClosed);
            Assert.True(frame.LeaseCloseEntered); Assert.True(frame.LeaseClosed); Assert.True(frame.Lease.FirstCloseReturned);
            Assert.False(stream.CanRead);
            Assert.Throws<ObjectDisposedException>(frame.Original.Check);
            Assert.Throws<ObjectDisposedException>(() => { _ = frame.StartupPhase.Token; });
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(Path.Combine(root, "native-host-failure.json")));
            Assert.Equal(2, Directory.EnumerateFiles(root).Count());
        }
        finally
        {
            if (!frame.DispositionEntered)
            {
                frame.StartupPhase.Dispose(); frame.Lease.Dispose(); frame.Original.Dispose();
            }
        }
        // Retain the tiny original artifact and writer DATA on success or failure.
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
        if (f.ObserveRuntimeTrustRoute)
        {
            var artifact = Assert.Single(completion.ArtifactPaths);
            var traceBytes = BoundedFiles.Read(artifact, frame.Original);
            InstalledHostAdmission.RequireRuntimeTrustRouteArtifact(traceBytes, f.Host, f.Host.Version);
        }
        var native = frame.Native ?? throw new IOException("Actual original process reference missing."); var sameRetirement = native.RetireAsync(false); frame.Retirement = await sameRetirement.WaitAsync(frame.Original.Remaining, TestContext.Current.CancellationToken); HostTransportRetirement.RequireSuccessful(frame.Retirement);
        var after = await frame.ObserveAfterRun(); Assert.True(after.IsClean); Assert.Equal(after.HeadCommit, completion.HeadCommit); Assert.Equal(after.TreeId, completion.TreeId); Assert.Equal(Encoding.UTF8.GetBytes(f.ExpectedText), BoundedFiles.Read(Path.Combine(f.Worktree, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)), frame.Original));
        Assert.NotEmpty(completion.ArtifactPaths);
        foreach (var artifact in completion.ArtifactPaths)
        {
            var bytes = BoundedFiles.Read(artifact, frame.Original);
            Assert.DoesNotContain(frame.Request.WriterToken, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            if (adapter == "codex.cli")
            {
                using var trace = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
                Assert.Equal(f.LinuxDangerFullAccessOnce, trace.RootElement.GetProperty("ConformanceDangerFullAccess").GetBoolean());
                if (f.LinuxDangerFullAccessOnce) Assert.Throws<ToolUsageException>(() => InstalledHostAdmission.RequireNonDfaConformanceArtifact(bytes));
                else
                {
                    InstalledHostAdmission.RequireNonDfaConformanceArtifact(bytes);
                    InstalledHostAdmission.RequireLinuxCodexCompletionArtifact(bytes);
                }
            }
        }
        // These are original-group facts. Final whole-VM conformance acceptance
        // additionally requires Root's outer actual Off/IO/frame audit; this Fact
        // cannot certify that no manager-brokered descendants left the group.
        frame.AcceptanceAssertionsReturned = true;
    }
    [Theory(Timeout = 700000)]
    [InlineData("codex.cli")]
    [InlineData("claude-code.cli")]
    [InlineData("pi.cli")]
    public Task Actual_installed_live_session_stops_only_through_its_same_original_group(string adapter) => ActualStop(adapter, requireInterruptSuccess: true);
    [Fact(Timeout = 700000)]
    [Trait("Category", "InstalledLocalRealAcceptance")]
    public Task Actual_installed_trust_route_same_original_stop_preserves_optional_interrupt_refusal_and_terminal_work_faults() => ActualStop("codex.cli", requireInterruptSuccess: false);
    static async Task ActualStop(string adapter, bool requireInterruptSuccess)
    {
        using var affinity = new OwnedDuplexProcessTests.Affinity(); await using var frame = Frame.Prepare(adapter, true);
        if (!requireInterruptSuccess) Assert.True(frame.Selected.ObserveRuntimeTrustRoute, "Contract-specific Stop requires the actual selected Codex trust route.");
        var accepted = await frame.Start(true); var binding = Frame.Binding(accepted);
        if (frame.Selected.ObserveRuntimeTrustRoute)
        {
            // Six startup replies only: stopping is not successful completion and
            // cannot satisfy the distinct seven-reply completion route artifact.
            var codex = Assert.IsType<DeliveryForge.Tool.Codex.CodexAppServerPort>(frame.Port);
            var startup = codex.SnapshotStartupDiagnostics();
            Assert.Equal(DeliveryForge.Tool.Codex.CodexStartupStage.Accepted, startup.Stage);
            Assert.False(startup.WireBoundExceeded);
            Assert.True(startup.ThreadReceived); Assert.True(startup.TurnReceived);
            Assert.Null(startup.ReplyFailureType); Assert.Null(startup.PumpFailureType);
            Assert.Empty(startup.PendingIds);
            Assert.DoesNotContain(startup.Wire, row => row.HasError || row.ErrorCode is not null);
            DeliveryForge.Tool.Codex.CodexWireMethod[] methods =
            [
                DeliveryForge.Tool.Codex.CodexWireMethod.Initialize,
                DeliveryForge.Tool.Codex.CodexWireMethod.ConfigRead,
                DeliveryForge.Tool.Codex.CodexWireMethod.AccountRead,
                DeliveryForge.Tool.Codex.CodexWireMethod.ThreadStart,
                DeliveryForge.Tool.Codex.CodexWireMethod.ConfigRead,
                DeliveryForge.Tool.Codex.CodexWireMethod.TurnStart
            ];
            var numbered = startup.Wire.Where(row => row.ClientId is not null).ToArray();
            Assert.Equal(18, numbered.Length);
            for (var index = 0; index < methods.Length; index++)
            {
                var id = index + 1L;
                var entered = Assert.Single(numbered, row => row.ClientId == id && row.Direction == DeliveryForge.Tool.Codex.CodexWireDirection.Client && row.State == DeliveryForge.Tool.Codex.CodexWireState.WriteEntered);
                var returned = Assert.Single(numbered, row => row.ClientId == id && row.Direction == DeliveryForge.Tool.Codex.CodexWireDirection.Client && row.State == DeliveryForge.Tool.Codex.CodexWireState.WriteReturned);
                var received = Assert.Single(numbered, row => row.ClientId == id && row.Direction == DeliveryForge.Tool.Codex.CodexWireDirection.Server && row.State == DeliveryForge.Tool.Codex.CodexWireState.Received);
                Assert.Equal(methods[index], entered.Method); Assert.Equal(methods[index], returned.Method);
                Assert.Equal(DeliveryForge.Tool.Codex.CodexWireMethod.None, received.Method);
                Assert.All(new[] { entered, returned, received }, row => Assert.Equal(DeliveryForge.Tool.Codex.CodexWireIdKind.ClientNumber, row.IdKind));
            }
            Assert.All(accepted.Capabilities, capability => Assert.Equal(AgentCapabilityStatus.Unverified, capability.Status));
            // This must still be a live turn before the actual Stop attempt.
            // The correlated id7 reply below independently refuses the branch
            // that skips interrupt because the turn has already finished.
            if (requireInterruptSuccess) Assert.DoesNotContain(startup.Wire, row => row.Method == DeliveryForge.Tool.Codex.CodexWireMethod.TurnCompleted);
        }
        await Assert.ThrowsAsync<WorkerEnvelopeException>(() => frame.Port.ControlAsync(binding with { RuntimeRunIdentity = binding.RuntimeRunIdentity + "-foreign" }, AgentControlAction.Stop, frame.Original.Remaining, TestContext.Current.CancellationToken));
        var native = frame.Native ?? throw new IOException("Actual native original missing."); var live = native.ObserveActualNative(); Assert.True(live.Known); Assert.False(live.RootExited); Assert.True(live.ActiveProcesses > 0);
        // The true-route probe must enter turn/interrupt while SAME original
        // WORK is still open. Reading CleanupRemaining here first closes WORK
        // and makes Rpc refuse before send. ControlAsync owns subsequent native
        // failure retirement; the legacy probe keeps its existing cleanup route.
        var stopAllowance = frame.Selected.ObserveRuntimeTrustRoute ? frame.Original.Remaining : frame.Original.CleanupRemaining;
        var actual = frame.Port.ControlAsync(binding, AgentControlAction.Stop, stopAllowance, CancellationToken.None); frame.ActualCalls.Add(actual); var control = await actual.WaitAsync(frame.Selected.ObserveRuntimeTrustRoute ? stopAllowance : frame.Original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.Equal(binding, control.Binding); Assert.Equal(AgentActivityState.Quiescent, control.Activity);
        var retired = native.RetireAsync(true); frame.Retirement = await retired.WaitAsync(frame.Original.CleanupRemaining, TestContext.Current.CancellationToken); Assert.True(frame.Retirement.KnownClosed); Assert.True(frame.Retirement.RootExited); Assert.Equal(0u, frame.Retirement.ActiveProcesses); Assert.True(frame.Retirement.DispositionReturned); _ = frame.Original.CleanupRemaining;
        if (frame.Selected.ObserveRuntimeTrustRoute)
        {
            Assert.Equal(AgentControlAction.Stop, control.Action); Assert.True(control.CapabilitySupported);
            Assert.False(string.IsNullOrWhiteSpace(control.EvidenceReference));
            if (requireInterruptSuccess) Assert.True(control.Limitation.EndsWith("InterruptFailure=", StringComparison.Ordinal), "Actual turn/interrupt must return successfully before same-original retirement.");
            var stopped = Assert.IsType<DeliveryForge.Tool.Codex.CodexAppServerPort>(frame.Port).SnapshotStartupDiagnostics();
            Assert.False(stopped.WireBoundExceeded);
            Assert.Empty(stopped.PendingIds);
            // ControlAsync deliberately closes the reply registry with this
            // fixed terminal cause after retirement. This is not an RPC refusal.
            if (requireInterruptSuccess)
            {
                Assert.Equal(typeof(OperationCanceledException).FullName, stopped.ReplyFailureType);
                Assert.DoesNotContain(stopped.Wire, row => row.HasError || row.ErrorCode is not null);
            }
            var numbered = stopped.Wire.Where(row => row.ClientId is not null).ToArray();
            Assert.Equal(21, numbered.Length);
            var entered = Assert.Single(numbered, row => row.ClientId == 7 && row.Direction == DeliveryForge.Tool.Codex.CodexWireDirection.Client && row.State == DeliveryForge.Tool.Codex.CodexWireState.WriteEntered);
            var returned = Assert.Single(numbered, row => row.ClientId == 7 && row.Direction == DeliveryForge.Tool.Codex.CodexWireDirection.Client && row.State == DeliveryForge.Tool.Codex.CodexWireState.WriteReturned);
            var received = Assert.Single(numbered, row => row.ClientId == 7 && row.Direction == DeliveryForge.Tool.Codex.CodexWireDirection.Server && row.State == DeliveryForge.Tool.Codex.CodexWireState.Received);
            Assert.Equal(DeliveryForge.Tool.Codex.CodexWireMethod.TurnInterrupt, entered.Method);
            Assert.Equal(DeliveryForge.Tool.Codex.CodexWireMethod.TurnInterrupt, returned.Method);
            Assert.Equal(DeliveryForge.Tool.Codex.CodexWireMethod.None, received.Method);
            Assert.All(new[] { entered, returned, received }, row => Assert.Equal(DeliveryForge.Tool.Codex.CodexWireIdKind.ClientNumber, row.IdKind));
            Assert.Same(native, frame.Native);
            Assert.Same(retired, native.OriginalRetirementTask);
            Assert.True(frame.Retirement.OutputEof); Assert.True(frame.Retirement.ErrorEof);
            Assert.True(frame.Retirement.InputCloseReturned);
            if (requireInterruptSuccess) Assert.Empty(frame.Retirement.Failures);
            Assert.False(native.HasUnknownIo);
            Assert.All(frame.ActualCalls, call => Assert.True(call.IsCompleted));
            Assert.All(Assert.IsType<DeliveryForge.Tool.Codex.CodexAppServerPort>(frame.Port).OriginalTasks, call => Assert.True(call.IsCompleted));
            // RouteConformanceOnly=true; RuntimeCertified=false. This existing
            // UNVERIFIED probe cannot construct Root trust or an accepted profile.
            Assert.All(frame.Port.Profile.Capabilities, capability => Assert.Equal(AgentCapabilityStatus.Unverified, capability.Status));
            if (!requireInterruptSuccess)
            {
                // This is a stopped owner, not successful WORK or a successful
                // optional protocol interrupt. Preserve refused/terminal faults
                // as bounded metadata after actual known physical disposition.
                _ = frame.Original.CleanupRemaining;
                var io = native.ActualIo;
                var faultRows = io.Where(row => row.Primary is not null || row.Post is not null).ToArray();
                var summary = new
                {
                    Kind = "ActualInstalledCodexContractStopDisposition-v1",
                    frame.Selected.Host,
                    Version = frame.Selected.Host.Version,
                    control.Action,
                    control.Activity,
                    control.CapabilitySupported,
                    control.EvidenceReference,
                    StartupTrustReplyCount = 6,
                    StartupTrustNumberedRows = 18,
                    FinalNumberedRows = numbered.Length,
                    OptionalInterrupt = new { RequestId = 7, WriteEntered = true, WriteReturned = true, ReplyReceived = true, Refused = received.HasError, received.ErrorCode },
                    Native = new { frame.Retirement.KnownClosed, frame.Retirement.RootExited, frame.Retirement.ActiveProcesses, frame.Retirement.OutputEof, frame.Retirement.ErrorEof, frame.Retirement.InputCloseReturned, frame.Retirement.DispositionReturned, frame.Retirement.ExitCode, FailureCount = frame.Retirement.Failures.Count },
                    IoCount = io.Count,
                    IoFaultCount = faultRows.Length,
                    IoFaultRows = faultRows.Take(128).Select(row => new { row.Kind, row.Entered, row.InvocationReturned, TaskStatus = row.ActualTask?.Status, PrimaryType = row.Primary?.GetType().FullName, PostType = row.Post?.GetType().FullName, row.Unknown }).ToArray(),
                    IoFaultMetadataTruncated = faultRows.Length > 128,
                    PortTasks = stopped.OriginalTasks,
                    FrameTaskCount = frame.ActualCalls.Count,
                    AllOriginalTasksTerminal = true,
                    stopped.ReplyFailureType,
                    stopped.PumpFailureType,
                    stopped.PumpStatus,
                    StopContractDispositionProven = true,
                    SuccessfulWorkerCompletionClaimed = false,
                    SuccessfulProtocolInterruptRequired = false,
                    RuntimeCertified = false,
                    AuthorityIssued = false,
                    RawAccountConfigPromptOrErrorTextExported = false
                };
                _ = frame.Original.CleanupRemaining;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(summary, BoundedFiles.Json);
                _ = frame.Original.CleanupRemaining;
                frame.FailureSnapshot = new(frame.Original);
                try { frame.FailureSnapshot.Write(Path.Combine(frame.Selected.ResultDirectory, "codex-contract-stop-disposition.json"), bytes); }
                catch (Exception failure) { frame.Failure ??= failure; throw; }
                _ = frame.Original.CleanupRemaining;
            }
        }
        // A stop attempt is not a successful worker commit. Native kill/actual
        // protocol-control outcomes remain separately recorded by the port.
        frame.AcceptanceAssertionsReturned = true;
    }
}
