#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Tests.Pricing;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// Consolidation on a staging-like install on a <b>real PostgreSQL</b> (saas-model-catalog T-100): the boot crash
/// (<c>DbUpdateConcurrencyException</c> out of <see cref="ModelCatalogUpgrader.ApplyConsolidationAsync"/>) when key-less
/// rows of one kind and endpoint were grouped as one account. Fixture, migrated through <c>AddModelCatalog</c>: (a) fal
/// plus eight image-tool rows with the fal ciphertext copied, (b) Anthropic Chat + Vision sharing a key, (c) key-less
/// rows of one kind and endpoint (whisper Transcription x2, embeddings x2 with one model code, a local Chat + Vision),
/// (d) <c>env:</c> sentinel rows, (e) undecryptable keys, (f) key-less rows of two organizations at one kind/endpoint.
/// Only (a) and (b) are accounts; every old id resolves the same afterwards. Skipped unless <c>REELBOLT_TEST_PG</c> is set.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class ModelCatalogConsolidationRealPostgresTests
{
    private const string PreviousMigration = "20261011181415_AddAssistantAutoApprove";
    private static readonly DateTime SeedFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Obvious placeholders: what matters is that each is encrypted for real and decrypts to itself afterwards.
    private const string AnthropicKey = "placeholder-anthropic";
    private const string FalKey = "placeholder-fal";
    private const string ByoKey = "placeholder-byo";

    private readonly ISecretProtector _secrets = new DataProtectionSecretProtector(
        new EphemeralDataProtectionProvider(), NullLogger<DataProtectionSecretProtector>.Instance);

    private readonly IConfiguration _configuration = new ConfigurationBuilder().Build();

    /// <summary>The fixture's ids, by role.</summary>
    private sealed record Fixture(
        string ConnectionString, Guid OrgA, Guid OrgB, Guid AnthChat, Guid AnthVision, Guid Fal, IReadOnlyList<Guid> FalTools, Guid Byo, Guid ByoVision,
        IReadOnlyList<Guid> Untouchable);

    private sealed class Database(string admin, string name, string connectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource source = PostgresTestEnvironment.CreateAdminDataSource(admin);
            await using NpgsqlCommand drop = source.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Database> CreateDatabaseAsync()
    {
        string admin = PostgresTestEnvironment.BaseConnectionString()!;
        string name = "reelbolt_consolidation_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (NpgsqlDataSource source = PostgresTestEnvironment.CreateAdminDataSource(admin))
        await using (NpgsqlCommand create = source.CreateCommand($"CREATE DATABASE {name}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(admin) { Database = name };
        return new Database(admin, name, csb.ConnectionString);
    }

    private static InferenceApiDbContext NewDb(string connectionString, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptors).Options);

    private ModelCatalogUpgrader Upgrader(InferenceApiDbContext db, ILogger<ModelCatalogUpgrader>? logger = null) =>
        new(db, _secrets, _configuration, logger ?? NullLogger<ModelCatalogUpgrader>.Instance);

    /// <summary>The pre-catalog schema, the staging-like rows and rate cards, then <c>AddModelCatalog</c>.</summary>
    private async Task<Fixture> SeedAsync(string connectionString)
    {
        await using (InferenceApiDbContext db = NewDb(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        Guid orgA = Guid.NewGuid(), orgB = Guid.NewGuid();
        Guid anthChat = Guid.NewGuid(), anthVision = Guid.NewGuid(), fal = Guid.NewGuid();
        List<Guid> falTools = [.. Enumerable.Range(0, 8).Select(_ => Guid.NewGuid())];
        string[] falCapabilities =
        [
            "ImageGeneration", "ImageEdit", "ImageErase", "ImageFill", "ImageExpand", "ImageBackgroundReplace", "ImageRelight",
            "ImageUpscale"
        ];
        string falCipher = _secrets.Protect(FalKey);
        string envCipherA = _secrets.Protect("env:"), envCipherB = _secrets.Protect("env:");

        // (c) key-less rows sharing one kind and endpoint, (d) env: sentinels, (e) undecryptable keys, (f) two orgs.
        Guid whisper = Guid.NewGuid(), whisperLarge = Guid.NewGuid(), embed = Guid.NewGuid(), embedAgain = Guid.NewGuid(),
            envChat = Guid.NewGuid(), envVision = Guid.NewGuid(),
            brokenA = Guid.NewGuid(), brokenB = Guid.NewGuid(), orgARow = Guid.NewGuid(), orgBRow = Guid.NewGuid(),
            orgAOther = Guid.NewGuid();

        // Rows a seed script wrote in one statement share created_at, so the oldest-first order falls to the ids. These
        // two are picked so the survivor (lowest provider id) owns the model that sorts SECOND (by model id): the
        // survivor's own model is then the one merged into its moved sibling.
        (Guid localChat, Guid localVision) = SurvivorOwnsTheYoungerModel();
        // The same tie on a real account (one key, encrypted twice): the crash's mechanics, where merging is right.
        (Guid byo, Guid byoVision) = SurvivorOwnsTheYoungerModel();

        string falToolRows = string.Join(",\n", falTools.Select((id, i) =>
            $" ('{id}', NULL, 'fal - tool {i}', 'FalAi', '{falCapabilities[i]}', 'https://fal.run', 'fal-ai/tool-{i}', '{falCipher}', '-fal', " +
            $"true, true, false, NULL, NULL, '2026-02-01 00:00:{i + 1:00}', '2026-02-01')"));

        await using NpgsqlDataSource data = NpgsqlDataSource.Create(connectionString);
        await using (NpgsqlCommand insert = data.CreateCommand($@"
INSERT INTO organizations (id, name, slug, kind, created_at, updated_at) VALUES
 ('{orgA}', 'Acme', 'acme', 'Team', now(), now()),
 ('{orgB}', 'Globex', 'globex', 'Team', now(), now());
INSERT INTO inference_providers (id, organization_id, name, kind, capability, endpoint, model_name, api_key_encrypted,
    api_key_last_four, is_default, is_enabled, personal_oauth_mode, timeout_seconds, extra_headers_json,
    created_at, updated_at) VALUES
 ('{anthChat}', NULL, 'Anthropic', 'Anthropic', 'Chat', 'https://api.anthropic.com', 'claude-sonnet-5-5', '{_secrets.Protect(AnthropicKey)}', 'opic', true, true, false, 120, NULL, '2026-01-01', '2026-01-02'),
 ('{anthVision}', NULL, 'Anthropic Vision', 'Anthropic', 'Vision', 'https://api.anthropic.com', 'claude-sonnet-5-5', '{_secrets.Protect(AnthropicKey)}', 'opic', true, true, false, 120, NULL, '2026-01-03', '2026-01-04'),
 ('{fal}', NULL, 'fal', 'FalAi', 'ImageEdit', 'https://fal.run', 'fal-ai/flux-pro/kontext', '{falCipher}', '-fal', false, true, false, NULL, '{{""X-Fal"":""1""}}', '2026-02-01', '2026-02-01'),
{falToolRows},
 ('{byo}', NULL, 'Acme gateway', 'OpenAICompatible', 'Chat', 'https://llm.acme.example/v1', 'qwen3', '{_secrets.Protect(ByoKey)}', '-byo', false, true, false, NULL, NULL, '2026-02-15', '2026-02-15'),
 ('{byoVision}', NULL, 'Acme gateway vision', 'OpenAICompatible', 'Vision', 'https://llm.acme.example/v1', 'qwen3', '{_secrets.Protect(ByoKey)}', '-byo', false, true, false, NULL, NULL, '2026-02-15', '2026-02-15'),
 ('{whisper}', NULL, 'Whisper', 'OpenAICompatible', 'Transcription', 'http://whisper:8000', 'whisper-1', NULL, NULL, true, true, false, NULL, NULL, '2026-03-01', '2026-03-01'),
 ('{whisperLarge}', NULL, 'Whisper large', 'OpenAICompatible', 'Transcription', 'http://whisper:8000', 'Systran/faster-whisper-large-v3', '', NULL, false, true, false, NULL, NULL, '2026-03-01', '2026-03-01'),
 ('{embed}', NULL, 'Embeddings', 'OpenAICompatible', 'Embedding', 'http://embeddings:80/v1', 'bge-m3', NULL, NULL, true, true, false, NULL, NULL, '2026-03-01', '2026-03-01'),
 ('{embedAgain}', NULL, 'Embeddings 2', 'OpenAICompatible', 'Embedding', 'http://embeddings:80/v1', 'bge-m3', NULL, NULL, false, true, false, NULL, NULL, '2026-03-01', '2026-03-01'),
 ('{localChat}', NULL, 'Local LLM', 'OpenAICompatible', 'Chat', 'http://llm:8000/v1', 'qwen3', NULL, NULL, false, true, false, NULL, NULL, '2026-03-05', '2026-03-05'),
 ('{localVision}', NULL, 'Local LLM vision', 'OpenAICompatible', 'Vision', 'http://llm:8000/v1', 'qwen3', NULL, NULL, false, true, false, NULL, NULL, '2026-03-05', '2026-03-05'),
 ('{envChat}', NULL, 'Gemini', 'Gemini', 'Chat', 'https://generativelanguage.googleapis.com', 'gemini-3-pro', '{envCipherA}', 'env:', false, true, false, NULL, NULL, '2026-03-07', '2026-03-07'),
 ('{envVision}', NULL, 'Gemini vision', 'Gemini', 'Vision', 'https://generativelanguage.googleapis.com', 'gemini-3-pro', '{envCipherB}', 'env:', false, true, false, NULL, NULL, '2026-03-08', '2026-03-08'),
 ('{brokenA}', NULL, 'Lost key', 'DeepSeek', 'Chat', 'https://api.deepseek.com', 'deepseek-chat', 'CfDJ8not-a-ciphertext', 'lost', false, true, false, NULL, NULL, '2026-03-09', '2026-03-09'),
 ('{brokenB}', NULL, 'Lost key 2', 'DeepSeek', 'Vision', 'https://api.deepseek.com', 'deepseek-chat', 'CfDJ8not-a-ciphertext', 'lost', false, true, false, NULL, NULL, '2026-03-10', '2026-03-10'),
 ('{orgARow}', '{orgA}', 'Acme LLM', 'OpenAICompatible', 'Chat', 'http://llm:8000/v1', 'qwen3', NULL, NULL, true, true, false, NULL, NULL, '2026-04-01', '2026-04-01'),
 ('{orgAOther}', '{orgA}', 'Acme LLM vision', 'OpenAICompatible', 'Vision', 'http://llm:8000/v1', 'qwen3', NULL, NULL, false, true, false, NULL, NULL, '2026-04-02', '2026-04-02'),
 ('{orgBRow}', '{orgB}', 'Globex LLM', 'OpenAICompatible', 'Chat', 'http://llm:8000/v1', 'qwen3', NULL, NULL, true, true, false, NULL, NULL, '2026-04-03', '2026-04-03');"))
        {
            await insert.ExecuteNonQueryAsync();
        }

        await using (InferenceApiDbContext db = NewDb(connectionString))
        {
            db.RateCards.AddRange([.. LegacyRateCardSeed.Cards(SeedFrom), .. LegacyRateCardSeed.ComputeCards(SeedFrom)]);
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
        }

        return new Fixture(connectionString, orgA, orgB, anthChat, anthVision, fal, falTools, byo, byoVision,
        [
            whisper, whisperLarge, embed, embedAgain, localChat, localVision, envChat, envVision, brokenA, brokenB, orgARow,
            orgAOther, orgBRow
        ]);
    }

    /// <summary>The model id <c>AddModelCatalog</c> gives a migrated row: <c>md5('model:' || id)::uuid</c>.</summary>
    private static Guid MigratedModelId(Guid providerId) =>
        Guid.Parse(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("model:" + providerId))));

    /// <summary>Two provider ids where the lower one's migrated model id sorts after the other's.</summary>
    private static (Guid Survivor, Guid Other) SurvivorOwnsTheYoungerModel()
    {
        while (true)
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            (Guid low, Guid high) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
            if (MigratedModelId(low).CompareTo(MigratedModelId(high)) > 0) return (low, high);
        }
    }

    /// <summary>
    /// Every capability id, through <see cref="ModelCapabilityView"/> as the resolver's store reads it (platform, then
    /// each organization): kind, endpoint, model code and the decrypted key (the raw value when it does not decrypt).
    /// </summary>
    private async Task<Dictionary<Guid, (InferenceProviderKind Kind, string Endpoint, string Model, string Key)>> ResolveAllAsync(
        Fixture fixture)
    {
        await using InferenceApiDbContext db = NewDb(fixture.ConnectionString);
        Dictionary<Guid, (InferenceProviderKind, string, string, string)> resolved = [];
        foreach (Guid? org in new Guid?[] { null, fixture.OrgA, fixture.OrgB })
        {
            foreach (ModelCapabilityView view in await ModelCapabilityView
                         .EnabledFor(db.ModelCapabilities.IgnoreQueryFilters().AsNoTracking(), org).ToListAsync())
            {
                string key = _secrets.TryUnprotect(view.ApiKeyEncrypted, out string plaintext) ? plaintext : view.ApiKeyEncrypted ?? string.Empty;
                resolved[view.Id] = (view.Kind, view.Endpoint, view.ModelName, key);
            }
        }

        return resolved;
    }

    private static async Task<(int Providers, int Models, int Capabilities)> CountAsync(string connectionString)
    {
        await using InferenceApiDbContext db = NewDb(connectionString);
        return (await db.InferenceProviders.IgnoreQueryFilters().CountAsync(), await db.InferenceModels.IgnoreQueryFilters().CountAsync(),
            await db.ModelCapabilities.IgnoreQueryFilters().CountAsync());
    }

    [PostgresFact]
    public async Task StagingLikeInstall_MergesOnlyVerifiableAccounts_AndEveryOldIdResolvesTheSame()
    {
        await using Database database = await CreateDatabaseAsync();
        Fixture fixture = await SeedAsync(database.ConnectionString);
        Dictionary<Guid, (InferenceProviderKind, string, string, string)> before = await ResolveAllAsync(fixture);
        before.Should().HaveCount(2 + 1 + 8 + 2 + fixture.Untouchable.Count);

        ConsolidationReport plan;
        await using (InferenceApiDbContext db = NewDb(fixture.ConnectionString))
        {
            plan = await Upgrader(db).PlanConsolidationAsync();
        }

        ModelCatalogUpgradeResult first;
        await using (InferenceApiDbContext db = NewDb(fixture.ConnectionString))
        {
            first = await Upgrader(db).RunAsync();
        }

        first.Consolidated.Groups.Should().BeEquivalentTo(plan.Groups, "the dry run reports the groups the apply uses");
        first.Consolidated.Groups.Should().BeEquivalentTo(
        [
            new ConsolidationGroup(fixture.AnthChat, "Anthropic", [fixture.AnthVision], 1),
            new ConsolidationGroup(fixture.Fal, "fal", fixture.FalTools, 8),
            new ConsolidationGroup(fixture.Byo, "Acme gateway", [fixture.ByoVision], 1)
        ], "only a present, decryptable, equal key in one scope is one account");

        await using (InferenceApiDbContext db = NewDb(fixture.ConnectionString))
        {
            List<InferenceProvider> providers = await db.InferenceProviders.IgnoreQueryFilters().AsNoTracking()
                .Include(p => p.Models).ThenInclude(m => m.Capabilities).ToListAsync();
            InferenceProvider gateway = providers.Single(p => p.Id == fixture.Byo);
            gateway.Models.Should().ContainSingle("the survivor's own model joined its same-code sibling")
                .Which.Capabilities.Select(c => c.Id).Should().BeEquivalentTo([fixture.Byo, fixture.ByoVision]);

            foreach (Guid id in fixture.Untouchable)
            {
                InferenceProvider own = providers.Single(p => p.Models.Any(m => m.Capabilities.Any(c => c.Id == id)));
                own.Id.Should().Be(id, "key-less, env:, undecryptable and org rows keep their own 1:1 provider");
                own.Models.Should().ContainSingle().Which.Capabilities.Should().ContainSingle();
            }
        }

        (await ResolveAllAsync(fixture)).Where(kv => kv.Key != WellKnownModelCatalog.ComputeCapabilityId)
            .Should().BeEquivalentTo(before, "every old id resolves to the same kind, endpoint, model and key");

        (int, int, int) counts = await CountAsync(fixture.ConnectionString);
        await using (InferenceApiDbContext db = NewDb(fixture.ConnectionString))
        {
            ModelCatalogUpgradeResult second = await Upgrader(db).RunAsync();
            second.Changed.Should().BeFalse("a second run is a no-op");
        }

        (await CountAsync(fixture.ConnectionString)).Should().Be(counts);
    }

    /// <summary>Two API replicas booting at once: both runs complete, one consolidates, the result is the same.</summary>
    [PostgresFact]
    public async Task TwoConcurrentRuns_EndWithOneConsolidatedResult_AndNoException()
    {
        await using Database database = await CreateDatabaseAsync();
        Fixture fixture = await SeedAsync(database.ConnectionString);
        Dictionary<Guid, (InferenceProviderKind, string, string, string)> before = await ResolveAllAsync(fixture);

        await using InferenceApiDbContext dbA = NewDb(fixture.ConnectionString);
        await using InferenceApiDbContext dbB = NewDb(fixture.ConnectionString);
        ModelCatalogUpgradeResult[] results = await Task.WhenAll(Task.Run(() => Upgrader(dbA).RunAsync()), Task.Run(() => Upgrader(dbB).RunAsync()));

        results.Count(r => r.Consolidated.Groups.Count > 0).Should().Be(1, "the second run re-reads inside the lock and finds nothing to merge");
        results.Count(r => r.PricesStamped).Should().Be(1);
        results.SelectMany(r => r.Consolidated.Groups).Select(g => g.SurvivorId).Should().BeEquivalentTo([fixture.AnthChat, fixture.Fal, fixture.Byo]);
        (await ResolveAllAsync(fixture)).Where(kv => kv.Key != WellKnownModelCatalog.ComputeCapabilityId).Should().BeEquivalentTo(before);
        (await CountAsync(fixture.ConnectionString)).Providers.Should().Be(1 + 1 + 1 + 1 + fixture.Untouchable.Count,
            "Anthropic, fal, the gateway, the system compute provider and the untouched rows");
    }

    /// <summary>Fails the consolidation's delete step: the shape a merge bug takes at boot.</summary>
    private sealed class FailOnProviderDelete : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<InferenceProvider>().Any(e => e.State == EntityState.Deleted))
                throw new DbUpdateConcurrencyException("forced by the test");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CapturingLogger : ILogger<ModelCatalogUpgrader>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [PostgresFact]
    public async Task AFailureInsideApply_LeavesTheCatalogUnchanged_LogsAndCounts_AndTheBootStepReturns()
    {
        await using Database database = await CreateDatabaseAsync();
        Fixture fixture = await SeedAsync(database.ConnectionString);
        Dictionary<Guid, (InferenceProviderKind, string, string, string)> before = await ResolveAllAsync(fixture);
        (int Providers, int Models, int Capabilities) countsBefore = await CountAsync(fixture.ConnectionString);

        long failures = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reelbolt.model_catalog.consolidation_failed") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref failures, value));
        listener.Start();

        CapturingLogger logger = new();
        ModelCatalogUpgradeResult result;
        await using (InferenceApiDbContext db = NewDb(fixture.ConnectionString, new FailOnProviderDelete()))
        {
            result = await Upgrader(db, logger).RunAsync();
        }

        result.Consolidated.Groups.Should().BeEmpty("nothing was merged");
        result.PricesStamped.Should().BeTrue("the other steps still ran and committed");
        Interlocked.Read(ref failures).Should().Be(1);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains(nameof(DbUpdateConcurrencyException))
                                                                       && e.Message.Contains(fixture.AnthChat.ToString()));
        logger.Entries.Should().NotContain(e => e.Message.Contains(AnthropicKey) || e.Message.Contains(FalKey) || e.Message.Contains(ByoKey));

        (await CountAsync(fixture.ConnectionString)).Should().Be((countsBefore.Providers + 1, countsBefore.Models + 1, countsBefore.Capabilities + 1),
            "no half-merged state: only the compute provider was added");
        (await ResolveAllAsync(fixture)).Where(kv => kv.Key != WellKnownModelCatalog.ComputeCapabilityId)
            .Should().BeEquivalentTo(before);

        // The next boot, without the fault, merges.
        await using (InferenceApiDbContext db = NewDb(fixture.ConnectionString))
        {
            (await Upgrader(db).RunAsync()).Consolidated.Groups.Should().HaveCount(3);
        }
    }

    [PostgresFact]
    public async Task CancellationOnShutdown_StillPropagates()
    {
        await using Database database = await CreateDatabaseAsync();
        Fixture fixture = await SeedAsync(database.ConnectionString);
        await using InferenceApiDbContext db = NewDb(fixture.ConnectionString);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Func<Task> run = () => Upgrader(db).RunAsync(cts.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
    }
}
