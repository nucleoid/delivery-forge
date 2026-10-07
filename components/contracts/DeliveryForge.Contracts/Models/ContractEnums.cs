namespace DeliveryForge.Contracts.Models;

public enum DeliveryMode { Plan, Implement, Pr, Merge, Resume }
public enum GateOutcome { Pass, Fail, Incomplete, Error, NotApplicable }
public enum WorkflowState
{
    Understanding, Planned, Ready, Executing, Paused, LocalComplete, Evaluating,
    IndependentReview, PrAuthorized, PrPublished, CiComplete, HostReviewComplete,
    MergeAuthorized, Merged, Blocked, Failed, Stopped
}

public enum AuthorizationCeiling { Plan, Implement, Pr, Merge }

public static class ContractValues
{
    public static string ToWireValue(this DeliveryMode value) => value switch
    {
        DeliveryMode.Plan => "plan",
        DeliveryMode.Implement => "implement",
        DeliveryMode.Pr => "pr",
        DeliveryMode.Merge => "merge",
        DeliveryMode.Resume => "resume",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string ToWireValue(this GateOutcome value) => value switch
    {
        GateOutcome.Pass => "PASS",
        GateOutcome.Fail => "FAIL",
        GateOutcome.Incomplete => "INCOMPLETE",
        GateOutcome.Error => "ERROR",
        GateOutcome.NotApplicable => "NOT_APPLICABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string ToWireValue(this WorkflowState value) =>
        string.Concat(value.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $"_{character}" : character.ToString())).ToUpperInvariant();
}
