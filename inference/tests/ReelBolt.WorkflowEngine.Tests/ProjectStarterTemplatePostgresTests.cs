#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using ReelBolt.WorkflowEngine.Tests.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The project-starter path against a real PostgreSQL (docs/design/new-user-funnel.md section 6): the
/// project and the recipe it was asked to start with are one unit. With real foreign keys and a real
/// transaction, a starter that cannot be created leaves no project behind, and one that can leaves
/// exactly the project and that one app. Follows <see cref="Data.SoftOpenFunnelQueryTests"/>: a per-test
/// database migrated in the compose order, dropped in <c>finally</c>.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class ProjectStarterTemplatePostgresTests
{
    private static async Task WithDatabase(Func<string, Task> body)
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_project_starter_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        string connectionString = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
        try
        {
            await using (InferenceApiDbContext api = NewDb(connectionString))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            // workflow_definitions and workflow_steps are the engine's tables.
            await using (WorkflowEngineDbContext engine = new(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connectionString).Options))
            {
                await engine.GetService<IMigrator>().MigrateAsync();
            }

            await body(connectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource dropAdmin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = dropAdmin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static InferenceApiDbContext NewDb(string connectionString)
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connectionString).Options);

    private static ICurrentUser User(Guid userId, Guid organizationId)
    {
        var u = new Mock<ICurrentUser>();
        u.SetupGet(x => x.UserId).Returns(userId);
        u.SetupGet(x => x.Email).Returns("person@example.test");
        u.SetupGet(x => x.IsAuthenticated).Returns(true);
        u.SetupGet(x => x.OrganizationId).Returns(organizationId);
        u.SetupGet(x => x.OrgRole).Returns(OrganizationRole.Owner);
        return u.Object;
    }

    private static ProjectCreationService Service(InferenceApiDbContext db, ICurrentUser user, RecordingProductEventLog log) => new(
        db, user,
        new WorkflowTemplateProvisioningService(
            db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
        new ProductEventEmitter(log, NullLogger<ProductEventEmitter>.Instance));

    private static async Task<long> CountAsync(string connectionString, string table)
    {
        await using NpgsqlConnection c = new(connectionString);
        await c.OpenAsync();
        await using NpgsqlCommand cmd = new($"SELECT count(*) FROM {table}", c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task A_starter_that_cannot_be_created_leaves_no_project_and_one_that_can_leaves_exactly_that_app()
    {
        await WithDatabase(async connectionString =>
        {
            Guid userId = Guid.NewGuid(), organizationId = Guid.NewGuid();
            await using (InferenceApiDbContext seed = NewDb(connectionString))
            {
                seed.ApplicationUsers.Add(new ApplicationUser { Id = userId, Email = "person@example.test", DisplayName = "Person", CreatedAt = DateTime.UtcNow });
                seed.Organizations.Add(new Organization { Id = organizationId, Name = "Personal", Slug = "o-" + organizationId.ToString("N"), Kind = OrganizationKind.Personal });
                await seed.SaveChangesAsync();
            }

            RecordingProductEventLog log = new();

            // No built-in agents yet (a deployment whose seeder has not run): the recipe cannot be created.
            await using (InferenceApiDbContext db = NewDb(connectionString))
            {
                Func<Task> act = () => Service(db, User(userId, organizationId), log)
                    .CreateAsync(new ProjectCreationInput("First video", null, StarterTemplateKey: "edit-my-video"), CancellationToken.None);
                await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Missing built-in agent definitions*");
            }

            (await CountAsync(connectionString, "projects")).Should().Be(0L, "the project and its starter are one transaction: no project without the app that was asked for");
            (await CountAsync(connectionString, "workflow_definitions")).Should().Be(0L);
            log.Rows.Should().BeEmpty("nothing happened, so nothing is recorded");

            // An unknown key is refused before anything is written, agents or not.
            await using (InferenceApiDbContext db = NewDb(connectionString))
            {
                ProjectCreationResult refused = await Service(db, User(userId, organizationId), log)
                    .CreateAsync(new ProjectCreationInput("First video", null, StarterTemplateKey: "no-such-recipe"), CancellationToken.None);
                refused.Project.Should().BeNull();
                refused.Failure.Should().BeOfType<BadRequestObjectResult>();
            }

            (await CountAsync(connectionString, "projects")).Should().Be(0L);

            // With the built-in agents in place the same request creates the project and that one app.
            await using (InferenceApiDbContext seed = NewDb(connectionString))
            {
                foreach (AgentType type in Enum.GetValues<AgentType>())
                {
                    seed.AgentDefinitions.Add(new AgentDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = "Built-in " + type,
                        Description = "d",
                        SystemPrompt = "p",
                        AgentType = type,
                        IsBuiltIn = true,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                await seed.SaveChangesAsync();
            }

            Guid projectId;
            await using (InferenceApiDbContext db = NewDb(connectionString))
            {
                ProjectCreationResult created = await Service(db, User(userId, organizationId), log)
                    .CreateAsync(new ProjectCreationInput("First video", "Phone clips", StarterTemplateKey: "edit-my-video"), CancellationToken.None);
                created.Failure.Should().BeNull();
                projectId = created.Project!.Id;
            }

            await using (InferenceApiDbContext read = NewDb(connectionString))
            {
                Project project = await read.Projects.IgnoreQueryFilters().AsNoTracking().SingleAsync();
                (project.Id, project.OwnerId, project.OrganizationId, project.Name).Should().Be((projectId, userId, organizationId, "First video"));

                List<WorkflowDefinition> apps = await read.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps).ToListAsync();
                WorkflowDefinition app = apps.Should().ContainSingle("the chosen recipe, and no automatic code-promo starter beside it").Subject;
                (app.ProjectId, app.TemplateKey, app.CreatedVia).Should().Be((projectId, "edit-my-video", WorkflowCreatedVia.Template));
                app.Steps.Should().HaveCount(WorkflowTemplateCatalog.GetByKey("edit-my-video")!.Steps.Count);
            }

            log.Rows.Select(r => (r.UserId, r.OrganizationId, r.Event, r.Detail)).Should().Equal(
                ((Guid?)userId, (Guid?)organizationId, "activation.project_created", null),
                ((Guid?)userId, (Guid?)organizationId, "product.template_chosen", "edit-my-video"));
        });
    }
}
