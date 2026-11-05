namespace DeliveryForge.Evidence;
// Optional outgoing preservation only. It cannot release a lifetime, issue
// knownness, modify a clock or construct a native owner. Six-member budget stays unchanged.
public interface IOwnedBudgetUnknownRetention { void PreserveUnknown(); }
