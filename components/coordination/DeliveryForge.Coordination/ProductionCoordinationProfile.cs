namespace DeliveryForge.Coordination;

public static class ProductionCoordinationProfile
{
    public static CoordinationCapabilities CurrentUnavailable() =>
        CoordinationCapabilities.Unavailable(
            "Production profile INCOMPLETE until the parent configures an authenticated Continuum transport, " +
            "trusted current deployment/key-to-principal evidence, actual owned-work quiescence, a clock and checkpoint store. " +
            "Source presence, discovery and isolated contract/live controls do not configure a production workflow.");
}
