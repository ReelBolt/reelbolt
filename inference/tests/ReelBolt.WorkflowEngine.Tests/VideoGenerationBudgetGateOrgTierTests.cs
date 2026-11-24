using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>A12: the organization's plan tier on the video-generation budget gate.</summary>
public class VideoGenerationBudgetGateOrgTierTests
{
    private static VideoGenerationBudgetGate NewGate(SharedInMemoryDbFactory factory, bool videoGenAllowed)
        => new(
            factory,
            Options.Create(new VideoGenerationOptions { DailyBudgetCredits = 1000m, GlobalDailyBudgetCredits = 1000m }),
            new ConfigOrganizationEntitlements(new TenancyOptions
            {
                DefaultEntitlements = new OrganizationEntitlementSet { VideoGenAllowed = videoGenAllowed }
            }));

    private static async Task SeedAsync(SharedInMemoryDbFactory factory, Guid projectId, Guid orgId, decimal spentUsd)
    {
        await using var db = factory.CreateDbContext();
        db.Projects.Add(new Project { Id = projectId, Name = "p", OrganizationId = orgId });
        if (spentUsd > 0m)
        {
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                StepResultId = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(), // a different project of the same org
                OrganizationId = orgId,
                ClipId = "c",
                RequestHash = Guid.NewGuid().ToString(),
                ProviderId = Guid.NewGuid(),
                Status = ExternalGenerationJobStatus.Succeeded,
                CostEstimate = spentUsd,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    private static VideoGenerationReservationRequest Request(Guid projectId, decimal cost)
        => new(projectId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m,
            new[] { new VideoGenerationTakeRequest("clip-1", "hash-1", cost) });

    [Fact]
    public async Task Other_orgs_spend_does_not_count_and_the_row_carries_the_org()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        await SeedAsync(factory, projectId, orgId, spentUsd: 0m);
        await SeedAsync(factory, Guid.NewGuid(), Guid.NewGuid(), spentUsd: 5m);

        var result = await NewGate(factory, videoGenAllowed: true).ReserveAsync(Request(projectId, 0.20m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Any(j => j.ProjectId == projectId && j.OrganizationId == orgId).Should().BeTrue();
    }

    [Fact]
    public async Task A_plan_without_video_generation_is_PLAN_NOT_ENTITLED_and_writes_nothing()
    {
        var factory = new SharedInMemoryDbFactory();
        var projectId = Guid.NewGuid();
        await SeedAsync(factory, projectId, Guid.NewGuid(), spentUsd: 0m);

        var result = await NewGate(factory, videoGenAllowed: false).ReserveAsync(Request(projectId, 0.20m), CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("PLAN_NOT_ENTITLED:");
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Any().Should().BeFalse();
    }
}
