namespace DeliveryForge.Scheduling;

public sealed record ChangeFootprint(IReadOnlyList<string> Paths, IReadOnlyList<string> SharedOwners, bool Unknown = false);

public static class OverlapDetector
{
    public static bool Conflicts(ChangeFootprint left, ChangeFootprint right) => throw new NotImplementedException();
}
