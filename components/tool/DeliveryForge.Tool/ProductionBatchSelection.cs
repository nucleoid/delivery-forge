using DeliveryForge.Scheduling;
using System.Text.Json.Serialization;

namespace DeliveryForge.Tool;

// Owner-selected input DATA only. Pins do not represent accepted workers,
// leases, measured resources, live tickets or publication authorization.
public sealed record ProductionBatchNodeSelection(string NodeId, AdmissionFile Plan, AdmissionFile Configuration, RunDemand Demand, int RiskClass = 0);
public sealed record ProductionBatchSelection(string StoreRoot, BudgetPolicy Policy, FairnessPolicy Fairness,
    IReadOnlyList<ProductionBatchNodeSelection> Nodes, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ControlPath = null);
