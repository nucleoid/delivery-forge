using DeliveryForge.Evidence;
namespace DeliveryForge.Tool;
// Ordinary installation/profile DATA under an independent trusted exact pin;
// never caller-selected private owner, WriterToken or accepted worker fields.
public sealed record StandaloneRootSelection(string Kind,string StoreRoot,LocalWorkerPaths WorkerPaths,AdmissionFile InstalledHostAdmission,AdmissionFile? LinuxScopeProfile=null);
