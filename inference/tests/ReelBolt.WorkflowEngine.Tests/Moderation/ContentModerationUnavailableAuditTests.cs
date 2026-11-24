using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// An outage is not a decision the gate can silently drop: the publish gate needs an audit row with
/// <see cref="ModerationAction.Unavailable"/> so the short negative cache can short-circuit the next
/// request before it samples the object again.
/// </summary>
public sealed class ContentModerationUnavailableAuditTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    private sealed class FakeClient : IContentModerationClient
    {
        public Exception? Throw { get; set; }

        public Task<ModerationClassification> ClassifyAsync(
            ModerationClassifyRequest request, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            return Task.FromResult(new ModerationClassification(
                Flagged: false,
                new Dictionary<string, double> { ["violence"] = 0.01 },
                new Dictionary<string, bool>(),
                ["text"]));
        }
    }

    private sealed class FakeFactory(IContentModerationClient client) : IContentModerationClientFactory
    {
        public IContentModerationClient Create(
            ResolvedContentModerationProvider provider, ContentModerationOptions options) => client;
    }

    private sealed class NullUsageRecorder : IUsageRecorder
    {
        public ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(0L);
    }

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

    private static (ContentModerationService Gate, InferenceApiDbContext Db, FakeClient Client) NewService(
        ContentModerationOptions options)
    {
        var db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var client = new FakeClient();

        var resolver = new Mock<IInferenceProviderResolver>();
        resolver
            .Setup(r => r.ResolveContentModerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Provider());

        var gate = new ContentModerationService(
            resolver.Object,
            new FakeFactory(client),
            new NullUsageRecorder(),
            db,
            Options.Create(options),
            NullLogger<ContentModerationService>.Instance);

        return (gate, db, client);
    }

    private static ModerationAttribution Attribution() => new(
        OrgId,
        ProjectId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        ModerationSurface.Publish,
        ModerationContentKind.Text);

    [Fact]
    public async Task A_vendor_failure_writes_an_unavailable_audit_row_for_the_negative_cache()
    {
        var options = new ContentModerationOptions { Enabled = true, UnavailableCacheSeconds = 60 };
        (ContentModerationService gate, InferenceApiDbContext db, FakeClient client) = NewService(options);
        client.Throw = new ContentModerationUnavailableException("vendor answered 400");
        var input = new ModerationInput("hello", [], ModerationContentKind.Text);
        ModerationAttribution attribution = Attribution() with { SourceVersion = "etag-v1:5" };

        ModerationDecision decision = await gate.ModerateAsync(input, attribution, CancellationToken.None);

        decision.Action.Should().Be(ModerationAction.Allow);
        decision.Available.Should().BeFalse("the surface's failure stance still applies");

        ContentModerationDecision row = await db.ContentModerationDecisions.SingleAsync();
        row.Action.Should().Be(nameof(ModerationAction.Unavailable));
        row.ContentSha256.Should().Be(ModerationContentHash.Compute(input));
        row.OrganizationId.Should().Be(OrgId);
        row.ProjectId.Should().Be(ProjectId);
        row.UserId.Should().Be(attribution.UserId);
        row.ExecutionId.Should().Be(attribution.ExecutionId);
        row.StepResultId.Should().Be(attribution.StepResultId);
        row.SourceVersion.Should().Be("etag-v1:5", "the outage row is bound to the same object version");
        row.Surface.Should().Be(nameof(ModerationSurface.Publish));
        row.ContentKind.Should().Be(nameof(ModerationContentKind.Text));
        row.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(60), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_timeout_writes_an_unavailable_audit_row_and_keeps_the_failure_stance()
    {
        var options = new ContentModerationOptions { Enabled = true, UnavailableCacheSeconds = 30 };
        (ContentModerationService gate, InferenceApiDbContext db, FakeClient client) = NewService(options);
        client.Throw = new OperationCanceledException("the moderation call timed out");

        ModerationDecision decision = await gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(),
            CancellationToken.None);

        decision.Action.Should().Be(ModerationAction.Allow);
        decision.Available.Should().BeFalse();

        ContentModerationDecision row = await db.ContentModerationDecisions.SingleAsync();
        row.Action.Should().Be(nameof(ModerationAction.Unavailable));
        row.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(30), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_caller_cancellation_does_not_write_an_audit_row()
    {
        var options = new ContentModerationOptions { Enabled = true };
        (ContentModerationService gate, InferenceApiDbContext db, FakeClient client) = NewService(options);
        using var cts = new CancellationTokenSource();
        client.Throw = new OperationCanceledException(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.ModerateAsync(
            new ModerationInput("hello", [], ModerationContentKind.Text),
            Attribution(),
            cts.Token));

        (await db.ContentModerationDecisions.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(60, 60)]
    [InlineData(9999, 3600)]
    public void The_unavailable_cache_window_is_clamped(int configured, int expected)
    {
        new ContentModerationOptions { UnavailableCacheSeconds = configured }
            .EffectiveUnavailableCacheSeconds.Should().Be(expected);
    }
}
