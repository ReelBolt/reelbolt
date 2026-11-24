using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.ExecutionResumeTests;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// The resume's persistence on the real schema (both contexts migrated): the intent column is consumed, the
/// reused rows are left byte-for-byte as their dispatch wrote them, only live steps hold credits, the written
/// order survives timestamptz precision, and a retry's superseded rows are kept but never matched.
/// </summary>
[Collection("Postgres")]
public class ExecutionResumePostgresTests
{
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_a_resume_reuses_untouched_rows_charges_only_live_steps_and_a_retry_keeps_superseded_rows()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_resume_test_" + Guid.NewGuid().ToString("N")[..12];
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
            WorkflowEngineDbContext NewDb() => new(options);

            Guid orgId = Guid.NewGuid();
            await using (NpgsqlConnection c = new(connection))
            {
                await c.OpenAsync();
                await using NpgsqlCommand org = new(
                    $"INSERT INTO organizations (id,name,slug,kind,created_at,updated_at,compute_preference) VALUES ('{orgId}','o','o-{orgId:N}','Team',now(),now(),'Auto')", c);
                await org.ExecuteNonQueryAsync();
            }

            // Seeded through the API context: it owns (and maps the jsonb columns of) agents, projects and users.
            Seeded s;
            await using (InferenceApiDbContext seed = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options))
                s = SeedIn(seed, orgId, new StepSpec(StepType.Agent, "S1"), new StepSpec(StepType.Agent, "S2"), new StepSpec(StepType.Agent, "S3"));
            Scopes scopes = new(NewDb);

            RecordingGate firstGate = new();
            Func<Task> first = () => Service(scopes, firstGate, null, null, new Executor(StepType.Agent, c =>
                c.Step.StepOrder == 3
                    ? throw new InvalidOperationException("lost")
                    : Task.FromResult(Done(c, "{\"from\":\"" + c.Step.Label + "\"}")))).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
            await first.Should().ThrowAsync<InvalidOperationException>();

            List<WorkflowStepResult> before;
            DateTime? startedAt;
            await using (WorkflowEngineDbContext db = NewDb())
            {
                before = await db.WorkflowStepResults.AsNoTracking().Where(r => r.WorkflowExecutionId == s.ExecutionId).ToListAsync();
                startedAt = (await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId)).StartedAt;
            }
            before.Should().HaveCount(3);
            before.Where(r => r.Status == StepStatus.Completed).Should().OnlyContain(r => r.CreditsChargedMillis == 7, "the first dispatch settled them");

            await RequeueAsync(NewDb, s.ExecutionId, resume: true);
            await using (WorkflowEngineDbContext db = NewDb())
                (await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId)).PendingResume.Should().BeTrue();

            RecordingGate gate = new();
            List<(string? Label, string Accumulated)> ran = [];
            await Service(scopes, gate, null, null, new Executor(StepType.Agent, c =>
            {
                ran.Add((c.Step.Label, c.AccumulatedOutput));
                return Task.FromResult(Done(c, "{\"from\":\"" + c.Step.Label + "\"}"));
            })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

            ran.Should().Equal([("S3", "{\"from\":\"S2\"}")]);
            await using (WorkflowEngineDbContext db = NewDb())
            {
                WorkflowExecution run = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId);
                run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
                run.PendingResume.Should().BeFalse("consumed in the Running save");
                run.ResumeCount.Should().Be(1, "the engine never counts resumes; the route does");
                run.StartedAt.Should().Be(startedAt, "a resume keeps the run's StartedAt");

                List<WorkflowStepResult> after = await db.WorkflowStepResults.AsNoTracking().Where(r => r.WorkflowExecutionId == s.ExecutionId).ToListAsync();
                after.Should().HaveCount(4);
                foreach (WorkflowStepResult original in before.Where(r => r.Status == StepStatus.Completed))
                {
                    WorkflowStepResult now = after.Single(r => r.Id == original.Id);
                    now.Should().BeEquivalentTo(original, o => o.Excluding(r => r.WorkflowExecution).Excluding(r => r.WorkflowStep),
                        "a reused row is read, never written");
                }

                // The failed S3 row is the one the live run replaced: superseded, otherwise left as it was written.
                WorkflowStepResult failedBefore = before.Single(r => r.Status == StepStatus.Failed);
                WorkflowStepResult failedNow = after.Single(r => r.Id == failedBefore.Id);
                failedNow.SupersededAt.Should().NotBeNull();
                failedNow.Should().BeEquivalentTo(failedBefore, o => o.Excluding(r => r.WorkflowExecution).Excluding(r => r.WorkflowStep).Excluding(r => r.SupersededAt));

                Guid live = after.Single(r => before.All(b => b.Id != r.Id)).Id;
                gate.Reserved.Should().Equal([live]);
                gate.Settled.Should().Equal([live]);
            }

            // Retry (Retry): every row stamped superseded, no intent: the run starts at step 1 and keeps every row.
            await RequeueAsync(NewDb, s.ExecutionId, resume: false, supersede: true);
            List<string?> retried = [];
            await Service(scopes, null, null, null, new Executor(StepType.Agent, c =>
            {
                retried.Add(c.Step.Label);
                return Task.FromResult(Done(c, "{}"));
            })).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

            retried.Should().Equal(["S1", "S2", "S3"]);
            await using (WorkflowEngineDbContext db = NewDb())
            {
                List<WorkflowStepResult> all = await db.WorkflowStepResults.AsNoTracking().Where(r => r.WorkflowExecutionId == s.ExecutionId).ToListAsync();
                all.Should().HaveCount(7);
                all.Count(r => r.SupersededAt != null).Should().Be(4);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
