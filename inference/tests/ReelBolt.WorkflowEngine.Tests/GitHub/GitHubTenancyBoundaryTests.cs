#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// The GitHub tables and the tenancy boundary, stated rather than left to be inferred from the absence
/// of a query filter.
///
/// <para>
/// None of <c>project_github_links</c>, <c>github_app_installations</c> or
/// <c>github_webhook_deliveries</c> carries the EF tenant filter, and each for a reason that would be
/// broken by adding one:
/// </para>
///
/// <list type="bullet">
/// <item><b>project_github_links</b> is a child of <c>projects</c>. It is reached either through the
/// project access check every <c>/api/v1/projects/{projectId}/github</c> action performs, or by the
/// webhook under an explicit system scope from a signature-verified repository id — never from a
/// caller-supplied id of its own.</item>
/// <item><b>github_app_installations</b> MUST be visible across organizations, because seeing another
/// organization's claim is how a link under someone else's installation is refused. A filter here
/// would hide the existing claim, let a second organization insert a row for the same installation,
/// and turn a clean 404 into a unique-index violation.</item>
/// <item><b>github_webhook_deliveries</b> is written before the payload's owner is known, and a
/// delivery for a repository nobody linked still has to be recorded so its redelivery is a no-op.</item>
/// </list>
/// </summary>
public class GitHubTenancyBoundaryTests
{
    private static IReadOnlyList<Type> TenantFilteredEntityTypes()
    {
        using InferenceApiDbContext db = GitHubTestDb.New(new TestTenant());

        return db.Model.GetEntityTypes()
            .Where(e => e.GetDeclaredQueryFilters().Any(filter => filter.Key == InferenceApiDbContext.TenantFilterName))
            .Select(e => e.ClrType)
            .ToList();
    }

    [Theory]
    [InlineData(typeof(ProjectGitHubLink))]
    [InlineData(typeof(GitHubAppInstallation))]
    [InlineData(typeof(GitHubWebhookDelivery))]
    public void No_github_table_carries_a_tenant_query_filter(Type type)
    {
        TenantFilteredEntityTypes().Should().NotContain(type,
            "each of these is guarded somewhere else, and each is documented in this file's summary; "
            + "a filter added by accident would silently break the webhook or the installation claim");
    }

    /// <summary>
    /// The mechanism, not the inventory: an installation claim made by one organization is visible to
    /// another, which is exactly what makes the refusal possible. If this ever stops being true, the
    /// cross-organization refusal in <c>GitHubLinkServiceTests</c> would become an accidental 500.
    /// </summary>
    [Fact]
    public async Task An_installation_claim_is_visible_from_another_organization()
    {
        TestTenant tenant = new();
        using InferenceApiDbContext db = GitHubTestDb.New(tenant);

        Guid claimingOrganization = Guid.NewGuid();
        db.GitHubAppInstallations.Add(new GitHubAppInstallation
        {
            Id = Guid.NewGuid(),
            InstallationId = GitHubTestData.InstallationId,
            OrganizationId = claimingOrganization,
            AccountLogin = "acme",
            ClaimedByUserId = Guid.NewGuid(),
            ClaimedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // Another organization's request scope.
        tenant.BeginOrganizationScope(Guid.NewGuid());

        GitHubAppInstallation? claim = await db.GitHubAppInstallations
            .FirstOrDefaultAsync(i => i.InstallationId == GitHubTestData.InstallationId);

        claim.Should().NotBeNull("hiding it would let a second organization claim the same installation");
        claim!.OrganizationId.Should().Be(claimingOrganization);
    }

    /// <summary>
    /// A link is resolved by GitHub's numeric repository id and nothing else. A payload cannot name a
    /// project, so the only way a delivery reaches a project is the link its repository created.
    /// </summary>
    [Fact]
    public async Task A_link_is_resolved_by_repository_id_only()
    {
        TestTenant tenant = new();
        using InferenceApiDbContext db = GitHubTestDb.New(tenant);

        Guid organizationId = Guid.NewGuid();
        Project project = GitHubTestData.NewProject(organizationId, Guid.NewGuid());
        db.Projects.Add(project);
        db.ProjectGitHubLinks.Add(GitHubTestData.NewLink(project.Id, organizationId, Guid.NewGuid()));
        await db.SaveChangesAsync();

        (await db.ProjectGitHubLinks.FirstOrDefaultAsync(l => l.RepositoryId == GitHubTestData.RepositoryId))
            .Should().NotBeNull();
        (await db.ProjectGitHubLinks.FirstOrDefaultAsync(l => l.RepositoryId == GitHubTestData.RepositoryId + 1))
            .Should().BeNull("a repository that is not linked resolves to no project at all");
    }
}
