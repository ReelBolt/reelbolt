using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The API <see cref="IContentModerationGate"/>: fail-open on generation/generated text, fail-closed at
/// publish, and one content-free audit row plus one $0 usage event per successful call.
/// </summary>
public sealed class ContentModerationGateTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    private static ModerationAttribution Attribution(ModerationSurface surface) =>
        new(OrgId, Guid.NewGuid(), Guid.NewGuid(), null, null, surface, ModerationContentKind.Text);

    private static ResolvedContentModerationProvider Provider() => new(
        Guid.NewGuid(),
        "openai-moderation",
        InferenceProviderKind.OpenAICompatible,
        "https://api.openai.com/v1",
        "omni-moderation-latest",
        string.Empty,
        8,
        IsPlatformManaged: true,
        AccountId: Guid.NewGuid());

    private static ModerationClassification Classify(params (string Category, double Score)[] scores)
    {
        Dictionary<string, double> byCategory = scores.ToDictionary(s => s.Category, s => s.Score);
        Dictionary<string, bool> flags = byCategory.ToDictionary(s => s.Key, s => false);
        return new ModerationClassification(flags.Values.Any(v => v), byCategory, flags, ["text"]);
    }

    private sealed class FakeClient : IContentModerationClient
    {
        public ModerationClassification Classification { get; set; } =
            new(false, new Dictionary<string, double> { ["violence"] = 0.01 }, new Dictionary<string, bool>(), ["text"]);

        public Exception? Throw { get; set; }

        public List<ModerationClassifyRequest> Requests { get; } = [];

        public Task<ModerationClassification> ClassifyAsync(ModerationClassifyRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Throw is not null) throw Throw;
            return Task.FromResult(Classification);
        }
    }

    private sealed class FakeFactory(IContentModerationClient client) : IContentModerationClientFactory
    {
        public ResolvedContentModerationProvider? Provider { get; private set; }
        public ContentModerationOptions? Options { get; private set; }

        public IContentModerationClient Create(ResolvedContentModerationProvider provider, ContentModerationOptions options)
        {
            Provider = provider;
            Options = options;
            return client;
        }
    }

    private sealed class CapturingUsageRecorder : IUsageRecorder
    {
        public List<UsageRecordRequest> Requests { get; } = [];
        public List<UsageScopeState?> Scopes { get; } = [];
        public Exception? Throw { get; set; }

        public ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default)
        {
            if (Throw is not null) throw Throw;
            Requests.Add(request);
            Scopes.Add(UsageScope.Current);
            return ValueTask.FromResult(0L);
        }
    }

    /// <summary>Makes every audit-row insert fail, so the Block/Review fail-closed path can be tested.</summary>
    private sealed class FailingAuditInterceptor : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is InferenceApiDbContext db
                && db.ChangeTracker.Entries<ContentModerationDecision>()
                    .Any(e => e.State == EntityState.Added))
            {
                Attempts++;
                throw new InvalidOperationException("simulated audit-store failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness(
            ContentModerationOptions options,
            ResolvedContentModerationProvider? provider,
            IInterceptor? interceptor = null)
        {
            Client = new FakeClient();
            Factory = new FakeFactory(Client);
            Usage = new CapturingUsageRecorder();
            DbContextOptionsBuilder<InferenceApiDbContext> builder =
                new DbContextOptionsBuilder<InferenceApiDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString());
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            Db = new InferenceApiDbContext(builder.Options);

            var resolver = new Mock<IInferenceProviderResolver>();
            resolver
                .Setup(r => r.ResolveContentModerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(provider);

            Gate = new ContentModerationService(
                resolver.Object,
                Factory,
                Usage,
                Db,
                Options.Create(options),
                NullLogger<ContentModerationService>.Instance);
        }

        public FakeClient Client { get; }
        public FakeFactory Factory { get; }
        public CapturingUsageRecorder Usage { get; }
        public InferenceApiDbContext Db { get; }
        public ContentModerationService Gate { get; }

        public void Dispose() => Db.Dispose();
    }

    private static Harness NewHarness(
        Action<ContentModerationOptions>? configure = null,
        bool withProvider = true,
        IInterceptor? interceptor = null)
    {
        var options = new ContentModerationOptions { Enabled = true };
        configure?.Invoke(options);
        return new Harness(options, withProvider ? Provider() : null, interceptor);
    }

    [Fact]
    public async Task Disabled_allows_publish_and_generation_and_writes_no_audit_or_usage_row()
    {
        using Harness harness = NewHarness(o => o.Enabled = false);

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeTrue();
        (await harness.Gate.ScreenGenerationPromptAsync(
            "hello", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeTrue();

        (await harness.Db.ContentModerationDecisions.CountAsync()).Should().Be(0);
        harness.Usage.Requests.Should().BeEmpty();
        harness.Client.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task No_provider_refuses_publish_but_allows_generation()
    {
        using Harness harness = NewHarness(withProvider: false);

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeFalse();
        (await harness.Gate.ScreenGenerationPromptAsync(
            "hello", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeTrue();

        (await harness.Db.ContentModerationDecisions.CountAsync()).Should().Be(0);
        harness.Usage.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Block_refuses_publish_and_generation_and_writes_only_a_hash()
    {
        using Harness harness = NewHarness();
        harness.Client.Classification = Classify(("violence", 0.91));
        const string text = "a very specific raw prompt that must never be stored";

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput(text, [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish) with { Kind = ModerationContentKind.Text },
            CancellationToken.None)).Should().BeFalse();
        (await harness.Gate.ScreenGenerationPromptAsync(
            text, [], Attribution(ModerationSurface.GenerationPrompt) with { Kind = ModerationContentKind.Text },
            CancellationToken.None)).Should().BeFalse();

        List<ContentModerationDecision> rows = await harness.Db.ContentModerationDecisions.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Action == nameof(ModerationAction.Block));
        rows.Should().OnlyContain(r => r.ReviewState == nameof(ModerationReviewState.NotRequired));
        string expectedHash = ModerationContentHash.Compute(
            new ModerationInput(text, Array.Empty<ModerationImageInput>(), ModerationContentKind.Text));
        rows.Should().OnlyContain(r => r.ContentSha256 == expectedHash);
        rows.Should().OnlyContain(r => r.ReasonCode == "violence");
        rows.Should().OnlyContain(r => r.ContentLength == Encoding.UTF8.GetByteCount(text));
        rows.Should().OnlyContain(r => r.ModelSnapshot == "omni-moderation-2024-09-26");
        rows.Should().OnlyContain(r => r.ScoresJson.Contains("violence"), "category scores are allowed");

        foreach (ContentModerationDecision row in rows)
        {
            string serialized = JsonSerializer.Serialize(row);
            serialized.Should().NotContain(text, "the audit row carries no raw content");
            row.ReasonMessage.Should().NotBeNullOrWhiteSpace();
            row.ReasonMessage.Should().NotContain("0.91", "the user message names no score");
        }
    }

    [Fact]
    public async Task The_source_version_is_recorded_on_the_decision_audit_row()
    {
        using Harness harness = NewHarness();
        harness.Client.Classification = Classify(("violence", 0.01));

        await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish) with { SourceVersion = "etag-v1:5" },
            CancellationToken.None);

        ContentModerationDecision row = await harness.Db.ContentModerationDecisions.SingleAsync();
        row.SourceVersion.Should().Be("etag-v1:5", "the verdict is bound to the object it classified");
    }

    [Fact]
    public async Task Review_allows_both_surfaces_and_writes_a_pending_row()
    {
        using Harness harness = NewHarness();
        harness.Client.Classification = Classify(("harassment", 0.61));

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("borderline", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeTrue();
        (await harness.Gate.ScreenGeneratedTextAsync(
            "borderline", "caption", Attribution(ModerationSurface.GeneratedText), CancellationToken.None)).Should().BeTrue();

        List<ContentModerationDecision> rows = await harness.Db.ContentModerationDecisions.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Action == nameof(ModerationAction.Review));
        rows.Should().OnlyContain(r => r.ReviewState == nameof(ModerationReviewState.Pending));
    }

    [Fact]
    public async Task Unavailable_refuses_publish_but_allows_generation()
    {
        using Harness harness = NewHarness();
        harness.Client.Throw = new ContentModerationUnavailableException("timeout");

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeFalse();
        (await harness.Gate.ScreenGenerationPromptAsync(
            "hello", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeTrue();

        (await harness.Db.ContentModerationDecisions.CountAsync()).Should().Be(0);
        harness.Usage.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancelled_caller_is_rethrown()
    {
        using Harness harness = NewHarness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        harness.Client.Throw = new OperationCanceledException(cts.Token);

        Func<Task> act = () => harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await harness.Db.ContentModerationDecisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_scoreless_classification_fails_closed_at_publish_but_open_at_generation()
    {
        using Harness harness = NewHarness();
        harness.Client.Classification = new ModerationClassification(
            false,
            new Dictionary<string, double>(),
            new Dictionary<string, bool>(),
            ["text"]);

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeFalse();
        (await harness.Gate.ScreenGenerationPromptAsync(
            "hello", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeTrue();

        List<ContentModerationDecision> rows = await harness.Db.ContentModerationDecisions.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Action == nameof(ModerationAction.Unavailable));
        rows.Should().NotContain(r => r.Action == nameof(ModerationAction.Allow),
            "an unclassified answer is never recorded as clean");
        rows.Should().OnlyContain(r => r.ReasonCode == "unclassified");
        rows.Should().OnlyContain(r => r.ReviewState == nameof(ModerationReviewState.NotRequired));
    }

    [Fact]
    public async Task Usage_is_one_moderation_call_that_prices_at_zero_with_a_price_id()
    {
        using Harness harness = NewHarness();
        harness.Client.Classification = Classify(("violence", 0.01));

        await harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.GenerationPrompt),
            CancellationToken.None);

        UsageRecordRequest request = harness.Usage.Requests.Should().ContainSingle().Subject;
        request.Kind.Should().Be(UsageKind.ModerationCalls);
        request.Quantity.Should().Be(1m);
        request.Capability.Should().Be(InferenceProviderCapability.ContentModeration);
        request.BillingMode.Should().Be(UsageBillingMode.Platform);
        request.ProviderKind.Should().Be(InferenceProviderKind.OpenAICompatible);
        request.ModelName.Should().Be("omni-moderation-latest");
        request.ProviderId.Should().NotBeNull();
        request.AccountId.Should().NotBeNull();
        harness.Usage.Scopes.Should().ContainSingle().Which!.Source.Should().Be(UsageSource.Moderation);

        // The committed seed's rule is a real $0 row, so pricing the captured request yields a price id.
        SeedPriceRow seedRow = ModelCatalogSeed.LoadDefault().Rows.Single(r =>
            r.Kind == InferenceProviderKind.OpenAICompatible
            && r.ModelCode == "omni-moderation-latest"
            && r.Capability == "ContentModeration");
        Guid capabilityId = request.ProviderId!.Value;
        DateTime now = DateTime.UtcNow;
        ModelCapabilityPrice price = new()
        {
            Id = Guid.NewGuid(),
            ModelCapabilityId = capabilityId,
            Unit = seedRow.Unit,
            Variant = seedRow.Variant,
            CostUsdPerUnit = seedRow.CostUsdPerUnit,
            EffectiveFrom = now.AddDays(-1)
        };
        ModelPriceSnapshot snapshot = new(
            [new PricedCapability(capabilityId, Guid.NewGuid(), null, request.ProviderKind,
                InferenceProviderCapability.ContentModeration, request.ModelName)],
            [price],
            []);

        PriceQuote quote = ModelPriceService.Price(snapshot, new PriceRequest(
            request.ProviderId,
            request.AccountId,
            request.ProviderKind,
            request.Capability!.Value,
            request.ModelName,
            request.Kind,
            null,
            request.BillingMode,
            request.Quantity,
            now), now);

        quote.PriceId.Should().NotBeNull("the $0 row is matched, not skipped as unpriced");
        quote.CreditsMillis.Should().Be(0);
        quote.CostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task A_usage_recording_failure_does_not_fail_the_decision()
    {
        using Harness harness = NewHarness();
        harness.Usage.Throw = new InvalidOperationException("ledger unavailable");

        ModerationDecision decision = await harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.GenerationPrompt),
            CancellationToken.None);

        decision.Available.Should().BeTrue();
        decision.Action.Should().Be(ModerationAction.Allow);
        (await harness.Db.ContentModerationDecisions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public void Hard_block_categories_bind_from_configuration()
    {
        Microsoft.Extensions.Configuration.IConfiguration configuration =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ContentModeration:HardBlockCategories:0"] = "sexual/minors"
                })
                .Build();

        var options = new ContentModerationOptions();
        configuration.GetSection(ContentModerationOptions.SectionName).Bind(options);

        options.HardBlockCategories.Should().ContainSingle().Which.Should().Be("sexual/minors");
    }

    [Fact]
    public async Task The_model_sent_is_the_pinned_snapshot_not_the_provider_alias()
    {
        using Harness harness = NewHarness(o => o.Model = "omni-moderation-2024-09-26");

        await harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.GenerationPrompt),
            CancellationToken.None);

        harness.Client.Requests.Should().ContainSingle()
            .Which.Model.Should().Be("omni-moderation-2024-09-26");
    }

    [Fact]
    public async Task An_unwritten_block_or_review_audit_row_is_never_silent_and_fails_publish_closed()
    {
        using Harness harness = NewHarness(interceptor: AuditFailure(out FailingAuditInterceptor interceptor));
        harness.Client.Classification = Classify(("sexual", 0.90));

        // Publish: a Block that cannot be recorded becomes Unavailable -> refused.
        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeFalse();

        // Generation: the Block is still enforced, never downgraded to Allow.
        (await harness.Gate.ScreenGenerationPromptAsync(
            "hello", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeFalse();

        // Review without a row: it proceeds (review never blocks) but never claims it was queued.
        harness.Client.Classification = Classify(("sexual", 0.60));
        ModerationDecision review = await harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None);
        review.Action.Should().Be(ModerationAction.Review);
        review.ReviewQueued.Should().BeFalse("no row was written to queue");

        // Review at publish: an unrecorded review fails closed too.
        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeFalse();

        interceptor.Attempts.Should().BeGreaterThanOrEqualTo(3, "the audit write is retried");
    }

    [Fact]
    public async Task An_audit_failure_for_a_block_denies_publish_even_when_fail_closed_is_off()
    {
        // FailClosedAtPublish is an operator choice for a moderation OUTAGE. An unrecorded Block is
        // a security gap, not an outage, so the deny must bypass that switch.
        using Harness harness = NewHarness(
            o => o.FailClosedAtPublish = false,
            interceptor: AuditFailure(out _));
        harness.Client.Classification = Classify(("sexual", 0.90));

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None))
            .Should().BeFalse("an unrecorded block must never be published");
    }

    [Fact]
    public async Task An_unwritten_allow_audit_row_is_best_effort_and_the_action_stands()
    {
        using Harness harness = NewHarness(interceptor: AuditFailure(out _));
        harness.Client.Classification = Classify(("violence", 0.01));

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeTrue();

        ModerationDecision decision = await harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None);
        decision.Action.Should().Be(ModerationAction.Allow);
        decision.ReviewQueued.Should().BeFalse();
    }

    private static IInterceptor AuditFailure(out FailingAuditInterceptor interceptor)
    {
        interceptor = new FailingAuditInterceptor();
        return interceptor;
    }
}
