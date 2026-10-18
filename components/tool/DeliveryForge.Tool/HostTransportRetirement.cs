using DeliveryForge.Evidence;
namespace DeliveryForge.Tool;
public static class HostTransportRetirement {
    public static void RequireSuccessful(DuplexRetirement actual) {
        if(!actual.KnownClosed||actual.ExitCode!=0||actual.Failures.Count!=0)throw new IOException("Actual original native/IO retirement is unknown or capture failed; no successful worker result.");
    }
}
