using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Persistence tests for <see cref="DecisionObservation"/>: round-trip of all fields,
/// and independent concurrent inserts by ExecutionId/Site.
/// </summary>
public class DecisionObservationPersistenceTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    private WorkflowEngineDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options);

    [Fact]
    public async Task RoundTrip_survives_every_field()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var id = Guid.NewGuid();

        var probabilitiesObj = new Dictionary<string, double>
        {
            { "option_a", 0.6 },
            { "option_b", 0.4 }
        };
        var probabilitiesJson = JsonSerializer.Serialize(probabilitiesObj);

        var outcomeObj = new Dictionary<string, object>
        {
            { "action", "approved" },
            { "timestamp", DateTime.UtcNow }
        };
        var downstreamOutcomeJson = JsonSerializer.Serialize(outcomeObj);

        var originalCreatedAt = new DateTime(2026, 1, 15, 10, 30, 45, DateTimeKind.Utc);

        var observation = new DecisionObservation
        {
            Id = id,
            ExecutionId = executionId,
            ProjectId = projectId,
            OrganizationId = organizationId,
            StepOrder = 3,
            AgentType = AgentType.Colorist,
            Site = "video-edit-decision",
            Question = "Should we apply color grading?",
            OptionsHash = "abc123def456",
            Kind = DecisionKind.Choice,
            Answer = "yes",
            Confidence = 0.95,
            ProbabilitiesJson = probabilitiesJson,
            Mode = "greedy",
            Escalated = false,
            DownstreamOutcomeJson = downstreamOutcomeJson,
            ProviderId = providerId,
            Model = "gpt-4-turbo",
            CreatedAt = originalCreatedAt
        };

        using (var db = NewDb())
        {
            // Act: Insert
            db.DecisionObservations.Add(observation);
            await db.SaveChangesAsync();
        }

        // Assert: Reload in new context and verify all fields
        using (var db = NewDb())
        {
            var reloaded = await db.DecisionObservations.FirstOrDefaultAsync(o => o.Id == id);

            reloaded.Should().NotBeNull();
            reloaded!.Id.Should().Be(id);
            reloaded.ExecutionId.Should().Be(executionId);
            reloaded.ProjectId.Should().Be(projectId);
            reloaded.OrganizationId.Should().Be(organizationId);
            reloaded.StepOrder.Should().Be(3);
            reloaded.AgentType.Should().Be(AgentType.Colorist);
            reloaded.Site.Should().Be("video-edit-decision");
            reloaded.Question.Should().Be("Should we apply color grading?");
            reloaded.OptionsHash.Should().Be("abc123def456");
            reloaded.Kind.Should().Be(DecisionKind.Choice);
            reloaded.Answer.Should().Be("yes");
            reloaded.Confidence.Should().Be(0.95);
            reloaded.Mode.Should().Be("greedy");
            reloaded.Escalated.Should().BeFalse();
            reloaded.ProviderId.Should().Be(providerId);
            reloaded.Model.Should().Be("gpt-4-turbo");
            reloaded.CreatedAt.Should().Be(originalCreatedAt);

            // Verify ProbabilitiesJson round-trips valid JSON
            var reloadedProbabilities = JsonSerializer.Deserialize<Dictionary<string, double>>(reloaded.ProbabilitiesJson);
            reloadedProbabilities.Should().NotBeNull();
            reloadedProbabilities!["option_a"].Should().Be(0.6);
            reloadedProbabilities["option_b"].Should().Be(0.4);

            // Verify DownstreamOutcomeJson round-trips valid JSON
            var reloadedOutcome = JsonSerializer.Deserialize<Dictionary<string, object>>(reloaded.DownstreamOutcomeJson!);
            reloadedOutcome.Should().NotBeNull();
            reloadedOutcome!["action"].ToString().Should().Be("approved");
        }
    }

    [Fact]
    public async Task Independent_concurrent_inserts_by_ExecutionId_and_Site()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var siteA = "site-a";
        var siteB = "site-b";

        var observationA = new DecisionObservation
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            StepOrder = 1,
            AgentType = AgentType.Colorist,
            Site = siteA,
            Question = "Question A",
            OptionsHash = "hash_a",
            Kind = DecisionKind.Choice,
            Answer = "answer_a",
            Confidence = 0.8,
            ProbabilitiesJson = "{\"a\":0.8,\"b\":0.2}",
            Mode = "sampling",
            Escalated = false,
            DownstreamOutcomeJson = null,
            ProviderId = null,
            Model = "model-v1"
        };

        var observationB = new DecisionObservation
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            StepOrder = 2,
            AgentType = AgentType.MusicSupervisor,
            Site = siteB,
            Question = "Question B",
            OptionsHash = "hash_b",
            Kind = DecisionKind.Score,
            Answer = "7.5",
            Confidence = 0.92,
            ProbabilitiesJson = "{\"score\":7.5}",
            Mode = "greedy",
            Escalated = true,
            DownstreamOutcomeJson = null,
            ProviderId = null,
            Model = "model-v2"
        };

        using (var db = NewDb())
        {
            // Act: Insert both observations with same ExecutionId but different Sites
            db.DecisionObservations.Add(observationA);
            db.DecisionObservations.Add(observationB);
            await db.SaveChangesAsync();
        }

        // Assert: Both are persisted independently
        using (var db = NewDb())
        {
            var allObservations = await db.DecisionObservations
                .Where(o => o.ExecutionId == executionId)
                .OrderBy(o => o.StepOrder)
                .ToListAsync();

            allObservations.Should().HaveCount(2);

            var reloadedA = allObservations[0];
            reloadedA.Id.Should().Be(observationA.Id);
            reloadedA.Site.Should().Be(siteA);
            reloadedA.Question.Should().Be("Question A");
            reloadedA.AgentType.Should().Be(AgentType.Colorist);
            reloadedA.Kind.Should().Be(DecisionKind.Choice);
            reloadedA.Escalated.Should().BeFalse();

            var reloadedB = allObservations[1];
            reloadedB.Id.Should().Be(observationB.Id);
            reloadedB.Site.Should().Be(siteB);
            reloadedB.Question.Should().Be("Question B");
            reloadedB.AgentType.Should().Be(AgentType.MusicSupervisor);
            reloadedB.Kind.Should().Be(DecisionKind.Score);
            reloadedB.Escalated.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Token_fields_round_trip_exactly()
    {
        // Arrange
        var id = Guid.NewGuid();

        var observation = new DecisionObservation
        {
            Id = id,
            ExecutionId = Guid.NewGuid(),
            StepOrder = 1,
            AgentType = AgentType.Colorist,
            Site = "color-grade",
            Question = "Which look?",
            OptionsHash = "hash_tokens",
            Kind = DecisionKind.Choice,
            Answer = "Warm",
            Confidence = 0.91,
            ProbabilitiesJson = "{\"Warm\":0.91,\"Cool\":0.09}",
            Mode = "greedy",
            Escalated = false,
            ProviderId = Guid.NewGuid(),
            Model = "decision-model-v1",
            DecisionInputTokens = 412,
            DecisionOutputTokens = 17,
            AgentTokensUsed = 5310
        };

        using (var db = NewDb())
        {
            // Act: Insert
            db.DecisionObservations.Add(observation);
            await db.SaveChangesAsync();
        }

        // Assert: Reload in a fresh context and verify the three token fields exactly
        using (var db = NewDb())
        {
            var reloaded = await db.DecisionObservations.FirstOrDefaultAsync(o => o.Id == id);

            reloaded.Should().NotBeNull();
            reloaded!.DecisionInputTokens.Should().Be(412);
            reloaded.DecisionOutputTokens.Should().Be(17);
            reloaded.AgentTokensUsed.Should().Be(5310);
        }
    }

    [Fact]
    public async Task Token_fields_null_still_load_preserving_nulls()
    {
        // Arrange: the historic-row shape — the columns did not exist when the row was written,
        // so all three are NULL ("not recorded"), never zero.
        var id = Guid.NewGuid();

        var observation = new DecisionObservation
        {
            Id = id,
            ExecutionId = Guid.NewGuid(),
            StepOrder = 2,
            AgentType = AgentType.MusicSupervisor,
            Site = "background-music",
            Question = "Which track?",
            OptionsHash = "hash_historic",
            Kind = DecisionKind.Noul,
            Answer = "no",
            Confidence = null,
            ProbabilitiesJson = "{}",
            Mode = "logprob",
            Escalated = true,
            ProviderId = null,
            Model = "decision-model-v0",
            DecisionInputTokens = null,
            DecisionOutputTokens = null,
            AgentTokensUsed = null
        };

        using (var db = NewDb())
        {
            // Act: Insert
            db.DecisionObservations.Add(observation);
            await db.SaveChangesAsync();
        }

        // Assert: the historic row still loads, with the nulls preserved as nulls
        using (var db = NewDb())
        {
            var reloaded = await db.DecisionObservations.FirstOrDefaultAsync(o => o.Id == id);

            reloaded.Should().NotBeNull();
            reloaded!.Site.Should().Be("background-music");
            reloaded.DecisionInputTokens.Should().BeNull();
            reloaded.DecisionOutputTokens.Should().BeNull();
            reloaded.AgentTokensUsed.Should().BeNull();
        }
    }
}
