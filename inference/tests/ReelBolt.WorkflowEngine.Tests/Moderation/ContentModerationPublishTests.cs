using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The judgement pipeline behind the publish/egress gate, run through <c>JudgeInlineAsync</c> (the
/// explicit-action path): Block, Unavailable, an unclassified answer, or an unrecorded Block/Review
/// refuses; Review allows it and blocks nobody. The stored-verdict read path that every output goes
/// through is pinned in <c>OutputModerationVerdictTests</c>.
/// </summary>
public sealed class ContentModerationPublishTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();

    /// <summary>The S3 ETag shape (quoted); <c>ReadObjectVersionAsync</c> trims the quotes.</summary>
    private const string DefaultETag = "\"etag-v1\"";

    private static ModerationAttribution Attribution(Guid? stepResultId = null) =>
        new(OrgId, ProjectId, Guid.NewGuid(), null, stepResultId, ModerationSurface.Publish, ModerationContentKind.Image);

    private sealed class FakeGate : IContentModerationGate
    {
        private int _calls;

        public ModerationDecision Decision { get; set; } = new(
            ModerationAction.Allow, Available: true, ReviewQueued: false, null, string.Empty, "hash");

        public int Calls => Volatile.Read(ref _calls);
        public List<ModerationInput> Inputs { get; } = [];

        /// <summary>When set, the fake records an audit row like the real gate does (for the negative cache).</summary>
        public InferenceApiDbContext? AuditDb { get; set; }

        public bool RecordAuditRow { get; set; }

        /// <summary>Overrides the action persisted in the audit row (the real gate records Unavailable on an outage).</summary>
        public ModerationAction? AuditAction { get; set; }

        public TimeSpan Delay { get; set; }

        public async Task<ModerationDecision> ModerateAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Inputs.Add(input);

            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, ct);

            if (RecordAuditRow && AuditDb is not null)
            {
                DateTime now = DateTime.UtcNow;
                AuditDb.ContentModerationDecisions.Add(new ContentModerationDecision
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = attribution.OrganizationId,
                    ProjectId = attribution.ProjectId,
                    UserId = attribution.UserId,
                    ExecutionId = attribution.ExecutionId,
                    StepResultId = attribution.StepResultId,
                    SourceVersion = attribution.SourceVersion,
                    Surface = attribution.Surface.ToString(),
                    Action = (AuditAction ?? Decision.Action).ToString(),
                    ContentKind = attribution.Kind.ToString(),
                    ContentSha256 = "hash",
                    ContentLength = 2,
                    ProviderKind = nameof(InferenceProviderKind.OpenAICompatible),
                    Model = "omni-moderation-latest",
                    ModelSnapshot = "omni-moderation-2024-09-26",
                    ReasonCode = Decision.ReasonCode,
                    ReasonMessage = Decision.UserMessage,
                    ReviewState = nameof(ModerationReviewState.NotRequired),
                    CreatedAt = now,
                    ExpiresAt = now.AddDays(30)
                });
                await AuditDb.SaveChangesAsync(ct);
            }

            return Decision;
        }

        public Task<bool> ScreenGenerationPromptAsync(
            string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ScreenGeneratedTextAsync(
            string text, string site, ModerationAttribution attribution, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ScreenPublishAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSampler : IModerationMediaSampler
    {
        private int _calls;

        public ModerationMediaSample Sample { get; set; } =
            new([new byte[] { 9, 9 }], null, ModerationContentKind.Image, null);

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Set after the first call: touching the sampler again fails the "no I/O" assertions loudly.</summary>
        public bool ThrowIfUsed { get; set; }

        public Task<ModerationMediaSample> SampleAsync(Stream media, string mimeType, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (ThrowIfUsed)
                throw new InvalidOperationException("the sampler must not be touched on a cached decision");

            return Task.FromResult(Sample);
        }
    }

    private static Mock<IFileStorageService> DefaultStorage()
    {
        var storage = new Mock<IFileStorageService>();
        storage
            .Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream("hello"u8.ToArray()));
        storage
            .Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(5, "text/plain", DefaultETag));
        return storage;
    }

    private static (PublishModeration Service, FakeGate Gate, InferenceApiDbContext Db) NewService(
        bool video = false,
        Action<ContentModerationOptions>? configure = null,
        IModerationMediaSampler? sampler = null,
        Mock<IFileStorageService>? storage = null,
        ContentModerationOptions? options = null)
    {
        var dbOptions = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new InferenceApiDbContext(dbOptions);
        var gate = new FakeGate();
        var fakeSampler = sampler as FakeSampler ?? new FakeSampler
        {
            Sample = video
                ? new ModerationMediaSample([], null, ModerationContentKind.Video, "no frames")
                : new ModerationMediaSample([new byte[] { 9, 9 }], null, ModerationContentKind.Image, null)
        };

        var providers = new Mock<IInferenceProviderResolver>();
        providers
            .Setup(p => p.ResolvePlatformTranscriptionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedTranscriptionProvider?)null);

        var configured = options ?? new ContentModerationOptions { Enabled = true };
        if (options is null)
            configure?.Invoke(configured);

        var service = new PublishModeration(
            gate, (storage ?? DefaultStorage()).Object, fakeSampler, providers.Object,
            new Mock<ITranscriptionClientFactory>().Object, db,
            Options.Create(configured),
            NullLogger<PublishModeration>.Instance,
            new MemoryCache(new MemoryCacheOptions()));

        return (service, gate, db);
    }

    [Fact]
    public async Task A_block_refuses_publication_with_a_plain_reason()
    {
        (PublishModeration service, FakeGate gate, _) = NewService();
        gate.Decision = new ModerationDecision(
            ModerationAction.Block, Available: true, ReviewQueued: false, "sexual",
            "This image may contain sexual content.", "hash");

        PublishModerationOutcome outcome = await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/a.mp4", "image/png", Attribution(), CancellationToken.None);

        outcome.Allowed.Should().BeFalse();
        outcome.Code.Should().Be("CONTENT_BLOCKED");
        outcome.Message.Should().Be("This image may contain sexual content.");
        outcome.Message.Should().NotContain("0.");
    }

    [Fact]
    public async Task An_unavailable_call_refuses_publication()
    {
        (PublishModeration service, FakeGate gate, _) = NewService();
        gate.Decision = new ModerationDecision(
            ModerationAction.Allow, Available: false, ReviewQueued: false, null, string.Empty, "hash");

        PublishModerationOutcome outcome = await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/a.mp4", "image/png", Attribution(), CancellationToken.None);

        outcome.Allowed.Should().BeFalse();
        outcome.Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
    }

    [Fact]
    public async Task A_scoreless_unavailable_verdict_refuses_publication()
    {
        (PublishModeration service, FakeGate gate, _) = NewService();
        gate.Decision = new ModerationDecision(
            ModerationAction.Unavailable, Available: false, ReviewQueued: false, "unclassified", string.Empty, "hash");

        (await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task An_unclassifiable_media_object_refuses_publication()
    {
        (PublishModeration service, FakeGate gate, _) = NewService(video: true);

        PublishModerationOutcome outcome = await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/a.mp4", "video/mp4", Attribution(), CancellationToken.None);

        outcome.Allowed.Should().BeFalse();
        outcome.Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        gate.Calls.Should().Be(0, "there was nothing to send");
    }

    [Fact]
    public async Task A_review_allows_publication()
    {
        (PublishModeration service, FakeGate gate, _) = NewService();
        gate.Decision = new ModerationDecision(
            ModerationAction.Review, Available: true, ReviewQueued: true, "sexual",
            "This image may contain sexual content.", "hash");

        (await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Allowed.Should().BeTrue("a review blocks nobody");
    }

    [Fact]
    public async Task A_cached_block_refuses_without_calling_the_gate_again()
    {
        (PublishModeration service, FakeGate gate, InferenceApiDbContext db) = NewService();
        ModerationInput input = new(null, [new ModerationImageInput("image/jpeg", [9, 9])], ModerationContentKind.Image);
        string hash = ModerationContentHash.Compute(input);
        db.ContentModerationDecisions.Add(CacheRow(hash, ModerationAction.Block, "This image may contain sexual content."));
        await db.SaveChangesAsync();

        PublishModerationOutcome outcome = await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/a.mp4", "image/png", Attribution(), CancellationToken.None);

        outcome.Allowed.Should().BeFalse();
        outcome.Code.Should().Be("CONTENT_BLOCKED");
        gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_cached_review_allows_without_calling_the_gate_again()
    {
        (PublishModeration service, FakeGate gate, InferenceApiDbContext db) = NewService();
        ModerationInput input = new(null, [new ModerationImageInput("image/jpeg", [9, 9])], ModerationContentKind.Image);
        string hash = ModerationContentHash.Compute(input);
        db.ContentModerationDecisions.Add(CacheRow(hash, ModerationAction.Review, null));
        await db.SaveChangesAsync();

        (await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Allowed.Should().BeTrue();
        gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Text_block_and_unavailable_refuse_publication()
    {
        (PublishModeration service, FakeGate gate, _) = NewService();

        gate.Decision = new ModerationDecision(
            ModerationAction.Block, Available: true, ReviewQueued: false, "violence",
            "This content may contain violent content.", "hash");
        (await service.EvaluateTextAsync("hello", Attribution(), CancellationToken.None))
            .Code.Should().Be("CONTENT_BLOCKED");

        gate.Decision = new ModerationDecision(
            ModerationAction.Allow, Available: false, ReviewQueued: false, null, string.Empty, "hash");
        (await service.EvaluateTextAsync("hello", Attribution(), CancellationToken.None))
            .Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
    }

    [Fact]
    public async Task A_text_output_is_classified_as_text_not_refused()
    {
        (PublishModeration service, FakeGate gate, _) = NewService();

        PublishModerationOutcome outcome = await service.JudgeInlineAsync(
            ProjectId, "projects/x/userFiles/a.txt", "text/plain", Attribution(), CancellationToken.None);

        outcome.Allowed.Should().BeTrue();
        gate.Calls.Should().Be(1);
        gate.Inputs.Single().Text.Should().Be("hello");
    }

    [Fact]
    public async Task A_long_text_payload_is_classified_in_every_chunk()
    {
        (PublishModeration service, FakeGate gate, _) = NewService();
        string text = string.Join("\n", Enumerable.Repeat("a line of generated text", 1000));

        PublishModerationOutcome outcome = await service.EvaluateTextAsync(
            text, Attribution(), CancellationToken.None);

        outcome.Allowed.Should().BeTrue();
        gate.Calls.Should().BeGreaterThan(1, "the payload exceeds the 8000-char call cap");
        gate.Inputs.Should().OnlyContain(i => i.Text!.Length <= 8000);
        string.Concat(gate.Inputs.Select(i => i.Text)).Should().Be(text,
            "every character the export contains was classified");
    }

    [Fact]
    public async Task Disabled_moderation_allows_publication_and_calls_no_provider()
    {
        (PublishModeration service, FakeGate gate, _) = NewService(configure: o => o.Enabled = false);

        (await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Allowed.Should().BeTrue("the feature is off, not an outage");
        gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task FailClosedAtPublish_false_allows_a_genuine_outage()
    {
        (PublishModeration service, FakeGate gate, _) = NewService(configure: o => o.FailClosedAtPublish = false);
        gate.Decision = new ModerationDecision(
            ModerationAction.Allow, Available: false, ReviewQueued: false, null, string.Empty, "hash");

        (await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Allowed.Should().BeTrue("the operator chose to keep publishing through an outage");
    }

    [Fact]
    public async Task FailClosedAtPublish_false_still_refuses_an_unclassified_answer()
    {
        (PublishModeration service, FakeGate gate, _) = NewService(configure: o => o.FailClosedAtPublish = false);
        gate.Decision = new ModerationDecision(
            ModerationAction.Unavailable, Available: false, ReviewQueued: false, "unclassified", string.Empty, "hash");

        (await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Allowed.Should().BeFalse("an unclassified answer is never an outage to fail open on");
    }

    [Fact]
    public async Task An_unavailable_outcome_without_a_step_result_is_negative_cached_in_process()
    {
        var sampler = new FakeSampler();
        var options = new ContentModerationOptions { Enabled = true, UnavailableCacheSeconds = 60 };
        (PublishModeration service, FakeGate gate, _) = NewService(sampler: sampler, options: options);
        gate.Decision = new ModerationDecision(
            ModerationAction.Allow, Available: false, ReviewQueued: false, null, string.Empty, "hash");

        (await service.JudgeInlineAsync(
            ProjectId, "exports/nothing/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        sampler.Calls.Should().Be(1);

        sampler.ThrowIfUsed = true;
        (await service.JudgeInlineAsync(
            ProjectId, "exports/nothing/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        sampler.Calls.Should().Be(1);
        gate.Calls.Should().Be(1);

        options.UnavailableCacheSeconds = 0;
        sampler.ThrowIfUsed = false;
        (await service.JudgeInlineAsync(
            ProjectId, "exports/nothing/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        sampler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_checks_for_one_object_run_the_sampler_once()
    {
        var sampler = new FakeSampler();
        (PublishModeration service, FakeGate gate, _) = NewService(sampler: sampler);
        gate.Delay = TimeSpan.FromMilliseconds(100);
        ModerationAttribution attribution = Attribution();

        PublishModerationOutcome[] outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            service.JudgeInlineAsync(
                ProjectId, "projects/x/outputFiles/concurrent.mp4", "image/png",
                attribution, CancellationToken.None)));

        outcomes.Should().OnlyContain(outcome => outcome.Allowed);
        sampler.Calls.Should().Be(1, "concurrent requests for one object share a single pipeline run");
        gate.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_rewritten_object_under_a_step_result_is_re_sampled_not_served_from_the_stale_verdict()
    {
        Guid stepResultId = Guid.NewGuid();
        var sampler = new FakeSampler();
        string etag = "\"v1\"";
        var storage = new Mock<IFileStorageService>();
        storage
            .Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream("hello"u8.ToArray()));
        storage
            .Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new StoredObjectMetadata(5, "image/png", etag));

        (PublishModeration service, FakeGate gate, InferenceApiDbContext db) =
            NewService(sampler: sampler, storage: storage);
        gate.AuditDb = db;
        gate.RecordAuditRow = true;
        ModerationAttribution attribution = Attribution(stepResultId);

        PublishModerationOutcome first = await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/retry.mp4", "image/png", attribution, CancellationToken.None);

        first.Allowed.Should().BeTrue();
        sampler.Calls.Should().Be(1);
        gate.Calls.Should().Be(1);

        // A retry of the same execution rewrote the same storage key with different bytes.
        etag = "\"v2\"";
        gate.Decision = new ModerationDecision(
            ModerationAction.Block, Available: true, ReviewQueued: false, "violence",
            "This image may contain violent content.", "hash");

        PublishModerationOutcome second = await service.JudgeInlineAsync(
            ProjectId, "projects/x/outputFiles/retry.mp4", "image/png", attribution, CancellationToken.None);

        second.Allowed.Should().BeFalse();
        second.Code.Should().Be("CONTENT_BLOCKED");
        sampler.Calls.Should().Be(2, "the old verdict was bound to the previous object version");
        gate.Calls.Should().Be(2);
    }

    [Fact]
    public async Task An_outage_on_a_non_step_result_is_fail_open_cached_for_the_negative_window()
    {
        var sampler = new FakeSampler();
        var options = new ContentModerationOptions
        {
            Enabled = true,
            FailClosedAtPublish = false,
            UnavailableCacheSeconds = 60
        };
        (PublishModeration service, FakeGate gate, _) = NewService(sampler: sampler, options: options);
        gate.Decision = new ModerationDecision(
            ModerationAction.Allow, Available: false, ReviewQueued: false, null, string.Empty, "hash");

        (await service.JudgeInlineAsync(
            ProjectId, "exports/outage/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Allowed.Should().BeTrue("the operator chose to keep publishing through an outage");

        // Inside the short window the fail-open answer is reused instead of re-sampling.
        sampler.ThrowIfUsed = true;
        (await service.JudgeInlineAsync(
            ProjectId, "exports/outage/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Allowed.Should().BeTrue();
        sampler.Calls.Should().Be(1);
        gate.Calls.Should().Be(1);

        // Outside the window the object is sampled again.
        options.UnavailableCacheSeconds = 0;
        sampler.ThrowIfUsed = false;
        (await service.JudgeInlineAsync(
            ProjectId, "exports/outage/a.mp4", "image/png", Attribution(), CancellationToken.None))
            .Allowed.Should().BeTrue();
        sampler.Calls.Should().Be(2);
    }

    private static ContentModerationDecision CacheRow(
        string hash, ModerationAction action, string? message, Guid? stepResultId = null,
        string? reasonCode = null, string? sourceVersion = null) => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrgId,
            ProjectId = ProjectId,
            StepResultId = stepResultId,
            SourceVersion = sourceVersion,
            Surface = nameof(ModerationSurface.Publish),
            Action = action.ToString(),
            ContentKind = nameof(ModerationContentKind.Image),
            ContentSha256 = hash,
            ContentLength = 2,
            ProviderKind = nameof(InferenceProviderKind.OpenAICompatible),
            Model = "omni-moderation-latest",
            ModelSnapshot = "omni-moderation-2024-09-26",
            ReasonCode = reasonCode ?? (action == ModerationAction.Block ? "violence" : null),
            ReasonMessage = message,
            ReviewState = action == ModerationAction.Review
                ? nameof(ModerationReviewState.Pending)
                : nameof(ModerationReviewState.NotRequired),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
}
