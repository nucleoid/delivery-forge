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
