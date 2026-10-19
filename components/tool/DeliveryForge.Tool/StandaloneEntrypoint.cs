using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
namespace DeliveryForge.Tool;

// The default executable owns ONE original for setup, real host/Git calls,
// disposition and final delivery. No JSON object can supply a private owner.
internal sealed class StandaloneEntrypoint
{
    static readonly List<StandaloneEntrypoint> Retained = [];
    readonly ToolLifetime original = new(TimeSpan.FromSeconds(1200));
    readonly TaskCompletionSource<int> unknownHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly List<object> actualReferences = [];
    readonly TextWriter output, errors; readonly ToolFailureAttention attention;
    StandaloneResources? resources; StandaloneOwnerComposition? composition; Task? delivery;
    Exception? primary; readonly List<Exception> secondary = []; bool deliveryEntered, deliveryReturned;
    StandaloneEntrypoint(TextWriter output, TextWriter errors) { this.output = output; this.errors = errors; attention = new(original, errors); original.BindAttention(attention); actualReferences.Add(original); actualReferences.Add(unknownHold); lock (Retained) Retained.Add(this); }
    internal static Task<int> RunAsync(string[] args, TextWriter output, TextWriter errors) => new StandaloneEntrypoint(output, errors).Run(args);
    T ReadPinned<T>(AdmissionFile pin, int maximum) { var bytes = InstalledHostAdmission.ReadExact(pin, original, maximum); _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(bytes); original.Check(); var value = JsonSerializer.Deserialize<T>(bytes, BoundedFiles.Json) ?? throw new IOException("Independent selected root DATA is null."); original.Check(); return value; }
    static void SeparateSelection(TrustedConfiguration config, string configPath, StandaloneRootSelection root)
    {
        if (root.Kind != "DeliveryForgeStandaloneRoot-v1" || root.WorkerPaths is null || root.InstalledHostAdmission is null) throw new ToolUsageException("Explicit trusted standalone root shape required.");
        var work = BoundedFiles.Plain(root.WorkerPaths.Worktree, true); var result = BoundedFiles.Plain(root.WorkerPaths.ResultDirectory, true); var store = BoundedFiles.Plain(root.StoreRoot, true);
        BoundedFiles.Separate(config.RepositoryRoot, store); BoundedFiles.Separate(config.RepositoryRoot, work); BoundedFiles.Separate(config.RepositoryRoot, result); BoundedFiles.Separate(work, result); BoundedFiles.Separate(store, work); BoundedFiles.Separate(store, result);
        foreach (var pin in new[] { config.StandaloneRoot!, root.InstalledHostAdmission, root.LinuxScopeProfile }.Where(pin => pin is not null)) { BoundedFiles.Separate(work, pin!.Path); BoundedFiles.Separate(result, pin.Path); }
        BoundedFiles.Separate(work, configPath); BoundedFiles.Separate(result, configPath);
        if (config.InstalledQuality is { } quality)
        {
            BoundedFiles.Separate(work, quality.ArtifactRoot); BoundedFiles.Separate(result, quality.ArtifactRoot);
            foreach (var gate in quality.Gates) { BoundedFiles.Separate(work, gate.Profile.Path); BoundedFiles.Separate(result, gate.Profile.Path); }
            if (quality.Registry is { } registry)
                foreach (var source in new[] { config.RepositoryRoot, work, result, quality.ArtifactRoot })
                    foreach (var input in new[] { registry.OwnerApproval.Path, registry.ArtifactRoot }) BoundedFiles.Separate(source, input);
        }
    }
    async Task<int> Run(string[] args)
    {
        ToolResult? result = null; var exit = 1;
        try
        {
            original.Check(); var command = CommandLine.Parse(args); original.Check();
            if (command.Has("help") || command.Has("version")) throw new ToolUsageException("Help/version use the diagnostic entry route.");
            resources = new(original); actualReferences.Add(resources); resources.Enter();
            if (command.Get("config") is not string configPath) result = ToolResult.Incomplete("TRUSTED_CONFIG_REQUIRED", "Select an explicit independent trusted configuration; no native operation occurred.");
            else
            {
                var config = TrustedConfiguration.LoadBound(configPath, original, out var configPin); original.Restrict(TimeSpan.FromSeconds(config.OperationSeconds));
                if (config.StandaloneRoot is null) result = ToolResult.Incomplete("STANDALONE_ROOT_REQUIRED", "An owner-reviewed installed root and actual selected Linux profile where applicable are required; no ambient host/Git fallback.");
                else
                {
                    var root = ReadPinned<StandaloneRootSelection>(config.StandaloneRoot, 65536); SeparateSelection(config, configPath, root); original.Check();
                    LinuxScopeSelection? scope = null;
                    if (OperatingSystem.IsLinux()) scope = ReadPinned<LinuxScopeSelection>(root.LinuxScopeProfile ?? throw new ToolUsageException("Exact Linux owner profile required before Git or host birth."), 65536);
                    ProcessCommandExecutor executor = OperatingSystem.IsLinux() ? new(scope!, original) : new();
                    ToolApplication application;
                    if (command.Mode == ToolMode.Plan || command.Has("dry-run") || command.Mode == ToolMode.Capabilities) application = new(actualExecutor: executor, actualOriginal: original, actualConfigurationPin: configPin);
                    else
                    {
                        var admission = InstalledHostAdmission.ReadRootAccepted(root.InstalledHostAdmission, config, original);
                        composition = new(config, root.StoreRoot, root.WorkerPaths, original, admission.Host, admission.Profile, scope, configPin); actualReferences.Add(composition); await composition.InitializeCoordinationAsync().ConfigureAwait(false); application = composition.Application;
                    }
                    actualReferences.Add(application); var actual = application.ExecuteAsync(command, original.Token); actualReferences.Add(actual);
                    try { result = await actual.WaitAsync(original.Remaining).ConfigureAwait(false); } finally { if (!actual.IsCompleted) original.RetainUnknown(); }
                    original.Check();
                }
            }
            exit = result?.ExitCode ?? 1;
        }
        catch (Exception error) { primary = error; Program.RetainFailure(error); try { original.BeginFailureCleanup(); } catch (Exception endpoint) { secondary.Add(endpoint); original.RetainUnknown(); } }
        // Unknown native/task frames are never disposed, restored or converted
        // into a normal exit. The same actual caller is passively retained for
        // its containing owner to reconcile; this is a failed, over-budget state.
        if (!original.HasRetainedUnknown && composition is not null) { try { await composition.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { secondary.Add(error); } }
        if (!original.HasRetainedUnknown && resources is not null) { try { resources.Close(); } catch (Exception error) { secondary.Add(error); } }
        if (original.HasRetainedUnknown || attention.Unknown) return await HoldUnknown().ConfigureAwait(false);
        if (secondary.Count != 0) { if (primary is null) primary = secondary[0]; var all = new List<Exception> { primary }; all.AddRange(secondary.Where(e => !ReferenceEquals(e, primary))); primary = all.Count == 1 ? primary : new AggregateException("Actual root failure and independent first dispositions.", all); exit = 1; }
        try
        {
            _ = original.CleanupRemaining;
            var text = Encode(primary is null ? (object)(result ?? ToolResult.Incomplete("NO_RESULT", "Actual root returned no result.")) : ToolFailureReport.Create(primary));
            var remaining = original.CleanupRemaining; deliveryEntered = true; delivery = (primary is null ? output : errors).WriteLineAsync(text.AsMemory(), CancellationToken.None); actualReferences.Add(delivery);
            await delivery.WaitAsync(remaining).ConfigureAwait(false); deliveryReturned = true; _ = original.CleanupRemaining;
        }
        catch (Exception error) { secondary.Add(error); original.RetainUnknown(); }
        if (original.HasRetainedUnknown || deliveryEntered && !deliveryReturned) return await HoldUnknown().ConfigureAwait(false);
        original.Dispose(); lock (Retained) Retained.Remove(this); return primary is null ? exit : 1;
    }
    async Task<int> HoldUnknown() { original.RetainUnknown(); attention.EmitOnce(); return await unknownHold.Task.ConfigureAwait(false); }
    static string Encode(object actual)
    {
        // Stream enforces the byte ceiling BEFORE every serializer write. No
        // unbounded SerializeToString/ToUtf8Bytes allocation precedes admission.
        using var bounded = new FiniteJsonBuffer(); JsonSerializer.Serialize(bounded, actual, actual.GetType(), BoundedFiles.Json); return new UTF8Encoding(false, true).GetString(bounded.GetBuffer(), 0, checked((int)bounded.Length));
    }
    sealed class FiniteJsonBuffer : MemoryStream
    {
        const int Maximum = 1048576;
        void Admit(int count) { if (count < 0 || Position > Maximum - count) throw new IOException("Complete root result exceeds finite one-MiB JSON delivery profile; no truncated output."); }
        public override void Write(byte[] buffer, int offset, int count) { Admit(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Admit(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Admit(1); base.WriteByte(value); }
    }
}
