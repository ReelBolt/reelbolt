using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// Output verdicts are judged once, in the background, and publish only reads the stored verdict.
/// Fakes only: no live provider and no customer content.
/// </summary>
public sealed class OutputModerationVerdictTests
{
    private const string DefaultETag = "\"e1\"";
    private const long DefaultSize = 1000;
    private const string DefaultVersion = "e1:1000";

    // -------------------------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------------------------

    private sealed class FakeGate : IContentModerationGate
    {
        private int _calls;

        public ModerationDecision Decision { get; set; } = new(
            ModerationAction.Allow, Available: true, ReviewQueued: false, null, string.Empty, "hash");

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>The scope's context, so a test can leave an unsaved audit row behind as a failed gate write would.</summary>
        public InferenceApiDbContext? Db { get; set; }

        public Action<InferenceApiDbContext>? OnModerate { get; set; }

        public Task<ModerationDecision> ModerateAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (OnModerate is not null && Db is not null) OnModerate(Db);
            return Task.FromResult(Decision);
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

        public Action? OnSample { get; set; }

        /// <summary>When set, the bytes are really sampled by this sampler (ffmpeg and synthetic media).</summary>
        public IModerationMediaSampler? Real { get; set; }

        public string? LastMime { get; private set; }

        public int Calls => Volatile.Read(ref _calls);

        public Task<ModerationMediaSample> SampleAsync(Stream media, string mimeType, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            LastMime = mimeType;
            OnSample?.Invoke();
            return Real is not null ? Real.SampleAsync(media, mimeType, ct) : Task.FromResult(Sample);
        }
    }

    /// <summary>A tenant context whose system scope can be made to fail, to prove a failed hold check sends nothing.</summary>
    private sealed class FlakyTenant : ITenantContext
    {
        private int _system;
        private Guid? _organization;

        public bool FailSystemScope { get; set; }

        public Guid? OrganizationId => _organization;

        public bool IsSystem => Volatile.Read(ref _system) > 0;

        public IDisposable BeginSystemScope()
        {
            if (FailSystemScope) throw new InvalidOperationException("the hold check failed");
            Interlocked.Increment(ref _system);
            return new Scope(() => Interlocked.Decrement(ref _system));
        }

        public IDisposable BeginOrganizationScope(Guid organizationId)
        {
            Guid? previous = _organization;
            _organization = organizationId;
            return new Scope(() => _organization = previous);
        }

        private sealed class Scope(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    /// <summary>Records which projects the lane asked the CSAM control to scan now.</summary>
    private sealed class RecordingCsamScans : ICsamScanRequests
    {
        private readonly List<Guid> _requested = [];

        public IReadOnlyList<Guid> Requested
        {
            get
            {
                lock (_requested) return _requested.ToList();
            }
        }

        public bool Request(Guid projectId)
        {
            lock (_requested) _requested.Add(projectId);
            return true;
        }
    }

    private sealed class Harness
    {
        public Guid OrgId { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = Guid.NewGuid();
        public FakeGate Gate { get; } = new();
        public FakeSampler Sampler { get; } = new();
        public FlakyTenant Tenant { get; } = new();
        public ContentModerationOptions Options { get; } = new() { Enabled = true };
        public CsamOptions Csam { get; } = new();
        public Mock<IFileStorageService> Storage { get; } = new();
        public Mock<IInferenceProviderResolver> Providers { get; } = new();
        public Mock<ITranscriptionClientFactory> Factory { get; } = new();
        public ChannelBackgroundTaskQueue<OutputModerationTask> Queue { get; } = new(100);
        public string ETag { get; set; } = DefaultETag;
        public long Size { get; set; } = DefaultSize;
        public bool MissingObject { get; set; }
        public string? MissingPrefix { get; set; }
        public bool HasTranscriptionProvider { get; set; }
        public bool TranscriptionThrows { get; set; }
        public byte[] Body { get; set; } = [1, 2, 3];
        public string TranscriptText { get; set; } = "hello";

        /// <summary>What the second transcription (the side signal of stereo) hears; null: the same as the first.</summary>
        public string? SideTranscriptText { get; set; }

        public bool SideTranscriptionThrows { get; set; }
        public int TranscribeCalls;
        public ServiceProvider Provider { get; private set; } = null!;
        public int Downloads;
        public int TranscriptionLookups;

        /// <summary>The store time reported for every object; null reports none.</summary>
        public DateTime? LastModified { get; set; }

        /// <summary>Bytes served for a key instead of <see cref="Body"/> (a step manifest).</summary>
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public static Harness Create(Action<Harness>? configure = null)
        {
            Harness h = new();
            h.Storage
                .Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, string k, CancellationToken _) =>
                    h.MissingObject || (h.MissingPrefix is not null && Path.GetFileName(k).StartsWith(h.MissingPrefix, StringComparison.Ordinal))
                        ? null
                        : new StoredObjectMetadata(h.Size, "video/mp4", h.ETag, h.LastModified));
            h.Storage
                .Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, string k, CancellationToken _) =>
                {
                    if (h.Objects.TryGetValue(k, out byte[]? stored)) return new MemoryStream(stored);
                    Interlocked.Increment(ref h.Downloads);
                    return new MemoryStream(h.Body);
                });
            h.Providers
                .Setup(p => p.ResolvePlatformTranscriptionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    Interlocked.Increment(ref h.TranscriptionLookups);
                    return h.HasTranscriptionProvider
                        ? new ResolvedTranscriptionProvider(null, "fake", InferenceProviderKind.OpenAICompatible, "http://x", "m", "k", 30)
                        : (ResolvedTranscriptionProvider?)null;
                });
            Mock<ITranscriptionClient> client = new();
            client.Setup(c => c.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    int call = Interlocked.Increment(ref h.TranscribeCalls);
                    return h.TranscriptionThrows || (call >= 2 && h.SideTranscriptionThrows)
                        ? Task.FromException<TranscriptResult>(new InvalidOperationException("vendor down"))
                        : Task.FromResult(new TranscriptResult(call >= 2 && h.SideTranscriptText is not null ? h.SideTranscriptText : h.TranscriptText, [], [], null));
                });
            h.Factory.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(client.Object);
            configure?.Invoke(h);

            string dbName = Guid.NewGuid().ToString();
            ServiceCollection services = new();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(dbName));
            services.AddScoped<IContentModerationGate>(sp =>
            {
                h.Gate.Db = sp.GetRequiredService<InferenceApiDbContext>();
                return h.Gate;
            });
            services.AddSingleton<IModerationMediaSampler>(h.Sampler);
            services.AddSingleton(h.Storage.Object);
            services.AddSingleton(h.Providers.Object);
            services.AddSingleton(h.Factory.Object);
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(h.Options));
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(h.Csam));
            services.AddSingleton<ITenantContext>(h.Tenant);
            services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<IBackgroundTaskQueue<OutputModerationTask>>(h.Queue);
            services.AddSingleton<IOutputModerationEnqueuer, OutputModerationEnqueuer>();
            services.AddScoped<PublishModeration>();
            services.AddScoped<OutputModerationProcessor>();
            h.Provider = services.BuildServiceProvider();

            using IServiceScope scope = h.Provider.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Projects.Add(new Project { Id = h.ProjectId, Name = "p", OrganizationId = h.OrgId });
            db.SaveChanges();
            return h;
        }

        public string Key(string name = "render.mp4") => $"projects/{ProjectId}/outputFiles/{name}";

        public OutputModerationConsumer NewConsumer() => new(
            Provider.GetRequiredService<IOutputModerationEnqueuer>(), Microsoft.Extensions.Options.Options.Create(Options),
            Provider.CreateScope().ServiceProvider.GetRequiredService<InferenceApiDbContext>(), Tenant);

        public RecordingCsamScans CsamScans { get; } = new();

        public OutputModerationWorker Worker(IConfiguration? configuration = null, IOutputModerationWake? wake = null) => new(
            Queue, Provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<OutputModerationWorker>.Instance, Microsoft.Extensions.Options.Options.Create(Csam),
            configuration: configuration, csamScans: CsamScans, wake: wake);

        public async Task<T> WithDbAsync<T>(Func<InferenceApiDbContext, Task<T>> body)
        {
            using IServiceScope scope = Provider.CreateScope();
            return await body(scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>());
        }

        public Task<PublishModerationOutcome> ReadAsync(string key) =>
            WithAsync<PublishModeration, PublishModerationOutcome>(p => p.EvaluateObjectAsync(
                ProjectId, key, "video/mp4",
                new ModerationAttribution(
                    OrgId, ProjectId, Guid.NewGuid(), null, null, ModerationSurface.Publish, ModerationContentKind.Video),
                CancellationToken.None));

        public Task<bool> ProcessAsync(string key) =>
            WithAsync<OutputModerationProcessor, bool>(p => p.ProcessAsync(
                new OutputModerationTask(ProjectId, key), CancellationToken.None));

        public async Task<TResult> WithAsync<TService, TResult>(Func<TService, Task<TResult>> body)
            where TService : notnull
        {
            using IServiceScope scope = Provider.CreateScope();
            return await body(scope.ServiceProvider.GetRequiredService<TService>());
        }

        public Task SeedVerdictAsync(
            string key, string state, DateTime decidedAt, string version = DefaultVersion,
            string? reasonCode = null, string? message = null, int attempts = 0, DateTime? nextAttemptAt = null) =>
            WithDbAsync(async db =>
            {
                db.OutputModerationVerdicts.Add(new OutputModerationVerdict
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = OrgId,
                    ProjectId = ProjectId,
                    StorageKey = key,
                    SourceVersion = version,
                    State = state,
                    ReasonCode = reasonCode,
                    ReasonMessage = message,
                    ModelSnapshot = "omni-moderation-2024-09-26",
                    Attempts = attempts,
                    NextAttemptAt = nextAttemptAt,
                    CreatedAt = decidedAt,
                    DecidedAt = decidedAt
                });
                await db.SaveChangesAsync();
                return 0;
            });

        public Task<Guid> SeedStepResultAsync(string key, DateTime? completedAt = null, Guid? projectId = null, Guid? organizationId = null) =>
            WithDbAsync(async db =>
            {
                Guid project = projectId ?? ProjectId;
                Guid workflowId = Guid.NewGuid();
                Guid stepId = Guid.NewGuid();
                Guid executionId = Guid.NewGuid();
                Guid resultId = Guid.NewGuid();
                DateTime at = completedAt ?? DateTime.UtcNow.AddSeconds(-5);
                db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = workflowId, StepOrder = 1 });
                db.WorkflowExecutions.Add(new WorkflowExecution
                {
                    Id = executionId,
                    WorkflowDefinitionId = workflowId,
                    ProjectId = project,
                    OrganizationId = organizationId ?? OrgId,
                    Status = ExecutionStatus.Passed,
                    StartedAt = at,
                    CompletedAt = at
                });
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = resultId,
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = stepId,
                    Status = StepStatus.Completed,
                    ExecutedAt = at,
                    CompletedAt = at,
                    OutputStorageKey = key
                });
                await db.SaveChangesAsync();
                return resultId;
            });

        public Task AddFileAsync(CsamScanStatus status, string? quarantineKey = null) =>
            AddFileReturningIdAsync(status, quarantineKey);

        public Task<Guid> AddFileReturningIdAsync(CsamScanStatus status, string? quarantineKey = null) =>
            WithDbAsync(async db =>
            {
                Guid id = Guid.NewGuid();
                db.ProjectFiles.Add(new ProjectFile
                {
                    Id = id,
                    ProjectId = ProjectId,
                    StorageKey = $"projects/{ProjectId}/userFiles/{Guid.NewGuid():N}.png",
                    CsamStatus = status,
                    CsamQuarantineKey = quarantineKey
                });
                await db.SaveChangesAsync();
                return id;
            });

        public Task SetCsamStatusAsync(Guid fileId, CsamScanStatus status) =>
            WithDbAsync(async db =>
            {
                ProjectFile file = await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == fileId);
                file.CsamStatus = status;
                await db.SaveChangesAsync();
                return 0;
            });

        public Task<OutputModerationVerdict?> RowAsync(string key) =>
            WithDbAsync(db => db.OutputModerationVerdicts.AsNoTracking()
                .FirstOrDefaultAsync(v => v.ProjectId == ProjectId && v.StorageKey == key));

        public Task<int> RowCountAsync() =>
            WithDbAsync(db => db.OutputModerationVerdicts.CountAsync());

        public Task<int> AuditCountAsync() =>
            WithDbAsync(db => db.ContentModerationDecisions.CountAsync());

        public void AssertNothingSent()
        {
            Gate.Calls.Should().Be(0, "nothing may reach the vendor");
            Sampler.Calls.Should().Be(0, "no frame may be sampled");
            Volatile.Read(ref TranscriptionLookups).Should().Be(0, "no audio may reach transcription");
            Volatile.Read(ref Downloads).Should().Be(0, "no body may be read");
        }
    }

    // -------------------------------------------------------------------------------------------
    // Read path
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(25)]
    [InlineData(24 * 31 + 5)]
    public async Task A_verdict_older_than_a_day_or_a_month_causes_no_re_check(int ageHours)
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedVerdictAsync(key, nameof(ModerationAction.Allow), DateTime.UtcNow.AddHours(-ageHours));

        PublishModerationOutcome outcome = await h.ReadAsync(key);

        outcome.Allowed.Should().BeTrue("a stored verdict has no expiry");
        h.Queue.TrackedCount.Should().Be(0, "nothing is offered for an old verdict");
        h.AssertNothingSent();

        // The worker handed the same key does nothing either: no read, no vendor call.
        (await h.ProcessAsync(key)).Should().BeFalse();
        h.AssertNothingSent();
        (await h.RowAsync(key))!.DecidedAt.Should().BeBefore(DateTime.UtcNow.AddHours(-24), "the row was not rewritten");
    }

    [Fact]
    public async Task Changed_bytes_read_as_pending_are_offered_and_judged_once()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedStepResultAsync(key);
        await h.SeedVerdictAsync(key, nameof(ModerationAction.Allow), DateTime.UtcNow.AddDays(-3));
        h.ETag = "\"e2\"";

        PublishModerationOutcome outcome = await h.ReadAsync(key);

        outcome.Allowed.Should().BeFalse();
        outcome.Code.Should().Be("CONTENT_MODERATION_PENDING");
        h.Queue.TrackedCount.Should().Be(1, "the new bytes are offered");
        h.Gate.Calls.Should().Be(0, "the read path never judges");

        await h.ProcessAsync(key);
        h.Gate.Calls.Should().Be(1);
        (await h.RowAsync(key))!.SourceVersion.Should().Be("e2:1000");

        await h.ProcessAsync(key);
        h.Gate.Calls.Should().Be(1, "the new bytes are judged once");
        (await h.ReadAsync(key)).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("Allow")]
    [InlineData("Review")]
    [InlineData("Block")]
    [InlineData("Unavailable")]
    [InlineData("NoRow")]
    [InlineData("OtherVersion")]
    public async Task The_read_path_never_touches_the_sampler_the_gate_transcription_or_the_body(string state)
    {
        Harness h = Harness.Create();
        string key = h.Key();
        if (state == "OtherVersion")
            await h.SeedVerdictAsync(key, nameof(ModerationAction.Allow), DateTime.UtcNow, version: "old:1");
        else if (state != "NoRow")
            await h.SeedVerdictAsync(key, state, DateTime.UtcNow, message: state == "Block" ? "Blocked." : null);

        await h.ReadAsync(key);

        h.AssertNothingSent();
        h.Storage.Verify(
            s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once, "one HEAD and nothing else");
    }

    [Fact]
    public async Task A_block_still_refuses_after_any_age()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedVerdictAsync(
            key, nameof(ModerationAction.Block), DateTime.UtcNow.AddDays(-400),
            reasonCode: "violence", message: "This video may contain violent content.");

        PublishModerationOutcome outcome = await h.ReadAsync(key);

        outcome.Allowed.Should().BeFalse();
        outcome.Code.Should().Be("CONTENT_BLOCKED");
        outcome.Message.Should().Be("This video may contain violent content.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pending_refuses_whatever_FailClosedAtPublish_says(bool failClosed)
    {
        Harness h = Harness.Create(x => x.Options.FailClosedAtPublish = failClosed);

        PublishModerationOutcome outcome = await h.ReadAsync(h.Key());

        outcome.Allowed.Should().BeFalse();
        outcome.Code.Should().Be("CONTENT_MODERATION_PENDING");
        outcome.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(true, null, false)]
    [InlineData(false, null, true)]
    [InlineData(true, "unclassified", false)]
    [InlineData(false, "unclassified", false)]
    [InlineData(false, "audit_unavailable", false)]
    [InlineData(false, "too_long", false)]
    public async Task A_recorded_outage_follows_FailClosedAtPublish_and_an_unclassified_answer_always_refuses(
        bool failClosed, string? reasonCode, bool expectedAllowed)
    {
        Harness h = Harness.Create(x => x.Options.FailClosedAtPublish = failClosed);
        string key = h.Key();
        await h.SeedVerdictAsync(
            key, nameof(ModerationAction.Unavailable), DateTime.UtcNow, reasonCode: reasonCode,
            attempts: 1, nextAttemptAt: DateTime.UtcNow.AddHours(1));

        PublishModerationOutcome outcome = await h.ReadAsync(key);

        outcome.Allowed.Should().Be(expectedAllowed);
        if (!expectedAllowed) outcome.Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
    }

    [Fact]
    public async Task Disabled_moderation_allows_queues_nothing_and_writes_no_row()
    {
        Harness h = Harness.Create(x => x.Options.Enabled = false);
        string key = h.Key();

        PublishModerationOutcome outcome = await h.ReadAsync(key);

        outcome.Allowed.Should().BeTrue("the feature is off, not an outage");
        h.Queue.TrackedCount.Should().Be(0);
        (await h.RowCountAsync()).Should().Be(0);
        h.AssertNothingSent();
    }

    [Fact]
    public async Task An_unreadable_version_is_unavailable_and_offers_nothing()
    {
        Harness h = Harness.Create(x => x.MissingObject = true);

        PublishModerationOutcome outcome = await h.ReadAsync(h.Key());

        outcome.Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        h.Queue.TrackedCount.Should().Be(0);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(60, false)]
    public async Task A_recorded_outage_is_offered_again_by_a_view_only_when_its_retry_time_has_come(
        int nextAttemptMinutes, bool offered)
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedVerdictAsync(
            key, nameof(ModerationAction.Unavailable), DateTime.UtcNow.AddMinutes(-30),
            attempts: 2, nextAttemptAt: DateTime.UtcNow.AddMinutes(nextAttemptMinutes));

        await h.ReadAsync(key);

        h.Queue.TrackedCount.Should().Be(offered ? 1 : 0);
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(1, false)]
    public async Task After_the_attempts_are_used_up_a_view_may_offer_at_most_every_six_hours(
        int hoursSinceLastTry, bool offered)
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedVerdictAsync(
            key, nameof(ModerationAction.Unavailable), DateTime.UtcNow.AddHours(-hoursSinceLastTry),
            attempts: OutputModerationBackoff.MaxAttempts, nextAttemptAt: null);

        await h.ReadAsync(key);

        h.Queue.TrackedCount.Should().Be(offered ? 1 : 0);
    }

    // -------------------------------------------------------------------------------------------
    // Processor
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_output_is_judged_once_and_the_verdict_is_stored_without_content()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        Guid stepResultId = await h.SeedStepResultAsync(key);

        await h.ProcessAsync(key);
        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(1);
        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Allow");
        row.SourceVersion.Should().Be(DefaultVersion);
        row.OrganizationId.Should().Be(h.OrgId);
        row.StepResultId.Should().Be(stepResultId);
        row.OutputProducedAt.Should().NotBeNull();
        row.NextAttemptAt.Should().BeNull();
        row.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task A_block_is_stored_with_its_plain_reason()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        h.Gate.Decision = new ModerationDecision(
            ModerationAction.Block, Available: true, ReviewQueued: false, "violence",
            "This video may contain violent content.", "hash");

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Block");
        row.ReasonCode.Should().Be("violence");
        row.ReasonMessage.Should().Be("This video may contain violent content.");
        (await h.ReadAsync(key)).Code.Should().Be("CONTENT_BLOCKED");
    }

    [Fact]
    public async Task A_missing_object_leaves_nothing_behind()
    {
        Harness h = Harness.Create(x => x.MissingObject = true);

        await h.ProcessAsync(h.Key());

        (await h.RowCountAsync()).Should().Be(0);
        h.AssertNothingSent();
    }

    [Fact]
    public async Task Bytes_replaced_while_judging_store_nothing_and_are_offered_again()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        h.Sampler.OnSample = () => h.ETag = "\"e2\"";

        OutputModerationTask task = new(h.ProjectId, key);
        h.Queue.TryQueue(task).Should().BeTrue();

        bool reoffered = await h.Worker().RunOneAsync(task, CancellationToken.None);

        reoffered.Should().BeTrue();
        (await h.RowCountAsync()).Should().Be(0, "a verdict for other bytes is worthless");
        h.Queue.TrackedCount.Should().Be(1, "the worker released the finished task, then offered it again");

        // The second pass sees stable bytes and stores the verdict for the new version.
        OutputModerationTask again = await h.Queue.DequeueAsync(CancellationToken.None);
        (await h.Worker().RunOneAsync(again, CancellationToken.None)).Should().BeFalse();
        (await h.RowAsync(key))!.SourceVersion.Should().Be("e2:1000");
    }

    [Fact]
    public async Task A_prior_block_audit_row_for_the_same_version_is_reused_without_a_vendor_call()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        Guid stepResultId = await h.SeedStepResultAsync(key);
        await h.WithDbAsync(async db =>
        {
            db.ContentModerationDecisions.Add(AuditRow(h, stepResultId, DefaultVersion, ModerationAction.Block));
            await db.SaveChangesAsync();
            return 0;
        });

        await h.ProcessAsync(key);

        h.AssertNothingSent();
        (await h.RowAsync(key))!.State.Should().Be("Block");
        (await h.AuditCountAsync()).Should().Be(1, "no new audit row was written");
    }

    [Theory]
    [InlineData(ModerationAction.Allow)]
    [InlineData(ModerationAction.Review)]
    public async Task An_old_allow_or_review_audit_row_is_not_good_enough_to_be_permanent(ModerationAction old)
    {
        // The row came from the older 40 s or partial sampling: the output is judged once more by the current pipeline.
        Harness h = Harness.Create();
        string key = h.Key();
        Guid stepResultId = await h.SeedStepResultAsync(key);
        await h.WithDbAsync(async db =>
        {
            db.ContentModerationDecisions.Add(AuditRow(h, stepResultId, DefaultVersion, old));
            await db.SaveChangesAsync();
            return 0;
        });

        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(1, "the output is judged afresh");
        (await h.RowAsync(key))!.State.Should().Be("Allow");
    }

    [Fact]
    public async Task An_audit_row_for_other_bytes_is_not_reused()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        Guid stepResultId = await h.SeedStepResultAsync(key);
        await h.WithDbAsync(async db =>
        {
            db.ContentModerationDecisions.Add(AuditRow(h, stepResultId, "old:1", ModerationAction.Allow));
            await db.SaveChangesAsync();
            return 0;
        });

        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Two_step_results_sharing_one_key_make_one_vendor_pass()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedStepResultAsync(key, DateTime.UtcNow.AddMinutes(-2));
        await h.SeedStepResultAsync(key, DateTime.UtcNow.AddMinutes(-1));

        await h.ProcessAsync(key);
        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(1);
        (await h.RowCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_media_output_with_no_frames_is_stored_as_unclassified_and_always_refuses()
    {
        Harness h = Harness.Create(x =>
        {
            x.Options.FailClosedAtPublish = false;
            x.Sampler.Sample = new ModerationMediaSample([], null, ModerationContentKind.Video, "no frames");
        });
        string key = h.Key();

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Unavailable");
        row.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified);
        (await h.ReadAsync(key)).Allowed.Should().BeFalse("an unclassified answer is never an outage to fail open on");
    }

    // -------------------------------------------------------------------------------------------
    // Backoff
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 30)]
    [InlineData(4, 120)]
    [InlineData(5, 360)]
    [InlineData(9, 360)]
    public void The_retry_gaps_are_one_five_thirty_minutes_two_then_six_hours(int attempts, int minutes)
    {
        DateTime now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        OutputModerationBackoff.NextAttemptAt(attempts, now).Should().Be(now.AddMinutes(minutes));
    }

    [Fact]
    public void After_ten_attempts_the_sweep_stops()
    {
        OutputModerationBackoff.NextAttemptAt(10, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public async Task An_outage_is_recorded_with_growing_gaps_and_stops_after_ten_attempts()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        h.Gate.Decision = new ModerationDecision(
            ModerationAction.Allow, Available: false, ReviewQueued: false, null, string.Empty, "hash");

        for (int attempt = 1; attempt <= OutputModerationBackoff.MaxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                await h.WithDbAsync(async db =>
                {
                    OutputModerationVerdict due = await db.OutputModerationVerdicts.SingleAsync();
                    due.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
                    await db.SaveChangesAsync();
                    return 0;
                });
            }

            await h.ProcessAsync(key);

            OutputModerationVerdict row = (await h.RowAsync(key))!;
            row.State.Should().Be("Unavailable");
            row.Attempts.Should().Be(attempt);
            if (attempt < OutputModerationBackoff.MaxAttempts)
            {
                row.NextAttemptAt.Should().NotBeNull();
                row.NextAttemptAt!.Value.Should().BeAfter(DateTime.UtcNow);
            }
            else
            {
                row.NextAttemptAt.Should().BeNull("the sweep stops after ten attempts");
            }
        }

        h.Gate.Calls.Should().Be(OutputModerationBackoff.MaxAttempts);
    }

    [Fact]
    public async Task A_retry_that_succeeds_clears_the_attempts_and_the_retry_time()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedVerdictAsync(
            key, nameof(ModerationAction.Unavailable), DateTime.UtcNow.AddHours(-1),
            attempts: 3, nextAttemptAt: DateTime.UtcNow.AddMinutes(-1));

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Allow");
        row.Attempts.Should().Be(0);
        row.NextAttemptAt.Should().BeNull();
    }

    [Fact]
    public async Task An_outage_that_is_not_due_yet_is_not_retried_by_the_lane()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedVerdictAsync(
            key, nameof(ModerationAction.Unavailable), DateTime.UtcNow,
            attempts: 1, nextAttemptAt: DateTime.UtcNow.AddMinutes(30));

        await h.ProcessAsync(key);

        h.AssertNothingSent();
    }

    // -------------------------------------------------------------------------------------------
    // CSAM hold
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("quarantined")]
    [InlineData("failed-move")]
    [InlineData("pending-control-on")]
    [InlineData("scanning-control-on")]
    public async Task A_held_project_reaches_no_body_sampler_transcription_or_gate_and_writes_no_row(string hold)
    {
        Harness h = Harness.Create(x => x.Csam.Arachnid.Enabled = true);
        string key = h.Key();
        await h.SeedStepResultAsync(key);
        switch (hold)
        {
            case "quarantined":
                await h.AddFileAsync(CsamScanStatus.Quarantined);
                break;
            case "failed-move":
                await h.AddFileAsync(CsamScanStatus.Clean, quarantineKey: "projects/x/quarantine/y");
                break;
            case "pending-control-on":
                await h.AddFileAsync(CsamScanStatus.Pending);
                break;
            default:
                await h.AddFileAsync(CsamScanStatus.Scanning);
                break;
        }

        await h.ProcessAsync(key);

        h.AssertNothingSent();
        (await h.RowCountAsync()).Should().Be(0, "the output stays pending");
        (await h.AuditCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_pending_csam_verdict_does_not_defer_judging_when_the_control_is_off()
    {
        Harness h = Harness.Create(x => x.Csam.Arachnid.Enabled = false);
        string key = h.Key();
        await h.AddFileAsync(CsamScanStatus.Pending);

        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_hold_that_appears_after_sampling_still_prevents_the_vendor_call()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedStepResultAsync(key);
        h.Sampler.OnSample = () => h.AddFileAsync(CsamScanStatus.Quarantined).GetAwaiter().GetResult();

        bool reoffer = await h.ProcessAsync(key);

        reoffer.Should().BeFalse();
        h.Sampler.Calls.Should().Be(1, "the bytes were sampled before the hold appeared");
        h.Gate.Calls.Should().Be(0, "but nothing was sent");
        (await h.RowCountAsync()).Should().Be(0);
        (await h.AuditCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_hold_that_appears_before_transcription_prevents_the_audio_leaving()
    {
        Harness h = Harness.Create(x => x.Sampler.Sample = new ModerationMediaSample(
            [new byte[] { 9 }], new byte[] { 7, 7 }, ModerationContentKind.Video, null));
        string key = h.Key();
        h.Sampler.OnSample = () => h.AddFileAsync(CsamScanStatus.Quarantined).GetAwaiter().GetResult();

        await h.ProcessAsync(key);

        h.TranscriptionLookups.Should().Be(0);
        h.Gate.Calls.Should().Be(0);
        (await h.RowCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_failed_hold_check_prevents_the_send()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedStepResultAsync(key);
        h.Sampler.OnSample = () => h.Tenant.FailSystemScope = true;

        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(0);
        (await h.RowCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_failed_hold_check_before_reading_reads_nothing()
    {
        Harness h = Harness.Create(x => x.Tenant.FailSystemScope = true);

        await h.ProcessAsync(h.Key());

        h.AssertNothingSent();
        (await h.RowCountAsync()).Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------
    // Sweep and trigger
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_offers_two_outputs_newest_first()
    {
        Harness h = Harness.Create();
        string oldest = h.Key("a.mp4");
        string middle = h.Key("b.mp4");
        string newest = h.Key("c.mp4");
        await h.SeedStepResultAsync(oldest, DateTime.UtcNow.AddHours(-3));
        await h.SeedStepResultAsync(newest, DateTime.UtcNow.AddHours(-1));
        await h.SeedStepResultAsync(middle, DateTime.UtcNow.AddHours(-2));

        int offered = await h.Worker().SweepOnceAsync(CancellationToken.None);

        offered.Should().Be(2);
        (await h.Queue.DequeueAsync(CancellationToken.None)).StorageKey.Should().Be(newest);
        (await h.Queue.DequeueAsync(CancellationToken.None)).StorageKey.Should().Be(middle);
    }

    [Fact]
    public async Task The_sweep_offers_nothing_while_the_lane_is_crowded()
    {
        Harness h = Harness.Create();
        await h.SeedStepResultAsync(h.Key());
        for (int i = 0; i < OutputModerationWorker.SweepQueueCeiling; i++)
            h.Queue.TryQueue(new OutputModerationTask(Guid.NewGuid(), $"projects/x/outputFiles/busy-{i}.mp4")).Should().BeTrue();

        (await h.Worker().SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task The_sweep_looks_back_only_over_the_fresh_window_but_a_read_still_judges_an_older_output()
    {
        Harness h = Harness.Create();
        string old = h.Key("old.mp4");
        await h.SeedStepResultAsync(old, DateTime.UtcNow - OutputModerationWorker.FreshWindow - TimeSpan.FromDays(1));

        (await h.Worker().SweepOnceAsync(CancellationToken.None)).Should().Be(0, "the bounded look-back keeps the idle query cheap");
        PublishModerationOutcome read = await h.ReadAsync(old);
        read.Code.Should().Be("CONTENT_MODERATION_PENDING");
        h.Queue.TrackedCount.Should().Be(1, "a read offers the output to the lane, however old it is");
    }

    [Fact]
    public async Task One_tenants_view_offers_do_not_stop_the_sweep_for_everyone()
    {
        Harness h = Harness.Create();
        await h.SeedStepResultAsync(h.Key("waiting.mp4"));
        IOutputModerationEnqueuer enqueuer = h.Provider.GetRequiredService<IOutputModerationEnqueuer>();
        for (int i = 0; i < OutputModerationEnqueuer.MaxQueuedPerOrganization + 3; i++)
            enqueuer.TryQueue(Guid.NewGuid(), $"projects/x/outputFiles/view-{i}.mp4", Guid.NewGuid());
        Guid busyTenant = Guid.NewGuid();
        for (int i = 0; i < 20; i++)
            enqueuer.TryQueue(Guid.NewGuid(), $"projects/y/outputFiles/spam-{i}.mp4", busyTenant);
        h.Queue.TrackedCount.Should().BeGreaterThan(0);

        (await h.Worker().SweepOnceAsync(CancellationToken.None)).Should().BeGreaterThan(0, "the sweep still offers the waiting output");
    }

    [Fact]
    public async Task View_offers_use_the_per_organization_cap()
    {
        Harness h = Harness.Create();
        h.MissingObject = false;
        string[] keys = Enumerable.Range(0, OutputModerationEnqueuer.MaxQueuedPerOrganization + 4)
            .Select(i => h.Key($"view-{i}.mp4")).ToArray();

        foreach (string key in keys)
        {
            await h.WithAsync<PublishModeration, PublishModerationOutcome>(p => p.EvaluateObjectAsync(
                h.ProjectId, key, "video/mp4",
                new ModerationAttribution(Guid.Empty, h.ProjectId, null, null, null, ModerationSurface.Publish, ModerationContentKind.Text),
                CancellationToken.None));
        }

        h.Queue.TrackedCount.Should().Be(OutputModerationEnqueuer.MaxQueuedPerOrganization, "one tenant's views cannot hold the lane");
    }

    [Fact]
    public async Task Two_busy_organizations_do_not_keep_a_third_waiting_and_twenty_five_all_get_a_turn()
    {
        Harness h = Harness.Create();
        // Organization A (the harness's own) and B each have a long backlog; C has one output.
        Guid orgB = Guid.NewGuid(), projectB = Guid.NewGuid(), orgC = Guid.NewGuid(), projectC = Guid.NewGuid();
        await h.WithDbAsync(async db =>
        {
            db.Projects.Add(new Project { Id = projectB, Name = "b", OrganizationId = orgB });
            db.Projects.Add(new Project { Id = projectC, Name = "c", OrganizationId = orgC });
            await db.SaveChangesAsync();
            return 0;
        });
        for (int i = 0; i < 12; i++)
        {
            await h.SeedStepResultAsync(h.Key($"a-{i}.mp4"), DateTime.UtcNow.AddMinutes(-1).AddSeconds(i));
            await h.SeedStepResultAsync($"projects/{projectB}/outputFiles/b-{i}.mp4", DateTime.UtcNow.AddMinutes(-1).AddSeconds(i), projectB, orgB);
        }

        await h.SeedStepResultAsync($"projects/{projectC}/outputFiles/c.mp4", DateTime.UtcNow.AddDays(-2), projectC, orgC);

        OutputModerationWorker worker = h.Worker();
        HashSet<Guid> served = [];
        for (int sweep = 0; sweep < 2; sweep++)
        {
            await worker.SweepOnceAsync(CancellationToken.None);
            while (h.Queue.TrackedCount > 0)
            {
                OutputModerationTask task = await h.Queue.DequeueAsync(CancellationToken.None);
                served.Add(task.ProjectId);
                h.Queue.Complete(task);
            }
        }

        served.Should().Contain(projectC, "the third organization is served within two sweeps, not after the other two run dry");
    }

    [Fact]
    public async Task The_sweep_never_offers_a_judged_output_whatever_its_age()
    {
        Harness h = Harness.Create();
        string allowed = h.Key("allowed.mp4");
        string blocked = h.Key("blocked.mp4");
        string stopped = h.Key("stopped.mp4");
        foreach (string key in new[] { allowed, blocked, stopped })
            await h.SeedStepResultAsync(key, DateTime.UtcNow.AddDays(-60));
        await h.SeedVerdictAsync(allowed, nameof(ModerationAction.Allow), DateTime.UtcNow.AddDays(-60));
        await h.SeedVerdictAsync(blocked, nameof(ModerationAction.Block), DateTime.UtcNow.AddDays(-60));
        await h.SeedVerdictAsync(
            stopped, nameof(ModerationAction.Unavailable), DateTime.UtcNow.AddDays(-60),
            attempts: OutputModerationBackoff.MaxAttempts, nextAttemptAt: null);

        (await h.Worker().SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task The_sweep_offers_a_recorded_outage_whose_retry_time_has_come()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedStepResultAsync(key);
        await h.SeedVerdictAsync(
            key, nameof(ModerationAction.Unavailable), DateTime.UtcNow.AddHours(-1),
            attempts: 1, nextAttemptAt: DateTime.UtcNow.AddMinutes(-1));

        (await h.Worker().SweepOnceAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task The_sweep_skips_a_held_project_and_non_output_keys()
    {
        Harness h = Harness.Create();
        await h.SeedStepResultAsync(h.Key());
        await h.SeedStepResultAsync($"projects/{h.ProjectId}/userFiles/upload.mp4");
        await h.AddFileAsync(CsamScanStatus.Quarantined);

        (await h.Worker().SweepOnceAsync(CancellationToken.None)).Should().Be(0);

        Harness clean = Harness.Create();
        await clean.SeedStepResultAsync($"projects/{clean.ProjectId}/userFiles/upload.mp4");
        (await clean.Worker().SweepOnceAsync(CancellationToken.None)).Should().Be(0, "only addressable outputs are swept");
    }

    [Fact]
    public async Task The_sweep_does_not_offer_the_same_missing_output_again_at_once()
    {
        Harness h = Harness.Create(x => x.MissingObject = true);
        await h.SeedStepResultAsync(h.Key());
        OutputModerationWorker worker = h.Worker();

        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        OutputModerationTask task = await h.Queue.DequeueAsync(CancellationToken.None);
        await worker.RunOneAsync(task, CancellationToken.None);

        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(0, "it would starve the rest of the backfill");
    }

    // -------------------------------------------------------------------------------------------
    // Latency (ISSUES-ig-reel-2026-11-03 #6): a held or failed output must not wait out the offer cooldown
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_output_held_by_a_pending_csam_scan_is_offered_again_as_soon_as_the_hold_lifts()
    {
        Harness h = Harness.Create(x => x.Csam.Arachnid.Enabled = true);
        string key = h.Key();
        await h.SeedStepResultAsync(key);
        OutputModerationWorker worker = h.Worker();

        // The sweep offers the output while nothing holds the project; the run then writes a file whose CSAM
        // scan is still pending before the lane takes the offer.
        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        OutputModerationTask task = await h.Queue.DequeueAsync(CancellationToken.None);
        Guid pending = await h.AddFileReturningIdAsync(CsamScanStatus.Pending);
        await worker.RunOneAsync(task, CancellationToken.None);
        h.AssertNothingSent();

        // The scan clears the file: the next sweep must offer the output, not wait out a ten-minute cooldown.
        await h.SetCsamStatusAsync(pending, CsamScanStatus.Clean);
        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(1,
            "a held output was never judged, so the offer cooldown must not keep it pending once the hold lifts");
    }

    [Fact]
    public async Task A_held_output_asks_the_csam_control_to_scan_the_project_now()
    {
        Harness h = Harness.Create(x => x.Csam.Arachnid.Enabled = true);
        string key = h.Key();
        await h.SeedStepResultAsync(key);
        await h.AddFileAsync(CsamScanStatus.Pending);
        OutputModerationWorker worker = h.Worker();

        await worker.RunOneAsync(new OutputModerationTask(h.ProjectId, key), CancellationToken.None);

        h.CsamScans.Requested.Should().Equal([h.ProjectId],
            "waiting for the fifteen-minute CSAM sweep is what kept new renders pending for 15-30 minutes");
        h.AssertNothingSent();
    }

    [Fact]
    public async Task A_judged_output_does_not_ask_for_a_csam_scan()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedStepResultAsync(key);

        await h.Worker().RunOneAsync(new OutputModerationTask(h.ProjectId, key), CancellationToken.None);

        h.CsamScans.Requested.Should().BeEmpty();
        (await h.RowAsync(key))!.State.Should().Be(nameof(ModerationAction.Allow));
    }

    [Fact]
    public async Task A_recorded_outage_is_retried_at_its_own_retry_time_not_after_the_offer_cooldown()
    {
        Harness h = Harness.Create();
        h.Gate.Decision = new ModerationDecision(ModerationAction.Unavailable, Available: false, ReviewQueued: false, null, string.Empty, "hash");
        string key = h.Key();
        await h.SeedStepResultAsync(key);
        OutputModerationWorker worker = h.Worker();

        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        await worker.RunOneAsync(await h.Queue.DequeueAsync(CancellationToken.None), CancellationToken.None);
        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be(nameof(ModerationAction.Unavailable));
        row.NextAttemptAt.Should().NotBeNull();

        // The first retry gap (one minute) has passed.
        await h.WithDbAsync(async db =>
        {
            OutputModerationVerdict tracked = await db.OutputModerationVerdicts.SingleAsync();
            tracked.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
            return 0;
        });

        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(1,
            "the stored row's retry time governs; the offer cooldown turned a one-minute retry into ten minutes");
    }

    [Fact]
    public async Task The_wake_signal_ends_the_wait_at_once_and_is_remembered_until_taken()
    {
        OutputModerationWake wake = new();
        wake.Wake();
        wake.Wake();

        (await wake.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None)).Should().BeTrue("a wake before the wait is not lost");
        (await wake.WaitAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None)).Should().BeFalse("two wakes collapse into one");
    }

    [Fact]
    public async Task The_sweep_loop_runs_again_at_once_when_woken()
    {
        Harness h = Harness.Create();
        OutputModerationWake wake = new();
        OutputModerationWorker worker = h.Worker(wake: wake);
        using CancellationTokenSource stop = new();
        Task loop = worker.StartAsync(stop.Token);

        // The first sweep found nothing; the loop now sleeps for the 30 s interval.
        await Task.Delay(300);
        string key = h.Key();
        await h.SeedStepResultAsync(key);
        wake.Wake();

        // The lane's own consumer judges what the woken sweep offers, long before the 30 s sleep would have ended.
        OutputModerationVerdict? row = null;
        for (int i = 0; i < 100 && row is null; i++)
        {
            await Task.Delay(50);
            row = await h.RowAsync(key);
        }

        row.Should().NotBeNull("the wake must end the sweep's sleep");

        await stop.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
        await loop;
    }

    [Fact]
    public async Task The_sweep_does_nothing_when_moderation_is_off()
    {
        Harness h = Harness.Create(x => x.Options.Enabled = false);
        await h.SeedStepResultAsync(h.Key());

        (await h.Worker().SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task The_backfill_judges_each_existing_output_once()
    {
        Harness h = Harness.Create();
        await h.SeedStepResultAsync(h.Key("a.mp4"), DateTime.UtcNow.AddDays(-20));
        await h.SeedStepResultAsync(h.Key("b.mp4"), DateTime.UtcNow.AddDays(-10));
        OutputModerationWorker worker = h.Worker();

        for (int round = 0; round < 4; round++)
        {
            await worker.SweepOnceAsync(CancellationToken.None);
            while (h.Queue.TrackedCount > 0)
            {
                OutputModerationTask task = await h.Queue.DequeueAsync(CancellationToken.None);
                await worker.RunOneAsync(task, CancellationToken.None);
            }
        }

        h.Gate.Calls.Should().Be(2);
        (await h.RowCountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_finished_step_offers_exactly_its_output_and_touches_no_row()
    {
        Harness h = Harness.Create();
        OutputModerationConsumer consumer = h.NewConsumer();
        string key = h.Key("film.mp4");

        await consumer.Consume(Message(h, key, "Completed"));

        h.Queue.TrackedCount.Should().Be(1);
        OutputModerationTask offered = await h.Queue.DequeueAsync(CancellationToken.None);
        offered.ProjectId.Should().Be(h.ProjectId, "the event's project");
        offered.StorageKey.Should().Be(key, "the event's OutputStorageKey");
        (await h.RowCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_view_offers_the_output_and_without_an_enqueuer_it_is_a_safe_no_op()
    {
        Harness h = Harness.Create();
        string key = h.Key();

        PublishModerationOutcome offered = await h.ReadAsync(key);

        offered.Code.Should().Be("CONTENT_MODERATION_PENDING");
        h.Queue.TrackedCount.Should().Be(1, "the view offers through the enqueuer");
        OutputModerationTask first = await h.Queue.DequeueAsync(CancellationToken.None);
        first.StorageKey.Should().Be(key);
        h.Queue.Complete(first);

        // A host that registers no enqueuer (a direct construction) still answers pending and queues nothing.
        string unroutable = h.Key("second.mp4");
        PublishModerationOutcome outcome = await h.WithDbAsync(db => new PublishModeration(
            h.Gate, h.Storage.Object, h.Sampler, h.Providers.Object, h.Factory.Object, db,
            Microsoft.Extensions.Options.Options.Create(h.Options),
            NullLogger<PublishModeration>.Instance,
            enqueuer: null, tenant: h.Tenant).EvaluateObjectAsync(
                h.ProjectId, unroutable, "video/mp4",
                new ModerationAttribution(h.OrgId, h.ProjectId, null, null, null, ModerationSurface.Publish, ModerationContentKind.Video),
                CancellationToken.None));

        outcome.Code.Should().Be("CONTENT_MODERATION_PENDING");
        h.Queue.TrackedCount.Should().Be(0, "no enqueuer, no offer");
    }

    [Fact]
    public async Task A_cap_refused_offer_is_picked_up_by_the_sweep_within_one_short_interval()
    {
        Harness h = Harness.Create();
        IOutputModerationEnqueuer enqueuer = h.Provider.GetRequiredService<IOutputModerationEnqueuer>();
        for (int i = 0; i < OutputModerationEnqueuer.MaxQueuedPerOrganization; i++)
            enqueuer.TryQueue(h.ProjectId, h.Key($"queued-{i}.mp4"), h.OrgId).Should().BeTrue();

        string key = h.Key("fresh.mp4");
        await h.SeedStepResultAsync(key, DateTime.UtcNow.AddSeconds(-31));

        // The production offer (and every view offer) is refused: the organization's few slots are full.
        enqueuer.TryQueue(h.ProjectId, key, h.OrgId).Should().BeFalse();

        OutputModerationWorker worker = h.Worker();
        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(1, "the sweep bypasses the per-organization cap");
        worker.LastSweepSawRecentRow.Should().BeTrue("the refused output was produced a moment ago");

        List<OutputModerationTask> queued = [];
        for (int i = 0; i < OutputModerationEnqueuer.MaxQueuedPerOrganization + 1; i++)
        {
            OutputModerationTask task = await h.Queue.DequeueAsync(CancellationToken.None);
            queued.Add(task);
            h.Queue.Complete(task);
        }
        queued.Select(t => t.StorageKey).Should().Contain(key);

        OutputModerationWorker.NextSweepInterval(
                worker.LastSweepFoundNothing, worker.LastSweepSawRecentRow, OutputModerationWorker.MaxIdleSweepInterval)
            .Should().Be(OutputModerationWorker.SweepInterval, "a recent output keeps the lane at the short interval");
    }

    [Fact]
    public async Task A_produced_row_older_than_one_short_interval_is_offered_on_the_next_sweep_while_the_lane_is_idle()
    {
        Harness h = Harness.Create();
        string key = h.Key("fresh.mp4");
        await h.SeedStepResultAsync(key, DateTime.UtcNow.AddSeconds(-31));

        OutputModerationWorker worker = h.Worker();
        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(1);
        (await h.Queue.DequeueAsync(CancellationToken.None)).StorageKey.Should().Be(key);
    }

    [Fact]
    public async Task The_idle_backoff_applies_only_while_no_row_is_recent()
    {
        // A recent output that already has its verdict is not a candidate, but it is recent work: the lane must not
        // back off, because an offer refused a moment ago may still be waiting for the next sweep.
        Harness h = Harness.Create();
        string recent = h.Key("recent.mp4");
        await h.SeedStepResultAsync(recent, DateTime.UtcNow.AddSeconds(-30));
        await h.SeedVerdictAsync(recent, nameof(ModerationAction.Allow), DateTime.UtcNow);

        OutputModerationWorker worker = h.Worker();
        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        worker.LastSweepFoundNothing.Should().BeTrue("there is nothing to offer");
        worker.LastSweepSawRecentRow.Should().BeTrue("but the newest output was produced seconds ago");
        OutputModerationWorker.NextSweepInterval(
                worker.LastSweepFoundNothing, worker.LastSweepSawRecentRow, OutputModerationWorker.MaxIdleSweepInterval)
            .Should().Be(OutputModerationWorker.SweepInterval, "recent rows are swept every 30 s");

        // Only old work lets the lane back off, and never past the maximum.
        Harness old = Harness.Create();
        string oldKey = old.Key("old.mp4");
        await old.SeedStepResultAsync(oldKey, DateTime.UtcNow.AddMinutes(-10));
        await old.SeedVerdictAsync(oldKey, nameof(ModerationAction.Allow), DateTime.UtcNow.AddMinutes(-10));

        OutputModerationWorker oldWorker = old.Worker();
        (await oldWorker.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        oldWorker.LastSweepSawRecentRow.Should().BeFalse("the newest output is older than the recent-row threshold");
        OutputModerationWorker.NextSweepInterval(true, false, OutputModerationWorker.SweepInterval)
            .Should().Be(TimeSpan.FromSeconds(60), "an idle lane with only old rows doubles the interval");
        OutputModerationWorker.NextSweepInterval(true, false, OutputModerationWorker.MaxIdleSweepInterval)
            .Should().Be(OutputModerationWorker.MaxIdleSweepInterval, "and never waits longer than the hard maximum");
    }

    [Fact]
    public async Task A_cap_refused_burst_produced_at_the_deepest_idle_backoff_waits_at_most_the_bounded_interval()
    {
        Harness h = Harness.Create();
        IOutputModerationEnqueuer enqueuer = h.Provider.GetRequiredService<IOutputModerationEnqueuer>();
        for (int i = 0; i < OutputModerationEnqueuer.MaxQueuedPerOrganization; i++)
            enqueuer.TryQueue(h.ProjectId, h.Key($"queued-{i}.mp4"), h.OrgId).Should().BeTrue();

        string key = h.Key("burst.mp4");
        await h.SeedStepResultAsync(key, DateTime.UtcNow.AddSeconds(-31));
        enqueuer.TryQueue(h.ProjectId, key, h.OrgId).Should().BeFalse("the organization's few slots are full");

        // The lane is as idle as it gets: the old ceiling would sleep ten minutes before seeing this burst.
        OutputModerationWorker worker = h.Worker();
        worker.IdleCeiling.Should().Be(TimeSpan.FromSeconds(60), "the shipped idle ceiling");
        OutputModerationWorker.NextSweepInterval(
                true, false, OutputModerationWorker.MaxIdleSweepInterval, worker.IdleCeiling)
            .Should().Be(TimeSpan.FromSeconds(60), "a fresh burst can never wait the old ten minutes");

        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(1, "the sweep offers the refused output");
        OutputModerationWorker.NextSweepInterval(
                worker.LastSweepFoundNothing, worker.LastSweepSawRecentRow, worker.IdleCeiling, worker.IdleCeiling)
            .Should().Be(OutputModerationWorker.SweepInterval, "and stays on the 30 s interval while the row is recent");
    }

    [Fact]
    public void The_idle_ceiling_is_configurable_and_clamped()
    {
        Harness h = Harness.Create();

        OutputModerationWorker configured = h.Worker(Configuration(("ContentModeration:MaxIdleSweepSeconds", "90")));
        configured.IdleCeiling.Should().Be(TimeSpan.FromSeconds(90));
        OutputModerationWorker.NextSweepInterval(
                true, false, OutputModerationWorker.MaxIdleSweepInterval, configured.IdleCeiling)
            .Should().Be(TimeSpan.FromSeconds(90), "the configured ceiling bounds the idle sleep");

        h.Worker(Configuration(("ContentModeration:MaxIdleSweepSeconds", "1"))).IdleCeiling
            .Should().Be(OutputModerationWorker.SweepInterval, "never below the short interval");
        h.Worker(Configuration(("ContentModeration:MaxIdleSweepSeconds", "86400"))).IdleCeiling
            .Should().Be(OutputModerationWorker.MaxIdleSweepInterval, "never past the hard maximum");
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Completed", false)]
    public async Task A_step_that_is_not_an_addressable_completed_output_offers_nothing(string status, bool addressable)
    {
        Harness h = Harness.Create();
        OutputModerationConsumer consumer = h.NewConsumer();
        string key = addressable ? h.Key() : $"projects/{h.ProjectId}/agentFiles/video-analysis/a.json";

        await consumer.Consume(Message(h, key, status));

        h.Queue.TrackedCount.Should().Be(0);
    }

    [Fact]
    public async Task A_finished_step_offers_nothing_when_moderation_is_off()
    {
        Harness h = Harness.Create(x => x.Options.Enabled = false);
        OutputModerationConsumer consumer = h.NewConsumer();

        await consumer.Consume(Message(h, h.Key(), "Completed"));

        h.Queue.TrackedCount.Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------
    // Metric
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(-3 * 3600, 0)]
    public async Task The_verdict_latency_is_recorded_for_a_fresh_output_and_not_for_backfill(
        int producedSecondsAgo, int expectedRecords)
    {
        List<double> recorded = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reelbolt.moderation.output_verdict_seconds")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            lock (recorded) recorded.Add(value);
        });
        listener.Start();

        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedStepResultAsync(key, DateTime.UtcNow.AddSeconds(producedSecondsAgo));

        await h.ProcessAsync(key);

        lock (recorded) recorded.Count.Should().Be(expectedRecords);
    }

    // -------------------------------------------------------------------------------------------
    // Review fixes
    // -------------------------------------------------------------------------------------------

    private static async Task AddAuditAsync(Harness h, params ContentModerationDecision[] rows) =>
        await h.WithDbAsync(async db =>
        {
            db.ContentModerationDecisions.AddRange(rows);
            await db.SaveChangesAsync();
            return 0;
        });

    [Fact]
    public async Task Frames_allow_then_a_chunk_outage_is_judged_afresh_not_reused()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        Guid step = await h.SeedStepResultAsync(key);
        await AddAuditAsync(h,
            AuditRow(h, step, DefaultVersion, ModerationAction.Allow, "[\"image\"]", DateTime.UtcNow.AddMinutes(-2)),
            AuditRow(h, step, DefaultVersion, ModerationAction.Unavailable, "[\"text\"]", DateTime.UtcNow.AddMinutes(-1)));

        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(1, "one frames-only Allow does not cover the object");
    }

    [Fact]
    public async Task A_retry_never_reuses_an_audit_row_even_a_whole_object_allow()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        Guid step = await h.SeedStepResultAsync(key);
        await AddAuditAsync(h, AuditRow(h, step, DefaultVersion, ModerationAction.Allow));
        await h.SeedVerdictAsync(
            key, nameof(ModerationAction.Unavailable), DateTime.UtcNow.AddHours(-1),
            attempts: 1, nextAttemptAt: DateTime.UtcNow.AddMinutes(-1));

        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_later_allow_does_not_outrank_an_earlier_block()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        Guid step = await h.SeedStepResultAsync(key);
        ContentModerationDecision block = AuditRow(h, step, DefaultVersion, ModerationAction.Block, "[\"text\"]", DateTime.UtcNow.AddMinutes(-5));
        block.ReasonMessage = "This video may contain violent content.";
        await AddAuditAsync(h, block, AuditRow(h, step, DefaultVersion, ModerationAction.Allow, "[\"image\",\"text\"]", DateTime.UtcNow));

        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(0);
        (await h.RowAsync(key))!.State.Should().Be("Block");
    }

    [Fact]
    public async Task Gate_to_serve_a_project_file_leaves_other_keys_alone_and_adds_no_head()
    {
        Harness h = Harness.Create();

        PublishModerationOutcome? outcome = await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p =>
            p.GateStoredFileAsync(h.ProjectId, $"projects/{h.ProjectId}/userFiles/a.mp4", CancellationToken.None));

        outcome.Should().BeNull();
        h.Storage.Verify(
            s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("NoRow", "CONTENT_MODERATION_PENDING")]
    [InlineData("Block", "CONTENT_BLOCKED")]
    [InlineData("Outage", "CONTENT_MODERATION_UNAVAILABLE")]
    [InlineData("Allow", null)]
    [InlineData("Review", null)]
    public async Task A_project_file_sharing_an_output_key_is_gated_by_the_stored_verdict(string state, string? code)
    {
        Harness h = Harness.Create();
        string key = h.Key();
        if (state != "NoRow")
        {
            await h.SeedVerdictAsync(
                key, state == "Outage" ? "Unavailable" : state, DateTime.UtcNow,
                message: state == "Block" ? "Blocked." : null, attempts: state == "Outage" ? 1 : 0,
                nextAttemptAt: state == "Outage" ? DateTime.UtcNow.AddHours(1) : null);
        }

        PublishModerationOutcome? outcome = await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p =>
            p.GateStoredFileAsync(h.ProjectId, key, CancellationToken.None));

        outcome?.Code.Should().Be(code);
        if (code is null) outcome.Should().BeNull();
        h.AssertNothingSent();
    }

    [Fact]
    public async Task A_project_file_route_still_serves_an_output_with_nothing_to_classify()
    {
        Harness h = Harness.Create();
        string key = h.Key("music.mp3");
        await h.SeedVerdictAsync(
            key, nameof(ModerationAction.Unavailable), DateTime.UtcNow,
            reasonCode: OutputModerationReasonCodes.NothingToClassify, attempts: 1);

        (await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p =>
            p.GateStoredFileAsync(h.ProjectId, key, CancellationToken.None))).Should().BeNull();
        (await h.ReadAsync(key)).Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE", "download still refuses it");
    }

    [Fact]
    public async Task The_project_file_row_mime_type_wins_over_the_extension()
    {
        Harness h = Harness.Create();
        string key = h.Key("noextension");
        await h.WithDbAsync(async db =>
        {
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = h.ProjectId,
                StorageKey = key,
                MimeType = "audio/mpeg",
                CsamStatus = CsamScanStatus.Clean
            });
            await db.SaveChangesAsync();
            return 0;
        });

        await h.ProcessAsync(key);

        h.Sampler.LastMime.Should().Be("audio/mpeg");
    }

    [Theory]
    [InlineData("a.avi", "video/x-msvideo")]
    [InlineData("a.mpg", "video/mpeg")]
    [InlineData("a.ts", "video/mp2t")]
    [InlineData("a.mxf", "video/mxf")]
    [InlineData("a.aac", "audio/aac")]
    [InlineData("a.flac", "audio/flac")]
    [InlineData("a.opus", "audio/ogg")]
    [InlineData("a.aiff", "audio/aiff")]
    [InlineData("a.bmp", "image/bmp")]
    [InlineData("a.avif", "image/avif")]
    public void The_extension_fallback_knows_the_media_types_the_sampler_takes(string key, string mime)
    {
        OutputMimeTypes.FromKey(key).Should().Be(mime);
    }

    [Fact]
    public async Task Wordless_audio_is_sampled_once_never_retried_refused_at_download_and_served_by_the_file_route()
    {
        Harness h = Harness.Create(x =>
        {
            x.HasTranscriptionProvider = true;
            x.TranscriptText = "";
            x.Sampler.Sample = new ModerationMediaSample([], new byte[] { 7, 7 }, ModerationContentKind.Audio, null);
        });
        string key = h.Key("music.mp3");

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Unavailable");
        row.ReasonCode.Should().Be(OutputModerationReasonCodes.NothingToClassify);
        row.NextAttemptAt.Should().BeNull("there is no timer retry");

        // Not retried by the lane, a view, or the sweep.
        await h.WithDbAsync(async db =>
        {
            OutputModerationVerdict r = await db.OutputModerationVerdicts.SingleAsync();
            r.DecidedAt = DateTime.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
            return 0;
        });
        (await h.ReadAsync(key)).Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        h.Queue.TrackedCount.Should().Be(0, "a view does not offer it again");
        await h.ProcessAsync(key);
        (await h.Worker().SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        h.Sampler.Calls.Should().Be(1, "the bytes were sampled once");
        h.Gate.Calls.Should().Be(0, "and never went to the vendor");

        (await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p =>
            p.GateStoredFileAsync(h.ProjectId, key, CancellationToken.None))).Should().BeNull("the file route serves it");
    }

    [Fact]
    public async Task A_failed_transcription_is_an_outage_that_retries()
    {
        Harness h = Harness.Create(x =>
            x.Sampler.Sample = new ModerationMediaSample([], new byte[] { 7, 7 }, ModerationContentKind.Audio, null));
        string key = h.Key("music.mp3");

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified);
        row.NextAttemptAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("held")]
    [InlineData("failed-check")]
    public async Task The_inline_path_sends_nothing_for_a_held_project_or_a_failed_hold_check(string mode)
    {
        Harness h = Harness.Create();
        if (mode == "held") await h.AddFileAsync(CsamScanStatus.Quarantined);
        else h.Tenant.FailSystemScope = true;

        PublishModerationOutcome outcome = await h.WithAsync<PublishModeration, PublishModerationOutcome>(p => p.JudgeInlineAsync(
            h.ProjectId, h.Key(), "image/png",
            new ModerationAttribution(h.OrgId, h.ProjectId, Guid.NewGuid(), null, null, ModerationSurface.Publish, ModerationContentKind.Image),
            CancellationToken.None));

        outcome.Allowed.Should().BeFalse();
        outcome.Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        h.AssertNothingSent();
    }

    [Fact]
    public async Task Fifty_newer_unjudgeable_outputs_do_not_starve_an_older_unjudged_one()
    {
        Harness h = Harness.Create(x => x.MissingPrefix = "missing-");
        for (int i = 0; i < 50; i++)
            await h.SeedStepResultAsync(h.Key($"missing-{i}.mp4"), DateTime.UtcNow.AddMinutes(-10).AddSeconds(i));
        string older = h.Key("older.mp4");
        await h.SeedStepResultAsync(older, DateTime.UtcNow.AddDays(-5));
        OutputModerationWorker worker = h.Worker();

        for (int round = 0; round < 40 && await h.RowAsync(older) is null; round++)
        {
            await worker.SweepOnceAsync(CancellationToken.None);
            while (h.Queue.TrackedCount > 0)
                await worker.RunOneAsync(await h.Queue.DequeueAsync(CancellationToken.None), CancellationToken.None);
        }

        (await h.RowAsync(older)).Should().NotBeNull("the sweep window moves past keys it already offered");
    }

    [Fact]
    public async Task An_audit_row_the_gate_failed_to_save_is_not_written_with_the_verdict()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        h.Gate.OnModerate = db => db.ContentModerationDecisions.Add(
            AuditRow(h, Guid.NewGuid(), DefaultVersion, ModerationAction.Allow));

        await h.ProcessAsync(key);

        (await h.RowAsync(key)).Should().NotBeNull("the verdict is stored");
        (await h.AuditCountAsync()).Should().Be(0, "the unsaved audit row was not swept along");
    }

    [Fact]
    public async Task A_text_row_on_a_media_key_is_unclassified_and_refused_never_judged_as_text()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.WithDbAsync(async db =>
        {
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = h.ProjectId,
                StorageKey = key,
                MimeType = "text/plain",
                CsamStatus = CsamScanStatus.Clean
            });
            await db.SaveChangesAsync();
            return 0;
        });

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Unavailable");
        row.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified, "a disagreement is not 'nothing to classify'");
        h.AssertNothingSent();
        (await h.ReadAsync(key)).Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
    }

    [Fact]
    public async Task A_video_row_on_an_image_key_is_a_disagreement_too()
    {
        Harness h = Harness.Create();
        string key = h.Key("still.png");
        await h.WithDbAsync(async db =>
        {
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = h.ProjectId,
                StorageKey = key,
                MimeType = "video/mp4",
                CsamStatus = CsamScanStatus.Clean
            });
            await db.SaveChangesAsync();
            return 0;
        });

        await h.ProcessAsync(key);

        (await h.RowAsync(key))!.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified);
        h.AssertNothingSent();
    }

    [Fact]
    public async Task A_text_row_never_selects_the_text_path_for_a_key_with_an_unknown_extension()
    {
        Harness h = Harness.Create();
        string key = h.Key("blob.xyz");
        await h.WithDbAsync(async db =>
        {
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = h.ProjectId,
                StorageKey = key,
                MimeType = "text/plain",
                CsamStatus = CsamScanStatus.Clean
            });
            await db.SaveChangesAsync();
            return 0;
        });

        await h.ProcessAsync(key);

        h.Sampler.LastMime.Should().Be(FfmpegModerationMediaSampler.FollowProbeMimeType, "the row's text type did not select the text path; the probe decides");
    }

    [Theory]
    [InlineData(new byte[] { 104, 105, 0, 106 })]
    [InlineData(new byte[] { 0xC3, 0x28, 0xFF })]
    public async Task A_txt_key_holding_binary_is_unclassified_and_refused(byte[] body)
    {
        Harness h = Harness.Create(x => x.Body = body);
        string key = h.Key("notes.txt");

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Unavailable");
        row.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified);
        h.Gate.Calls.Should().Be(0);
        (await h.ReadAsync(key)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task A_real_text_output_is_still_classified_as_text()
    {
        Harness h = Harness.Create(x => x.Body = "a caption"u8.ToArray());

        await h.ProcessAsync(h.Key("caption.txt"));

        h.Gate.Calls.Should().Be(1);
        (await h.RowAsync(h.Key("caption.txt")))!.State.Should().Be("Allow");
    }

    [Fact]
    public async Task A_copy_of_an_output_is_refused_on_an_outage_even_when_the_operator_fails_open()
    {
        Harness h = Harness.Create(x => x.Options.FailClosedAtPublish = false);
        string key = h.Key();
        await h.SeedVerdictAsync(
            key, nameof(ModerationAction.Unavailable), DateTime.UtcNow, attempts: 1, nextAttemptAt: DateTime.UtcNow.AddHours(1));

        (await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p => p.GateStoredCopyAsync(h.ProjectId, key, CancellationToken.None)))!
            .Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        (await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p => p.GateStoredFileAsync(h.ProjectId, key, CancellationToken.None)))
            .Should().BeNull("serving follows the operator's choice; leaving the gate for good does not");
    }

    [Fact]
    public async Task Rewritten_bytes_at_an_output_key_read_as_a_new_version_and_are_judged_again()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedStepResultAsync(key);
        await h.SeedVerdictAsync(key, nameof(ModerationAction.Allow), DateTime.UtcNow.AddDays(-1));
        h.ETag = "\"rewritten\"";

        (await h.ReadAsync(key)).Code.Should().Be("CONTENT_MODERATION_PENDING");
        (await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p => p.GateStoredCopyAsync(h.ProjectId, key, CancellationToken.None)))!
            .Code.Should().Be("CONTENT_MODERATION_PENDING");
        await h.ProcessAsync(key);
        h.Gate.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_video_with_a_partial_frames_failure_is_unclassified_and_never_an_allow()
    {
        Harness h = Harness.Create(x => x.Sampler.Sample = new ModerationMediaSample(
            [new byte[] { 9 }], null, ModerationContentKind.Video, "video frame sampling failed"));
        string key = h.Key();

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Unavailable");
        row.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified);
        row.NextAttemptAt.Should().NotBeNull("a real failure retries");
        h.Gate.Calls.Should().Be(0, "the frames that remain are not sent");
    }

    [Fact]
    public async Task A_video_whose_transcription_failed_is_unclassified_and_retries_not_a_frames_only_allow()
    {
        Harness h = Harness.Create(x =>
        {
            x.HasTranscriptionProvider = true;
            x.TranscriptionThrows = true;
            x.Sampler.Sample = new ModerationMediaSample(
                [new byte[] { 9 }], new byte[] { 7, 7 }, ModerationContentKind.Video, null);
        });
        string key = h.Key();

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Unavailable");
        row.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified);
        row.NextAttemptAt.Should().NotBeNull();
        h.Gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_video_with_audio_and_no_transcription_provider_is_refused_as_an_outage_by_default()
    {
        Harness h = Harness.Create(x => x.Sampler.Sample = new ModerationMediaSample(
            [new byte[] { 9 }], new byte[] { 7, 7 }, ModerationContentKind.Video, null));
        string key = h.Key();

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Unavailable", "the speech cannot be heard, so no frames-only Allow is stored");
        row.NextAttemptAt.Should().NotBe(OutputModerationBackoff.Never, "a transcription provider may be configured later");
        h.Gate.Calls.Should().Be(0, "nothing is sent for a check that cannot be complete");
    }

    [Fact]
    public async Task Frames_only_is_allowed_only_when_the_operator_says_so_and_the_row_records_it()
    {
        Harness h = Harness.Create(x =>
        {
            x.Options.AllowFramesOnlyWithoutTranscription = true;
            x.Sampler.Sample = new ModerationMediaSample(
                [new byte[] { 9 }], new byte[] { 7, 7 }, ModerationContentKind.Video, null);
        });
        string key = h.Key();

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Allow");
        row.ReasonCode.Should().Be(OutputModerationReasonCodes.FramesOnly);
        h.Gate.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_video_with_no_audio_stream_is_judged_on_its_frames()
    {
        Harness h = Harness.Create(x => x.Sampler.Sample = new ModerationMediaSample(
            [new byte[] { 9 }], null, ModerationContentKind.Video, null));

        await h.ProcessAsync(h.Key());

        (await h.RowAsync(h.Key()))!.State.Should().Be("Allow");
    }

    [Fact]
    public async Task A_video_with_zero_frames_and_no_words_is_unclassified_not_nothing_to_classify()
    {
        Harness h = Harness.Create(x => x.Sampler.Sample = new ModerationMediaSample(
            [], null, ModerationContentKind.Video, null));

        await h.ProcessAsync(h.Key());

        (await h.RowAsync(h.Key()))!.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified);
    }

    [Fact]
    public async Task An_unsupported_type_is_unclassified_with_no_timer_retry_and_the_file_route_refuses_it()
    {
        Harness h = Harness.Create(x => x.Sampler.Sample = new ModerationMediaSample(
            [], null, ModerationContentKind.Text, "Unsupported moderation media type.", Deterministic: true));
        string key = h.Key("blob.xyz");

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified);
        row.NextAttemptAt.Should().Be(OutputModerationBackoff.Never, "it can never succeed, so nothing offers it again by time");
        (await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p =>
            p.GateStoredFileAsync(h.ProjectId, key, CancellationToken.None)))!.Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        (await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p =>
            p.GateStoredCopyAsync(h.ProjectId, key, CancellationToken.None)))!.Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
    }

    [Theory]
    [InlineData("export.csv")]
    [InlineData("cuts.edl")]
    [InlineData("feed.xml")]
    public async Task Text_extensions_take_the_text_path(string name)
    {
        Harness h = Harness.Create(x => x.Body = "a,b"u8.ToArray());

        await h.ProcessAsync(h.Key(name));

        h.Gate.Calls.Should().Be(1);
        h.Sampler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_sampler_flagged_deterministic_failure_has_no_timer_retry()
    {
        Harness h = Harness.Create(x => x.Sampler.Sample = new ModerationMediaSample(
            [new byte[] { 9 }], null, ModerationContentKind.Video, "more than one audio track", Deterministic: true));

        await h.ProcessAsync(h.Key());

        var row = (await h.RowAsync(h.Key()))!;
        row.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified);
        row.NextAttemptAt.Should().Be(OutputModerationBackoff.Never);
    }

    [Fact]
    public async Task A_refusal_for_length_says_length_is_the_reason_and_not_to_re_export()
    {
        Harness h = Harness.Create(x => x.Sampler.Sample = new ModerationMediaSample(
            [new byte[] { 9 }], null, ModerationContentKind.Video, "the video is longer than 12 hours", Deterministic: true, TooLong: true));

        await h.ProcessAsync(h.Key());

        var row = (await h.RowAsync(h.Key()))!;
        row.ReasonCode.Should().Be(ModerationReasonCodes.TooLong);
        row.NextAttemptAt.Should().Be(OutputModerationBackoff.Never);
        PublishModerationOutcome outcome = await h.ReadAsync(h.Key());
        outcome.Final.Should().BeTrue();
        outcome.Message.Should().Contain("longer than 12 hours").And.NotContain("Re-export");
        PublishModerationOutcome.UnavailableFinalClip("cam a.mp4", tooLong: true).Message
            .Should().Contain("cam a.mp4").And.Contain("longer than 12 hours").And.NotContain("Re-export");
        PublishModerationOutcome.UnavailableFinal().Message.Should().Contain("Re-export it in a common format");
    }

    [Fact]
    public async Task A_real_jpeg_output_is_sampled_judged_and_allowed_not_refused_for_good()
    {
        if (!OnPath("ffmpeg")) return;

        string jpg = Path.Combine(Path.GetTempPath(), "verdict-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                       "ffmpeg", $"-y -loglevel error -f lavfi -i testsrc=size=320x240 -frames:v 1 \"{jpg}\"")
            { RedirectStandardError = true })!)
            {
                p.StandardError.ReadToEnd();
                p.WaitForExit();
            }

            Harness h = Harness.Create(x =>
            {
                x.Body = File.ReadAllBytes(jpg);
                x.Sampler.Real = new FfmpegModerationMediaSampler(new SystemProcessRunner(), x.Options);
            });
            string key = h.Key("photo.jpg");

            await h.ProcessAsync(key);

            OutputModerationVerdict row = (await h.RowAsync(key))!;
            row.State.Should().Be("Allow", "every JPEG used to be refused because it opened as an image sequence");
            h.Gate.Calls.Should().Be(1);
        }
        finally
        {
            File.Delete(jpg);
        }
    }

    private static bool OnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(directory => File.Exists(Path.Combine(directory, executable)));

    [Fact]
    public async Task A_deterministic_refusal_is_never_sampled_again_by_time_while_its_bytes_are_unchanged()
    {
        Harness h = Harness.Create(x => x.Sampler.Sample = new ModerationMediaSample(
            [new byte[] { 9 }], null, ModerationContentKind.Video, "more than one audio track", Deterministic: true));
        string key = h.Key();
        await h.ProcessAsync(key);
        int sampled = h.Sampler.Calls;

        // Years later: neither the sweep, nor a view, nor the processor samples it again.
        await h.WithDbAsync(async db =>
        {
            OutputModerationVerdict row = await db.OutputModerationVerdicts.SingleAsync();
            row.DecidedAt = DateTime.UtcNow.AddDays(-400);
            await db.SaveChangesAsync();
            return 0;
        });
        (await h.Worker().SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        PublishModerationOutcome view = await h.WithAsync<PublishModeration, PublishModerationOutcome>(p =>
            p.EvaluateObjectAsync(h.ProjectId, key, "video/mp4",
                new ModerationAttribution(Guid.Empty, h.ProjectId, null, null, null, ModerationSurface.Publish, ModerationContentKind.Text),
                CancellationToken.None));
        view.Allowed.Should().BeFalse();
        view.Message.Should().NotContain("Try again", "a refusal that is certain for these bytes does not promise a retry");
        h.Queue.TrackedCount.Should().Be(0, "no offer was made by the view");
        await h.ProcessAsync(key);

        h.Sampler.Calls.Should().Be(sampled);
    }

    [Fact]
    public async Task One_organization_cannot_fill_the_lane_with_offers()
    {
        Harness h = Harness.Create();
        OutputModerationConsumer consumer = h.NewConsumer();

        for (int i = 0; i < OutputModerationEnqueuer.MaxQueuedPerOrganization + 4; i++)
            await consumer.Consume(Message(h, h.Key($"burst-{i}.mp4"), "Completed"));

        h.Queue.TrackedCount.Should().Be(OutputModerationEnqueuer.MaxQueuedPerOrganization, "the rest is left to the sweep");
        IOutputModerationEnqueuer enqueuer = h.Provider.GetRequiredService<IOutputModerationEnqueuer>();
        Guid other = Guid.NewGuid();
        enqueuer.TryQueue(Guid.NewGuid(), "projects/x/outputFiles/other.mp4", other).Should().BeTrue("another organization still fits");

        // A finished task frees a slot for its organization.
        OutputModerationTask done = await h.Queue.DequeueAsync(CancellationToken.None);
        h.Queue.Complete(done);
        enqueuer.Released(done);
        await consumer.Consume(Message(h, h.Key("after.mp4"), "Completed"));
        h.Queue.TrackedCount.Should().Be(OutputModerationEnqueuer.MaxQueuedPerOrganization + 1, "five waiting plus the other organization's offer");
    }

    [Fact]
    public async Task The_sweep_takes_one_output_per_organization_in_turn()
    {
        Harness h = Harness.Create();
        Guid otherOrg = Guid.NewGuid();
        Guid otherProject = Guid.NewGuid();
        await h.WithDbAsync(async db =>
        {
            db.Projects.Add(new Project { Id = otherProject, Name = "o", OrganizationId = otherOrg });
            await db.SaveChangesAsync();
            return 0;
        });
        await h.SeedStepResultAsync(h.Key("a1.mp4"), DateTime.UtcNow.AddMinutes(-1));
        await h.SeedStepResultAsync(h.Key("a2.mp4"), DateTime.UtcNow.AddMinutes(-2));
        await h.WithDbAsync(async db =>
        {
            Guid wf = Guid.NewGuid(), step = Guid.NewGuid(), exec = Guid.NewGuid();
            db.WorkflowSteps.Add(new WorkflowStep { Id = step, WorkflowDefinitionId = wf, StepOrder = 1 });
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = exec,
                WorkflowDefinitionId = wf,
                ProjectId = otherProject,
                OrganizationId = otherOrg,
                Status = ExecutionStatus.Passed,
                StartedAt = DateTime.UtcNow.AddHours(-5),
                CompletedAt = DateTime.UtcNow.AddHours(-5)
            });
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = exec,
                WorkflowStepId = step,
                Status = StepStatus.Completed,
                ExecutedAt = DateTime.UtcNow.AddHours(-5),
                CompletedAt = DateTime.UtcNow.AddHours(-5),
                OutputStorageKey = $"projects/{otherProject}/outputFiles/old.mp4"
            });
            await db.SaveChangesAsync();
            return 0;
        });

        (await h.Worker().SweepOnceAsync(CancellationToken.None)).Should().Be(2);

        OutputModerationTask first = await h.Queue.DequeueAsync(CancellationToken.None);
        OutputModerationTask second = await h.Queue.DequeueAsync(CancellationToken.None);
        new[] { first.ProjectId, second.ProjectId }.Should().BeEquivalentTo(new[] { h.ProjectId, otherProject },
            "one output from each organization, not the two newest of the busy one");
    }

    [Fact]
    public async Task The_queue_depth_gauge_reports_the_lane()
    {
        Harness h = Harness.Create();
        h.Worker();
        h.Queue.TryQueue(new OutputModerationTask(h.ProjectId, h.Key("g.mp4")));

        OutputModerationDiagnostics.DepthProbe!().Should().Be(1);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Text_of_a_held_project_never_reaches_the_vendor()
    {
        Harness h = Harness.Create();
        await h.AddFileAsync(CsamScanStatus.Quarantined);

        PublishModerationOutcome outcome = await h.WithAsync<PublishModeration, PublishModerationOutcome>(p => p.EvaluateTextAsync(
            "hello", new ModerationAttribution(h.OrgId, h.ProjectId, Guid.NewGuid(), null, null, ModerationSurface.Publish, ModerationContentKind.Text),
            CancellationToken.None));

        outcome.Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        h.Gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Text_of_a_clean_project_is_still_classified()
    {
        Harness h = Harness.Create();

        PublishModerationOutcome outcome = await h.WithAsync<PublishModeration, PublishModerationOutcome>(p => p.EvaluateTextAsync(
            "hello", new ModerationAttribution(h.OrgId, h.ProjectId, Guid.NewGuid(), null, null, ModerationSurface.Publish, ModerationContentKind.Text),
            CancellationToken.None));

        outcome.Allowed.Should().BeTrue();
        h.Gate.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_refusal_on_a_file_or_copy_path_uses_plain_wording_not_publication()
    {
        Harness h = Harness.Create();
        string key = h.Key();
        await h.SeedVerdictAsync(key, nameof(ModerationAction.Unavailable), DateTime.UtcNow, attempts: 1, nextAttemptAt: DateTime.UtcNow.AddHours(1));

        PublishModerationOutcome? file = await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p => p.GateStoredFileAsync(h.ProjectId, key, CancellationToken.None));
        PublishModerationOutcome? copy = await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p => p.GateStoredCopyAsync(h.ProjectId, key, CancellationToken.None));

        file.Should().NotBeNull();
        copy!.Code.Should().Be("CONTENT_MODERATION_UNAVAILABLE");
        copy.Message.Should().NotContain("Publication");
    }

    private async Task<Harness> UploadedClipAsync(FileModerationStatus status, string name = "clip.mp4")
    {
        Harness h = Harness.Create();
        await h.WithDbAsync(async db =>
        {
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = h.ProjectId,
                StorageKey = $"projects/{h.ProjectId}/userFiles/{name}",
                MimeType = "video/mp4",
                CsamStatus = CsamScanStatus.Clean,
                ModerationStatus = status
            });
            await db.SaveChangesAsync();
            return 0;
        });
        return h;
    }

    [Theory]
    [InlineData(FileModerationStatus.Clean)]
    [InlineData(FileModerationStatus.Unmoderated)]
    [InlineData(FileModerationStatus.Scanning)]
    [InlineData(FileModerationStatus.Error)]
    public async Task No_ingest_status_ever_becomes_an_allow_an_uploaded_clip_is_offered_to_the_output_lane(FileModerationStatus status)
    {
        Harness h = await UploadedClipAsync(status);
        string key = $"projects/{h.ProjectId}/userFiles/clip.mp4";

        PublishModerationOutcome outcome = await h.ReadAsync(key);

        outcome.Code.Should().Be("CONTENT_MODERATION_PENDING");
        h.Queue.TrackedCount.Should().Be(1, "the output lane judges it under its own rules");
        (await h.RowCountAsync()).Should().Be(0, "no Allow row is written from an ingest status");
        h.AssertNothingSent();
    }

    [Fact]
    public async Task An_uploaded_clip_quarantined_by_ingest_still_refuses_as_blocked()
    {
        Harness h = await UploadedClipAsync(FileModerationStatus.Quarantined);

        (await h.ReadAsync($"projects/{h.ProjectId}/userFiles/clip.mp4")).Code.Should().Be("CONTENT_BLOCKED");
        (await h.RowCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_uploaded_clip_with_a_verdict_row_at_its_version_is_answered_by_the_row()
    {
        Harness h = await UploadedClipAsync(FileModerationStatus.Unmoderated);
        string key = $"projects/{h.ProjectId}/userFiles/clip.mp4";
        await h.SeedVerdictAsync(key, nameof(ModerationAction.Allow), DateTime.UtcNow);

        (await h.ReadAsync(key)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_hand_off_style_read_of_several_pending_clips_offers_each_of_them()
    {
        Harness h = Harness.Create();
        foreach (string name in new[] { "a.mp4", "b.mp4", "c.mp4" })
            await h.ReadAsync($"projects/{h.ProjectId}/userFiles/{name}");

        h.Queue.TrackedCount.Should().Be(3);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------

    private static ConsumeContext<WorkflowStepCompleted> Message(Harness h, string key, string status)
    {
        Mock<ConsumeContext<WorkflowStepCompleted>> context = new();
        context.SetupGet(c => c.Message).Returns(new WorkflowStepCompleted
        {
            ExecutionId = Guid.NewGuid(),
            StepId = Guid.NewGuid(),
            StepResultId = Guid.NewGuid(),
            ProjectId = h.ProjectId,
            StepStatus = status,
            OutputStorageKey = key
        });
        return context.Object;
    }

    private static ContentModerationDecision AuditRow(
        Harness h, Guid stepResultId, string version, ModerationAction action,
        string inputTypes = "[\"image\",\"text\"]", DateTime? createdAt = null) => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = h.OrgId,
            ProjectId = h.ProjectId,
            StepResultId = stepResultId,
            SourceVersion = version,
            Surface = nameof(ModerationSurface.Publish),
            Action = action.ToString(),
            ContentKind = nameof(ModerationContentKind.Video),
            ContentSha256 = "hash",
            InputTypesJson = inputTypes,
            ContentLength = 2,
            ProviderKind = nameof(InferenceProviderKind.OpenAICompatible),
            Model = "omni-moderation-latest",
            ModelSnapshot = "omni-moderation-2024-09-26",
            ReviewState = nameof(ModerationReviewState.NotRequired),
            CreatedAt = createdAt ?? DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };

    // -------------------------------------------------------------------------------------------
    // Wordless audio our own Music or Sound step made (generated_wordless_audio)
    // -------------------------------------------------------------------------------------------

    private static readonly DateTime StepDone = DateTime.UtcNow.AddMinutes(-10);

    /// <summary>A 16 kHz mono 16-bit WAV of the given length, as the sampler hands it on.</summary>
    private static byte[] Wav(double seconds) => new byte[44 + (int)(seconds * 32000)];

    private sealed record GeneratedSeed(
        string StepKind = "AudioGenerate", string ManifestKind = "Music", string? PromptCheck = "Allow",
        bool Reused = false, bool Carried = false, bool CopiedRow = false, bool NoManifest = false,
        StepStatus Status = StepStatus.Completed, string TakeStatus = "succeeded", bool TakeListedUnderOtherKey = false, string Extension = "mp3");

    /// <summary>
    /// Seeds a Completed step result (and, unless asked not to, its manifest) the way the Music or Sound step leaves them,
    /// and makes the harness serve a wordless one-track clip of 10 seconds whose bytes were written 1 s before the step ended.
    /// </summary>
    private static async Task<string> SeedGeneratedAsync(Harness h, GeneratedSeed? seed = null)
    {
        seed ??= new GeneratedSeed();
        Guid executionId = Guid.NewGuid();
        string key = h.Key($"{executionId:D}/step-1-a1.{seed.Extension}");
        string manifestKey = $"projects/{h.ProjectId}/artifacts/audio-generate/{executionId:D}/step-1-takes.json";
        string take = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["take"] = 1,
            ["status"] = seed.TakeStatus,
            ["storageKey"] = seed.TakeListedUnderOtherKey ? h.Key($"{executionId:D}/step-1-a2.mp3") : key,
            ["reused"] = seed.Reused,
            ["carried"] = seed.Carried,
            ["promptCheck"] = seed.PromptCheck,
        });
        if (!seed.NoManifest)
            h.Objects[manifestKey] = System.Text.Encoding.UTF8.GetBytes($"{{\"version\":1,\"kind\":\"{seed.ManifestKind}\",\"takes\":[{take}]}}");

        await h.WithDbAsync(async db =>
        {
            Guid workflowId = Guid.NewGuid();
            Guid stepId = Guid.NewGuid();
            Guid sourceId = Guid.NewGuid();
            db.WorkflowSteps.Add(new WorkflowStep
            {
                Id = stepId,
                WorkflowDefinitionId = workflowId,
                StepOrder = 1,
                StepType = Enum.Parse<StepType>(seed.StepKind)
            });
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = workflowId,
                ProjectId = h.ProjectId,
                OrganizationId = h.OrgId,
                Status = ExecutionStatus.Passed,
                StartedAt = StepDone,
                CompletedAt = StepDone
            });
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = stepId,
                Status = seed.Status,
                ExecutedAt = StepDone,
                CompletedAt = StepDone,
                OutputStorageKey = key,
                ArtifactStorageKey = manifestKey,
                ReusedFromStepResultId = seed.CopiedRow ? sourceId : null
            });
            await db.SaveChangesAsync();
            return 0;
        });

        h.LastModified = StepDone.AddSeconds(-1);
        h.Sampler.Sample = new ModerationMediaSample([], Wav(10), ModerationContentKind.Audio, null);
        return key;
    }

    private static Harness GeneratedHarness(Action<Harness>? more = null) => Harness.Create(x =>
    {
        x.HasTranscriptionProvider = true;
        x.TranscriptText = "";
        more?.Invoke(x);
    });

    [Fact]
    public async Task Generated_wordless_audio_with_every_condition_holding_is_cleared_and_served_everywhere()
    {
        Harness h = GeneratedHarness();
        string key = await SeedGeneratedAsync(h);

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Allow");
        row.ReasonCode.Should().Be(OutputModerationReasonCodes.GeneratedWordlessAudio);
        row.SourceVersion.Should().Be(DefaultVersion, "bound to the bytes like any verdict");
        h.Gate.Calls.Should().Be(0, "no vendor call is added");

        (await h.ReadAsync(key)).Allowed.Should().BeTrue("download, link and hand-off read the stored verdict");
        (await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p => p.GateStoredFileAsync(h.ProjectId, key, CancellationToken.None))).Should().BeNull();
        (await h.WithAsync<PublishModeration, PublishModerationOutcome?>(p => p.GateStoredCopyAsync(h.ProjectId, key, CancellationToken.None))).Should().BeNull();
    }

    [Fact]
    public async Task A_wordless_take_with_an_audible_side_clears_only_when_both_transcripts_are_empty()
    {
        Harness both = GeneratedHarness();
        string bothKey = await SeedGeneratedAsync(both);
        both.Sampler.Sample = both.Sampler.Sample! with { SideAudio = Wav(10) };
        await both.ProcessAsync(bothKey);
        both.TranscribeCalls.Should().Be(2, "the side signal is transcribed too");
        (await both.RowAsync(bothKey))!.ReasonCode.Should().Be(OutputModerationReasonCodes.GeneratedWordlessAudio);

        Harness hidden = GeneratedHarness(x => x.SideTranscriptText = "words only in the side");
        string hiddenKey = await SeedGeneratedAsync(hidden);
        hidden.Sampler.Sample = hidden.Sampler.Sample! with { SideAudio = Wav(10) };
        await hidden.ProcessAsync(hiddenKey);
        (await hidden.RowAsync(hiddenKey))!.ReasonCode.Should().NotBe(OutputModerationReasonCodes.GeneratedWordlessAudio);
        hidden.Gate.Calls.Should().Be(1, "the words heard in the side are judged");
    }

    [Fact]
    public async Task A_side_transcription_that_fails_is_the_same_outage_as_the_main_one()
    {
        Harness h = GeneratedHarness(x => x.SideTranscriptionThrows = true);
        string key = await SeedGeneratedAsync(h);
        h.Sampler.Sample = h.Sampler.Sample! with { SideAudio = Wav(10) };

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Unavailable");
        row.ReasonCode.Should().NotBe(OutputModerationReasonCodes.GeneratedWordlessAudio);
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("xyz")]
    public async Task Generated_wordless_audio_under_a_name_that_tells_nothing_follows_the_probe_and_clears(string extension)
    {
        Harness h = GeneratedHarness();
        string key = await SeedGeneratedAsync(h, new GeneratedSeed(Extension: extension));

        await h.ProcessAsync(key);

        h.Sampler.LastMime.Should().Be(FfmpegModerationMediaSampler.FollowProbeMimeType, "the probe decides, not the extension");
        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Allow");
        row.ReasonCode.Should().Be(OutputModerationReasonCodes.GeneratedWordlessAudio);
    }

    [Fact]
    public async Task A_sound_effect_step_clears_the_same_way_and_a_review_prompt_counts()
    {
        Harness h = GeneratedHarness();
        string key = await SeedGeneratedAsync(h, new GeneratedSeed(ManifestKind: "SoundEffect", PromptCheck: "Review"));

        await h.ProcessAsync(key);

        (await h.RowAsync(key))!.ReasonCode.Should().Be(OutputModerationReasonCodes.GeneratedWordlessAudio);
    }

    [Theory]
    [InlineData("not_an_audio_generate_step")]
    [InlineData("kind_is_not_music_or_sound")]
    [InlineData("prompt_never_judged")]
    [InlineData("prompt_check_is_not_a_pass")]
    [InlineData("take_was_reused_from_another_run")]
    [InlineData("take_was_carried_from_another_run")]
    [InlineData("row_is_a_copy_of_another")]
    [InlineData("no_manifest")]
    [InlineData("step_not_completed")]
    [InlineData("take_not_succeeded")]
    [InlineData("manifest_lists_another_key")]
    [InlineData("object_time_unreadable")]
    [InlineData("object_written_after_the_step")]
    public async Task Each_condition_failing_alone_leaves_the_audio_not_cleared(string failing)
    {
        Harness h = GeneratedHarness();
        GeneratedSeed seed = failing switch
        {
            "not_an_audio_generate_step" => new GeneratedSeed(StepKind: "Agent"),
            "kind_is_not_music_or_sound" => new GeneratedSeed(ManifestKind: "Narration"),
            "prompt_never_judged" => new GeneratedSeed(PromptCheck: null),
            "prompt_check_is_not_a_pass" => new GeneratedSeed(PromptCheck: "Block"),
            "take_was_reused_from_another_run" => new GeneratedSeed(Reused: true),
            "take_was_carried_from_another_run" => new GeneratedSeed(Carried: true),
            "row_is_a_copy_of_another" => new GeneratedSeed(CopiedRow: true),
            "no_manifest" => new GeneratedSeed(NoManifest: true),
            "step_not_completed" => new GeneratedSeed(Status: StepStatus.Failed),
            "take_not_succeeded" => new GeneratedSeed(TakeStatus: "failed"),
            "manifest_lists_another_key" => new GeneratedSeed(TakeListedUnderOtherKey: true),
            _ => new GeneratedSeed()
        };
        string key = await SeedGeneratedAsync(h, seed);
        if (failing == "object_time_unreadable") h.LastModified = null;
        if (failing == "object_written_after_the_step") h.LastModified = StepDone.AddMinutes(1);

        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Unavailable");
        row.ReasonCode.Should().Be(OutputModerationReasonCodes.NothingToClassify);
        (await h.ReadAsync(key)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Audio_of_a_generated_file_that_is_exactly_the_skew_after_the_step_is_still_cleared_but_a_second_more_is_not()
    {
        Harness h = GeneratedHarness();
        string key = await SeedGeneratedAsync(h);
        h.LastModified = StepDone + GeneratedAudioClearance.Skew;
        await h.ProcessAsync(key);
        (await h.RowAsync(key))!.ReasonCode.Should().Be(OutputModerationReasonCodes.GeneratedWordlessAudio);

        Harness other = GeneratedHarness();
        string key2 = await SeedGeneratedAsync(other);
        other.LastModified = StepDone + GeneratedAudioClearance.Skew + TimeSpan.FromSeconds(1);
        await other.ProcessAsync(key2);
        (await other.RowAsync(key2))!.ReasonCode.Should().Be(OutputModerationReasonCodes.NothingToClassify);
    }

    [Fact]
    public async Task An_uploaded_wordless_file_is_not_cleared()
    {
        Harness h = GeneratedHarness(x => x.LastModified = StepDone);
        h.Sampler.Sample = new ModerationMediaSample([], Wav(10), ModerationContentKind.Audio, null);
        string key = h.Key("userFiles/uploaded.mp3");

        await h.ProcessAsync(key);

        (await h.RowAsync(key))!.ReasonCode.Should().Be(OutputModerationReasonCodes.NothingToClassify);
        (await h.ReadAsync(key)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task A_generated_file_rewritten_with_the_same_size_after_it_was_cleared_is_not_cleared()
    {
        Harness h = GeneratedHarness();
        string key = await SeedGeneratedAsync(h);
        await h.ProcessAsync(key);
        (await h.ReadAsync(key)).Allowed.Should().BeTrue();

        // Same size, new bytes: a new ETag and a later store time.
        h.ETag = "\"rewritten\"";
        h.LastModified = DateTime.UtcNow;

        (await h.ReadAsync(key)).Code.Should().Be("CONTENT_MODERATION_PENDING", "the cleared verdict does not follow other bytes");
        await h.ProcessAsync(key);

        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Unavailable");
        row.ReasonCode.Should().Be(OutputModerationReasonCodes.NothingToClassify);
        h.Size.Should().Be(DefaultSize);
        (await h.ReadAsync(key)).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Generated_audio_with_two_tracks_is_not_cleared()
    {
        Harness h = GeneratedHarness();
        string key = await SeedGeneratedAsync(h);
        h.Sampler.Sample = new ModerationMediaSample([], null, ModerationContentKind.Audio, "more than one audio track", Deterministic: true);

        await h.ProcessAsync(key);

        (await h.RowAsync(key))!.ReasonCode.Should().Be(ModerationReasonCodes.Unclassified);
        (await h.ReadAsync(key)).Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData(600)]
    [InlineData(599.5)]
    [InlineData(1500)]
    public async Task Generated_audio_not_heard_in_full_because_it_reaches_the_listening_limit_is_not_cleared(double seconds)
    {
        Harness h = GeneratedHarness();
        string key = await SeedGeneratedAsync(h);
        h.Sampler.Sample = new ModerationMediaSample([], Wav(Math.Min(seconds, 600)), ModerationContentKind.Audio, null);

        await h.ProcessAsync(key);

        (await h.RowAsync(key))!.ReasonCode.Should().Be(OutputModerationReasonCodes.NothingToClassify);
    }

    [Fact]
    public async Task Generated_audio_just_under_the_limit_is_cleared()
    {
        Harness h = GeneratedHarness();
        string key = await SeedGeneratedAsync(h);
        h.Sampler.Sample = new ModerationMediaSample([], Wav(595), ModerationContentKind.Audio, null);

        await h.ProcessAsync(key);

        (await h.RowAsync(key))!.ReasonCode.Should().Be(OutputModerationReasonCodes.GeneratedWordlessAudio);
    }

    [Fact]
    public async Task Generated_audio_with_speech_is_judged_as_usual()
    {
        Harness h = GeneratedHarness(x => x.TranscriptText = "a sentence");
        string key = await SeedGeneratedAsync(h);

        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(1, "the words went to the vendor");
        OutputModerationVerdict row = (await h.RowAsync(key))!;
        row.State.Should().Be("Allow");
        row.ReasonCode.Should().BeNull();
    }

    [Fact]
    public async Task Generated_audio_with_a_cover_still_judges_the_cover()
    {
        Harness h = GeneratedHarness();
        string key = await SeedGeneratedAsync(h);
        h.Sampler.Sample = new ModerationMediaSample([new byte[] { 9, 9 }], Wav(10), ModerationContentKind.Audio, null);

        await h.ProcessAsync(key);

        h.Gate.Calls.Should().Be(1, "the cover is judged");
        (await h.RowAsync(key))!.ReasonCode.Should().NotBe(OutputModerationReasonCodes.GeneratedWordlessAudio);
    }

    [Fact]
    public async Task A_final_refusal_carries_the_machine_field_and_an_outage_does_not()
    {
        ObjectResult final = PublishModerationOutcome.UnavailableFinal().ToRefusal(new TestController());
        ObjectResult outage = PublishModerationOutcome.Unavailable().ToRefusal(new TestController());
        ObjectResult blocked = PublishModerationOutcome.Blocked("no").ToRefusal(new TestController());

        FinalOf(final).Should().BeTrue();
        FinalOf(outage).Should().BeFalse();
        FinalOf(blocked).Should().BeFalse();
        System.Text.Json.JsonSerializer.Serialize(final.Value).Should().Contain("\"final\":true").And.Contain("CONTENT_MODERATION_UNAVAILABLE");
        await Task.CompletedTask;
    }

    private static bool FinalOf(ObjectResult result) =>
        (bool)result.Value!.GetType().GetProperty("final")!.GetValue(result.Value)!;

    private sealed class TestController : Microsoft.AspNetCore.Mvc.ControllerBase;
}
