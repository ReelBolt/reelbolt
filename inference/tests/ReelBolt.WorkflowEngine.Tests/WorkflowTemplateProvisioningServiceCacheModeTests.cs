using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Confirms <see cref="WorkflowTemplateProvisioningService"/> actually copies
/// <see cref="WorkflowTemplateStepDefinition.CacheMode"/> onto the materialized
/// <see cref="WorkflowStep.CacheMode"/> entity property when a built-in template is applied to a
/// project — the mapping added alongside <see cref="WorkflowTemplateCatalogCacheModePolicyTests"/>,
/// which only checks the template DEFINITIONS, not that provisioning actually respects them.
/// </summary>
public class WorkflowTemplateProvisioningServiceCacheModeTests
{
    private static InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    [Fact]
    public async Task Applying_quick_win_promo_propagates_each_step_definitions_CacheMode_onto_the_entity()
    {
        using InferenceApiDbContext db = CreateContext();

        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.StarterTemplateKey)!;
        foreach (AgentType agentType in template.Steps.Select(s => s.AgentType).Distinct())
        {
            db.AgentDefinitions.Add(new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = agentType.ToString(),
                Description = "d",
                SystemPrompt = "p",
                AgentType = agentType,
                IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow,
            });
        }
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        WorkflowTemplateProvisioningService service = new(
            db,
            NullLogger<WorkflowTemplateProvisioningService>.Instance,
            new ConfigurationBuilder().Build());

        WorkflowDefinition? workflow = await service.CreateFromTemplateAsync(
            project.Id, WorkflowTemplateCatalog.StarterTemplateKey, skipIfExists: false, CancellationToken.None);

        workflow.Should().NotBeNull();
        WorkflowDefinition reloaded = await db.WorkflowDefinitions
            .Include(w => w.Steps)
            .SingleAsync(w => w.Id == workflow!.Id);

        reloaded.Steps.OrderBy(s => s.StepOrder).Should().HaveSameCount(template.Steps);

        foreach ((WorkflowStep entity, WorkflowTemplateStepDefinition definition) in reloaded.Steps.OrderBy(s => s.StepOrder).Zip(template.Steps))
        {
            entity.CacheMode.Should().Be(definition.CacheMode, $"step '{definition.Label}' must carry the template's CacheMode onto the entity");
        }

        // Spot-check the two policy extremes so this test fails loudly if the wiring silently
        // regresses back to "always null" (the original bug) rather than only via the generic loop.
        reloaded.Steps.Single(s => s.Label == "Assemble and render master composition").CacheMode.Should().Be(StepCacheMode.Never);
        reloaded.Steps.Single(s => s.Label == "Analyze code structure").CacheMode.Should().BeNull();
    }
}
