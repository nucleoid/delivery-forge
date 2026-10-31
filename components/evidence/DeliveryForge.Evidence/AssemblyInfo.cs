using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DeliveryForge.Evidence.Tests")]

// Only the owning execution implementation can construct its closed read-only cleanup view.
[assembly: InternalsVisibleTo("DeliveryForge.Execution")]
[assembly: InternalsVisibleTo("DeliveryForge.Tool.Tests")]
