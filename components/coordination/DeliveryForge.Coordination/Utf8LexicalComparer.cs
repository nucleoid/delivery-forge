using System.Text;

namespace DeliveryForge.Coordination;

internal sealed class Utf8LexicalComparer : IComparer<CoordinationResource>
{
    public static Utf8LexicalComparer Instance { get; } = new();

    public int Compare(CoordinationResource? left, CoordinationResource? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var scope = CompareBytes(left.Scope, right.Scope);
        return scope != 0 ? scope : CompareBytes(left.ResourceKey, right.ResourceKey);
    }

    private static int CompareBytes(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        var length = Math.Min(leftBytes.Length, rightBytes.Length);
        for (var index = 0; index < length; index++)
        {
            var compared = leftBytes[index].CompareTo(rightBytes[index]);
            if (compared != 0) return compared;
        }
        return leftBytes.Length.CompareTo(rightBytes.Length);
    }
}
