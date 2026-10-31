namespace DeliveryForge.Evidence.CensusFixture;

// Ordinary compiled declarations have no executable method body or visit rows.
public interface IDeclarations
{
    int Ping(int value);
}

public enum DeclarationState
{
    Ready,
    Done
}
