using System.Text.Json;

namespace DeliveryForge.Tool;

// Independently selected local configuration DATA only. Registration neither
// verifies conformance nor constructs a host, lease, writer or capability.
public sealed record RepositorySetupProfileRegistration(string Kind, string Repository, string RepositoryRoot, AdmissionFile Template);
public sealed record RepositorySetupProfileResult(AdmissionFile Registration, AdmissionFile Template, string Repository, string RepositoryRoot)
{
    public bool RuntimeCertified => false;
}
internal sealed record SelectedRepositorySetupProfile(TrustedConfiguration Template, AdmissionFile TemplatePin, AdmissionFile Registration);

internal static class RepositorySetupProfiles
{
    const string Kind = "DeliveryForgeRepositorySetupProfile-v1";
    static StringComparison Paths => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string Location(string repository, string repositoryRoot, string stateRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(BoundedFiles.Plain(repositoryRoot, true));
        if (!Directory.Exists(root)) throw new ToolUsageException("Profile selection requires an existing repository directory.");
        var state = BoundedFiles.Plain(stateRoot, true);
        BoundedFiles.Separate(root, state);
        return BoundedFiles.Plain(Path.Combine(state, "repos", AutomaticSetup.RepositoryKey(repository, root), "profile.json"));
    }

    static void RequireRepository(TrustedConfiguration template, string repository, string repositoryRoot)
    {
        if (!string.Equals(template.Repository, repository, StringComparison.Ordinal) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(template.RepositoryRoot)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)), Paths))
            throw new ToolUsageException("Registered setup template belongs to a different repository or checkout.");
    }

    static void SeparateRegistration(TrustedConfiguration template, string templatePath, string registrationPath, ToolLifetime original)
    {
        // Validate the actual containing configuration path as well as the root
        // topology before either accepting or creating the local selection.
        var root = AutomaticSetup.ReadOrdinaryTemplateRoot(template, templatePath, original);
        foreach (var source in new[] { template.RepositoryRoot, root.WorkerPaths.Worktree, root.WorkerPaths.ResultDirectory, root.StoreRoot })
            BoundedFiles.Separate(source, registrationPath);
        BoundedFiles.Separate(templatePath, registrationPath);
        BoundedFiles.Separate(template.StandaloneRoot!.Path, registrationPath);
        BoundedFiles.Separate(template.GitExecutable, registrationPath);
        if (template.PolicyFile is { } policy) BoundedFiles.Separate(policy, registrationPath);
        if (template.Coordination is { } coordination)
            foreach (var path in new[] { coordination.PrivateKeyPath, coordination.CheckpointRoot, coordination.DeploymentEvidence.Path })
                BoundedFiles.Separate(path, registrationPath);
        BoundedFiles.Separate(root.InstalledHostAdmission.Path, registrationPath);
        if (root.LinuxScopeProfile is { } scope) BoundedFiles.Separate(scope.Path, registrationPath);
        if (template.InstalledQuality is { } quality)
        {
            BoundedFiles.Separate(quality.ArtifactRoot, registrationPath);
            foreach (var gate in quality.Gates) BoundedFiles.Separate(gate.Profile.Path, registrationPath);
            if (quality.Registry is { } registry)
            {
                BoundedFiles.Separate(registry.ArtifactRoot, registrationPath);
                BoundedFiles.Separate(registry.OwnerApproval.Path, registrationPath);
            }
        }
        if (template.InstalledReview is { } review)
        {
            BoundedFiles.Separate(review.ArtifactRoot, registrationPath);
            BoundedFiles.Separate(review.GitImage.Path, registrationPath);
            if (review.IndependentReviewer is { } reviewer) BoundedFiles.Separate(reviewer.Image.Path, registrationPath);
        }
    }

    internal static RepositorySetupProfileResult Register(string templatePath, string repository, string repositoryRoot, string stateRoot, ToolLifetime original)
    {
        original.Check();
        var path = Location(repository, repositoryRoot, stateRoot);
        var template = TrustedConfiguration.LoadBound(templatePath, original, out var templatePin);
        RequireRepository(template, repository, repositoryRoot);
        SeparateRegistration(template, templatePath, path, original);
        var body = new RepositorySetupProfileRegistration(Kind, template.Repository,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(template.RepositoryRoot)), templatePin);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, BoundedFiles.Json);
        if (bytes.Length > 65536) throw new ToolUsageException("Repository profile registration exceeds its finite DATA bound.");
        // No overwrite or replacement endpoint: even an identical existing
        // registration stays preserved until its owner explicitly reconciles it.
        if (File.Exists(path) || Directory.Exists(path))
            throw new ToolUsageException("Repository setup profile already exists; preserve and reconcile the existing registration.");
        original.Check();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        original.Check();
        _ = BoundedFiles.Plain(Path.GetDirectoryName(path)!, true);
        BoundedFiles.WriteNew(path, bytes, original);
        return new(new(path, bytes.LongLength, BoundedFiles.Sha(bytes)), templatePin, template.Repository, body.RepositoryRoot);
    }

    internal static SelectedRepositorySetupProfile? Read(string repository, string repositoryRoot, string stateRoot, ToolLifetime original)
    {
        original.Check();
        var path = Location(repository, repositoryRoot, stateRoot);
        // Distinguish genuine absence from a corrupt or redirected selection.
        // A present invalid registration never falls back to plan-only setup.
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { original.Check(); return null; }
        catch (DirectoryNotFoundException) { original.Check(); return null; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Repository setup registration must be a plain file.");
        var bytes = BoundedFiles.Read(path, original, 65536);
        _ = DeliveryForge.Contracts.Serialization.CanonicalJson.Canonicalize(bytes);
        var registration = JsonSerializer.Deserialize<RepositorySetupProfileRegistration>(bytes, BoundedFiles.Json)
            ?? throw new ToolUsageException("Repository setup registration is null.");
        if (registration.Kind != Kind || registration.Template is null ||
            !string.Equals(registration.Repository, repository, StringComparison.Ordinal) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(registration.RepositoryRoot)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)), Paths))
            throw new ToolUsageException("Repository setup registration does not bind this exact repository and checkout.");
        var template = TrustedConfiguration.LoadSelected(registration.Template.Path, original, registration.Template);
        RequireRepository(template, repository, repositoryRoot);
        SeparateRegistration(template, registration.Template.Path, path, original);
        original.Check();
        return new(template, registration.Template, new(path, bytes.LongLength, BoundedFiles.Sha(bytes)));
    }
}
