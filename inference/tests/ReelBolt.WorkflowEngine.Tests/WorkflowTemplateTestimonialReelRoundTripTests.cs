using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A testimonial-reel workflow provisioned from the catalog, saved to the database and reloaded keeps
/// the shape the shape test asserts: the same steps, orders and decision/analysis/overlay-plan refs.
/// Pins the provisioning copy, not just the template definition.
/// </summary>
public class WorkflowTemplateTestimonialReelRoundTripTests
{
    private static JsonElement Json(string? raw) => JsonDocument.Parse(raw!).RootElement;

    private static InferenceApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task A_testimonial_workflow_saved_and_reloaded_keeps_the_same_shape()
    {
        using InferenceApiDbContext db = CreateContext();

        WorkflowTemplateDefinition template =
            WorkflowTemplateCatalog.GetByKey(ReelBolt.Shared.Workflows.Apps.VideoAppTemplates.EditMyVideoKey)!;
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

        WorkflowDefinition? created = await service.CreateFromTemplateAsync(
            project.Id, WorkflowTemplateCatalog.TestimonialReelTemplateKey, skipIfExists: false, CancellationToken.None);

        created.Should().NotBeNull();
        WorkflowDefinition reloaded = await db.WorkflowDefinitions
            .Include(w => w.Steps).ThenInclude(s => s.AgentDefinition)
            .SingleAsync(w => w.Id == created!.Id);

        // The old testimonial key opens "Edit my video" (the key is a hidden alias); the steps are the app's own.
        reloaded.TemplateKey.Should().Be(template.Key);
        reloaded.Steps.Should().HaveCount(template.Steps.Count);
        reloaded.Steps.Select(s => s.StepOrder).Should().Equal(Enumerable.Range(1, template.Steps.Count));
        reloaded.Steps.OrderBy(s => s.StepOrder).Select(s => s.StepType).Should()
            .Equal(template.Steps.Select(s => s.StepType));
        reloaded.Steps.OrderBy(s => s.StepOrder).Select(s => s.AgentDefinition.AgentType).Should()
            .Equal(template.Steps.Select(s => s.AgentType));

        WorkflowStep[] ordered = [.. reloaded.Steps.OrderBy(s => s.StepOrder)];
        JsonElement analyze = Json(ordered[0].VideoAnalyzeConfigJson);
        analyze.GetProperty("analyzeEvents").GetBoolean().Should().BeTrue();
        analyze.GetProperty("outline").GetBoolean().Should().BeTrue();

        WorkflowStep review = ordered[^1];
        review.StepType.Should().Be(StepType.ReviewLoop);
        review.LoopTargetStepOrder.Should().Be(2, "the review goes back to the story editor by explicit order");
        JsonElement compile = Json(ordered[^2].VideoCompileConfigJson);
        compile.GetProperty("decision").GetProperty("stepOrder").GetInt32().Should().Be(2);
        compile.GetProperty("analysisStepOrder").GetInt32().Should().Be(1);
        review.MaxIterations.Should().Be(3);
        review.MinScore.Should().Be(8);
        // Video events on by default (#1092): the motion director plans before the compile reads its plan.
        int motion = Array.FindIndex(ordered, s => s.AgentDefinition.AgentType == AgentType.MotionDirector);
        motion.Should().BeGreaterThan(0).And.BeLessThan(ordered.Length - 2, "the motion plan is made before the compile");

        AppManifestJson.TryParse(reloaded.AppManifestJson).Should().NotBeNull("the curated manifest is copied onto the workflow");
    }
}
