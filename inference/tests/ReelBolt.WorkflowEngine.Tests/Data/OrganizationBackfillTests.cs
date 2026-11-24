using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// A1 (organizations) coverage.
///
/// The backfill is raw PostgreSQL (<c>gen_random_uuid()</c>, <c>UPDATE ... FROM</c>), which neither
/// InMemory nor Sqlite can run. So it is tested twice: the schema rules (unique Personal org per
/// creator, built-in-or-org check, per-org provider names) against Sqlite, which enforces real
/// constraints, and the migration's actual data movement against a real PostgreSQL when
/// <c>REELBOLT_TEST_PG</c> is set to a connection string with CREATEDB rights (for example
/// <c>Host=127.0.0.1;Username=postgres;Password=postgres</c>). Without it the PostgreSQL test
/// returns immediately (xUnit v2 has no dynamic skip), so CI without a database stays green;
/// the verification against a copy of the dev database is recorded in the A1 status notes.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class OrganizationBackfillTests
{
    private const string PreviousMigration = "20261005061327_AddEditingStyles";

    private static (SqliteConnection Connection, InferenceApiDbContext Context) NewSqliteContext()
    {
        SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection)
            .Options;
        InferenceApiDbContext context = new InferenceApiDbContext(options);
        context.Database.EnsureCreated();
        return (connection, context);
    }

    private static ApplicationUser NewUser(string email) => new ApplicationUser { Id = Guid.NewGuid(), Email = email, DisplayName = email };

    private static Organization NewOrg(Guid? creator, OrganizationKind kind, string slug) => new Organization
    {
        Id = Guid.NewGuid(),
        Name = slug,
        Slug = slug,
        Kind = kind,
        CreatedByUserId = creator
    };

    [Fact]
    public void Organization_DefaultsToAutoComputePreference_AndStoresEnumsAsStrings()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqliteContext();
        using (connection)
        using (db)
        {
            Organization org = NewOrg(null, OrganizationKind.Team, "acme");
            db.Organizations.Add(org);
            db.SaveChanges();

            db.Database.SqlQuery<string>($"SELECT compute_preference || '/' || kind AS \"Value\" FROM organizations")
                .Single().Should().Be("Auto/Team");
        }
    }

    [Fact]
    public void OnlyOnePersonalOrganization_PerCreator()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqliteContext();
        using (connection)
        using (db)
        {
            ApplicationUser user = NewUser("a@example.com");
            db.ApplicationUsers.Add(user);
            db.Organizations.Add(NewOrg(user.Id, OrganizationKind.Personal, "p1"));
            db.SaveChanges();

            // A Team org by the same creator is fine; a second Personal org is not.
            db.Organizations.Add(NewOrg(user.Id, OrganizationKind.Team, "t1"));
            db.SaveChanges();

            db.Organizations.Add(NewOrg(user.Id, OrganizationKind.Personal, "p2"));
            Action act = () => db.SaveChanges();
            act.Should().Throw<DbUpdateException>();
        }
    }

    [Fact]
    public void CustomAgent_RequiresAnOrganization_BuiltInDoesNot()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqliteContext();
        using (connection)
        using (db)
        {
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "builtin", IsBuiltIn = true });
            db.SaveChanges();

            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "custom", IsBuiltIn = false });
            Action act = () => db.SaveChanges();
            act.Should().Throw<DbUpdateException>();
        }
    }

    [Fact]
    public void MembershipKey_IsOrganizationAndUser()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqliteContext();
        using (connection)
        using (db)
        {
            ApplicationUser user = NewUser("m@example.com");
            Organization org = NewOrg(null, OrganizationKind.Team, "team");
            db.ApplicationUsers.Add(user);
            db.Organizations.Add(org);
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org.Id, UserId = user.Id, Role = OrganizationRole.Owner });
            db.SaveChanges();

            db.Entry(new OrganizationMembership { OrganizationId = org.Id, UserId = user.Id, Role = OrganizationRole.Member })
                .Metadata.FindPrimaryKey()!.Properties.Select(p => p.Name)
                .Should().Equal("OrganizationId", "UserId");
            db.ChangeTracker.Clear();
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org.Id, UserId = user.Id, Role = OrganizationRole.Member });
            Action act = () => db.SaveChanges();
            act.Should().Throw<DbUpdateException>();
        }
    }

    [Fact]
    public async Task Migration_BackfillsEveryProjectIntoItsOwnersPersonalOrg_OnPostgres()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_a1_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseNpgsql(csb.ConnectionString)
                .Options;
            await using InferenceApiDbContext db = new InferenceApiDbContext(options);
            IMigrator migrator = db.GetService<IMigrator>();

            // Schema as it was before A1, then legacy rows.
            await migrator.MigrateAsync(PreviousMigration);

            Guid admin1 = Guid.NewGuid(), bob = Guid.NewGuid(), carol = Guid.NewGuid();
            Guid bobProject = Guid.NewGuid(), carolProject = Guid.NewGuid(), adminProject = Guid.NewGuid();
            Guid bobAgent = Guid.NewGuid(), orphanAgent = Guid.NewGuid(), builtInAgent = Guid.NewGuid();
            Guid bobThread = Guid.NewGuid();
            string sql = $@"
INSERT INTO application_users (id,email,display_name,created_at,password_hash,is_admin,must_change_password) VALUES
 ('{admin1}','admin@t.local','Admin',now(),'x',true,false),
 ('{bob}','bob@t.local','Bob',now(),'x',false,false),
 ('{carol}','carol@t.local','Carol',now(),'x',false,false);
INSERT INTO projects (id,name,owner_id,status,created_at,updated_at) VALUES
 ('{bobProject}','b','{bob}','Draft',now(),now()),
 ('{carolProject}','c','{carol}','Draft',now(),now()),
 ('{adminProject}','a','{admin1}','Draft',now(),now());
INSERT INTO agent_definitions (id,name,description,system_prompt,agent_type,is_built_in,owner_id,created_at) VALUES
 ('{bobAgent}','bob agent','d','p','Custom',false,'{bob}',now()),
 ('{orphanAgent}','orphan','d','p','Custom',false,NULL,now()),
 ('{builtInAgent}','builtin','d','p','Custom',true,NULL,now());
INSERT INTO assistant_threads (id,user_id,title,created_at,updated_at) VALUES
 ('{bobThread}','{bob}','t',now(),now());
INSERT INTO inference_providers (id,name,kind,capability,endpoint,model_name,is_default,is_enabled,created_at,updated_at) VALUES
 ('{Guid.NewGuid()}','legacy','OpenAICompatible','Chat','http://x','m',true,true,now(),now());";
            await db.Database.ExecuteSqlRawAsync(sql);

            await migrator.MigrateAsync();

            async Task<Guid> Scalar(string query)
            {
                await using NpgsqlConnection c = new NpgsqlConnection(csb.ConnectionString);
                await c.OpenAsync();
                await using NpgsqlCommand cmd = new NpgsqlCommand(query, c);
                return (Guid)(await cmd.ExecuteScalarAsync())!;
            }

            async Task<Guid> PersonalOf(Guid user) =>
                await Scalar($"SELECT id FROM organizations WHERE kind='Personal' AND created_by_user_id='{user}'");

            (await Scalar($"SELECT organization_id FROM projects WHERE id='{bobProject}'")).Should().Be(await PersonalOf(bob));
            (await Scalar($"SELECT organization_id FROM projects WHERE id='{carolProject}'")).Should().Be(await PersonalOf(carol));
            (await Scalar($"SELECT organization_id FROM projects WHERE id='{adminProject}'")).Should().Be(await PersonalOf(admin1));
            (await Scalar($"SELECT organization_id FROM assistant_threads WHERE id='{bobThread}'")).Should().Be(await PersonalOf(bob));
            (await Scalar($"SELECT organization_id FROM agent_definitions WHERE id='{bobAgent}'")).Should().Be(await PersonalOf(bob));
            (await Scalar($"SELECT organization_id FROM agent_definitions WHERE id='{orphanAgent}'")).Should().Be(WellKnownOrganizations.DefaultId);
            (await Scalar($"SELECT COALESCE(organization_id, '{Guid.Empty}') FROM agent_definitions WHERE id='{builtInAgent}'")).Should().Be(Guid.Empty);
            (await Scalar($"SELECT COALESCE(organization_id, '{Guid.Empty}') FROM inference_providers WHERE name='legacy'")).Should().Be(Guid.Empty);
            (await Scalar($"SELECT last_active_organization_id FROM application_users WHERE id='{bob}'")).Should().Be(await PersonalOf(bob));

            // Platform: only the admin; Default: everyone, admin as Owner.
            (await Scalar($"SELECT user_id FROM organization_memberships WHERE organization_id='{WellKnownOrganizations.PlatformId}'")).Should().Be(admin1);
            (await Scalar($"SELECT user_id FROM organization_memberships WHERE organization_id='{WellKnownOrganizations.DefaultId}' AND role='Owner'")).Should().Be(admin1);
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
