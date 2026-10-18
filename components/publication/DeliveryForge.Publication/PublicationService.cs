using DeliveryForge.Coordination;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Publication.GitHost;

namespace DeliveryForge.Publication;

public sealed class PublicationService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefLocks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Task> OutstandingActions = new(StringComparer.Ordinal);
    private readonly PublicationAuthorityHost authority;
    private readonly IPublicationFactSource facts;
    private readonly IGitHostAdapter adapter;
    private readonly IPublicationJournal journal;
    private readonly PublicationLimits limits;
    private readonly ICoordinatedEffectAdmission? coordinationAdmission;

    public PublicationService(
        PublicationAuthorityHost authority,
        IPublicationFactSource facts,
        IGitHostAdapter adapter,
        IPublicationJournal journal,
        PublicationLimits? limits = null, ICoordinatedEffectAdmission? coordinationAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(journal);
        this.coordinationAdmission = coordinationAdmission;
        this.authority = authority;
        this.facts = facts;
        this.adapter = adapter;
        this.journal = journal;
        this.limits = limits ?? PublicationLimits.FixtureDefault;
        this.limits.Validate();
    }

    public bool UsesCoordinationAdmission(ICoordinatedEffectAdmission actual) => ReferenceEquals(coordinationAdmission, actual);

    public async Task<PublicationResult> ExecuteAsync(
        PublicationRun run,
        PublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(request);
        var action = request.Mode == PublicationMode.Merge ? PublicationAction.MergeNormal : PublicationAction.CreateOrUpdatePr;
        if (!authority.Owns(run) || adapter.Realm != PublicationRealm.Fixture)
            return Blocked(PublicationDenial.Authority, "Current fixture parent authority cannot be borrowed or enable a production adapter.");
        if (!Enum.IsDefined(request.Mode) || request.Mode is PublicationMode.Plan or PublicationMode.Implement ||
            run.Definition.Ceiling < (request.Mode == PublicationMode.Merge ? AuthorizationCeiling.Merge : AuthorizationCeiling.Pr))
            return Blocked(PublicationDenial.Ceiling, "The requested mode cannot raise an immutable run ceiling.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 256 ||
            request.IdempotencyKey.Any(char.IsControl))
            return Blocked(PublicationDenial.Idempotency, "An explicit bounded idempotency key is required.");
        var target = run.Definition.Target;
        var resource = target.Repository.ToLowerInvariant() + "\0" + target.TargetRef;
        var mutex = RefLocks.GetOrAdd(resource, _ => new SemaphoreSlim(1, 1));
        var entered = false;
        PublicationIntent? intent = null;
        var sent = false;
        Task<GitHostMutationResult>? actionTask = null;
        try
        {
            entered = await mutex.WaitAsync(limits.LockTimeout, cancellationToken).ConfigureAwait(false);
            if (!entered) return Blocked(PublicationDenial.Ownership, "Repository/ref publication serialization deadline elapsed.");
            if (OutstandingActions.TryGetValue(resource, out var outstanding))
            {
                if (!outstanding.IsCompleted)
                    return Blocked(PublicationDenial.Uncertain, "A timed-out action remains unsettled; the ref cannot receive another action.");
                ObserveLate(outstanding);
                OutstandingActions.TryRemove(resource, out _);
            }
            var local = await Bounded(token => facts.ObserveAsync(run, token), limits.ReadTimeout, cancellationToken).ConfigureAwait(false);
            var remote = await Bounded(token => adapter.ReadAsync(target, request.IdempotencyKey, token), limits.ReadTimeout, cancellationToken).ConfigureAwait(false);
            intent = await Bounded(token => journal.ReadAsync(request.IdempotencyKey, token), limits.JournalTimeout, cancellationToken).ConfigureAwait(false);
            if (intent is not null && (!Matches(intent, run, request, action) || !Enum.IsDefined(intent.State)))
                return Blocked(PublicationDenial.Idempotency, "The key already names another run/revision/action/target or malformed intent.");
            var preflight = PublicationPreflight.EvaluateCore(authority, run, request, local, remote, intent);
            if (!preflight.Allowed) return Blocked(preflight.Denial, preflight.Reason);
            if (request.DryRun)
                return new(PublicationOutcome.DryRun, PublicationDenial.None, "Fixture dry-run only; zero external mutations.", action, PublicationRealm.Fixture);
            if (intent is not null && intent.State != PublicationIntentState.Prepared)
            {
                local = await Bounded(token => facts.ObserveAsync(run, token), limits.ReadTimeout, cancellationToken).ConfigureAwait(false);
                preflight = PublicationPreflight.EvaluateCore(authority, run, request, local, remote, intent);
                if (!preflight.Allowed) return Blocked(preflight.Denial, preflight.Reason);
                return await Reconcile(run, request, intent, remote, replay: true, cancellationToken).ConfigureAwait(false);
            }
            intent ??= new(request.IdempotencyKey, run.Definition.RunIdentity, run.Definition.RevisionIdentity, action,
                target, run.Definition.Patch.BaseCommit, run.Definition.Patch.HeadCommit, run.Definition.Patch.HeadTree,
                PublicationIntentState.Prepared, CreatedAt: DateTimeOffset.UtcNow);
            await Append(intent, cancellationToken).ConfigureAwait(false);
            intent = intent with { State = PublicationIntentState.Sent };
            await Append(intent, cancellationToken).ConfigureAwait(false);
            sent = true;
            // The durable send barrier precedes the final fresh remote/local/stop/review checks.
            remote = await Bounded(token => adapter.ReadAsync(target, request.IdempotencyKey, token), limits.ReadTimeout, cancellationToken).ConfigureAwait(false);
            local = await Bounded(token => facts.ObserveAsync(run, token), limits.ReadTimeout, cancellationToken).ConfigureAwait(false);
            preflight = PublicationPreflight.EvaluateCore(authority, run, request, local, remote, intent);
            if (!preflight.Allowed)
            {
                await MarkUncertain(intent).ConfigureAwait(false);
                return Blocked(preflight.Denial, preflight.Reason + " Recorded intent is retained; no automatic resend.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var review = PublicationPreflight.ReviewAllowed(authority, run, request.Mode);
            if (!review.Allowed)
            {
                await MarkUncertain(intent).ConfigureAwait(false);
                return Blocked(review.Denial, review.Reason);
            }
            using var actionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            actionCancellation.CancelAfter(limits.ActionTimeout);
            var mutation = new GitHostMutation(intent, remote.TargetHead);
            Task<GitHostMutationResult> InvokeMutation() => action == PublicationAction.MergeNormal
                ? adapter.MergeNormalAsync(mutation, actionCancellation.Token)
                : adapter.CreateOrUpdatePrAsync(mutation, actionCancellation.Token);
            actionTask = coordinationAdmission is null ? InvokeMutation() :
                coordinationAdmission.EnterAsync(run.Definition.RunIdentity, InvokeMutation);
            GitHostMutationResult result;
            try { result = await actionTask.WaitAsync(limits.ActionTimeout, cancellationToken).ConfigureAwait(false); }
            catch
            {
                actionCancellation.Cancel();
                throw;
            }
            if (result.Outcome == GitHostMutationOutcome.Rejected)
            {
                await MarkUncertain(intent).ConfigureAwait(false);
                return Blocked(PublicationDenial.Uncertain, "Host rejected the action; sent intent still requires explicit reconciliation.");
            }
            if (result.Outcome == GitHostMutationOutcome.Uncertain) await MarkUncertain(intent).ConfigureAwait(false);
            remote = await Bounded(token => adapter.ReadAsync(target, request.IdempotencyKey, token), limits.ReadTimeout, cancellationToken).ConfigureAwait(false);
            return await Reconcile(run, request, intent, remote, result.Outcome != GitHostMutationOutcome.Confirmed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (sent && intent is not null)
            {
                if (actionTask is not null && !actionTask.IsCompleted)
                {
                    OutstandingActions[resource] = actionTask;
                    ObserveLate(actionTask);
                }
                await MarkUncertain(intent).ConfigureAwait(false);
                return Blocked(PublicationDenial.Uncertain,
                    $"Publication interruption requires readback without resend ({error.GetType().Name}); late action settlement cannot change this result.");
            }
            return Blocked(cancellationToken.IsCancellationRequested ? PublicationDenial.Stopped : PublicationDenial.Uncertain,
                $"Publication observation/journal unavailable before action ({error.GetType().Name}).");
        }
        finally { if (entered) mutex.Release(); }
    }

    private async Task<PublicationResult> Reconcile(PublicationRun run, PublicationRequest request, PublicationIntent intent,
        GitHostObservation remote, bool replay, CancellationToken cancellationToken)
    {
        var completed = PublicationPreflight.SameTarget(remote.Target, intent.Target) && remote.SourceHead == intent.HeadCommit &&
            remote.PrHead == intent.HeadCommit && remote.PublishedIdempotencyKey == intent.IdempotencyKey &&
            !string.IsNullOrWhiteSpace(remote.ResultId) && (intent.Action != PublicationAction.MergeNormal || remote.MergedHead == intent.HeadCommit);
        if (!completed)
        {
            await MarkUncertain(intent).ConfigureAwait(false);
            return Blocked(PublicationDenial.Uncertain, "No exact readback proves the sent action; automatic retry is forbidden.");
        }
        var receipt = Receipt(intent, remote.ResultId!);
        var d = run.Definition;
        var transition = new TransitionEvidence(d.Ceiling, d.Patch.BaseCommit, d.Patch.HeadCommit, d.Patch.HeadTree,
            Policy: d.Policy, ReviewReceipt: d.ReviewReceipt, HostReviewReceipt: d.HostedReviewReceipt, PublicationReceipt: receipt);
        authority.ReviewHost.EnsureContractFixtureTransition(intent.Action == PublicationAction.MergeNormal ? WorkflowState.MergeAuthorized : WorkflowState.PrAuthorized,
            intent.Action == PublicationAction.MergeNormal ? WorkflowState.Merged : WorkflowState.PrPublished, transition, d.Receiver);
        await Bounded(async token => { await journal.AppendReceiptAsync(receipt, token).ConfigureAwait(false); return true; },
            limits.JournalTimeout, cancellationToken).ConfigureAwait(false);
        await Append(intent with { State = PublicationIntentState.Confirmed, ResultId = remote.ResultId, ReceiptIdentity = receipt.Identity }, cancellationToken).ConfigureAwait(false);
        return new(replay ? PublicationOutcome.ReconciledFixture : PublicationOutcome.PublishedFixture, PublicationDenial.None,
            "Exact fixture readback recorded; external targets do not enforce fences and a check-to-action race remains.",
            intent.Action, PublicationRealm.Fixture, receipt);
    }

    private Task Append(PublicationIntent intent, CancellationToken cancellationToken) => Bounded(async token =>
    {
        await journal.AppendAsync(intent, token).ConfigureAwait(false);
        return true;
    }, limits.JournalTimeout, cancellationToken);

    private async Task MarkUncertain(PublicationIntent intent)
    {
        coordinationAdmission?.RetainUncertain(intent.RunIdentity, "Publication intent remains unresolved; no resend or lease release authority.");
        try { await Append(intent with { State = PublicationIntentState.Uncertain }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* The earlier durable Sent intent remains unresolved; no retry is authorized. */ }
    }

    private static bool Matches(PublicationIntent i, PublicationRun run, PublicationRequest request, PublicationAction action)
    {
        var d = run.Definition;
        return i.IdempotencyKey == request.IdempotencyKey && i.RunIdentity == d.RunIdentity && i.RevisionIdentity == d.RevisionIdentity &&
            i.Action == action && PublicationPreflight.SameTarget(i.Target, d.Target) && i.BaseCommit == d.Patch.BaseCommit &&
            i.HeadCommit == d.Patch.HeadCommit && i.TreeId == d.Patch.HeadTree;
    }

    private static ValidatedContract Receipt(PublicationIntent i, string resultId)
    {
        var node = new JsonObject
        {
            ["schemaVersion"] = "1.0.0",
            ["kind"] = "publication-receipt",
            ["identity"] = "sha256:" + new string('0', 64),
            ["baseCommit"] = i.BaseCommit,
            ["headCommit"] = i.HeadCommit,
            ["treeId"] = i.TreeId,
            ["action"] = i.Action == PublicationAction.MergeNormal ? "MERGED" : "PR_PUBLISHED",
            ["remoteRef"] = i.Action == PublicationAction.MergeNormal ? i.Target.TargetRef : i.Target.SourceRef,
            ["resultId"] = resultId,
            ["createdAt"] = i.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        };
        node["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(node.ToJsonString()));
        return ContractValidator.ParseAndValidate(Encoding.UTF8.GetBytes(node.ToJsonString()));
    }

    private static async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var task = operation(deadline.Token);
        try { return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch { deadline.Cancel(); ObserveLate(task); throw; }
    }

    private static void ObserveLate(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    private static PublicationResult Blocked(PublicationDenial denial, string reason) =>
        new(PublicationOutcome.Blocked, denial, reason, PublicationAction.None, PublicationRealm.Fixture);
}
