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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Moderation;
using ReelBolt.WorkflowEngine.Tests;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The ENGINE <see cref="IContentModerationGate"/>: same shared policy and client as the API, but the
/// audit row is written through <see cref="WorkflowEngineDbContext"/> and the usage scope is only
/// opened when the run has not already opened one. Fail open on generation, fail closed at publish.
/// </summary>
public sealed class EngineContentModerationGateTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    private static ModerationAttribution Attribution(ModerationSurface surface) =>
        new(OrgId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), surface, ModerationContentKind.Text);

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
        public IContentModerationClient Create(ResolvedContentModerationProvider provider, ContentModerationOptions options) => client;
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

    private sealed class FailingAuditInterceptor : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is WorkflowEngineDbContext db
                && db.ChangeTracker.Entries<ContentModerationDecision>()
                    .Any(e => e.State == EntityState.Added))
            {
                Attempts++;
                throw new InvalidOperationException("simulated audit-store failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class Harness
    {
        public Harness(
            ContentModerationOptions options,
            ResolvedContentModerationProvider? provider,
            IInterceptor? interceptor = null)
        {
            Client = new FakeClient();
            Usage = new CapturingUsageRecorder();
            Db = new SharedInMemoryDbFactory(interceptor: interceptor);

            var resolver = new Mock<IInferenceProviderResolver>();
            resolver
                .Setup(r => r.ResolveContentModerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(provider);
            Resolver = resolver;

            Gate = new ContentModerationGate(
                resolver.Object,
                new FakeFactory(Client),
                Options.Create(options),
                Usage,
                Db,
                NullLogger<ContentModerationGate>.Instance);
        }

        public FakeClient Client { get; }
        public CapturingUsageRecorder Usage { get; }
        public SharedInMemoryDbFactory Db { get; }
        public Mock<IInferenceProviderResolver> Resolver { get; }
        public ContentModerationGate Gate { get; }

        public List<ContentModerationDecision> Rows()
        {
            using WorkflowEngineDbContext db = Db.CreateDbContext();
            return [.. db.ContentModerationDecisions.AsNoTracking().OrderBy(r => r.CreatedAt).ToList()];
        }
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
    public async Task Disabled_allows_both_surfaces_and_writes_no_audit_or_usage_row()
    {
        Harness harness = NewHarness(o => o.Enabled = false);

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeTrue();
        (await harness.Gate.ScreenGenerationPromptAsync(
            "hello", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeTrue();

        harness.Rows().Should().BeEmpty();
        harness.Usage.Requests.Should().BeEmpty();
        harness.Client.Requests.Should().BeEmpty();
        harness.Resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task No_provider_refuses_publish_but_allows_generation()
    {
        Harness harness = NewHarness(withProvider: false);

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeFalse();
        (await harness.Gate.ScreenGenerationPromptAsync(
            "hello", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeTrue();

        harness.Rows().Should().BeEmpty();
        harness.Usage.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Block_refuses_generation_and_writes_a_pending_free_audit_row()
    {
        Harness harness = NewHarness();
        harness.Client.Classification = Classify(("violence", 0.91));
        const string text = "a very specific raw prompt that must never be stored";

        (await harness.Gate.ScreenGenerationPromptAsync(
            text, [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeFalse();

        ContentModerationDecision row = harness.Rows().Should().ContainSingle().Subject;
        row.Action.Should().Be(nameof(ModerationAction.Block));
        row.ReviewState.Should().Be(nameof(ModerationReviewState.NotRequired));
        row.OrganizationId.Should().Be(OrgId);
        row.Surface.Should().Be(nameof(ModerationSurface.GenerationPrompt));
        row.ContentSha256.Should().Be(ModerationContentHash.Compute(
            new ModerationInput(text, Array.Empty<ModerationImageInput>(), ModerationContentKind.Text)));
        row.ContentLength.Should().Be(Encoding.UTF8.GetByteCount(text));
        row.ReasonMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("0.91");
        row.ExpiresAt.Should().BeAfter(row.CreatedAt);
        JsonSerializer.Serialize(row).Should().NotContain(text, "the audit row carries no raw content");
    }

    [Fact]
    public async Task Review_allows_both_surfaces_and_writes_a_pending_row()
    {
        Harness harness = NewHarness();
        harness.Client.Classification = Classify(("harassment", 0.61));

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("borderline", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeTrue();
        (await harness.Gate.ScreenGenerationPromptAsync(
            "borderline", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeTrue();

        List<ContentModerationDecision> rows = harness.Rows();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Action == nameof(ModerationAction.Review));
        rows.Should().OnlyContain(r => r.ReviewState == nameof(ModerationReviewState.Pending));
    }

    [Fact]
    public async Task An_unavailable_client_fails_open_on_generation_and_closed_at_publish()
    {
        Harness harness = NewHarness();
        harness.Client.Throw = new ContentModerationUnavailableException("timeout");

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeFalse();
        (await harness.Gate.ScreenGenerationPromptAsync(
            "hello", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeTrue();

        harness.Rows().Should().BeEmpty("a call that never classified has nothing to audit");
        harness.Usage.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_scoreless_classification_is_never_clean_and_is_audited_as_unavailable()
    {
        Harness harness = NewHarness();
        harness.Client.Classification = new ModerationClassification(
            false, new Dictionary<string, double>(), new Dictionary<string, bool>(), ["text"]);

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeFalse();
        (await harness.Gate.ScreenGenerationPromptAsync(
            "hello", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeTrue();

        List<ContentModerationDecision> rows = harness.Rows();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Action == nameof(ModerationAction.Unavailable));
        rows.Should().NotContain(r => r.Action == nameof(ModerationAction.Allow));
        rows.Should().OnlyContain(r => r.ReasonCode == "unclassified");
    }

    [Fact]
    public async Task A_cancelled_caller_is_rethrown_and_writes_nothing()
    {
        Harness harness = NewHarness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        harness.Client.Throw = new OperationCanceledException(cts.Token);

        Func<Task> act = () => harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.GenerationPrompt),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.Rows().Should().BeEmpty();
        harness.Usage.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_timeout_is_rethrown_as_unavailable_not_a_step_failure()
    {
        Harness harness = NewHarness();
        harness.Client.Throw = new OperationCanceledException("vendor timeout", new TimeoutException());

        ModerationDecision decision = await harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.GenerationPrompt),
            CancellationToken.None);

        decision.Available.Should().BeFalse();
        decision.Action.Should().Be(ModerationAction.Allow);
        harness.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task Usage_is_one_zero_priced_moderation_call_attributed_to_the_run()
    {
        Harness harness = NewHarness();

        await harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.GenerationPrompt),
            CancellationToken.None);

        UsageRecordRequest request = harness.Usage.Requests.Should().ContainSingle().Subject;
        request.Kind.Should().Be(UsageKind.ModerationCalls);
        request.Quantity.Should().Be(1m);
        request.Capability.Should().Be(InferenceProviderCapability.ContentModeration);
        request.ProviderKind.Should().Be(InferenceProviderKind.OpenAICompatible);
        request.ProviderId.Should().NotBeNull();
        request.AccountId.Should().NotBeNull();

        UsageScopeState? scope = harness.Usage.Scopes.Should().ContainSingle().Subject;
        scope.Should().NotBeNull();
        scope!.Source.Should().Be(UsageSource.Moderation);
        scope.OrganizationId.Should().Be(OrgId);
    }

    [Fact]
    public async Task An_ambient_usage_scope_is_reused_rather_than_replaced()
    {
        Harness harness = NewHarness();
        Guid executionId = Guid.NewGuid();

        using (UsageScope.Begin(OrgId, Guid.NewGuid(), executionId, Guid.NewGuid(), Guid.NewGuid(), UsageSource.Moderation))
        {
            UsageScopeState? ambient = UsageScope.Current;

            await harness.Gate.ModerateAsync(
                new ModerationInput("hello", [], ModerationContentKind.Text),
                Attribution(ModerationSurface.GenerationPrompt),
                CancellationToken.None);

            UsageScopeState? seen = harness.Usage.Scopes.Should().ContainSingle().Subject;
            seen.Should().BeSameAs(ambient);
            UsageScope.Current.Should().BeSameAs(ambient);
        }

        UsageScope.Current.Should().BeNull();
    }

    [Fact]
    public async Task Usage_and_audit_failures_are_logged_never_thrown()
    {
        Harness harness = NewHarness();
        harness.Usage.Throw = new InvalidOperationException("ledger unavailable");

        ModerationDecision decision = await harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.GenerationPrompt),
            CancellationToken.None);

        decision.Available.Should().BeTrue();
        decision.Action.Should().Be(ModerationAction.Allow);
        harness.Rows().Should().ContainSingle("the audit insert still happened");
    }

    [Fact]
    public async Task An_unwritten_block_or_review_audit_row_fails_publish_closed_and_never_claims_a_queue()
    {
        var interceptor = new FailingAuditInterceptor();
        Harness harness = NewHarness(interceptor: interceptor);
        harness.Client.Classification = Classify(("sexual", 0.90));

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeFalse();

        // The Block is still enforced off the publish surface, never downgraded to Allow.
        (await harness.Gate.ScreenGenerationPromptAsync(
            "hello", [], Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None)).Should().BeFalse();

        harness.Client.Classification = Classify(("sexual", 0.60));
        ModerationDecision review = await harness.Gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.GenerationPrompt), CancellationToken.None);
        review.Action.Should().Be(ModerationAction.Review);
        review.ReviewQueued.Should().BeFalse("no row was written to queue");

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeFalse();

        interceptor.Attempts.Should().BeGreaterThanOrEqualTo(3, "the audit write is retried");
    }

    [Fact]
    public async Task An_unwritten_allow_audit_row_is_best_effort()
    {
        Harness harness = NewHarness(interceptor: new FailingAuditInterceptor());
        harness.Client.Classification = Classify(("violence", 0.01));

        (await harness.Gate.ScreenPublishAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(ModerationSurface.Publish), CancellationToken.None)).Should().BeTrue();
    }
}
