#nullable enable
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// The model catalog schema (docs/design/model-catalog.md §2) and its <c>AddModelCatalog</c> migration (§4.1).
/// Like <see cref="OrganizationBackfillTests"/>: the constraints are checked against Sqlite (real unique
/// indexes), and the migration's data movement — raw PostgreSQL — against a real PostgreSQL when
/// <c>REELBOLT_TEST_PG</c> is set (e.g. <c>Host=127.0.0.1;Port=55432;Username=postgres;Password=postgres</c>).
/// Without it the PostgreSQL tests are skipped (<see cref="PostgresFactAttribute"/>), and reported as skipped.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class ModelCatalogMigrationTests
{
    private const string PreviousMigration = "20261011181415_AddAssistantAutoApprove";

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

    private static Organization NewOrg(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Name = slug,
        Slug = slug,
        Kind = OrganizationKind.Team
    };

    private static InferenceProvider NewAccount(Guid? org, string name) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = org,
        Name = name,
        Kind = InferenceProviderKind.OpenAICompatible,
        Endpoint = "https://x.example/v1"
    };

    private static InferenceModel NewModel(InferenceProvider account, string displayName) => new()
    {
        Id = Guid.NewGuid(),
        ProviderId = account.Id,
        OrganizationId = account.OrganizationId,
        ModelCode = "m",
        DisplayName = displayName
    };

    private static ModelCapability NewCapability(InferenceModel model, InferenceProviderCapability capability, bool isDefault = false) => new()
    {
        Id = Guid.NewGuid(),
        ModelId = model.Id,
        OrganizationId = model.OrganizationId,
        Capability = capability,
        IsDefault = isDefault
    };

    [Fact]
    public void A_model_serves_each_capability_at_most_once()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqliteContext();
        using (connection)
        using (db)
        {
            InferenceProvider account = NewAccount(null, "acct");
            InferenceModel model = NewModel(account, "model");
            db.AddRange(account, model, NewCapability(model, InferenceProviderCapability.Chat),
                NewCapability(model, InferenceProviderCapability.Vision));
            db.SaveChanges();

            db.Add(NewCapability(model, InferenceProviderCapability.Chat));
            Action act = () => db.SaveChanges();
            act.Should().Throw<DbUpdateException>("unique (model_id, capability)");
        }
    }

    [Fact]
    public void One_default_per_organization_and_capability()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqliteContext();
        using (connection)
        using (db)
        {
            Organization orgA = NewOrg("a"), orgB = NewOrg("b");
            InferenceProvider a = NewAccount(orgA.Id, "a"), b = NewAccount(orgB.Id, "b");
            InferenceModel a1 = NewModel(a, "a1"), a2 = NewModel(a, "a2"), b1 = NewModel(b, "b1");
            db.AddRange(orgA, orgB, a, b, a1, a2, b1,
                NewCapability(a1, InferenceProviderCapability.Chat, isDefault: true),
                // Another org's default and another capability's default coexist with it.
                NewCapability(b1, InferenceProviderCapability.Chat, isDefault: true),
                NewCapability(a2, InferenceProviderCapability.Vision, isDefault: true),
                // Any number of non-defaults.
                NewCapability(a2, InferenceProviderCapability.Chat));
            db.SaveChanges();

            ModelCapability second = NewCapability(a2, InferenceProviderCapability.Transcription, isDefault: true);
            db.Add(second);
            db.SaveChanges();
            InferenceModel a3 = NewModel(a, "a3");
            db.AddRange(a3, NewCapability(a3, InferenceProviderCapability.Transcription, isDefault: true));
            Action act = () => db.SaveChanges();
            act.Should().Throw<DbUpdateException>("one Transcription default per organization");
        }
    }

    /// <summary>
    /// Sqlite treats NULLs as distinct in a unique index, so the PLATFORM scope (null organization) is
    /// enforced by NULLS NOT DISTINCT, which only PostgreSQL honours: it is pinned on the model here and
    /// exercised for real in <see cref="AddModelCatalog_SplitsEveryRow_PreservingIds"/>.
    /// </summary>
    [Fact]
    public void The_platform_scope_counts_as_one_organization_for_defaults_and_display_names()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only").Options);
        IModel model = db.GetService<IDesignTimeModel>().Model;

        model.FindEntityType(typeof(ModelCapability))!.GetIndexes()
            .Single(i => i.GetFilter() == "is_default")
            .GetAreNullsDistinct().Should().Be(false);
        model.FindEntityType(typeof(InferenceModel))!.GetIndexes()
            .Single(i => i.IsUnique && i.Properties.Any(p => p.Name == nameof(InferenceModel.DisplayName)))
            .GetAreNullsDistinct().Should().Be(false);
    }

    [Fact]
    public void Display_names_are_unique_per_organization()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqliteContext();
        using (connection)
        using (db)
        {
            Organization orgA = NewOrg("a"), orgB = NewOrg("b");
            InferenceProvider a = NewAccount(orgA.Id, "a"), b = NewAccount(orgB.Id, "b");
            db.AddRange(orgA, orgB, a, b, NewModel(a, "Claude"), NewModel(b, "Claude"));
            db.SaveChanges();

            db.Add(NewModel(a, "Claude"));
            Action act = () => db.SaveChanges();
            act.Should().Throw<DbUpdateException>("unique (organization_id, display_name)");
        }
    }

    [Fact]
    public void Deleting_a_provider_cascades_to_its_models_and_capabilities_and_nulls_references()
    {
        (SqliteConnection connection, InferenceApiDbContext db) = NewSqliteContext();
        using (connection)
        using (db)
        {
            InferenceProvider account = NewAccount(null, "acct");
            InferenceModel model = NewModel(account, "model");
            ModelCapability capability = NewCapability(model, InferenceProviderCapability.Chat);
            AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "builtin", IsBuiltIn = true, InferenceProviderId = capability.Id };
            db.AddRange(account, model, capability, agent);
            db.SaveChanges();
            db.ChangeTracker.Clear();

            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
            db.Database.ExecuteSql($"DELETE FROM inference_providers WHERE id = {account.Id}");

            db.ModelCapabilities.Count().Should().Be(0);
            db.InferenceModels.Count().Should().Be(0);
            db.AgentDefinitions.Single().InferenceProviderId.Should().BeNull("the agent falls back to the default");
        }
    }

    [PostgresFact]
    public async Task AddModelCatalog_SplitsEveryRow_PreservingIds()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_catalog_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new NpgsqlConnectionStringBuilder(baseConnection) { Database = dbName };
        try
        {
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseNpgsql(csb.ConnectionString)
                .Options;
            await using InferenceApiDbContext db = new InferenceApiDbContext(options);
            IMigrator migrator = db.GetService<IMigrator>();
            await using NpgsqlDataSource data = NpgsqlDataSource.Create(csb.ConnectionString);

            async Task<List<object?[]>> Rows(string sql)
            {
                await using NpgsqlCommand cmd = data.CreateCommand(sql);
                await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
                List<object?[]> rows = [];
                while (await reader.ReadAsync())
                {
                    object?[] values = new object?[reader.FieldCount];
                    reader.GetValues(values!);
                    rows.Add(values.Select(v => v is DBNull ? null : v).ToArray());
                }
                return rows;
            }

            async Task<object?> Scalar(string sql) => (await Rows(sql)).Single()[0];
            async Task Exec(string sql)
            {
                await using NpgsqlCommand cmd = data.CreateCommand(sql);
                await cmd.ExecuteNonQueryAsync();
            }

            async Task<string> FkTarget(string table) => (string)(await Scalar(
                $@"SELECT confrelid::regclass::text FROM pg_constraint
                   WHERE contype = 'f' AND conrelid = '{table}'::regclass
                     AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = '{table}'::regclass AND attname = 'inference_provider_id')]::smallint[]"))!;

            // ---- The pre-catalog schema and a realistic install. ----
            await migrator.MigrateAsync(PreviousMigration);

            Guid org = Guid.NewGuid(), user = Guid.NewGuid(), project = Guid.NewGuid();
            Guid anthChat = Guid.NewGuid(), anthVision = Guid.NewGuid(), fal = Guid.NewGuid(), falErase = Guid.NewGuid(),
                falUpscale = Guid.NewGuid(), byo = Guid.NewGuid(), disabled = Guid.NewGuid(), higgs = Guid.NewGuid(),
                speech = Guid.NewGuid(), spareSpeech = Guid.NewGuid();
            Guid builtInAgent = Guid.NewGuid(), customAgent = Guid.NewGuid(), voice = Guid.NewGuid(), spareVoice = Guid.NewGuid();
            DateTime tested = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

            await Exec($@"
INSERT INTO organizations (id, name, slug, kind, created_at, updated_at) VALUES ('{org}', 'Acme', 'acme', 'Team', now(), now());
INSERT INTO application_users (id, email, display_name, created_at, is_admin, must_change_password)
  VALUES ('{user}', 'u@t.local', 'U', now(), false, false);
INSERT INTO projects (id, name, owner_id, organization_id, status, created_at, updated_at)
  VALUES ('{project}', 'p', '{user}', '{org}', 'Draft', now(), now());
INSERT INTO inference_providers (id, organization_id, name, kind, capability, endpoint, model_name, api_key_encrypted,
    api_key_last_four, is_default, is_enabled, personal_oauth_mode, timeout_seconds, extra_headers_json,
    created_at, updated_at, last_test_at, last_test_ok, last_test_error) VALUES
 ('{anthChat}', NULL, 'Anthropic', 'Anthropic', 'Chat', 'https://api.anthropic.com', 'claude-sonnet-4-5', 'cipher-anth', '1234', true, true, false, 120, NULL, '2026-01-01', '2026-01-02', '{tested:O}', true, NULL),
 ('{anthVision}', NULL, 'Anthropic Vision', 'Anthropic', 'Vision', 'https://api.anthropic.com', 'claude-sonnet-4-5', 'cipher-anth', '1234', true, true, false, 120, NULL, '2026-01-03', '2026-01-04', NULL, NULL, NULL),
 ('{fal}', NULL, 'fal', 'FalAi', 'ImageEdit', 'https://fal.run', 'fal-ai/flux-pro/kontext', 'cipher-fal', 'abcd', true, true, false, NULL, NULL, '2026-02-01', '2026-02-01', '{tested:O}', false, 'HTTP 401'),
 ('{falErase}', NULL, 'fal - Remove objects', 'FalAi', 'ImageErase', 'https://fal.run', 'fal-ai/object-removal', 'cipher-fal', 'abcd', true, true, false, NULL, NULL, '2026-02-01', '2026-02-01', NULL, NULL, NULL),
 ('{falUpscale}', NULL, 'fal - Upscale', 'FalAi', 'ImageUpscale', 'https://fal.run', 'fal-ai/clarity-upscaler', 'cipher-fal', 'abcd', false, true, false, NULL, NULL, '2026-02-01', '2026-02-01', NULL, NULL, NULL),
 ('{byo}', '{org}', 'Org LLM', 'OpenAICompatible', 'Chat', 'https://llm.acme.example/v1', 'qwen3', 'cipher-byo', '9999', true, true, false, NULL, '{{""X-Org"":""acme""}}', '2026-03-01', '2026-03-01', NULL, NULL, NULL),
 ('{disabled}', NULL, 'Old', 'OpenAICompatible', 'Chat', 'https://old.example/v1', 'old-model', NULL, NULL, false, false, false, NULL, NULL, '2025-01-01', '2025-01-01', NULL, NULL, NULL),
 ('{higgs}', NULL, 'Higgsfield', 'Higgsfield', 'VideoGeneration', 'https://api.higgsfield.ai', 'dop-turbo', 'cipher-h', 'hhhh', true, true, false, NULL, NULL, '2026-04-01', '2026-04-01', NULL, NULL, NULL),
 ('{speech}', NULL, 'Voices', 'FishAudio', 'SpeechSynthesis', 'https://api.fish.audio', 's1', 'cipher-fish', 'ffff', true, true, false, NULL, NULL, '2026-05-01', '2026-05-01', NULL, NULL, NULL),
 ('{spareSpeech}', NULL, 'Spare voices', 'FishAudio', 'SpeechSynthesis', 'https://api.fish.audio', 's1-mini', 'cipher-fish', 'ffff', false, true, false, NULL, NULL, '2026-05-02', '2026-05-02', NULL, NULL, NULL);
INSERT INTO agent_definitions (id, name, description, system_prompt, agent_type, is_built_in, organization_id, inference_provider_id, created_at) VALUES
 ('{builtInAgent}', 'builtin', 'd', 'p', 'Custom', true, NULL, '{anthChat}', now()),
 ('{customAgent}', 'custom', 'd', 'p', 'Custom', false, '{org}', '{byo}', now());
INSERT INTO organization_agent_provider_overrides (organization_id, agent_type, inference_provider_id, updated_at)
  VALUES ('{org}', 'Colorist', '{byo}', now());
INSERT INTO project_voices (id, project_id, inference_provider_id, name, source, remote_voice_id, created_by_user_id, created_at) VALUES
 ('{voice}', '{project}', '{speech}', 'Narrator', 'Library', 'voice-1', '{user}', now()),
 ('{spareVoice}', '{project}', '{spareSpeech}', 'Spare', 'Library', 'voice-2', '{user}', now());");

            const string OldColumns = @"id, organization_id, name, kind, capability, endpoint, model_name, api_key_encrypted,
                api_key_last_four, is_default, is_enabled, personal_oauth_mode, timeout_seconds, extra_headers_json::text,
                created_at, updated_at, last_test_at, last_test_ok, last_test_error";
            Dictionary<Guid, object?[]> before = (await Rows($"SELECT {OldColumns} FROM inference_providers"))
                .ToDictionary(r => (Guid)r[0]!);
            before.Should().HaveCount(10);

            // ---- Up ----
            await migrator.MigrateAsync();

            // Every old id is now a capability carrying the row's capability, default, enabled and last test.
            foreach ((Guid id, object?[] old) in before)
            {
                object?[] cap = (await Rows($@"SELECT c.capability, c.is_default, c.is_enabled, c.last_test_at, c.last_test_ok,
                        c.last_test_error, c.organization_id, c.price_source, c.model_id, c.created_at, c.updated_at
                    FROM model_capabilities c WHERE c.id = '{id}'")).Single();
                cap[0].Should().Be(old[4], "capability of {0}", old[2]);
                cap[1].Should().Be(old[9]);
                cap[2].Should().Be(old[10]);
                cap[3].Should().Be(old[16]);
                cap[4].Should().Be(old[17]);
                cap[5].Should().Be(old[18]);
                cap[6].Should().Be(old[1]);
                cap[7].Should().Be((string)old[3]! == "Higgsfield" ? "Oracle" : "Table");
                cap[9].Should().Be(old[14]);
                cap[10].Should().Be(old[15]);

                // Its model: deterministic id, the row's model name and name, under the row's own provider.
                Guid expectedModelId = (Guid)(await Scalar($"SELECT md5('model:{id}')::uuid"))!;
                cap[8].Should().Be(expectedModelId);
                object?[] model = (await Rows($@"SELECT provider_id, organization_id, model_code, display_name, is_enabled
                    FROM inference_models WHERE id = '{expectedModelId}'")).Single();
                model[0].Should().Be(id);
                model[1].Should().Be(old[1]);
                model[2].Should().Be(old[6]);
                model[3].Should().Be(old[2]);
                model[4].Should().Be(true);

                // The provider keeps the account fields.
                object?[] account = (await Rows($@"SELECT organization_id, name, kind, endpoint, api_key_encrypted, api_key_last_four,
                        is_enabled, personal_oauth_mode, timeout_seconds, extra_headers_json::text, is_system
                    FROM inference_providers WHERE id = '{id}'")).Single();
                account[0].Should().Be(old[1]);
                account[1].Should().Be(old[2]);
                account[2].Should().Be(old[3]);
                account[3].Should().Be(old[5]);
                account[4].Should().Be(old[7]);
                account[5].Should().Be(old[8]);
                account[6].Should().Be(old[10]);
                account[7].Should().Be(old[11]);
                account[8].Should().Be(old[12]);
                account[9].Should().Be(old[13]);
                account[10].Should().Be(false);
            }

            (await Scalar("SELECT count(*) FROM model_capabilities")).Should().Be(10L);
            (await Scalar("SELECT count(*) FROM inference_models")).Should().Be(10L);
            (await Scalar(@"SELECT count(*) FROM information_schema.columns WHERE table_name = 'inference_providers'
                AND column_name IN ('capability', 'model_name', 'is_default', 'last_test_at', 'last_test_ok', 'last_test_error')"))
                .Should().Be(0L, "the moved columns are gone");
            (await Scalar(@"SELECT count(*) FROM information_schema.columns WHERE table_name = 'usage_events'
                AND column_name IN ('price_id', 'variant')")).Should().Be(2L);
            (await Rows("SELECT id, markup, credit_usd_value, catalog_upgraded_at FROM pricing_settings")).Single()
                .Should().Equal(1, 1.2m, 0.006m, null);

            // The FK columns kept their values and now name model capabilities (ON DELETE SET NULL).
            (await Scalar($"SELECT inference_provider_id FROM agent_definitions WHERE id = '{builtInAgent}'")).Should().Be(anthChat);
            (await Scalar($"SELECT inference_provider_id FROM agent_definitions WHERE id = '{customAgent}'")).Should().Be(byo);
            (await Scalar($"SELECT inference_provider_id FROM organization_agent_provider_overrides WHERE organization_id = '{org}'")).Should().Be(byo);
            (await Scalar($"SELECT inference_provider_id FROM project_voices WHERE id = '{voice}'")).Should().Be(speech);
            foreach (string table in new[] { "agent_definitions", "organization_agent_provider_overrides", "project_voices" })
            {
                (await FkTarget(table)).Should().Be("model_capabilities", $"{table}.inference_provider_id names a model capability");
            }

            // The migrated catalog resolves through the real store exactly as the rows did.
            List<ModelCapabilityView> visible = await ModelCapabilityView.EnabledFor(db.ModelCapabilities.AsNoTracking(), org).ToListAsync();
            visible.Select(v => v.Id).Should().BeEquivalentTo(new[] { anthChat, anthVision, fal, falErase, falUpscale, byo, higgs, speech, spareSpeech });
            ModelCapabilityView chat = visible.Single(v => v.Id == anthChat);
            (chat.Name, chat.ModelName, chat.Endpoint, chat.ApiKeyEncrypted, chat.TimeoutSeconds, chat.ProviderId)
                .Should().Be(("Anthropic", "claude-sonnet-4-5", "https://api.anthropic.com", "cipher-anth", 120, anthChat));

            // NULLS NOT DISTINCT, for real: a second PLATFORM Chat default is refused.
            Func<Task> secondPlatformDefault = () => Exec($@"
INSERT INTO inference_models (id, provider_id, organization_id, model_code, display_name, is_enabled, created_at, updated_at)
  VALUES ('{Guid.NewGuid()}', '{disabled}', NULL, 'x', 'Second', true, now(), now());
INSERT INTO model_capabilities (id, model_id, organization_id, capability, is_default, is_enabled, price_source, created_at, updated_at)
  SELECT '{Guid.NewGuid()}', id, NULL, 'Chat', true, true, 'Table', now(), now() FROM inference_models WHERE display_name = 'Second';");
            (await secondPlatformDefault.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
            Func<Task> duplicatePlatformName = () => Exec($@"
INSERT INTO inference_models (id, provider_id, organization_id, model_code, display_name, is_enabled, created_at, updated_at)
  VALUES ('{Guid.NewGuid()}', '{disabled}', NULL, 'x', 'Anthropic', true, now(), now());");
            (await duplicatePlatformName.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");

            // ON DELETE SET NULL: deleting a capability releases the voice that named it.
            await Exec($"DELETE FROM model_capabilities WHERE id = '{spareSpeech}'");
            (await Scalar($"SELECT inference_provider_id FROM project_voices WHERE id = '{spareVoice}'")).Should().BeNull();

            // What a consolidation (T-003) leaves behind: the Vision row's model moved under the Chat account and
            // its own account deleted, so its capability id is no longer a provider id. Plus the system compute
            // provider the upgrader creates, which has no pre-catalog equivalent.
            await Exec($@"
UPDATE inference_models SET provider_id = '{anthChat}' WHERE provider_id = '{anthVision}';
DELETE FROM inference_providers WHERE id = '{anthVision}';
INSERT INTO inference_providers (id, organization_id, name, kind, endpoint, is_enabled, is_system, personal_oauth_mode, created_at, updated_at)
  VALUES ('{WellKnownModelCatalog.ComputeProviderId}', NULL, 'Platform compute', 'OpenAICompatible', '', true, true, false, now(), now());
INSERT INTO inference_models (id, provider_id, organization_id, model_code, display_name, is_enabled, created_at, updated_at)
  VALUES ('{WellKnownModelCatalog.ComputeModelId}', '{WellKnownModelCatalog.ComputeProviderId}', NULL, 'compute', 'Rendering and encoding', true, now(), now());
INSERT INTO model_capabilities (id, model_id, organization_id, capability, is_default, is_enabled, price_source, created_at, updated_at)
  VALUES ('{WellKnownModelCatalog.ComputeCapabilityId}', '{WellKnownModelCatalog.ComputeModelId}', NULL, 'Compute', true, true, 'Table', now(), now());");
            (await Scalar($"SELECT count(*) FROM inference_providers WHERE id = '{anthVision}'")).Should().Be(0L);
            (await Scalar($"SELECT count(*) FROM model_capabilities WHERE id = '{anthVision}'")).Should().Be(1L);

            // ---- Down ----
            await migrator.MigrateAsync(PreviousMigration);

            Dictionary<Guid, object?[]> after = (await Rows($"SELECT {OldColumns} FROM inference_providers"))
                .ToDictionary(r => (Guid)r[0]!);
            // The spare capability was deleted, so its account (named by no capability) is gone; the compute
            // provider is gone; every other row is back with every old column and value.
            after.Keys.Should().BeEquivalentTo(before.Keys.Where(k => k != spareSpeech));
            foreach ((Guid id, object?[] row) in after)
            {
                row.Should().Equal(expected: before[id], because: $"row {before[id][2]} must come back exactly as it was");
            }

            foreach (string table in new[] { "agent_definitions", "organization_agent_provider_overrides", "project_voices" })
            {
                (await FkTarget(table)).Should().Be("inference_providers", $"{table}.inference_provider_id names a provider row again");
            }
            (await Scalar($"SELECT inference_provider_id FROM agent_definitions WHERE id = '{builtInAgent}'")).Should().Be(anthChat);
            (await Scalar($"SELECT inference_provider_id FROM organization_agent_provider_overrides WHERE organization_id = '{org}'")).Should().Be(byo);
            (await Scalar($"SELECT inference_provider_id FROM project_voices WHERE id = '{voice}'")).Should().Be(speech);
            (await Scalar(@"SELECT count(*) FROM information_schema.tables
                WHERE table_name IN ('inference_models', 'model_capabilities', 'model_capability_prices', 'pricing_settings')"))
                .Should().Be(0L);
            (await Scalar(@"SELECT count(*) FROM information_schema.columns
                WHERE table_name = 'inference_providers' AND column_name = 'is_system'")).Should().Be(0L);
            (await Scalar(@"SELECT count(*) FROM information_schema.columns
                WHERE table_name = 'usage_events' AND column_name IN ('price_id', 'variant')")).Should().Be(2L,
                "design §4.3: usage recorded in between keeps its price_id (and variant); the old code ignores the columns");
            (await Scalar("SELECT count(*) FROM pg_indexes WHERE indexname = 'IX_inference_providers_organization_id_capability'"))
                .Should().Be(1L, "the old one-default-per-capability index is back");

            // And Up again over the restored rows is still lossless (the round trip is repeatable).
            await migrator.MigrateAsync();
            (await Scalar($"SELECT count(*) FROM model_capabilities WHERE id IN ('{anthChat}', '{anthVision}', '{byo}')")).Should().Be(3L);
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// A fresh database at the pre-catalog migration, handed to <paramref name="body"/> with a migrator and a raw
    /// data source; dropped afterwards. Its callers are <see cref="PostgresFactAttribute"/> tests, skipped without
    /// <c>REELBOLT_TEST_PG</c>.
    /// </summary>
    private static async Task WithPreCatalogDatabase(Func<IMigrator, NpgsqlDataSource, Task> body)
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_catalog_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(baseConnection) { Database = dbName };
        try
        {
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseNpgsql(csb.ConnectionString)
                .Options;
            await using InferenceApiDbContext db = new(options);
            IMigrator migrator = db.GetService<IMigrator>();
            await using NpgsqlDataSource data = NpgsqlDataSource.Create(csb.ConnectionString);
            await migrator.MigrateAsync(PreviousMigration);
            await body(migrator, data);
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource data, string sql)
    {
        await using NpgsqlCommand cmd = data.CreateCommand(sql);
        object? value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task ExecAsync(NpgsqlDataSource data, string sql)
    {
        await using NpgsqlCommand cmd = data.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task AddModelCatalog_MigratesLegacyNamesAndModelNamesLongerThan200CharactersLosslessly()
    {
        await WithPreCatalogDatabase(async (migrator, data) =>
        {
            // The pre-catalog columns are unbounded text, so an install can hold names no varchar(200) fits.
            string longName = "Legacy provider " + new string('n', 284);
            string longModel = "vendor/" + new string('m', 293);
            longName.Length.Should().Be(300);
            longModel.Length.Should().Be(300);
            Guid id = Guid.NewGuid();
            await ExecAsync(data, $@"
INSERT INTO inference_providers (id, organization_id, name, kind, capability, endpoint, model_name, is_default, is_enabled,
    personal_oauth_mode, created_at, updated_at)
VALUES ('{id}', NULL, '{longName}', 'OpenAICompatible', 'Chat', 'https://llm.example/v1', '{longModel}', true, true, false, now(), now());");

            await migrator.MigrateAsync();

            (await ScalarAsync(data, $"SELECT display_name FROM inference_models WHERE provider_id = '{id}'")).Should().Be(longName);
            (await ScalarAsync(data, $"SELECT model_code FROM inference_models WHERE provider_id = '{id}'")).Should().Be(longModel);
            (await ScalarAsync(data, $"SELECT name FROM inference_providers WHERE id = '{id}'")).Should().Be(longName);
            (await ScalarAsync(data, @"SELECT string_agg(column_name || ':' || data_type, ',' ORDER BY column_name)
                FROM information_schema.columns WHERE table_name = 'inference_models' AND column_name IN ('model_code', 'display_name')"))
                .Should().Be("display_name:text,model_code:text");

            // And back: the restored row is the same row.
            await migrator.MigrateAsync(PreviousMigration);
            (await ScalarAsync(data, $"SELECT name FROM inference_providers WHERE id = '{id}'")).Should().Be(longName);
            (await ScalarAsync(data, $"SELECT model_name FROM inference_providers WHERE id = '{id}'")).Should().Be(longModel);
        });
    }

    [PostgresFact]
    public async Task AddModelCatalog_Down_RefusesWithAClearError_WhenAModelsOrganizationDiffersFromItsAccounts()
    {
        await WithPreCatalogDatabase(async (migrator, data) =>
        {
            Guid org = Guid.NewGuid(), platform = Guid.NewGuid(), stray = Guid.NewGuid();
            await ExecAsync(data, $@"
INSERT INTO organizations (id, name, slug, kind, created_at, updated_at) VALUES ('{org}', 'Acme', 'acme', 'Team', now(), now());
INSERT INTO inference_providers (id, organization_id, name, kind, capability, endpoint, model_name, is_default, is_enabled,
    personal_oauth_mode, created_at, updated_at)
VALUES ('{platform}', NULL, 'Platform', 'OpenAICompatible', 'Chat', 'https://llm.example/v1', 'm', true, true, false, now(), now());");
            await migrator.MigrateAsync();

            // A BYO model (and capability) hanging off a PLATFORM account: no admin path writes this, and restoring it
            // would copy the platform account's key into the organization's row.
            await ExecAsync(data, $@"
INSERT INTO inference_models (id, provider_id, organization_id, model_code, display_name, is_enabled, created_at, updated_at)
  VALUES ('{stray}', '{platform}', '{org}', 'x', 'Stray', true, now(), now());
INSERT INTO model_capabilities (id, model_id, organization_id, capability, is_default, is_enabled, price_source, created_at, updated_at)
  VALUES ('{Guid.NewGuid()}', '{stray}', '{org}', 'Chat', false, true, 'Table', now(), now());");

            Func<Task> down = () => migrator.MigrateAsync(PreviousMigration);

            PostgresException error = (await down.Should().ThrowAsync<PostgresException>()).Which;
            error.MessageText.Should().Contain("organization").And.Contain("AddModelCatalog");
            (await ScalarAsync(data, "SELECT count(*) FROM model_capabilities")).Should().Be(2L, "the refused Down rolled back");
            (await ScalarAsync(data, $"SELECT count(*) FROM inference_providers WHERE id = '{platform}' AND organization_id IS NULL")).Should().Be(1L);
        });
    }
}
