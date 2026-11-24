#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The sweep's queries must translate and run on PostgreSQL, not only on the in-memory provider: they use
/// a per-organization aggregate, prefix matches built from the project id, and two anti-joins. Runs on a
/// throwaway server (see <see cref="PostgresTestEnvironment"/>); a database of its own is created and dropped.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class OutputModerationSweepPostgresTests
{
    private sealed class SystemTenant : ITenantContext
    {
        public Guid? OrganizationId => null;

        public bool IsSystem => true;

        public IDisposable BeginSystemScope() => new Noop();

        public IDisposable BeginOrganizationScope(Guid organizationId) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    [PostgresFact]
    public async Task The_sweep_queries_translate_and_run_on_postgres_and_never_offer_a_decided_output()
    {
        string baseCs = PostgresTestEnvironment.BaseConnectionString()!;
        string name = "mbsweep_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseCs);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
            await create.ExecuteNonQueryAsync();

        try
        {
            string cs = PostgresTestEnvironment.ForDatabase(baseCs, name);
            DbContextOptions<InferenceApiDbContext> apiOptions = new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(cs).Options;
            await using (InferenceApiDbContext migrate = new(apiOptions, new SystemTenant()))
                await migrate.Database.MigrateAsync();
            await using (ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext engine = new(
                new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>()
                    .UseNpgsql(cs, n => n.MigrationsHistoryTable("__EFMigrationsHistory_Workflow")).Options))
                await engine.Database.MigrateAsync();

            ServiceCollection services = new();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(cs));
            services.AddSingleton<ITenantContext>(new SystemTenant());
            ChannelBackgroundTaskQueue<OutputModerationTask> queue = new(100);
            ServiceProvider sp = services.BuildServiceProvider();
            OutputModerationWorker worker = new(
                queue, sp.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ContentModerationOptions { Enabled = true }), NullLogger<OutputModerationWorker>.Instance);

            // Empty tables: every query still has to translate and execute.
            (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
            worker.LastSweepFoundNothing.Should().BeTrue("an idle sweep with nothing to do backs off");

            // Two outputs of one organization: one never judged, one with a decided verdict.
            Guid org = Guid.NewGuid(), project = Guid.NewGuid(), workflow = Guid.NewGuid(), step = Guid.NewGuid(), execution = Guid.NewGuid();
            string fresh = $"projects/{project}/outputFiles/fresh.mp4", decided = $"projects/{project}/outputFiles/decided.mp4";
            await using (InferenceApiDbContext seed = new(apiOptions, new SystemTenant()))
            {
                seed.Organizations.Add(new ReelBolt.Shared.Data.Models.Organization
                {
                    Id = org,
                    Name = "o",
                    Slug = "o-" + org.ToString("N")[..8],
                    Kind = ReelBolt.Shared.Tenancy.OrganizationKind.Team
                });
                Guid owner = Guid.NewGuid();
                seed.ApplicationUsers.Add(new ReelBolt.Shared.Data.Models.ApplicationUser
                {
                    Id = owner,
                    Email = $"{owner:N}@example.com",
                    DisplayName = "o",
                    PasswordHash = "h"
                });
                seed.Projects.Add(new ReelBolt.Shared.Data.Models.Project { Id = project, Name = "p", OrganizationId = org, OwnerId = owner });
                seed.WorkflowDefinitions.Add(new ReelBolt.Shared.Data.Models.WorkflowDefinition { Id = workflow, ProjectId = project, Name = "wf" });
                Guid agent = Guid.NewGuid();
                seed.AgentDefinitions.Add(new ReelBolt.Shared.Data.Models.AgentDefinition
                {
                    Id = agent,
                    Name = "A",
                    AgentType = ReelBolt.Shared.Data.Models.AgentType.VideoTransform,
                    IsBuiltIn = true,
                    SystemPrompt = string.Empty
                });
                seed.WorkflowSteps.Add(new ReelBolt.Shared.Data.Models.WorkflowStep { Id = step, WorkflowDefinitionId = workflow, AgentDefinitionId = agent, StepOrder = 1 });
                seed.WorkflowExecutions.Add(new ReelBolt.Shared.Data.Models.WorkflowExecution
                {
                    Id = execution,
                    WorkflowDefinitionId = workflow,
                    ProjectId = project,
                    OrganizationId = org,
                    Status = ReelBolt.Shared.Data.Models.ExecutionStatus.Passed
                });
                foreach (string key in new[] { fresh, decided })
                {
                    seed.WorkflowStepResults.Add(new ReelBolt.Shared.Data.Models.WorkflowStepResult
                    {
                        Id = Guid.NewGuid(),
                        WorkflowExecutionId = execution,
                        WorkflowStepId = step,
                        Status = ReelBolt.Shared.Data.Models.StepStatus.Completed,
                        OutputStorageKey = key,
                        ExecutedAt = DateTime.UtcNow,
                        CompletedAt = DateTime.UtcNow
                    });
                }

                seed.OutputModerationVerdicts.Add(new OutputModerationVerdict
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = org,
                    ProjectId = project,
                    StorageKey = decided,
                    SourceVersion = "e:1",
                    State = "Allow",
                    ModelSnapshot = "m"
                });
                await seed.SaveChangesAsync();
            }

            (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
            (await queue.DequeueAsync(CancellationToken.None)).StorageKey.Should().Be(fresh, "a decided output is never offered");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
