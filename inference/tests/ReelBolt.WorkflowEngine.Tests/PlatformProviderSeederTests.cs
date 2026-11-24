using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// F6: <see cref="PlatformProviderSeeder"/> upserts the platform (null-org) model catalog from config: entries sharing
/// one kind, endpoint and key land under one provider account (docs/design/model-catalog.md §6).
/// </summary>
public class PlatformProviderSeederTests
{
    /// <summary>Reversible, so the seeder's decrypted-key comparison is exercised for real.</summary>
    private sealed class ReversibleSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = string.Empty;
            if (ciphertext is null || !ciphertext.StartsWith("enc:", StringComparison.Ordinal)) return false;
            plaintext = ciphertext[4..];
            return true;
        }
    }

    private sealed class Harness
    {
        public required InferenceApiDbContext Db { get; init; }
        public required InferenceProviderAdminService Admin { get; init; }
        public required Mock<IInferenceProviderResolver> Resolver { get; init; }
    }

    private static Harness Create()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        InferenceApiDbContext db = new(options);
        ReversibleSecrets secrets = new();
        Mock<IInferenceProviderResolver> resolver = new();
        InferenceProviderTester tester = new(
            db, secrets, new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new EmbeddingClientFactory(), new Mock<IDecisionClientFactory>().Object,
            new Mock<IVideoGenerationClientFactory>().Object, new Mock<ISpeechSynthesisClientFactory>().Object,
            NullLogger<InferenceProviderTester>.Instance);
        return new Harness
        {
            Db = db,
            Resolver = resolver,
            Admin = new InferenceProviderAdminService(db, secrets, tester, resolver.Object)
        };
    }

    private static IConfiguration Config(params Dictionary<string, string?>[] entries)
    {
        Dictionary<string, string?> flat = [];
        for (int i = 0; i < entries.Length; i++)
            foreach ((string k, string? v) in entries[i]) flat[$"PlatformProviders:{i}:{k}"] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(flat).Build();
    }

    private static Dictionary<string, string?> Asr(string key = "sk-asr-1234") => new()
    {
        ["Name"] = "cloud-asr",
        ["Kind"] = "OpenAICompatible",
        ["Capability"] = "Transcription",
        ["Endpoint"] = "https://198.51.100.7/v1",
        ["Model"] = "whisper-large-v3",
        ["ApiKey"] = key,
        ["IsDefault"] = "true"
    };

    private static Dictionary<string, string?> Embedding() => new()
    {
        ["Name"] = "cloud-embeddings",
        ["Kind"] = "OpenAICompatible",
        ["Capability"] = "Embedding",
        ["Endpoint"] = "https://198.51.100.8/v1",
        ["Model"] = "bge-m3",
        ["ApiKey"] = "sk-emb-5678",
        ["IsDefault"] = "true"
    };

    private static Task<int> Seed(Harness h, IConfiguration c) =>
        PlatformProviderSeeder.SeedAsync(h.Admin, c, NullLogger.Instance);

    private static Task<List<ModelCapabilityView>> Rows(Harness h) =>
        h.Db.FlatProviders().IgnoreQueryFilters().ToListAsync();

    [Fact]
    public async Task Empty_section_is_a_no_op()
    {
        Harness h = Create();
        (await Seed(h, new ConfigurationBuilder().Build())).Should().Be(0);
        // Blank-name entries are the unconfigured compose slots.
        (await Seed(h, Config(new Dictionary<string, string?> { ["Name"] = "", ["Kind"] = "OpenAICompatible" }))).Should().Be(0);
        (await Rows(h)).Should().BeEmpty();
        h.Resolver.Verify(r => r.InvalidateAll(), Times.Never);
    }

    [Fact]
    public async Task Creates_platform_rows_and_is_idempotent()
    {
        Harness h = Create();
        IConfiguration c = Config(Asr(), Embedding());

        (await Seed(h, c)).Should().Be(2);
        (await Seed(h, c)).Should().Be(2);

        List<ModelCapabilityView> rows = await Rows(h);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.OrganizationId == null && r.IsDefault && r.IsEnabled);
        rows.Single(r => r.Name == "cloud-asr").Capability.Should().Be(InferenceProviderCapability.Transcription);
        rows.Single(r => r.Name == "cloud-embeddings").Capability.Should().Be(InferenceProviderCapability.Embedding);
        h.Resolver.Verify(r => r.InvalidateAll(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Changed_config_updates_the_existing_row_in_place()
    {
        Harness h = Create();
        await Seed(h, Config(Asr()));
        Dictionary<string, string?> changed = Asr();
        changed["Model"] = "whisper-v4";
        await Seed(h, Config(changed));

        List<ModelCapabilityView> rows = await Rows(h);
        rows.Should().ContainSingle().Which.ModelName.Should().Be("whisper-v4");
    }

    [Fact]
    public async Task Empty_key_never_overwrites_a_stored_key()
    {
        Harness h = Create();
        await Seed(h, Config(Asr("sk-asr-1234")));
        string stored = (await Rows(h)).Single().ApiKeyEncrypted!;

        await Seed(h, Config(Asr("")));

        ModelCapabilityView row = (await Rows(h)).Single();
        row.ApiKeyEncrypted.Should().Be(stored);
        row.ApiKeyLastFour.Should().Be("1234");
    }

    [Fact]
    public async Task Invalid_entries_are_rejected_without_writing_and_do_not_block_valid_ones()
    {
        Harness h = Create();
        Dictionary<string, string?> deepseekEmbedding = Embedding();
        deepseekEmbedding["Name"] = "bad-kind";
        deepseekEmbedding["Kind"] = "DeepSeek"; // not on the Embedding allowlist
        Dictionary<string, string?> privateEndpoint = Asr();
        privateEndpoint["Name"] = "bad-endpoint";
        privateEndpoint["Kind"] = "AzureOpenAI"; // no private-address exemption
        privateEndpoint["Endpoint"] = "http://10.0.0.5";
        Dictionary<string, string?> envSentinel = Asr("env:");
        envSentinel["Name"] = "bad-env";
        envSentinel["IsDefault"] = "false";

        int applied = await Seed(h, Config(deepseekEmbedding, privateEndpoint, envSentinel, Embedding()));

        applied.Should().Be(1);
        (await Rows(h)).Should().ContainSingle().Which.Name.Should().Be("cloud-embeddings");
    }

    [Fact]
    public async Task Rows_absent_from_config_are_never_deleted()
    {
        Harness h = Create();
        await Seed(h, Config(Asr(), Embedding()));
        await Seed(h, Config(Asr()));
        (await Rows(h)).Select(r => r.Name).Should().BeEquivalentTo("cloud-asr", "cloud-embeddings");
    }

    [Fact]
    public async Task Env_sentinel_is_accepted_for_a_kind_that_supports_it()
    {
        Harness h = Create();
        Dictionary<string, string?> claude = new()
        {
            ["Name"] = "claude",
            ["Kind"] = "Anthropic",
            ["Capability"] = "Chat",
            ["Endpoint"] = "https://api.anthropic.com",
            ["Model"] = "claude-sonnet",
            ["ApiKey"] = "env:"
        };
        (await Seed(h, Config(claude))).Should().Be(1);
        (await Rows(h)).Single().Kind.Should().Be(InferenceProviderKind.Anthropic);
    }

    [Fact]
    public async Task An_ElevenLabs_entry_seeds_nothing_because_the_kind_is_dormant()
    {
        // Product decision 7: music and sound effects through fal only; no ElevenLabs rows and no keys, with or without env:.
        Harness h = Create();
        Dictionary<string, string?> music = new()
        {
            ["Name"] = "eleven-music",
            ["Kind"] = "ElevenLabs",
            ["Capability"] = "MusicGeneration",
            ["Endpoint"] = "https://api.elevenlabs.io",
            ["Model"] = "music_v2_5",
            ["ApiKey"] = "env:"
        };
        Dictionary<string, string?> keyed = new(music) { ["Name"] = "eleven-keyed", ["ApiKey"] = "sk_test_value" };
        (await Seed(h, Config(music, keyed))).Should().Be(0);
        (await Rows(h)).Should().BeEmpty();
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Two_entries_with_the_same_kind_endpoint_and_key_become_one_provider_with_two_models()
    {
        Harness h = Create();
        Dictionary<string, string?> large = Asr();
        Dictionary<string, string?> turbo = Asr();
        turbo["Name"] = "cloud-asr-turbo";
        turbo["Model"] = "whisper-large-v3-turbo";
        turbo["IsDefault"] = "false";

        (await Seed(h, Config(large, turbo))).Should().Be(2);
        (await Seed(h, Config(large, turbo))).Should().Be(2, "seeding again is a no-op that still counts as applied");

        List<ModelCapabilityView> rows = await Rows(h);
        rows.Select(r => r.Name).Should().BeEquivalentTo("cloud-asr", "cloud-asr-turbo");
        rows.Select(r => r.ProviderId).Distinct().Should().ContainSingle("one key, one endpoint, one kind: one account");
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await h.Db.InferenceModels.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        rows.Single(r => r.Name == "cloud-asr").IsDefault.Should().BeTrue();
        rows.Single(r => r.Name == "cloud-asr-turbo").IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task A_different_key_on_the_same_endpoint_is_a_different_account()
    {
        Harness h = Create();
        Dictionary<string, string?> other = Asr("sk-other-9999");
        other["Name"] = "cloud-asr-other-account";
        other["IsDefault"] = "false";

        await Seed(h, Config(Asr(), other));

        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Two_capabilities_of_one_model_code_under_one_account_share_the_model()
    {
        Harness h = Create();
        Dictionary<string, string?> chat = new()
        {
            ["Name"] = "claude",
            ["Kind"] = "Anthropic",
            ["Capability"] = "Chat",
            ["Endpoint"] = "https://api.anthropic.com",
            ["Model"] = "claude-opus-5-5",
            ["ApiKey"] = "sk-ant-shared"
        };
        Dictionary<string, string?> vision = new(chat) { ["Name"] = "claude-vision", ["Capability"] = "Vision" };

        (await Seed(h, Config(chat, vision))).Should().Be(2);

        List<ModelCapabilityView> rows = await Rows(h);
        rows.Select(r => r.Capability).Should().BeEquivalentTo([InferenceProviderCapability.Chat, InferenceProviderCapability.Vision]);
        rows.Select(r => r.ModelId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task A_rotated_key_re_keys_the_account_that_holds_the_entrys_model_instead_of_duplicating_it()
    {
        Harness h = Create();
        await Seed(h, Config(Asr("sk-asr-1234")));
        CapturingLogger log = new();

        await PlatformProviderSeeder.SeedAsync(h.Admin, Config(Asr("sk-asr-5678")), log);

        ModelCapabilityView row = (await Rows(h)).Single();
        row.ApiKeyEncrypted.Should().Be("enc:sk-asr-5678");
        row.ApiKeyLastFour.Should().Be("5678");
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        log.Messages.Should().ContainSingle(m => m.Contains("re-keyed") && m.Contains(row.ProviderId.ToString()),
            "every re-key is logged with the account it changed");
        log.Messages.Should().NotContain(m => m.Contains("sk-asr") || m.Contains("5678") || m.Contains("1234"), "no key material");
    }

    [Fact]
    public async Task A_rotated_key_never_re_keys_an_account_that_also_serves_other_models_it_gets_its_own_account()
    {
        Harness h = Create();
        Dictionary<string, string?> large = Asr("sk-asr-1234");
        Dictionary<string, string?> turbo = Asr("sk-asr-1234");
        turbo["Name"] = "cloud-asr-turbo";
        turbo["Model"] = "whisper-large-v3-turbo";
        turbo["IsDefault"] = "false";
        await Seed(h, Config(large, turbo));
        Guid shared = (await h.Db.InferenceProviders.IgnoreQueryFilters().SingleAsync()).Id;
        CapturingLogger log = new();

        // Only one entry's key changes: the other model must keep the account's key.
        (await PlatformProviderSeeder.SeedAsync(h.Admin, Config(Asr("sk-asr-5678"), turbo), log)).Should().Be(2);

        List<InferenceProvider> accounts = await h.Db.InferenceProviders.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        accounts.Should().HaveCount(2);
        accounts.Single(a => a.Id == shared).ApiKeyEncrypted.Should().Be("enc:sk-asr-1234", "the shared account is never re-keyed");
        accounts.Single(a => a.Id != shared).ApiKeyEncrypted.Should().Be("enc:sk-asr-5678");
        List<ModelCapabilityView> rows = await Rows(h);
        rows.Single(r => r.Name == "cloud-asr-turbo").ProviderId.Should().Be(shared);
        rows.Should().Contain(r => r.ProviderId != shared && r.ModelName == "whisper-large-v3");
        log.Messages.Should().NotContain(m => m.Contains("re-keyed"));

        // And it is stable: the next boot finds the new account by its key and changes nothing.
        (await Seed(h, Config(Asr("sk-asr-5678"), turbo))).Should().Be(2);
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        (await h.Db.InferenceModels.IgnoreQueryFilters().CountAsync()).Should().Be(3);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task A_disabled_entry_switches_its_capability_off()
    {
        Harness h = Create();
        await Seed(h, Config(Asr()));
        Dictionary<string, string?> off = Asr();
        off["Enabled"] = "false";
        off["IsDefault"] = "false";

        await Seed(h, Config(off));

        ModelCapabilityView row = (await Rows(h)).Single();
        row.IsEnabled.Should().BeFalse();
        row.IsDefault.Should().BeTrue("the entry never demotes a default, it only promotes one");
    }
}
