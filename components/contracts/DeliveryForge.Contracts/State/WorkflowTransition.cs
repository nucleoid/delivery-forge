using DeliveryForge.Contracts.Models;

namespace DeliveryForge.Contracts.State;

public sealed record TransitionEvidence(
    AuthorizationCeiling Ceiling,
    string? GateReceiptId = null,
    string? ReviewReceiptId = null,
    string? PublicationReceiptId = null,
    string? HostReviewReceiptId = null);

public static class WorkflowTransition
{
    public static void EnsureAllowed(WorkflowState from, WorkflowState to, TransitionEvidence evidence) =>
        throw new NotImplementedException("Workflow transition validation is not implemented.");
}
