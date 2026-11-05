using DeliveryForge.Contracts.Validation;
using DeliveryForge.Coordination;

namespace DeliveryForge.Publication.Tests;

// Real service with isolated fixture ports only. No remote or production authority.
public sealed class UncertainPublicationNoticeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_notice_failure_cannot_skip_uncertain_journal_or_authorize_another_mutation(bool journalFails)
    {
        var fixture = new PublicationFixture();
        var facts = new FixtureFacts(fixture);
        var host = new FixtureGitHost(fixture) { LoseResponse = true };
        var absent = host.State;
        host.Read = _ => absent;
        var journal = new Journal(journalFails);
        var owner = new Owner();
        var service = new PublicationService(fixture.Authority, facts, host, journal, coordinationAdmission: owner);
        var run = fixture.Authority.IssueRun(fixture.Definition());
        var request = new PublicationRequest(PublicationMode.Pr, "uncertain-owner-notice", false);

        var first = await service.ExecuteAsync(run, request, TestContext.Current.CancellationToken);
        Assert.Equal(PublicationOutcome.Blocked, first.Outcome);
        Assert.Equal(PublicationDenial.Uncertain, first.Denial);
        Assert.Contains("This service retains uncertainty-notice or journal failures", first.Reason);
        Assert.DoesNotContain(owner.Failure.Message, first.Reason);
        Assert.DoesNotContain(journal.Failure.Message, first.Reason);
        Assert.Equal(1, host.Mutations);
        Assert.True(journal.UncertainAttempts > 0);
        Assert.Contains(owner.Failure, service.ActualUncertaintyFailures);
        if (journalFails) Assert.Contains(journal.Failure, service.ActualUncertaintyFailures);
        else Assert.Contains(journal.Records, row => row.State == PublicationIntentState.Uncertain);
        Assert.Empty(journal.Receipts);

        var retry = await service.ExecuteAsync(run, request, TestContext.Current.CancellationToken);
        Assert.Equal(PublicationOutcome.Blocked, retry.Outcome);
        Assert.Equal(PublicationDenial.Uncertain, retry.Denial);
        Assert.Equal(1, host.Mutations);
    }

    [Fact]
    public async Task Known_uncertainty_notices_do_not_claim_diagnostic_failure()
    {
        var fixture = new PublicationFixture();
        var host = new FixtureGitHost(fixture) { LoseResponse = true };
        var absent = host.State; host.Read = _ => absent;
        var service = new PublicationService(fixture.Authority, new FixtureFacts(fixture), host,
            new Journal(false), coordinationAdmission: new Owner(fail: false));
        var result = await service.ExecuteAsync(fixture.Authority.IssueRun(fixture.Definition()),
            new PublicationRequest(PublicationMode.Pr, "known-uncertainty-notice", false), TestContext.Current.CancellationToken);
        Assert.Equal(PublicationOutcome.Blocked, result.Outcome);
        Assert.Equal(PublicationDenial.Uncertain, result.Denial);
        Assert.DoesNotContain("This service retains uncertainty-notice or journal failures", result.Reason);
        Assert.Empty(service.ActualUncertaintyFailures); Assert.Equal(1, host.Mutations);
    }

    sealed class Owner(bool fail = true) : ICoordinatedEffectAdmission
    {
        internal readonly IOException Failure = new("Fixture owner uncertainty notification failed.");
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) => invoke();
        public void RetainUncertain(string runId, string reason) { if (fail) throw Failure; }
    }

    sealed class Journal(bool fail) : IPublicationJournal
    {
        internal readonly IOException Failure = new("Fixture uncertainty append failed.");
        internal readonly List<PublicationIntent> Records = [];
        internal readonly List<ValidatedContract> Receipts = [];
        internal int UncertainAttempts;
        public Task<PublicationIntent?> ReadAsync(string key, CancellationToken token)
            => Task.FromResult(Records.LastOrDefault(row => row.IdempotencyKey == key));
        public Task AppendAsync(PublicationIntent intent, CancellationToken token)
        {
            if (intent.State == PublicationIntentState.Uncertain)
            {
                UncertainAttempts++;
                if (fail) throw Failure;
            }
            Records.Add(intent);
            return Task.CompletedTask;
        }
        public Task AppendReceiptAsync(ValidatedContract receipt, CancellationToken token)
        {
            Receipts.Add(receipt);
            return Task.CompletedTask;
        }
    }
}
