#nullable enable
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Tests.Pricing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// The model catalog upgrade (docs/design/model-catalog.md §4) end to end on a <b>real PostgreSQL</b>: a realistic
/// pre-catalog install with REAL Data Protection ciphertexts (Anthropic Chat and Vision encrypted separately from one
/// key, fal image-tool rows holding the fal row's ciphertext byte for byte) and the pre-catalog rate cards (plus one an
/// operator edited), migrated to the catalog, then upgraded. Review focus 1: the shared accounts come out as one provider
/// each and every old id still resolves — through the real resolver over the real store — to the same kind, endpoint,
/// model and plaintext key. Skipped unless <c>REELBOLT_TEST_PG</c> is set (see <see cref="PostgresFactAttribute"/>).
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class ModelCatalogUpgradeRealPostgresTests
{
    private const string PreviousMigration = "20261011181415_AddAssistantAutoApprove";
    private static readonly DateTime SeedFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Obvious placeholders: what matters is that each is encrypted for real and must decrypt to itself afterwards.
    private const string AnthropicKey = "placeholder-anthropic";
    private const string FalKey = "placeholder-fal";
    private const string ByoKey = "placeholder-byo";
    private const string FishKey = "placeholder-fish";
    private const string HiggsKey = "placeholder-higgs";
    private const string MiniMaxKey = "placeholder-minimax";

    private sealed class TenantAccessor : InferenceTenantAccessorBase
    {
        public Guid? Org { get; set; }

        protected override Guid? AmbientOrganizationId => Org;
    }

    [PostgresFact]
    public async Task Upgrade_RealisticInstall_ConsolidatesAndResolvesSame()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_catalog_up_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(baseConnection) { Database = dbName };
        csb.Database.Should().StartWith("reelbolt_catalog_up_test_");
        try
        {
            ISecretProtector secrets = new DataProtectionSecretProtector(
                new EphemeralDataProtectionProvider(), NullLogger<DataProtectionSecretProtector>.Instance);
            TenantAccessor tenant = new();
            IConfiguration configuration = new ConfigurationBuilder().Build();
            ServiceCollection services = new();
            services.AddLogging();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(csb.ConnectionString));
            services.AddScoped<IInferenceProviderStore, InferenceApiProviderStore>();
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using NpgsqlDataSource data = NpgsqlDataSource.Create(csb.ConnectionString);

            async Task Exec(string sql)
            {
                await using NpgsqlCommand cmd = data.CreateCommand(sql);
                await cmd.ExecuteNonQueryAsync();
            }

            // ---- the pre-catalog schema and a realistic install ----
            using (IServiceScope scope = provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>().GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            Guid org = Guid.NewGuid();
            Guid anthChat = Guid.NewGuid(), anthVision = Guid.NewGuid(), fal = Guid.NewGuid(),
                byo = Guid.NewGuid(), disabled = Guid.NewGuid(), higgs = Guid.NewGuid(),
                speech = Guid.NewGuid(), spareSpeech = Guid.NewGuid(), minimax = Guid.NewGuid();
            // The fal row plus the eight image-tool rows the pre-catalog CreateImageToolRowsAsync wrote (one per image
            // capability), each holding the fal row's ciphertext byte for byte and a distinct model code.
            List<(Guid Id, InferenceProviderCapability Capability, string Name, string Model, bool IsDefault)> falTools =
            [
                (Guid.NewGuid(), InferenceProviderCapability.ImageGeneration, "fal - Generate", "blackforestlabs/flux-3/text-to-image", true),
                (Guid.NewGuid(), InferenceProviderCapability.ImageEdit, "fal - Edit", "fal-ai/qwen-image-edit-2511", false),
                (Guid.NewGuid(), InferenceProviderCapability.ImageErase, "fal - Remove objects", "fal-ai/object-removal/mask", true),
                (Guid.NewGuid(), InferenceProviderCapability.ImageFill, "fal - Fill", "fal-ai/flux-pro/v1/fill", true),
                (Guid.NewGuid(), InferenceProviderCapability.ImageExpand, "fal - Expand", "fal-ai/bria/expand", true),
                (Guid.NewGuid(), InferenceProviderCapability.ImageBackgroundReplace, "fal - Replace background", "fal-ai/bria/background/replace", true),
                (Guid.NewGuid(), InferenceProviderCapability.ImageRelight, "fal - Relight", "fal-ai/image-apps-v2/relighting", true),
                (Guid.NewGuid(), InferenceProviderCapability.ImageUpscale, "fal - Upscale", "fal-ai/seedvr/upscale/image", false),
            ];
            string anthChatCipher = secrets.Protect(AnthropicKey), anthVisionCipher = secrets.Protect(AnthropicKey);
            anthChatCipher.Should().NotBe(anthVisionCipher, "Data Protection uses a random IV, so SQL could never compare them");
            string falCipher = secrets.Protect(FalKey);
            string byoCipher = secrets.Protect(ByoKey), higgsCipher = secrets.Protect(HiggsKey), mmCipher = secrets.Protect(MiniMaxKey);
            string speechCipher = secrets.Protect(FishKey), spareSpeechCipher = secrets.Protect(FishKey);

            // Expected resolution of every old id: (kind, endpoint, model, plaintext key).
            Dictionary<Guid, (InferenceProviderKind Kind, string Endpoint, string Model, string Key)> expected = new()
            {
                [anthChat] = (InferenceProviderKind.Anthropic, "https://api.anthropic.com", "claude-sonnet-5-5", AnthropicKey),
                [anthVision] = (InferenceProviderKind.Anthropic, "https://api.anthropic.com", "claude-sonnet-5-5", AnthropicKey),
                [fal] = (InferenceProviderKind.FalAi, "https://fal.run", "fal-ai/flux-pro/kontext", FalKey),
                [byo] = (InferenceProviderKind.OpenAICompatible, "https://llm.acme.example/v1", "qwen3", ByoKey),
                [higgs] = (InferenceProviderKind.Higgsfield, "https://api.higgsfield.ai", "dop-turbo", HiggsKey),
                [speech] = (InferenceProviderKind.FishAudio, "https://api.fish.audio", "s1", FishKey),
                [spareSpeech] = (InferenceProviderKind.FishAudio, "https://api.fish.audio", "s2-pro", FishKey),
                [minimax] = (InferenceProviderKind.MiniMax, "https://api.minimax.io", "MiniMax-H3", MiniMaxKey),
            };
            foreach ((Guid id, _, _, string model, _) in falTools)
            {
                expected[id] = (InferenceProviderKind.FalAi, "https://fal.run", model, FalKey);
            }

            string falToolRows = string.Join(",\n", falTools.Select((t, i) =>
                $" ('{t.Id}', NULL, '{t.Name}', 'FalAi', '{t.Capability}', 'https://fal.run', '{t.Model}', '{falCipher}', '-fal', " +
                $"{(t.IsDefault ? "true" : "false")}, true, false, NULL, NULL, '2026-02-01 00:00:{i + 1:00}', '2026-02-01')"));

            await Exec($@"
INSERT INTO organizations (id, name, slug, kind, created_at, updated_at) VALUES ('{org}', 'Acme', 'acme', 'Team', now(), now());
INSERT INTO inference_providers (id, organization_id, name, kind, capability, endpoint, model_name, api_key_encrypted,
    api_key_last_four, is_default, is_enabled, personal_oauth_mode, timeout_seconds, extra_headers_json,
    created_at, updated_at) VALUES
 ('{anthChat}', NULL, 'Anthropic', 'Anthropic', 'Chat', 'https://api.anthropic.com', 'claude-sonnet-5-5', '{anthChatCipher}', 'opic', true, true, false, 120, NULL, '2026-01-01', '2026-01-02'),
 ('{anthVision}', NULL, 'Anthropic Vision', 'Anthropic', 'Vision', 'https://api.anthropic.com', 'claude-sonnet-5-5', '{anthVisionCipher}', 'opic', true, true, false, 120, NULL, '2026-01-03', '2026-01-04'),
 ('{fal}', NULL, 'fal', 'FalAi', 'ImageEdit', 'https://fal.run', 'fal-ai/flux-pro/kontext', '{falCipher}', '-fal', true, true, false, NULL, NULL, '2026-02-01', '2026-02-01'),
{falToolRows},
 ('{byo}', '{org}', 'Org LLM', 'OpenAICompatible', 'Chat', 'https://llm.acme.example/v1', 'qwen3', '{byoCipher}', '-byo', true, true, false, NULL, '{{""X-Org"":""acme""}}', '2026-03-01', '2026-03-01'),
 ('{disabled}', NULL, 'Old', 'OpenAICompatible', 'Chat', 'https://old.example/v1', 'old-model', NULL, NULL, false, false, false, NULL, NULL, '2025-01-01', '2025-01-01'),
 ('{higgs}', NULL, 'Higgsfield', 'Higgsfield', 'VideoGeneration', 'https://api.higgsfield.ai', 'dop-turbo', '{higgsCipher}', 'iggs', true, true, false, NULL, NULL, '2026-04-01', '2026-04-01'),
 ('{minimax}', NULL, 'MiniMax', 'MiniMax', 'VideoGeneration', 'https://api.minimax.io', 'MiniMax-H3', '{mmCipher}', 'imax', false, true, false, NULL, NULL, '2026-04-02', '2026-04-02'),
 ('{speech}', NULL, 'Voices', 'FishAudio', 'SpeechSynthesis', 'https://api.fish.audio', 's1', '{speechCipher}', 'fish', true, true, false, NULL, NULL, '2026-05-01', '2026-05-01'),
 ('{spareSpeech}', NULL, 'Spare voices', 'FishAudio', 'SpeechSynthesis', 'https://api.fish.audio', 's2-pro', '{spareSpeechCipher}', 'fish', false, true, false, NULL, NULL, '2026-05-02', '2026-05-02');");

            // The pre-catalog seed, as the old seeder wrote it, plus one card an operator priced by hand.
            using (IServiceScope scope = provider.CreateScope())
            {
                InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                List<RateCard> cards = [.. LegacyRateCardSeed.Cards(SeedFrom), .. LegacyRateCardSeed.ComputeCards(SeedFrom)];
                RateCard edited = cards.Single(c => c.ProviderKind == InferenceProviderKind.FishAudio
                    && c.UsageKind == UsageKind.TtsInputBytes && c.ModelPattern == "s1");
                edited.CreditsMillisPerUnit = 20m;
                edited.Notes = "an operator priced this by hand";
                db.RateCards.AddRange(cards);
                await db.SaveChangesAsync();
            }

            // ---- Up, then the boot-time upgrade ----
            ModelCatalogUpgradeResult first;
            using (IServiceScope scope = provider.CreateScope())
            {
                InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                await db.Database.MigrateAsync();
                first = await new ModelCatalogUpgrader(db, secrets, configuration, NullLogger<ModelCatalogUpgrader>.Instance).RunAsync();
            }

            Console.WriteLine($"[upgrade 1] compute created={first.ComputeProviderCreated}, prices ported={first.PricesPorted}, " +
                              $"stamped={first.PricesStamped}, video variants={first.VideoVariantsAdded}, groups=" +
                              string.Join("; ", first.Consolidated.Groups.Select(g =>
                                  $"{g.SurvivorName} <- {g.MergedProviderIds.Count} provider(s), {g.MergedModelCount} model(s)")));
            first.ComputeProviderCreated.Should().BeTrue();
            first.PricesStamped.Should().BeTrue();
            first.PricesPorted.Should().BePositive();
            first.VideoVariantsAdded.Should().Be(2, "MiniMax-H3 gains 768P and 2K");
            first.Consolidated.Groups.Select(g => g.SurvivorName).Should().BeEquivalentTo(["Anthropic", "fal", "Voices"]);

            using (IServiceScope scope = provider.CreateScope())
            {
                InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                List<InferenceProvider> providers = await db.InferenceProviders.IgnoreQueryFilters().AsNoTracking()
                    .Include(p => p.Models).ThenInclude(m => m.Capabilities).ToListAsync();

                InferenceProvider anthropic = providers.Single(p => p.Kind == InferenceProviderKind.Anthropic);
                anthropic.Id.Should().Be(anthChat);
                anthropic.Models.Should().ContainSingle().Which.Capabilities.Select(c => c.Id).Should().BeEquivalentTo([anthChat, anthVision]);

                InferenceProvider falProvider = providers.Single(p => p.Kind == InferenceProviderKind.FalAi);
                falProvider.Id.Should().Be(fal, "the oldest of the nine survives");
                falProvider.Models.Should().HaveCount(9, "fal + eight image-tool rows, nine distinct model codes");
                first.Consolidated.Groups.Single(g => g.SurvivorId == fal).MergedProviderIds.Should()
                    .BeEquivalentTo(falTools.Select(t => t.Id));
                falProvider.ApiKeyEncrypted.Should().Be(falCipher);

                providers.Single(p => p.Kind == InferenceProviderKind.FishAudio).Models.Should().HaveCount(2);

                InferenceProvider byoProvider = providers.Single(p => p.Id == byo);
                (byoProvider.OrganizationId, byoProvider.ApiKeyEncrypted, byoProvider.Models.Count)
                    .Should().Be(((Guid?)org, byoCipher, 1), "BYO untouched");
                byoProvider.ExtraHeadersJson.Should().Contain("acme");
                providers.Single(p => p.Id == disabled).IsEnabled.Should().BeFalse();

                InferenceProvider compute = providers.Single(p => p.IsSystem);
                compute.Id.Should().Be(WellKnownModelCatalog.ComputeProviderId);
                (await db.ModelCapabilityPrices.CountAsync(p => p.ModelCapabilityId == WellKnownModelCatalog.ComputeCapabilityId)).Should().Be(9);

                IReadOnlyList<PriceCoverageGap> gaps = await PriceCoverage.FindAsync(db, DateTime.UtcNow);
                gaps.Should().BeEmpty("every enabled platform capability is priced: " +
                    string.Join("; ", gaps.Select(g => $"{g.ModelDisplayName} {g.Capability} {string.Join(",", g.MissingUnits)}")));
                (await db.ModelCapabilityPrices.CountAsync(p => p.ModelCapabilityId == byo)).Should().Be(0, "BYO is never priced");

                ModelCapabilityPrice handPriced = await db.ModelCapabilityPrices.SingleAsync(p => p.ModelCapabilityId == speech);
                handPriced.CreditsMillisPerUnitOverride.Should().Be(20m, "the operator's credits survive as an override");
                handPriced.Notes.Should().Be("an operator priced this by hand");
                (await db.ModelCapabilityPrices.SingleAsync(p => p.ModelCapabilityId == spareSpeech && p.Unit == UsageKind.TtsInputBytes))
                    .CreditsMillisPerUnitOverride.Should().BeNull("a seeded card is cost-derived and picks up the markup");
                (await db.ModelCapabilityPrices.CountAsync(p => p.ModelCapabilityId == minimax && p.Variant == "2K")).Should().Be(1);
                Console.WriteLine($"[upgrade 1] providers={providers.Count}, models={providers.Sum(p => p.Models.Count)}, " +
                                  $"capabilities={providers.Sum(p => p.Models.Sum(m => m.Capabilities.Count))}, " +
                                  $"prices={await db.ModelCapabilityPrices.CountAsync()}, coverage gaps={gaps.Count}");
            }

            // ---- every old id resolves exactly as it did, through the real resolver over the real store ----
            InferenceProviderResolver resolver = new(provider.GetRequiredService<IServiceScopeFactory>(), configuration, secrets,
                NullLogger<InferenceProviderResolver>.Instance, tenant);
            List<(Guid Id, InferenceProviderKind Kind, string Endpoint, string Model, string Key)> resolved =
            [
                Shape(anthChat, await resolver.ResolveChatAsync(anthChat, default)),
                Shape(anthVision, await resolver.ResolveVisionAsync(anthVision, default)),
                Shape(fal, await resolver.ResolveImageEditAsync(fal, default)),
                Shape(higgs, await resolver.ResolveVideoGenerationAsync(higgs, default)),
                Shape(minimax, await resolver.ResolveVideoGenerationAsync(minimax, default)),
                Shape(speech, await resolver.ResolveSpeechSynthesisAsync(speech, default)),
                Shape(spareSpeech, await resolver.ResolveSpeechSynthesisAsync(spareSpeech, default)),
            ];
            foreach ((Guid id, InferenceProviderCapability capability, _, _, _) in falTools)
            {
                ResolvedImageProvider? tool = capability switch
                {
                    InferenceProviderCapability.ImageGeneration => await resolver.ResolveImageGenerationAsync(id, default),
                    InferenceProviderCapability.ImageEdit => await resolver.ResolveImageEditAsync(id, default),
                    _ => await resolver.ResolveImageJobAsync(capability, id, default),
                };
                resolved.Add(Shape(id, tool));
            }

            tenant.Org = org;
            resolver.InvalidateAll();
            resolved.Add(Shape(byo, await resolver.ResolveChatAsync(byo, default)));

            foreach ((Guid id, InferenceProviderKind kind, string endpoint, string model, string key) in resolved)
            {
                (kind, endpoint, model, key).Should().Be(expected[id], $"capability {id} must resolve as its pre-catalog row did");
            }

            resolved.Should().HaveCount(expected.Count);
            Console.WriteLine($"[resolve] {resolved.Count} old ids resolved to the same kind/endpoint/model/key");

            // ---- and a second boot changes nothing ----
            using (IServiceScope scope = provider.CreateScope())
            {
                InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                int pricesBefore = await db.ModelCapabilityPrices.CountAsync();
                ModelCatalogUpgradeResult second =
                    await new ModelCatalogUpgrader(db, secrets, configuration, NullLogger<ModelCatalogUpgrader>.Instance).RunAsync();
                second.Changed.Should().BeFalse();
                (await db.ModelCapabilityPrices.CountAsync()).Should().Be(pricesBefore);
                (await db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(8);
                Console.WriteLine($"[upgrade 2] changed={second.Changed}");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Two API replicas booting together: each runs the upgrader, and the advisory lock makes the second wait for the
    /// first and then find the stamp, so the one-time port runs once. Held here from a third connection, so the test
    /// can see both runs wait.
    /// </summary>
    [PostgresFact]
    public async Task RunAsync_WaitsForTheCatalogLock_AndOnlyOneOfTwoConcurrentRunsPortsThePrices()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_catalog_lock_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(baseConnection) { Database = dbName };
        try
        {
            ISecretProtector secrets = new DataProtectionSecretProtector(
                new EphemeralDataProtectionProvider(), NullLogger<DataProtectionSecretProtector>.Instance);
            IConfiguration configuration = new ConfigurationBuilder().Build();
            InferenceApiDbContext NewDb() => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options);
            await using (InferenceApiDbContext migrate = NewDb())
            {
                await migrate.GetService<IMigrator>().MigrateAsync();
            }

            await using NpgsqlDataSource data = NpgsqlDataSource.Create(csb.ConnectionString);
            await using (NpgsqlCommand seed = data.CreateCommand($@"
INSERT INTO inference_providers (id, organization_id, name, kind, endpoint, is_enabled, is_system, personal_oauth_mode, created_at, updated_at)
  VALUES ('{Guid.NewGuid()}', NULL, 'Anthropic', 'Anthropic', 'https://api.anthropic.com', true, false, false, now(), now())
  RETURNING id"))
            {
                Guid account = (Guid)(await seed.ExecuteScalarAsync())!;
                await using NpgsqlCommand rest = data.CreateCommand($@"
INSERT INTO inference_models (id, provider_id, organization_id, model_code, display_name, is_enabled, created_at, updated_at)
  VALUES ('{Guid.NewGuid()}', '{account}', NULL, 'claude-sonnet-5-5', 'Claude', true, now(), now());
INSERT INTO model_capabilities (id, model_id, organization_id, capability, is_default, is_enabled, price_source, created_at, updated_at)
  SELECT '{Guid.NewGuid()}', id, NULL, 'Chat', true, true, 'Table', now(), now() FROM inference_models;");
                await rest.ExecuteNonQueryAsync();
            }

            await using InferenceApiDbContext cards = NewDb();
            cards.RateCards.AddRange(LegacyRateCardSeed.Cards(SeedFrom).Concat(LegacyRateCardSeed.ComputeCards(SeedFrom)));
            await cards.SaveChangesAsync();

            // Another replica holds the catalog lock.
            await using NpgsqlConnection holder = await data.OpenConnectionAsync();
            await using NpgsqlTransaction held = await holder.BeginTransactionAsync();
            await using (NpgsqlCommand take = new($"SELECT pg_advisory_xact_lock({ModelCatalogUpgrader.AdvisoryLockKey})", holder, held))
            {
                await take.ExecuteNonQueryAsync();
            }

            await using InferenceApiDbContext dbA = NewDb();
            await using InferenceApiDbContext dbB = NewDb();
            Task<ModelCatalogUpgradeResult> runA = new ModelCatalogUpgrader(dbA, secrets, configuration, NullLogger<ModelCatalogUpgrader>.Instance).RunAsync();
            Task<ModelCatalogUpgradeResult> runB = new ModelCatalogUpgrader(dbB, secrets, configuration, NullLogger<ModelCatalogUpgrader>.Instance).RunAsync();

            Task both = Task.WhenAll(runA, runB);
            (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(both);
            runA.IsCompleted.Should().BeFalse("the run waits for the lock another replica holds");
            runB.IsCompleted.Should().BeFalse();
            await using (NpgsqlCommand stamp = data.CreateCommand("SELECT count(*) FROM model_capability_prices"))
            {
                ((long)(await stamp.ExecuteScalarAsync())!).Should().Be(0L, "nothing was written while the lock was held");
            }

            await held.CommitAsync();
            ModelCatalogUpgradeResult[] results = await Task.WhenAll(runA, runB);

            results.Count(r => r.PricesStamped).Should().Be(1, "the second run re-reads the stamp inside the lock");
            results.Count(r => r.ComputeProviderCreated).Should().Be(1);
            await using (NpgsqlCommand count = data.CreateCommand("SELECT count(*) FROM inference_providers WHERE is_system"))
            {
                ((long)(await count.ExecuteScalarAsync())!).Should().Be(1L);
            }
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static (Guid, InferenceProviderKind, string, string, string) Shape(Guid id, ResolvedInferenceProvider? p) =>
        (id, p!.Kind, p.Endpoint, p.ModelName, p.ApiKey);

    private static (Guid, InferenceProviderKind, string, string, string) Shape(Guid id, ResolvedImageProvider? p) =>
        (id, p!.Kind, p.Endpoint, p.ModelName, p.ApiKey);

    private static (Guid, InferenceProviderKind, string, string, string) Shape(Guid id, ResolvedVideoGenerationProvider? p) =>
        (id, p!.Kind, p.Endpoint, p.ModelName, p.ApiKey);

    private static (Guid, InferenceProviderKind, string, string, string) Shape(Guid id, ResolvedSpeechSynthesisProvider? p) =>
        (id, p!.Kind, p.Endpoint, p.ModelName, p.ApiKey);
}
