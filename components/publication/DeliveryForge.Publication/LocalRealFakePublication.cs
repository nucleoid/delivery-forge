using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;
using DeliveryForge.Publication.GitHost;

namespace DeliveryForge.Publication;

// Internal contract for the actual containing journal. Every operation admits
// its own synchronous effect with the SAME original parent before file birth.
// The caller must not wrap its synchronous prefix in EnterAsync's gate.
internal interface ILocalRealAdmittedJournal
{
    Task<PublicationIntent?> ReadAdmittedAsync(string key, CancellationToken token);
    Task AppendAdmittedAsync(PublicationIntent row, CancellationToken token);
    Task AppendReceiptAdmittedAsync(ValidatedContract receipt, CancellationToken token);
}

// Internal containing-root composition only. The actual production review
// receiver is checked unchanged; the ONLY transport is this private local fake.
// No adapter/factory/URL/remote target can be selected by configuration or DATA.
internal static class LocalRealFakePublication
{
    internal static async Task<PublicationResult> ExecuteAsync(string runId, string revision,
        TransitionEvidence evidence, ReviewTransitionReceiver receiver, IOwnedDuplexBudget original,
        ICoordinatedSynchronousEffectAdmission parent, Func<Task> verifyCurrent, ILocalRealAdmittedJournal journal)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(runId, "^run-[a-f0-9]{32}$") ||
            revision.Length is < 1 or > 256 || revision.Any(char.IsControl))
            throw new ArgumentException("Exact bounded local-real revision required.");
        void Current() { original.CheckWork(); parent.CheckCurrent(runId); }
        void Barrier()
        {
            Current();
            WorkflowTransition.EnsureAllowed(WorkflowState.Evaluating, WorkflowState.IndependentReview, evidence, receiver);
            WorkflowTransition.EnsureAllowed(WorkflowState.IndependentReview, WorkflowState.PrAuthorized, evidence, receiver);
            Current();
        }
        async Task Verify() { Current(); await verifyCurrent().ConfigureAwait(false); Barrier(); }
        async Task Append(PublicationIntent row)
        {
            await Verify().ConfigureAwait(false);
            // The actual journal enrolls the synchronous invocation itself.
            // EnterAsync here would hold the revocation monitor through its
            // synchronous file/write/Flush prefix despite nested admission.
            await journal.AppendAdmittedAsync(row, original.Token).ConfigureAwait(false);
            Current();
        }
        // The journal lookup is read-only private state. Check the genuine
        // barriers now; Append(Prepared) does the full live Verify after that
        // lookup and before the first durable journal or fake mutation.
        Barrier();
        var target = new PublicationTarget("local-real/no-network", "refs/heads/local-real/" + runId, "refs/heads/local-real-base");
        var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(runId + "\0" + revision + "\0local-fake-pr")));
        if (await journal.ReadAdmittedAsync(key, original.Token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Local-real publication cannot replay or adopt journal DATA.");
        Current();
        var intent = new PublicationIntent(key, runId, revision, PublicationAction.CreateOrUpdatePr, target,
            evidence.BaseCommit!, evidence.HeadCommit!, evidence.TreeId!, PublicationIntentState.Prepared, CreatedAt: DateTimeOffset.UtcNow);
        var adapter = new LocalOnlyAdapter(); var sent = false;
        await Append(intent).ConfigureAwait(false);
        try
        {
            await Append(intent with { State = PublicationIntentState.Sent }).ConfigureAwait(false); sent = true;
            await Verify().ConfigureAwait(false);
            var mutation = await parent.EnterAsync(runId, () => adapter.CreateOrUpdatePrAsync(new(intent, intent.BaseCommit), original.Token)).ConfigureAwait(false);
            await Verify().ConfigureAwait(false);
            var readback = await parent.EnterAsync(runId, () => adapter.ReadAsync(target, key, original.Token)).ConfigureAwait(false);
            Current();
            if (mutation.Outcome != GitHostMutationOutcome.Confirmed || mutation.ResultId is null ||
                readback.ResultId != mutation.ResultId || readback.PublishedIdempotencyKey != key ||
                readback.SourceHead != intent.HeadCommit || readback.PrHead != intent.HeadCommit || readback.TargetHead != intent.BaseCommit)
                throw new IOException("Exact local fake readback is incomplete; no resend.");
            var node = new JsonObject
            {
                ["schemaVersion"] = "1.0.0",
                ["kind"] = "publication-receipt",
                ["identity"] = "sha256:" + new string('0', 64),
                ["baseCommit"] = intent.BaseCommit,
                ["headCommit"] = intent.HeadCommit,
                ["treeId"] = intent.TreeId,
                ["action"] = "PR_PUBLISHED",
                ["remoteRef"] = "local-ref:" + target.SourceRef,
                ["resultId"] = mutation.ResultId,
                ["createdAt"] = intent.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            };
            node["identity"] = CanonicalJson.ComputeIdentity(Encoding.UTF8.GetBytes(node.ToJsonString()));
            var receipt = ContractValidator.ParseAndValidate(Encoding.UTF8.GetBytes(node.ToJsonString()));
            await Verify().ConfigureAwait(false);
            // The genuine review/PR-authorization barriers above remain production
            // checks. This local readback is DATA, never a production PrPublished transition.
            RequireLocalReadbackReceipt(receipt, intent, mutation.ResultId);
            await journal.AppendReceiptAdmittedAsync(receipt, original.Token).ConfigureAwait(false);
            await Append(intent with { State = PublicationIntentState.Confirmed, ResultId = mutation.ResultId, ReceiptIdentity = receipt.Identity }).ConfigureAwait(false);
            await Verify().ConfigureAwait(false);
            return new(PublicationOutcome.PublishedFixture, PublicationDenial.None,
                "Actual production review/gate barriers with local fake publication only; no remote mutation or production publication authority.",
                PublicationAction.CreateOrUpdatePr, PublicationRealm.Fixture, receipt);
        }
        catch (Exception primary)
        {
            if (sent)
            {
                try { parent.RetainUncertain(runId, "Local fake publication did not complete its same-original journal/readback; no replay."); }
                catch (Exception retention) { primary.Data["LocalFakePublicationRetentionFailure"] = retention; }
            }
            throw;
        }
    }

    internal static void RequireLocalReadbackReceipt(ValidatedContract receipt, PublicationIntent intent, string resultId)
    {
        using var document = System.Text.Json.JsonDocument.Parse(receipt.CanonicalBytes);
        var row = document.RootElement;
        if (receipt.SchemaName != "publication-receipt" ||
            row.GetProperty("action").GetString() != "PR_PUBLISHED" ||
            row.GetProperty("remoteRef").GetString() != "local-ref:" + intent.Target.SourceRef ||
            row.GetProperty("baseCommit").GetString() != intent.BaseCommit ||
            row.GetProperty("headCommit").GetString() != intent.HeadCommit ||
            row.GetProperty("treeId").GetString() != intent.TreeId ||
            row.GetProperty("resultId").GetString() != resultId ||
            !resultId.StartsWith("local-fake-", StringComparison.Ordinal))
            throw new IOException("Exact local-only publication DATA readback required.");
    }

    private sealed class LocalOnlyAdapter : IGitHostAdapter
    {
        PublicationIntent? published; string? result;
        public PublicationRealm Realm => PublicationRealm.Fixture;
        public Task<GitHostMutationResult> CreateOrUpdatePrAsync(GitHostMutation mutation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (published is not null || mutation.Intent.Target.Repository != "local-real/no-network" || mutation.ExpectedTargetHead != mutation.Intent.BaseCommit)
                throw new InvalidOperationException("One local fake action only.");
            published = mutation.Intent; result = "local-fake-" + Guid.NewGuid().ToString("N");
            return Task.FromResult(new GitHostMutationResult(GitHostMutationOutcome.Confirmed, result));
        }
        public Task<GitHostObservation> ReadAsync(PublicationTarget target, string key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var row = published ?? throw new IOException("No local fake action to read.");
            if (target != row.Target || key != row.IdempotencyKey) throw new IOException("Foreign local fake readback refused.");
            return Task.FromResult(new GitHostObservation(target, row.HeadCommit, row.BaseCommit, row.HeadCommit,
                GitHostChecks.Unknown, GitHostProtection.Unknown, key, result));
        }
        public Task<GitHostMutationResult> MergeNormalAsync(GitHostMutation mutation, CancellationToken token) =>
            throw new InvalidOperationException("Local-real golden never merges or invokes a remote adapter.");
    }
}
