using DeliveryForge.Evidence;
using DeliveryForge.Execution.Host;

namespace DeliveryForge.Execution.Tests;

public sealed class ReviewGitPreparationStartupDataTests
{
    [Fact]
    public void Absent_capture_is_DATA_absence_not_native_closure()
    {
        var data = ReviewGitPreparationCaptureData.FromCapture(null);
        Assert.False(data.CaptureReturned);
        Assert.Null(data.ExitCode);
        Assert.Null(data.Startup);
        Assert.Null(data.Lifecycle);
        Assert.False(data.OwnedProcessQuiescent);
        Assert.False(data.RootExitObserved);
        Assert.False(data.StandardOutputDrainCompleted);
        Assert.False(data.StandardErrorDrainCompleted);
    }

    [Theory]
    [InlineData(129, 0, 0)]
    [InlineData(0, 129, 0)]
    [InlineData(0, 0, 2049)]
    public void Startup_DATA_refuses_oversized_text(int stage, int failure, int reason)
        => Assert.Throws<ArgumentException>(() => Data(new string('s', stage), new string('f', failure), 0, 5, 4, new string('r', reason)));

    [Theory]
    [InlineData(double.NaN, 5, 4)]
    [InlineData(double.PositiveInfinity, 5, 4)]
    [InlineData(-1, 5, 4)]
    [InlineData(0, 5, 6)]
    public void Startup_DATA_refuses_nonfinite_or_incoherent_allowances(double elapsed, double total, double work)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Data("stage", null, elapsed, total, work, null));

    [Fact]
    public void Bounded_DATA_can_record_late_elapsed_without_promoting_its_allowance()
    {
        var data = Data(new string('s', 128), new string('f', 128), 6, 5, 4, new string('r', 2048));
        Assert.Equal(6, data.OriginalElapsedSeconds);
        Assert.Equal(5, data.OriginalTotalSeconds);
        Assert.Equal(4, data.OriginalWorkSeconds);
        Assert.Equal(CommandStartupState.AttemptedUnknown, data.Creation);
        Assert.Equal(CommandStartupState.NotObserved, data.RootHandle);
    }

    [Fact]
    public void Startup_DATA_refuses_an_unknown_enum_value()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new CommandStartupObservation((CommandStartupState)99,
            CommandStartupState.NotObserved, CommandStartupState.NotApplicable, CommandStartupState.NotApplicable,
            CommandStartupState.NotObserved, CommandStartupState.NotObserved, CommandStartupState.NotObserved,
            "stage", null, 0, 5, 4, null));

    private static CommandStartupObservation Data(string stage, string? failure, double elapsed, double total, double work, string? reason)
        => new(CommandStartupState.AttemptedUnknown, CommandStartupState.NotObserved, CommandStartupState.NotApplicable,
            CommandStartupState.NotApplicable, CommandStartupState.NotObserved, CommandStartupState.NotObserved,
            CommandStartupState.NotObserved, stage, failure, elapsed, total, work, reason);
}
