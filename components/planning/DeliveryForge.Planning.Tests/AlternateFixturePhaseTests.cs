namespace DeliveryForge.Planning.Tests;

// Fixture timing DATA only. This cannot construct a process, core Ready or native closure.
internal readonly record struct AlternateFixturePhase(long Started, long Deadline, long Frequency)
{
    internal TimeSpan Remaining(long originalDispatch, long now, long actualFrequency, TimeSpan originalWork)
    {
        if (originalWork <= TimeSpan.Zero) throw new TimeoutException("Original fixture work expired.");
        if (Frequency <= 0 || Frequency != actualFrequency || Started <= 0 || Started < originalDispatch || Started > now ||
            Deadline <= Started || (decimal)Deadline - Started > (decimal)Frequency * 10)
            throw new IOException("Original creator phase timing is malformed, future, stale or rebound.");
        if (now >= Deadline) throw new TimeoutException("Original creator alternate phase expired.");
        var left = TimeSpan.FromSeconds((double)(Deadline - now) / Frequency);
        return left < originalWork ? left : originalWork;
    }
}

[Collection(RichPlanImportFixtureCollection.Name)]
public sealed class AlternateFixturePhaseTests
{
    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("frequency")]
    public void Invalid_actual_phase_timing_cannot_reset_or_extend_observation(string scenario)
    {
        var phase = new AlternateFixturePhase(scenario == "future" ? 3000 : 1000,
            scenario == "future" ? 13000 : 11000, scenario == "frequency" ? 1001 : 1000);
        if (scenario == "expired") Assert.Throws<TimeoutException>(() => phase.Remaining(100, 11000, 1000, TimeSpan.FromSeconds(100)));
        else Assert.Throws<IOException>(() => phase.Remaining(100, 2000, 1000, TimeSpan.FromSeconds(100)));
    }
    [Fact]
    public void Genuine_phase_data_only_shrinks_original_work_allowance()
    {
        var phase = new AlternateFixturePhase(1000, 11000, 1000);
        Assert.Equal(TimeSpan.FromSeconds(9), phase.Remaining(100, 2000, 1000, TimeSpan.FromSeconds(100)));
        Assert.Equal(TimeSpan.FromSeconds(2), phase.Remaining(100, 2000, 1000, TimeSpan.FromSeconds(2)));
    }
}
