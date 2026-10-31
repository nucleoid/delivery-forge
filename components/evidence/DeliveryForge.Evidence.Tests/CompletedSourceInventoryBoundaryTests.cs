using DeliveryForge.Coordination;

namespace DeliveryForge.Evidence.Tests;

public sealed class CompletedSourceInventoryBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_result_DTO_or_foreign_original_cannot_select_a_completed_source_inventory(bool foreignOriginal)
    {
        var original = new Original(); var parent = new Parent();
        var scope = new InstalledToolHostScope(original, parent, "run-original");
        var repository = TestEvidence.Repository();
        var fabricated = new GateRunResult(GateOutcome.Pass, "not actual evidence", false, false, [], false,
            new("not-executed", [], Path.GetTempPath()), TestEvidence.CommandResult(), repository, repository,
            TestEvidence.Identity('1'), TestEvidence.Identity('2'), TestEvidence.Identity('3'), "all",
            new Dictionary<string, string>(), "absent-receipt.json", null);
        Assert.Throws<EvidenceWriteException>(() => scope.CompletedSourceInventory(fabricated,
            foreignOriginal ? new Original() : original, parent, "run-original", Path.GetTempPath(), repository, verifyCurrent: true));
        Assert.Equal(0, parent.Entered); Assert.False(scope.HasUncertainOriginals);
    }
    sealed class Original : IOwnedDuplexBudget
    {
        public long OriginalStarted { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public TimeSpan WholeLimit => TimeSpan.FromSeconds(30);
        public TimeSpan WorkLimit => TimeSpan.FromSeconds(20);
        public TimeSpan CleanupRemaining => TimeSpan.FromSeconds(10);
        public CancellationToken Token => TestContext.Current.CancellationToken;
        public void CheckWork() => Token.ThrowIfCancellationRequested();
    }
    sealed class Parent : ICoordinatedEffectAdmission
    {
        public int Entered { get; private set; }
        public Task<T> EnterAsync<T>(string runId, Func<Task<T>> invoke) { Entered++; throw new InvalidOperationException("No actual owner exists in this refusal test."); }
        public void RetainUncertain(string runId, string reason) { Entered++; throw new InvalidOperationException("No original was entered."); }
    }
}
