namespace DeliveryForge.Coordination;

public static class ProductionCoordinationProfile
{
    public static CoordinationCapabilities CurrentUnavailable() =>
        CoordinationCapabilities.Unavailable(
            "Production profile INCOMPLETE: nucleoid/continuum#7 is open and no deployed, configured, capability-proven transport is supplied. " +
            "The isolated contract fixture is test-only and cannot satisfy production readiness.");
}
