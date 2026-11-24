using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Compositions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>A run's latest film render is the current attempt's: a Retry's or a resume's replaced rows are audit only.</summary>
public class CompositionWorkspaceStoreSupersededTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_latest_render_of_a_run_ignores_superseded_rows(bool currentAttemptRendered)
    {
        ServiceCollection services = new();
        string database = Guid.NewGuid().ToString("N");
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(database));
        using ServiceProvider provider = services.BuildServiceProvider();

        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        string oldKey = $"projects/{projectId}/outputFiles/{executionId}/{Guid.NewGuid():N}/film.mp4";
        string newKey = $"projects/{projectId}/outputFiles/{executionId}/{Guid.NewGuid():N}/film.mp4";
        using (IServiceScope scope = provider.CreateScope())
        {
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            WorkflowStep author = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), StepType = StepType.Agent, StepOrder = 1 };
            db.WorkflowSteps.Add(author);
            DateTime now = DateTime.UtcNow;
            // The replaced attempt's render is the NEWEST row by time: it is what an unfiltered "latest" picks.
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = author.Id,
                Status = StepStatus.Completed,
                OutputStorageKey = oldKey,
                ExecutedAt = now,
                CompletedAt = now,
                SupersededAt = now.AddSeconds(1)
            });
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = author.Id,
                Status = currentAttemptRendered ? StepStatus.Completed : StepStatus.Running,
                OutputStorageKey = currentAttemptRendered ? newKey : null,
                ExecutedAt = now.AddMinutes(-1),
                CompletedAt = currentAttemptRendered ? now.AddMinutes(-1) : null
            });
            await db.SaveChangesAsync();
        }

        CompositionWorkspaceStore store = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IAmazonS3>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<CompositionWorkspaceStore>.Instance);

        string? latest = await store.FindLatestRenderKeyAsync(executionId, CancellationToken.None);

        latest.Should().Be(currentAttemptRendered ? newKey : null);
    }
}
