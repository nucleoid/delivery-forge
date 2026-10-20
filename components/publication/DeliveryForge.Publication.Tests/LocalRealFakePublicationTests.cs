using System.Diagnostics;
using DeliveryForge.Contracts.State;
using DeliveryForge.Contracts.Validation;
using DeliveryForge.Coordination;
using DeliveryForge.Evidence;

namespace DeliveryForge.Publication.Tests;

public sealed class LocalRealFakePublicationTests
{
    [Theory]
    [InlineData("fixture-review")]
    [InlineData("missing-gates")]
    [InlineData("failed-gate")]
    [InlineData("foreign-receiver")]
    [InlineData("revoked-parent")]
    [InlineData("expired-original")]
    public async Task Public_fixture_or_incomplete_evidence_cannot_reach_even_the_local_fake_journal(string refusal)
    {
        var fixture = new PublicationFixture(); var parent = new Parent { Revoked = refusal == "revoked-parent" };
        var original = new Budget { Expired = refusal == "expired-original" }; var journal = new Journal();
        var evidence = fixture.Transition;
        if (refusal == "missing-gates") evidence = evidence with { GateReceipts = [] };
        if (refusal == "failed-gate") evidence = evidence with { GateReceipts = [fixture.Gate("build", "FAIL"), fixture.Gate("test", "PASS")] };
        var receiver = refusal == "foreign-receiver" ? PublicationFixture.NewRoot().Receiver : fixture.Receiver;
        await Assert.ThrowsAnyAsync<Exception>(() => LocalRealFakePublication.ExecuteAsync("run-" + new string('a', 32), "unit-revision",
            evidence, receiver, original, parent, () => Task.CompletedTask, journal));
        Assert.Equal(0, journal.Calls); Assert.Equal(0, parent.EffectCalls);
        // These are refusal controls. No production reviewer, native process,
        // registry baseline or local-real success has been manufactured.
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
    sealed class Journal : IPublicationJournal
    {
        public int Calls;
        public Task<PublicationIntent?> ReadAsync(string key, CancellationToken token) { Calls++; throw new InvalidOperationException("Forbidden journal read."); }
        public Task AppendAsync(PublicationIntent intent, CancellationToken token) { Calls++; throw new InvalidOperationException("Forbidden journal write."); }
        public Task AppendReceiptAsync(ValidatedContract receipt, CancellationToken token) { Calls++; throw new InvalidOperationException("Forbidden receipt write."); }
    }
}
