using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DeliveryForge.Evidence;

namespace DeliveryForge.Tool.Tests;

// Exact private kernel observations on this test's unstarted owner only. No
// Process.Start, cgroup/namespace change, gate build, scope or runtime authority.
public sealed class OwnedLinuxNamespaceObservationTests
{
    public static bool IsLinux68X64 => OperatingSystem.IsLinux() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor == 8;

    [Fact(Timeout = 90000, Skip = "Actual namespace observation requires Linux x64 kernel6.8; skips are not acceptance.", SkipUnless = nameof(IsLinux68X64))]
    public void Actual_initial_namespace_and_anchored_membership_close_all_five_descriptors_before_birth()
    {
        using var frame = new KernelFrame();
        var membership = frame.ObserveNamespace(); // A nested namespace must fail, not masquerade as a pass.
        Assert.StartsWith("0::/", membership, StringComparison.Ordinal);
        Assert.EndsWith("\n", membership, StringComparison.Ordinal);
        Assert.Equal(1, membership.Count(c => c == '\n'));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(membership) <= 8192);
        Assert.Equal(23, frame.Rows.Count(row => KernelFrame.Kind(row).StartsWith("actual-owner-", StringComparison.Ordinal)));
        Assert.Equal(5, frame.OpenRows.Length);
        Assert.Equal(5, frame.Rows.Count(row => KernelFrame.Kind(row) == "actual-owner-namespace-first-close"));
        Assert.All(frame.OpenRows, row =>
        {
            Assert.True((bool)KernelFrame.Field(row, "Entered")!);
            Assert.True((bool)KernelFrame.Field(row, "Returned")!);
            Assert.IsType<int>(KernelFrame.Field(row, "Resource"));
            Assert.True((bool)KernelFrame.Field(row, "CloseEntered")!);
            Assert.True((bool)KernelFrame.Field(row, "Closed")!);
        });
        Assert.All(frame.Rows, row => Assert.False(KernelFrame.Unknown(row)));
        frame.AssertNoBirthOrLibrary();
        Assert.True(frame.Finish(false));
        Assert.True(frame.KnownDisposed);
    }

    [Fact(Timeout = 90000, Skip = "Actual post-open disposition requires Linux x64 kernel6.8; skips are not acceptance.", SkipUnless = nameof(IsLinux68X64))]
    public void Actual_first_open_postcheck_refusal_closes_its_original_descriptor_in_same_cleanup()
    {
        using var frame = new KernelFrame();
        var started = frame.Original.OriginalStarted;
        object? actualOpen = null;
        frame.Budget.ArmAfterFirstOpen(() =>
        {
            actualOpen = Assert.Single(frame.OpenRows);
            Assert.True((bool)KernelFrame.Field(actualOpen, "Returned")!);
            Assert.IsType<int>(KernelFrame.Field(actualOpen, "Resource"));
            Assert.False((bool)KernelFrame.Field(actualOpen, "Closed")!);
        });
        Assert.Same(frame.Budget.Refusal, Assert.Throws<TimeoutException>(() => frame.ObserveNamespace()));
        Assert.NotNull(actualOpen);
        Assert.Same(frame.Budget.Refusal, KernelFrame.Field(actualOpen, "Post"));
        Assert.True((bool)KernelFrame.Field(actualOpen, "CloseEntered")!);
        Assert.True((bool)KernelFrame.Field(actualOpen, "Closed")!);
        Assert.Single(frame.Rows, row => KernelFrame.Kind(row) == "actual-owner-namespace-first-close");
        Assert.All(frame.Rows, row => Assert.False(KernelFrame.Unknown(row)));
        Assert.Equal(1, frame.Budget.CleanupNotifications);
        Assert.Equal(started, frame.Original.OriginalStarted);
        frame.AssertNoBirthOrLibrary();
        Assert.True(frame.Finish(true));
        Assert.True(frame.KnownDisposed);
    }

    sealed class PostOpenBudget(ToolLifetime parent) : IOwnedDuplexBudget, IOwnedFailureCleanup
    {
        int armedChecks;
        Action? witness;
        internal readonly TimeoutException Refusal = new("Test-owned actual post-open WORK refusal.");
        internal int CleanupNotifications;
        internal void ArmAfterFirstOpen(Action actualWitness) { armedChecks = 3; witness = actualWitness; }
        public long OriginalStarted => parent.OriginalStarted;
        public TimeSpan WholeLimit => parent.WholeLimit;
        public TimeSpan WorkLimit => parent.WorkLimit;
        public CancellationToken Token => parent.Token;
        public TimeSpan CleanupRemaining => parent.CleanupRemaining;
        public void CheckWork()
        {
            parent.CheckWork();
            if (armedChecks > 0 && --armedChecks == 0) { witness!(); throw Refusal; }
        }
        public void BeginFailureCleanup() { CleanupNotifications++; parent.BeginFailureCleanup(); }
    }

    sealed class KernelFrame : IDisposable
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Type ScopeType = typeof(OwnedDuplexProcess).Assembly.GetType("DeliveryForge.Evidence.LinuxCommandScope", true)!;
        static readonly List<KernelFrame> Retained = [];
        internal readonly ToolLifetime Original = new(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        internal readonly PostOpenBudget Budget;
        readonly Process owner = new();
        readonly object scope;
        internal KernelFrame()
        {
            Budget = new(Original);
            // Constructor-only finite DATA: none of these unopened selectors is
            // loaded, hashed, admitted or presented as a compiled gate receipt.
            var unused = new LinuxScopeImage(Path.Combine(Path.GetTempPath(), "forge-kernel-observation-only-UNISSUED"), 1, new string('0', 64));
            var selection = new LinuxScopeSelection(unused, unused, "255", "6.8.constructor-only-DATA", unused,
                Array.AsReadOnly(Enumerable.Repeat(unused, 5).ToArray()), unused)
            { RestrictiveChildPolicy = "nnp-user1000-ancestor-v3" };
            var constructor = ScopeType.GetConstructor(Instance, null,
                [typeof(Process), typeof(IOwnedDuplexBudget), typeof(LinuxScopeSelection), typeof(LinuxScopeImage)], null);
            Assert.NotNull(constructor);
            scope = constructor.Invoke([owner, Budget, selection, null]);
            lock (Retained) Retained.Add(this);
        }
        internal static object? Field(object target, string name) => target.GetType().GetField(name, Instance)!.GetValue(target);
        internal static string Kind(object row) => (string)Field(row, "Kind")!;
        internal static bool Unknown(object row) => (bool)row.GetType().GetProperty("Unknown", Instance)!.GetValue(row)!;
        internal object[] Rows => ((System.Collections.IEnumerable)Field(scope, "operations")!).Cast<object>().ToArray();
        internal object[] OpenRows => Rows.Where(row => Kind(row).StartsWith("actual-owner-namespace-open:", StringComparison.Ordinal)).ToArray();
        internal bool KnownDisposed => (bool)ScopeType.GetProperty("KnownDisposed", Instance)!.GetValue(scope)!;
        object? Invoke(string name, params object?[] arguments)
        {
            var method = ScopeType.GetMethod(name, Instance); Assert.NotNull(method);
            try { return method.Invoke(scope, arguments); }
            catch (TargetInvocationException failure) when (failure.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); throw; }
        }
        internal string ObserveNamespace() => (string)Invoke("RequireInitialCgroupNamespace")!;
        internal bool Finish(bool cleanup) => (bool)Invoke("DisposeKnown", cleanup)!;
        internal void AssertNoBirthOrLibrary()
        {
            foreach (var name in new[] { "prepared", "creationEntered", "creationReturned", "created", "bound", "referenceEntered" })
                Assert.False((bool)Field(scope, name)!);
            Assert.Null(Field(scope, "library")); Assert.Null(Field(scope, "bus")); Assert.Null(Field(scope, "kernel"));
        }
        public void Dispose()
        {
            try
            {
                if (!KnownDisposed) { Original.BeginFailureCleanup(); if (!Finish(true)) throw new IOException("Test-owned namespace observation remains UNKNOWN; retain original without retry."); }
                owner.Dispose(); Original.Dispose(); lock (Retained) Retained.Remove(this);
            }
            catch { Original.RetainUnknown(); throw; }
        }
    }
}
