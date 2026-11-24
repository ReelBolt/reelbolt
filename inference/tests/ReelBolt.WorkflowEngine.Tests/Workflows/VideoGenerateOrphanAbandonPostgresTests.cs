using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.ExecutionResumeTests;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// T-015 on the real schema (both contexts migrated, the advisory lock and transaction live): a resumed run's
/// earlier-attempt orphan is stored as <c>'Abandoned'</c> (status is persisted by name) and KEEPS counting toward
/// the daily, global and step caps (it may have been paid at the vendor), the take gets a new Reserved row, a
/// denial in the same transaction abandons nothing, and a second abandon of the take is refused.
/// </summary>
[Collection("Postgres")]
public class VideoGenerateOrphanAbandonPostgresTests
{
    private sealed class Factory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_a_resume_abandons_the_earlier_attempts_orphan_releases_it_and_reserves_a_new_row()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_orphan_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
            await create.ExecuteNonQueryAsync();

        string connection = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
        try
        {
            await using (InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options))
                await api.GetService<IMigrator>().MigrateAsync();
            DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;
            await using (WorkflowEngineDbContext engine = new(options))
                await engine.GetService<IMigrator>().MigrateAsync();

            Guid orgId = Guid.NewGuid();
            await using (NpgsqlConnection c = new(connection))
            {
                await c.OpenAsync();
                await using NpgsqlCommand org = new(
                    $"INSERT INTO organizations (id,name,slug,kind,created_at,updated_at,compute_preference) VALUES ('{orgId}','o','o-{orgId:N}','Team',now(),now(),'Auto')", c);
                await org.ExecuteNonQueryAsync();
            }

            Seeded s;
            await using (InferenceApiDbContext seed = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options))
                s = SeedIn(seed, orgId, new StepSpec(StepType.VideoGenerate, "Clips"));

            decimal take = CreditCurrency.CreditsFromUsd(0.48m);
            Guid earlier = Guid.NewGuid();
            Guid current = Guid.NewGuid();
            Guid orphan = Guid.NewGuid();
            await using (WorkflowEngineDbContext db = new(options))
            {
                WorkflowExecution run = await db.WorkflowExecutions.SingleAsync(e => e.Id == s.ExecutionId);
                Guid stepId = await db.WorkflowSteps.Where(st => st.WorkflowDefinitionId == run.WorkflowDefinitionId).Select(st => st.Id).SingleAsync();
                run.Status = ExecutionStatus.Running;
                run.ResumeCount = 1;
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = earlier,
                    WorkflowExecutionId = s.ExecutionId,
                    WorkflowStepId = stepId,
                    Status = StepStatus.Failed,
                    ExecutedAt = DateTime.UtcNow.AddMinutes(-30)
                });
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = current,
                    WorkflowExecutionId = s.ExecutionId,
                    WorkflowStepId = stepId,
                    Status = StepStatus.Running
                });
                db.ExternalGenerationJobs.Add(new ExternalGenerationJob
                {
                    Id = orphan,
                    ExecutionId = s.ExecutionId,
                    StepResultId = earlier,
                    ProjectId = s.ProjectId,
                    OrganizationId = orgId,
                    ClipId = "clip-1",
                    RequestHash = "hash-1",
                    ProviderId = Guid.NewGuid(),
                    Status = ExternalGenerationJobStatus.Reserved,
                    CostEstimate = take,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-20),
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-20)
                });
                await db.SaveChangesAsync();
            }

            VideoGenerationBudgetGate GateWithDailyCaps(decimal cap) => new(new Factory(options),
                Options.Create(new VideoGenerationOptions { DailyBudgetCredits = cap, GlobalDailyBudgetCredits = cap }));
            VideoGenerationReservationRequest Request(decimal stepCap) =>
                new(s.ProjectId, s.ExecutionId, current, Guid.NewGuid(), stepCap, [new VideoGenerationTakeRequest("clip-1", "hash-1", take)]);

            // The orphan still counts: with caps of ONE take, its re-buy does not fit (daily, then step cap).
            VideoGenerationReservation deniedDaily = await GateWithDailyCaps(take).ReserveAsync(Request(100_000m), CancellationToken.None);
            deniedDaily.Allowed.Should().BeFalse();
            deniedDaily.DenyReason.Should().StartWith("BUDGET_EXCEEDED: project daily");
            deniedDaily.ProjectSpentToday.Should().Be(take);
            VideoGenerationReservation deniedStep = await GateWithDailyCaps(2 * take).ReserveAsync(Request(2 * take - 1m), CancellationToken.None);
            deniedStep.Allowed.Should().BeFalse();
            deniedStep.DenyReason.Should().StartWith("BUDGET_EXCEEDED: step");
            (await StatusOf(connection, orphan)).Should().Be("Reserved", "a denial rolls back the abandon with the rest");

            VideoGenerationBudgetGate gate = GateWithDailyCaps(2 * take);
            VideoGenerationReservation reservation = await gate.ReserveAsync(Request(2 * take), CancellationToken.None);

            reservation.Allowed.Should().BeTrue(reservation.DenyReason);
            reservation.ProjectSpentToday.Should().Be(take, "the orphan is counted as possibly spent");
            reservation.GlobalSpentToday.Should().Be(take);
            ReservedTake fresh = reservation.Takes.Should().ContainSingle().Subject;
            fresh.Existing.Should().BeFalse();
            fresh.JobId.Should().NotBe(orphan);
            (await StatusOf(connection, orphan)).Should().Be("Abandoned");
            (await StatusOf(connection, fresh.JobId)).Should().Be("Reserved");

            await using (WorkflowEngineDbContext db = new(options))
            {
                ExternalGenerationJob row = await db.ExternalGenerationJobs.AsNoTracking().SingleAsync(j => j.Id == fresh.JobId);
                row.StepResultId.Should().Be(current);
                (await db.ExternalGenerationJobs.AsNoTracking().SingleAsync(j => j.Id == orphan)).ActualCost.Should().BeNull();

                // The re-buy is orphaned too (a later attempt fails before recording a remote id) and the run is
                // resumed again: the default MaxAbandonsPerTake (1) is used up, so it stays existing, unbought.
                Guid third = Guid.NewGuid();
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = third,
                    WorkflowExecutionId = s.ExecutionId,
                    WorkflowStepId = (await db.WorkflowStepResults.AsNoTracking().SingleAsync(r => r.Id == current)).WorkflowStepId,
                    Status = StepStatus.Running
                });
                WorkflowStepResult second = await db.WorkflowStepResults.SingleAsync(r => r.Id == current);
                second.Status = StepStatus.Failed;
                await db.SaveChangesAsync();

                VideoGenerationReservation again = await gate.ReserveAsync(
                    new VideoGenerationReservationRequest(s.ProjectId, s.ExecutionId, third, Guid.NewGuid(), 100_000m,
                        [new VideoGenerationTakeRequest("clip-1", "hash-1", take)]),
                    CancellationToken.None);
                again.Allowed.Should().BeTrue(again.DenyReason);
                again.Takes.Single().Existing.Should().BeTrue();
                again.Takes.Single().JobId.Should().Be(fresh.JobId);
                (await StatusOf(connection, fresh.JobId)).Should().Be("Reserved");
                (await db.ExternalGenerationJobs.AsNoTracking().CountAsync(j => j.ExecutionId == s.ExecutionId)).Should().Be(2);

                // A zombie attempt that read the row as Reserved cannot overwrite it once a newer attempt abandoned it:
                // the status is a concurrency token, so the UPDATE is conditional on the status it read.
                await using WorkflowEngineDbContext zombie = new(options);
                ExternalGenerationJob stale = await zombie.ExternalGenerationJobs.SingleAsync(j => j.Id == fresh.JobId);
                await using (WorkflowEngineDbContext newer = new(options))
                {
                    ExternalGenerationJob row2 = await newer.ExternalGenerationJobs.SingleAsync(j => j.Id == fresh.JobId);
                    row2.Status = ExternalGenerationJobStatus.Abandoned;
                    await newer.SaveChangesAsync();
                }

                stale.Status = ExternalGenerationJobStatus.Failed;
                Func<Task> flip = () => zombie.SaveChangesAsync();
                await flip.Should().ThrowAsync<DbUpdateConcurrencyException>();
                (await StatusOf(connection, fresh.JobId)).Should().Be("Abandoned");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string?> StatusOf(string connection, Guid jobId)
    {
        await using NpgsqlConnection c = new(connection);
        await c.OpenAsync();
        await using NpgsqlCommand cmd = new("SELECT status FROM external_generation_jobs WHERE id = @id", c);
        cmd.Parameters.AddWithValue("id", jobId);
        return (string?)await cmd.ExecuteScalarAsync();
    }
}
