using System.Text.Json;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Execution;
using DeliveryForge.Execution.Host;
using DeliveryForge.Planning;
using DeliveryForge.Publication;

namespace DeliveryForge.Tool;

// Selection is a locator only. No ready flag, review/gate outcome, transport,
// remote target or serialized native owner is accepted by this profile.
public sealed record LocalRealGoldenSelection(string ArtifactRoot);

internal sealed class LocalRealGoldenOwner(TrustedConfiguration configuration, AdmissionFile configurationPin, ToolLifetime original)
{
    readonly LocalRealGoldenSelection selection = configuration.LocalRealGolden ?? throw new ToolUsageException("No explicit local-real selection.");
    FrozenPlan? frozen; FrozenWorkerPlan? assignment; bool entered;
    internal static void Validate(LocalRealGoldenSelection value) => BoundedFiles.Plain(value.ArtifactRoot, true);
    internal void Bind(FrozenPlan plan, FrozenWorkerPlan worker, TrustedConfiguration config, ToolLifetime life)
    {
        original.Check();
        _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536);
        if (frozen is not null || config.LocalRealGolden != selection || !ReferenceEquals(life, original) ||
            plan.ContractIdentity != worker.PlanIdentity || plan.Identity != config.ExpectedBundleIdentity || plan.BaseCommit != worker.BaseCommit)
            throw new WorkerEnvelopeException("Local-real binds the same actual plan/configuration/assignment/original once.");
        foreach (var root in new[] { worker.ParentCheckout, worker.Worktree, worker.ResultDirectory }) BoundedFiles.Separate(root, selection.ArtifactRoot);
        BoundedFiles.Separate(configurationPin.Path, selection.ArtifactRoot);
        if (config.InstalledQuality is { } quality)
        {
            BoundedFiles.Separate(quality.ArtifactRoot, selection.ArtifactRoot);
            foreach (var gate in quality.Gates) BoundedFiles.Separate(gate.Profile.Path, selection.ArtifactRoot);
            if (quality.Registry is { } registry)
                foreach (var path in new[] { registry.OwnerApproval.Path, registry.ArtifactRoot }) BoundedFiles.Separate(path, selection.ArtifactRoot);
        }
        if (config.InstalledReview is { } review)
        {
            BoundedFiles.Separate(review.ArtifactRoot, selection.ArtifactRoot); BoundedFiles.Separate(review.GitImage.Path, selection.ArtifactRoot);
            if (review.IndependentReviewer is { } reviewer) BoundedFiles.Separate(reviewer.Image.Path, selection.ArtifactRoot);
        }
        frozen = plan; assignment = worker;
    }
    internal async Task<ToolResult> RunAsync(HostBridgeFrame frame, ParentCoordinationHost parent,
        InstalledQualityHost? quality, InstalledReviewHost? review, Func<bool> nativeCompletedAndQuiet, string actualStoreRoot)
    {
        if (entered || frozen is null || assignment is null) throw new ToolUsageException("Actual local-real original cannot replay or be imported.");
        entered = true;
        if (quality is null || review is null) return ToolResult.Incomplete("LOCAL_REAL_PRODUCERS_REQUIRED", "Actual worker, protected quality/baseline and independent runtime review owners are required.");
        if (TrustedConfiguration.Ceiling(assignment.AuthorizationCeiling) < AuthorizationCeiling.Pr)
            return ToolResult.Incomplete("LOCAL_REAL_PR_CEILING_REQUIRED", "Local fake publication still requires the original immutable PR ceiling and protected policy; no ceiling is raised.");
        void Current()
        {
            original.Check(); parent.CheckCurrent(frame.RunId);
            if (!ReferenceEquals(frame.Lifetime, original) || frame.RunId != assignment.RunId || !frame.OwnerCompleted || frame.OwnerStopped ||
                frame.Primary is not null || frame.Post is not null || frame.InvocationUnknown || frame.EnteredTasks.Any(task => !task.IsCompleted) ||
                frame.Quality.Unknown || frame.IndependentReview.Unknown || quality.Unknown || review.Unknown || !nativeCompletedAndQuiet())
                throw new WorkerEnvelopeException("Same actual settled worker/quality/review original is required for local-real acceptance.");
            parent.EnterSynchronous(frame.RunId, () => { _ = InstalledHostAdmission.ReadExact(configurationPin, original, 65536); return true; });
        }
        Current();
        var gates = await quality.LocalRealEvidenceAsync(frame, parent).ConfigureAwait(false);
        if (gates is null) return ToolResult.Incomplete("LOCAL_REAL_PROTECTED_BASELINE_REQUIRED", "Complete actual gates and an independently approved exact protected baseline comparison are required; no registry waiver.");
        var reviewed = await review.LocalRealEvidenceAsync(frame, parent).ConfigureAwait(false);
        if (reviewed is null) return ToolResult.Incomplete("LOCAL_REAL_ACCEPTED_REVIEW_REQUIRED", "Same-original production issuer acceptance from the actual independent runtime reviewer is required.");
        var accepted = reviewed.Value.Accepted; var policy = reviewed.Value.Policy;
        if (accepted.Patch.PlanIdentity != frozen.ContractIdentity || policy.Identity != configuration.InstalledQuality?.PolicyIdentity)
            throw new WorkerEnvelopeException("Actual quality/review protected policy or frozen plan differs.");
        var evidence = new TransitionEvidence(TrustedConfiguration.Ceiling(assignment.AuthorizationCeiling), assignment.BaseCommit,
            accepted.Patch.HeadCommit, accepted.Patch.HeadTree, policy, gates, ReviewReceipt: accepted.Receipt);
        async Task Verify()
        {
            Current();
            var currentGates = await quality.LocalRealEvidenceAsync(frame, parent).ConfigureAwait(false);
            var currentReview = await review.LocalRealEvidenceAsync(frame, parent).ConfigureAwait(false);
            if (currentGates is null || currentReview is null || !ReferenceEquals(currentReview.Value.Accepted, accepted) ||
                currentReview.Value.Policy.Identity != policy.Identity || !gates.Select(row => row.Identity).SequenceEqual(currentGates.Select(row => row.Identity)))
                throw new WorkerEnvelopeException("Private live local-real acceptance/current immutable evidence changed.");
            Current();
        }
        // The full pair above is already fresh; only immutable managed evidence
        // was assembled since it returned. Keep the live guard before effects.
        Current();
        // The production receiver rejects Test/fixture acceptance before even a
        // local result directory or journal can be created.
        WorkflowTransition.EnsureAllowed(WorkflowState.Evaluating, WorkflowState.IndependentReview, evidence, accepted.Receiver);
        WorkflowTransition.EnsureAllowed(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, evidence, accepted.Receiver);
        var output = Path.Combine(selection.ArtifactRoot, frame.RunId);
        BoundedFiles.Separate(actualStoreRoot, output);
        parent.EnterSynchronous(frame.RunId, () =>
        {
            Current(); BoundedFiles.Plain(output, true);
            if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Fresh local-real result namespace required; no adoption.");
            Directory.CreateDirectory(output); original.Check(); return true;
        });
        parent.EnterSynchronous(frame.RunId, () =>
        {
            Current(); BoundedFiles.WriteNew(Path.Combine(output, "selection.json"), JsonSerializer.SerializeToUtf8Bytes(
                new RunSelection(frame.RunId, actualStoreRoot, assignment.Repository, assignment.ParentCheckout, assignment.AuthorizationCeiling, assignment.PlanIdentity), BoundedFiles.Json), original);
            Current(); return true;
        });
        var journal = new LocalJournal(output, original, parent, frame.RunId, Current);
        var published = await LocalRealFakePublication.ExecuteAsync(frame.RunId, frozen.Identity, evidence, accepted.Receiver,
            original, parent, Verify, journal).ConfigureAwait(false);
        if (published.Outcome != PublicationOutcome.PublishedFixture || published.Realm != PublicationRealm.Fixture || published.Receipt is null)
            throw new WorkerEnvelopeException("Exact completed local-only publication readback required before local-real summary DATA.");
        // ExecuteAsync ends with the same full Verify after confirmed readback.
        // Only its immutable result tuple was inspected here, so retain the live
        // guard without recomputing the identical pair before this summary.
        Current();
        var path = Path.Combine(output, "local-real-receipt.json");
        parent.EnterSynchronous(frame.RunId, () =>
        {
            Current();
            BoundedFiles.WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Kind = "ActualInstalledLocalRealChainData-v1",
                Outcome = "PASS",
                Platform = OperatingSystem.IsWindows() ? "windows" : "linux",
                frame.RunId,
                PlanIdentity = frozen.ContractIdentity,
                RichPlanIdentity = frozen.Identity,
                assignment.BaseCommit,
                accepted.Patch.HeadCommit,
                accepted.Patch.HeadTree,
                accepted.Patch.PatchSha256,
                ProtectedPolicy = policy.Identity,
                GateReceipts = gates.Select(row => row.Identity).ToArray(),
                IndependentReview = accepted.Receipt.Identity,
                PublicationReceipt = published.Receipt?.Identity,
                OriginalStarted = original.OriginalStarted,
                Frequency = System.Diagnostics.Stopwatch.Frequency,
                FakePublication = true,
                RemoteWrites = 0,
                MergeCalls = 0,
                ProductionPublicationAuthority = false,
                RecoveryCertified = false,
                SchedulingCertified = false,
                WholeIssueCertified = false
            }, BoundedFiles.Json), original);
            Current(); return true;
        });
        await Verify().ConfigureAwait(false);
        return new("COMPLETE", "ACTUAL_LOCAL_REAL_CHAIN_PASSED", "Actual worker, protected quality/baseline and production-accepted independent review passed with local fake publication; scheduling, recovery and production publication remain separate.", new { frame.RunId, Receipt = path, RunDirectory = output, FakePublication = true, RemoteWrites = 0, RecoveryCertified = false, SchedulingCertified = false });
    }

    internal sealed class LocalJournal(string root, ToolLifetime original, DeliveryForge.Coordination.ICoordinatedSynchronousEffectAdmission parent, string runId, Action current) : ILocalRealAdmittedJournal
    {
        readonly Dictionary<string, PublicationIntent> latest = new(StringComparer.Ordinal); int sequence;
        public Task<PublicationIntent?> ReadAdmittedAsync(string key, CancellationToken token)
        {
            var row = parent.EnterSynchronous(runId, () => { token.ThrowIfCancellationRequested(); current(); latest.TryGetValue(key, out var found); return found; });
            return Task.FromResult<PublicationIntent?>(row);
        }
        public Task AppendAdmittedAsync(PublicationIntent row, CancellationToken token)
        {
            Write("intent", JsonSerializer.SerializeToUtf8Bytes(row, BoundedFiles.Json), token); latest[row.IdempotencyKey] = row; return Task.CompletedTask;
        }
        public Task AppendReceiptAdmittedAsync(ValidatedContract receipt, CancellationToken token)
        { Write("receipt", receipt.CanonicalBytes.ToArray(), token); return Task.CompletedTask; }
        void Write(string kind, byte[] bytes, CancellationToken token)
        {
            var writeEntered = false;
            try
            {
                parent.EnterSynchronous(runId, () =>
                {
                    token.ThrowIfCancellationRequested(); current();
                    if (sequence >= 8 || bytes.Length > 1048576) throw new IOException("Finite local fake journal profile exceeded.");
                    var path = Path.Combine(root, $"{sequence++:D2}-{kind}.json");
                    writeEntered = true;
                    BoundedFiles.WriteNew(path, bytes, original); current(); return true;
                });
            }
            catch (Exception primary)
            {
                // BoundedFiles owns the actual first-close protocol. Retain the
                // unresolved durable journal even when that file is known closed.
                if (writeEntered)
                {
                    try { parent.RetainUncertain(runId, "Local fake journal write entered but did not complete admission; durable evidence retained, no replay."); }
                    catch (Exception retention) { primary.Data["LocalFakeJournalRetentionFailure"] = retention; }
                }
                throw;
            }
        }
    }
}
