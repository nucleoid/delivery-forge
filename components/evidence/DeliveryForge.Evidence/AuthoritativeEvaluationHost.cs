namespace DeliveryForge.Evidence;

/// <summary>
/// Reserved production-authority composition point. Construction remains unavailable until
/// an external separately privileged launcher/signer can enforce the required boundary.
/// </summary>
public sealed class AuthoritativeEvaluationHost
{
    public AuthoritativeEvaluationHost(
        ICommandExecutor executor,
        IRepositoryIdentityReader repository,
        string trustedWorkingDirectory,
        string trustedProtectedBaseCommit,
        DeterministicEvaluationPluginRegistry? plugins = null)
    {
        throw new NotSupportedException(
            "Production authority is disabled: this repository does not provide an enforceable separate-principal gate launcher and signer. " +
            "Deploy an external privileged producer before enabling v2 producers or consumers.");
    }
}
