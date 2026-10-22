using System.Diagnostics;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Publication.Tests;

public sealed class LocalRealFakePublicationTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("unmarked")]
    [InlineData("foreign-head")]
    [InlineData("foreign-result")]
    [InlineData("merge")]
    public void Local_readback_rule_accepts_only_exact_marked_fake_data(string change)
    {
        var fixture = new PublicationFixture();
        var intent = new PublicationIntent("unit-key", "unit-run", "unit-revision", PublicationAction.CreateOrUpdatePr,
            new("local-real/no-network", "refs/heads/local-real/unit", "refs/heads/base"),
            fixture.Patch.BaseCommit, fixture.Patch.HeadCommit, fixture.Patch.HeadTree, PublicationIntentState.Sent);
        var node = System.Text.Json.Nodes.JsonNode.Parse(fixture.PublicationReceipt("PR_PUBLISHED").CanonicalBytes.Span)!.AsObject();
        node["remoteRef"] = "local-ref:" + intent.Target.SourceRef;
        node["resultId"] = "local-fake-unit";
        if (change == "unmarked") node["remoteRef"] = intent.Target.SourceRef;
        if (change == "foreign-head") node["headCommit"] = new string('f', 40);
        if (change == "foreign-result") node["resultId"] = "local-fake-other";
        if (change == "merge") node["action"] = "MERGED";
        var receipt = PublicationFixture.Contract(node.ToJsonString());
        if (change == "none") LocalRealFakePublication.RequireLocalReadbackReceipt(receipt, intent, "local-fake-unit");
        else Assert.Throws<IOException>(() => LocalRealFakePublication.RequireLocalReadbackReceipt(receipt, intent, "local-fake-unit"));
        // This is a local DATA rule, not a production reviewer/publication issuer.
    }

    [Theory]
    [InlineData("fixture-review")]
    [InlineData("missing-gates")]
    [InlineData("failed-gate")]
    [InlineData("foreign-test-receiver")]
    [InlineData("revoked-parent")]
    [InlineData("expired-original")]
    public async Task Public_fixture_or_incomplete_evidence_cannot_reach_even_the_local_fake_journal(string refusal)
    {
        var fixture = new PublicationFixture(); var parent = new Parent { Revoked = refusal == "revoked-parent" };
        var original = new Budget { Expired = refusal == "expired-original" }; var journal = new Journal();
        var evidence = fixture.Transition;
        if (refusal == "missing-gates") evidence = evidence with { GateReceipts = [] };
        if (refusal == "failed-gate")
        {
            // First show that this exact passing tuple gets past gate validation
            // and reaches the later production review-realm barrier, without effects.
            var passingError = await Assert.ThrowsAsync<InvalidWorkflowTransitionException>(() => LocalRealFakePublication.ExecuteAsync(
                "run-" + new string('a', 32), "unit-revision", evidence, fixture.Receiver,
                original, parent, () => Task.CompletedTask, journal));
            Assert.Contains("authority realm", passingError.Message);
            Assert.Equal(0, journal.Calls); Assert.Equal(0, parent.EffectCalls);

            var passing = fixture.Gates[0];
            var node = System.Text.Json.Nodes.JsonNode.Parse(passing.CanonicalBytes.Span)!.AsObject();
            node["outcome"] = "FAIL"; node["exitCode"] = 1;
            var failing = PublicationFixture.Contract(node.ToJsonString());
            node["outcome"] = "PASS"; node["exitCode"] = 0;
            // Exact canonical roundtrip proves no policy/gate/revision/authority
            // field was substituted along with the disallowed outcome.
            Assert.Equal(passing.Identity, PublicationFixture.Contract(node.ToJsonString()).Identity);
            evidence = evidence with { GateReceipts = [failing, .. fixture.Gates.Skip(1)] };
        }
        var receiver = refusal == "foreign-test-receiver" ? PublicationFixture.NewRoot().Receiver : fixture.Receiver;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => LocalRealFakePublication.ExecuteAsync("run-" + new string('a', 32), "unit-revision",
            evidence, receiver, original, parent, () => Task.CompletedTask, journal));
        if (refusal == "expired-original") Assert.IsType<TimeoutException>(error);
        else if (refusal == "revoked-parent") Assert.IsType<IOException>(error);
        else
        {
            Assert.IsType<InvalidWorkflowTransitionException>(error);
            Assert.Contains(refusal switch { "missing-gates" => "every evidence-policy gate", "failed-gate" => "allowed outcome", _ => "authority realm" }, error.Message);
        }
        Assert.Equal(0, journal.Calls); Assert.Equal(0, parent.EffectCalls);
        // These are refusal controls. No production reviewer, native process,
        // registry baseline or local-real success has been manufactured.
        // Both normal and foreign Test receivers fail the production realm
        // barrier; the foreign variant is NOT foreign-production-issuer proof.
    }
    sealed class Budget : IOwnedDuplexBudget
    {
        public bool Expired;
        public long OriginalStarted { get; } = Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(10);
        public TimeSpan WorkLimit => TimeSpan.FromSeconds(9);
        public CancellationToken Token => TestContext.Current.CancellationToken;
        public void CheckWork() { Token.ThrowIfCancellationRequested(); if (Expired || Stopwatch.GetElapsedTime(OriginalStarted) >= WorkLimit) throw new TimeoutException("Unit original expired."); }
        public TimeSpan CleanupRemaining => WholeLimit - Stopwatch.GetElapsedTime(OriginalStarted);
    }
    sealed class Parent : ICoordinatedSynchronousEffectAdmission
    {
        public bool Revoked; public int EffectCalls;
        public void CheckCurrent(string runId) { if (Revoked) throw new IOException("Unit parent revoked."); }
        public T EnterSynchronous<T>(string runId, Func<T> invoke) { CheckCurrent(runId); EffectCalls++; return invoke(); }
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) { CheckCurrent(runId); EffectCalls++; return invoke(); }
        public void RetainUncertain(string runId, string reason) => throw new InvalidOperationException("No unit effect was admitted.");
    }
    sealed class Journal : ILocalRealAdmittedJournal
    {
        public int Calls;
        public Task<PublicationIntent?> ReadAdmittedAsync(string key, CancellationToken token) { Calls++; throw new InvalidOperationException("Forbidden journal read."); }
        public Task AppendAdmittedAsync(PublicationIntent intent, CancellationToken token) { Calls++; throw new InvalidOperationException("Forbidden journal write."); }
        public Task AppendReceiptAdmittedAsync(ValidatedContract receipt, CancellationToken token) { Calls++; throw new InvalidOperationException("Forbidden receipt write."); }
    }
}
