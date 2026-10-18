using DeliveryForge.Coordination;
namespace DeliveryForge.Coordination.Tests;

public sealed class ParentResourceTests
{
    [Fact]
    public void Canonical_parent_resources_are_distinct_bounded_and_preserve_exact_branch_and_workspace_identity()
    {
        var issue = CoordinationResource.GitHubIssue("fixture", "Owner", "Repo", 17);
        var branch = CoordinationResource.GitHubBranch("fixture", "Owner", "Repo", "refs/heads/Main");
        var workspace = CoordinationResource.GitHubWorkspace("fixture", "Owner", "Repo", "C:/OWNED/WORKTREE");
        var footprint = CoordinationResource.GitHubRepositoryWrite("fixture", "Owner", "Repo");
        Assert.Equal(4, new[] { issue, branch, workspace, footprint }.Distinct().Count());
        Assert.NotEqual(branch, CoordinationResource.GitHubBranch("fixture", "owner", "repo", "refs/heads/main"));
        Assert.NotEqual(workspace, CoordinationResource.GitHubWorkspace("fixture", "owner", "repo", "C:/OWNED/OTHER"));
        Assert.Equal(footprint, CoordinationResource.GitHubRepositoryWrite("fixture", "owner", "repo"));
        Assert.All(new[] { issue, branch, workspace, footprint }, resource => Assert.Equal(resource, new CoordinationResource(resource.Scope, resource.ResourceKey)));
    }
    [Fact]
    public void Unsupported_parent_resource_aliases_and_noncanonical_hashes_refuse()
    {
        Assert.Throws<ArgumentException>(() => new CoordinationResource("fixture", "github:owner/..:repository-write"));
        Assert.Throws<ArgumentException>(() => new CoordinationResource("fixture", "github:owner/repo:branch-sha256:" + new string('A', 64)));
        Assert.Throws<ArgumentException>(() => new CoordinationResource("fixture", "github:owner/repo:workspace-sha256:short"));
        Assert.Throws<ArgumentException>(() => CoordinationResource.GitHubBranch("fixture", "owner", "repo", " refs/heads/main"));
    }
}
