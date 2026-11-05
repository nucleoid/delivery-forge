using DeliveryForge.Scheduling;

namespace DeliveryForge.Tool;

// Independently pinned policy DATA. Approval must come from the containing
// owner's reviewed configuration; this type issues no reservation or lifetime.
public sealed record LocalSingleSchedulingSelection(BudgetPolicy Policy, RunDemand Demand);
