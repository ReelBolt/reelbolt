#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Builds;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Tests.Billing;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Scaffolding for the avatar build tests (lane L12 of docs/design/avatar-objects.md): the avatar harness plus a fake
/// picture model that records what it was sent and meters what a test says, recording credits, a plan gate, a run-rate
/// guard and a prompt screen, and the build service, processor and controller wired on them.
/// </summary>
internal sealed class AvatarBuildRig
{
    /// <summary>A picture model that records every edit and answers what the test queued.</summary>
    internal sealed class FakeImageClient : IImageClient
    {
        public List<ImageEditRequest> Edits { get; } = [];

        /// <summary>Per call (1-based): the picture to answer; null answers no picture; throwing fails the call.</summary>
        public Func<int, ImageEditRequest, byte[]?> Answer { get; set; } = (n, _) => H.Png(1024, 1024, (byte)(200 + n));

        /// <summary>What each call meters into the open usage scope (millicredits).</summary>
        public long MeterMillis { get; set; } = 4_000;

        /// <summary>Runs just before a call answers (a test's "meanwhile").</summary>
        public Func<int, Task>? Meanwhile { get; set; }

        public async Task<GeneratedImageSet> EditAsync(ImageEditRequest request, CancellationToken ct)
        {
            Edits.Add(request);
            int n = Edits.Count;
            if (Meanwhile is { } meanwhile) await meanwhile(n);
            byte[]? bytes = Answer(n, request);
            if (bytes is null) return new GeneratedImageSet([]);
            if (MeterMillis > 0) UsageScope.Current?.AddCredits(MeterMillis);
            return new GeneratedImageSet([new GeneratedImage(bytes, "image/png") { Width = 1024, Height = 1024 }]) { ResolvedModel = request.Model };
        }

        public Task<GeneratedImageSet> GenerateAsync(ImageGenerationRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<GeneratedImageSet> TransformAsync(ImageTransformRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ImageProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new ImageProbeResult(true, null));
        public ImageModelCapabilities Describe(string model) => throw new NotSupportedException();
    }

    internal sealed class Factory(IImageClient client) : IImageClientFactory
    {
        public IImageClient Get(ResolvedImageProvider provider) => client;
    }

    /// <summary>A storage quota a test fills: every check is recorded, and refused from <see cref="RefuseFromCheck"/> on.</summary>
    internal sealed class QuotaGate : IPlanGateService
    {
        public List<long> UploadChecks { get; } = [];

        /// <summary>The (1-based) upload check from which every check is refused; null = never.</summary>
        public int? RefuseFromCheck { get; set; }

        public Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
        {
            UploadChecks.Add(incomingBytes);
            return Task.FromResult(RefuseFromCheck is { } n && UploadChecks.Count >= n
                ? new PlanGateViolation(PlanGate.StorageCode, "storage", "Your workspace's storage is full.", 413, 100, 100, incomingBytes)
                : null);
        }

        public Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<ReelBolt.Inference.Api.Controllers.Dto.CreateWorkflowStepRequest> steps, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) => Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);
    }

    /// <summary>The hourly run limit as a test double: allows (running the callback) or refuses, and counts the hits.</summary>
    internal sealed class RunRate : IAssistantExecutionGuard
    {
        public bool Refuse { get; set; }
        public List<(Guid UserId, Guid ProjectId, RunRateBucket Bucket, string Source)> Hits { get; } = [];

        public async Task<RunRateDecision> TryAcquireAsync(
            Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
        {
            Hits.Add((userId, projectId, bucket, source));
            if (Refuse) return new RunRateDecision(false, bucket, 10, 10, 3600, 780);
            await insertCallback();
            return new RunRateDecision(true, bucket, 1, 10, 3600, 0);
        }
    }

    /// <summary>The prompt screen as a test double: blocks what the test says, records every text it saw.</summary>
    internal sealed class Screen(Func<string, bool> blocks) : IContentModerationGate
    {
        public List<string> Texts { get; } = [];

        public Task<ModerationDecision> ModerateAsync(ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            Texts.Add(input.Text ?? string.Empty);
            bool block = blocks(input.Text ?? string.Empty);
            return Task.FromResult(new ModerationDecision(
                block ? ModerationAction.Block : ModerationAction.Allow, true, false,
                block ? "violence" : null, block ? "This request cannot be used." : string.Empty, "hash"));
        }

        public Task<bool> ScreenGenerationPromptAsync(string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct)
        {
            Texts.Add(text);
            return Task.FromResult(!blocks(text));
        }

        public Task<bool> ScreenGeneratedTextAsync(string text, string site, ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> ScreenPublishAsync(ModerationInput input, ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>The stills path as a test double: the sources it knows, and the stills it answers once they were asked for.</summary>
    internal sealed class IOutputDescriberDouble : ReelBolt.Inference.Api.Services.Outputs.IOutputDescriber
    {
        public Dictionary<Guid, ReelBolt.Inference.Api.Services.Outputs.OutputSource> Sources { get; } = [];

        /// <summary>The stills of the source, made by the first request; null until then.</summary>
        public IReadOnlyList<ReelBolt.Inference.Api.Services.Outputs.OutputStillObject>? Stills { get; set; }

        /// <summary>What the next request makes (null: the request makes nothing yet).</summary>
        public Func<IReadOnlyList<int>, IReadOnlyList<ReelBolt.Inference.Api.Services.Outputs.OutputStillObject>>? MakeOnRequest { get; set; }

        public List<IReadOnlyList<int>> Requests { get; } = [];

        public List<IReadOnlyList<int>> Lookups { get; } = [];

        public Task<ReelBolt.Inference.Api.Services.Outputs.OutputDescription?> DescribeAsync(Project project, Guid outputId, Guid userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ReelBolt.Inference.Api.Services.Outputs.OutputRender?> FindRenderAsync(Guid projectId, Guid stepResultId, CancellationToken ct) =>
            Task.FromResult<ReelBolt.Inference.Api.Services.Outputs.OutputRender?>(null);

        public Task<ReelBolt.Inference.Api.Services.Outputs.OutputSource?> FindSourceAsync(Guid projectId, Guid outputId, CancellationToken ct) =>
            Task.FromResult(Sources.GetValueOrDefault(outputId));

        public Task<IReadOnlyList<ReelBolt.Inference.Api.Services.Outputs.OutputStillObject>?> FindStillsAsync(
            Guid projectId, ReelBolt.Inference.Api.Services.Outputs.OutputSource source, IReadOnlyList<int> timesMs, CancellationToken ct)
        {
            Lookups.Add(timesMs);
            return Task.FromResult(Stills);
        }

        public Task RequestStillsAsync(Guid projectId, ReelBolt.Inference.Api.Services.Outputs.OutputSource source, IReadOnlyList<int> timesMs, CancellationToken ct)
        {
            Requests.Add(timesMs);
            if (MakeOnRequest is { } make) Stills = make(timesMs);
            return Task.CompletedTask;
        }
    }

    /// <summary>A video model that starts from a picture (or not), with the price list a test gives.</summary>
    internal sealed class FakeVideoClients : IVideoGenerationClientFactory, IVideoGenerationClient, IVideoImageInputSupport
    {
        public VideoImageInputSupport Support { get; set; } = VideoImageInputSupport.FirstFrame;

        public VideoGenerationCapabilities Capabilities { get; set; } = new(
            new Dictionary<string, decimal> { ["768P"] = 0.05m, ["1080P"] = 0.08m }, 6, 10, ["16:9", "9:16", "1:1"]);

        public IVideoGenerationClient Get(ResolvedVideoGenerationProvider provider) => this;

        public VideoImageInputSupport ImageInputSupport(string model) => Support;

        public VideoGenerationCapabilities Describe(string model) => Capabilities;

        public Task<ExternalJobHandle> SubmitAsync(VideoGenerationRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<ExternalJobStatus> PollAsync(ExternalJobHandle handle, CancellationToken ct) => throw new NotSupportedException();

        public Task<VideoGenerationProbeResult> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<Stream> DownloadAsync(string downloadUrl, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A segmentation model that answers what the test says for each call (1-based): a result, or a failure.</summary>
    internal sealed class FakeSegmenter : IImageAnalysisClientFactory, IImageAnalysisClient
    {
        public List<SegmentationRequest> Calls { get; } = [];

        /// <summary>What call n answers; throwing fails the call.</summary>
        public Func<int, SegmentationResult> Answer { get; set; } = _ => Box(0.4, 0.4, 0.2, 0.2);

        /// <summary>What each call meters into the open usage scope (millicredits).</summary>
        public long MeterMillis { get; set; } = 1_000;

        /// <summary>A result whose one mask is the box (normalized centre x, centre y, width, height).</summary>
        public static SegmentationResult Box(double cx, double cy, double w, double h) =>
            new(new GeneratedImage(H.Png(8, 8, 1), "image/png"), null)
            {
                Masks = [new SegmentedMask(new GeneratedImage(H.Png(8, 8, 1), "image/png"), null, 0.9, [cx, cy, w, h])]
            };

        /// <summary>A result with no mask at all: the object is not there.</summary>
        public static SegmentationResult Nothing() => new(new GeneratedImage(H.Png(8, 8, 1), "image/png"), null) { Masks = [] };

        public IImageAnalysisClient Get(ResolvedImageAnalysisProvider provider) => this;

        public Task<SegmentationResult> SegmentAsync(SegmentationRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            if (MeterMillis > 0) UsageScope.Current?.AddCredits(MeterMillis);
            return Task.FromResult(Answer(Calls.Count));
        }

        public Task<DepthResult> EstimateDepthAsync(DepthRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<ImageAnalysisProbeResult> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();

        public ImageAnalysisModelCapabilities Describe(string model) => throw new NotSupportedException();
    }

    internal sealed class Clock(DateTime start) : TimeProvider
    {
        public DateTime Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    public const string Model = "google/nano-banana-2.1/edit";

    public H H { get; }
    public FakeImageClient Images { get; } = new();
    public FakeNonWorkflowCharges? Charges { get; set; } = new() { AvailableMillis = 1_000_000, EstimateMillis = 4_000 };
    public QuotaGate Quota { get; } = new();
    public RunRate Rate { get; } = new();
    public Screen? Moderation { get; set; }
    public AvatarBuildQueue Queue { get; } = new();
    public AvatarBuildOptions BuildOptions { get; } = new();
    public Clock Time { get; } = new(new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc));
    public Mock<IInferenceProviderResolver> Providers { get; } = new();

    /// <summary>The default picture-edit model the resolver answers; null = none is set up.</summary>
    public ResolvedImageProvider? Provider { get; set; } = new(
        Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit,
        "https://queue.fal.run", Model, "key", 60);

    public AvatarBuildRig()
    {
        H = new H();
        H.Options.Enabled = true;
        H.Options.Objects.Enabled = true;
        H.Entitlements.Set = new ReelBolt.Shared.Tenancy.OrganizationEntitlementSet { AvatarsAllowed = true, AvatarObjectsAllowed = true };

        Providers.Setup(p => p.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Provider);

        // The store keeps what a build uploads under the key a direct upload of the same file would get.
        H.Storage.SetupGet(s => s.BucketName).Returns("test-bucket");
        H.Storage.Setup(s => s.UploadAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((Guid projectId, Guid fileId, Stream content, string fileName, string contentType,
                IReadOnlyDictionary<string, string>? _, CancellationToken _, string category, string? originalPath) =>
            {
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);
                string key = DirectUploadService.DeriveKey(projectId, fileId, fileName, originalPath)
                             ?? throw new InvalidOperationException("no key for " + originalPath);
                H.Objects[key] = buffer.ToArray();
                Uploads.Add(key);
                string path = originalPath ?? fileName;
                return Task.FromResult(new StoredFileObject(
                    key, "test-bucket", $"projects/{projectId}/{category}/", null, category, fileName, path,
                    ReelBolt.Shared.ProjectFilePath.GetDirectoryPath(path), ReelBolt.Shared.ProjectFilePath.BuildStorageFileName(fileId, fileName)));
            });
        H.Storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string key, CancellationToken _) =>
                H.Objects.TryGetValue(key, out byte[]? bytes) ? new StoredObjectMetadata(bytes.LongLength, "application/octet-stream") : null);
        WireRuns();
    }

    /// <summary>Every key a build uploaded, in order.</summary>
    public List<string> Uploads { get; } = [];

    public AvatarVariantPictures Pictures(InferenceApiDbContext db) =>
        new(db, Providers.Object, new Factory(Images), NullLogger<AvatarVariantPictures>.Instance, Charges);

    public AvatarBuildFiles Files(InferenceApiDbContext db) =>
        new(db, H.Storage.Object, new OrganizationStorageLock(), NullLogger<AvatarBuildFiles>.Instance, effects: null, planGate: Quota);

    public ObjectAvatarCreator Creator(InferenceApiDbContext db) =>
        new(db, H.Checks(db), Microsoft.Extensions.Options.Options.Create(H.Options));

    public AvatarBuildService Service(InferenceApiDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(H.Options), H.Checks(db), Pictures(db), Queue,
            NullLogger<AvatarBuildService>.Instance, H.Entitlements, Charges, Rate, Moderation, Time,
            Describer, Providers.Object, VideoClients, Prices, Presigned?.Object, SegmenterClients, StoredCopies);

    public AvatarBuildProcessor Processor(InferenceApiDbContext db) =>
        new(db, Microsoft.Extensions.Options.Options.Create(H.Options), Microsoft.Extensions.Options.Options.Create(BuildOptions),
            Pictures(db), Files(db), Creator(db), H.Storage.Object, NullLogger<AvatarBuildProcessor>.Instance, Charges, Moderation, Time,
            new AvatarBuildRuns(db, NullLogger<AvatarBuildRuns>.Instance, Executions.Object, Publish.Object), H.Checks(db),
            Describer, Providers.Object, Presigned?.Object, SegmenterClients, StoredCopies);

    // ------------------------------------------------------------------ the hidden runs and the media they read (part 2)

    /// <summary>The engine's queue as a test double: a queued run becomes a Queued execution row; <see cref="FinishRun"/> ends it.</summary>
    public Mock<IWorkflowExecutionService> Executions { get; } = new();

    /// <summary>Every run handed to the engine: its workflow, its execution and its kind.</summary>
    public List<(Guid WorkflowId, Guid ExecutionId, WorkflowDefinitionKind Kind)> Queued { get; } = [];

    /// <summary>How many of the next queue calls the plan's concurrency cap refuses.</summary>
    public int RefuseQueues { get; set; }

    /// <summary>The bus a stop request goes out on.</summary>
    public Mock<MassTransit.IPublishEndpoint> Publish { get; } = new();

    public IOutputDescriberDouble? Describer { get; set; }

    public FakeVideoClients? VideoClients { get; set; }

    public ReelBolt.Shared.Metering.IModelPriceService? Prices { get; set; }

    public Mock<ReelBolt.Shared.Storage.IPresignedUrlService>? Presigned { get; set; }

    public FakeSegmenter? SegmenterClients { get; set; }

    /// <summary>The publish gate over stored bytes (an output's verdict); null = none (no content moderation).</summary>
    public ReelBolt.Inference.Api.Services.Moderation.IStoredCopyGate? StoredCopies { get; set; }

    /// <summary>The default video model the resolver answers; null = none is set up.</summary>
    public ResolvedVideoGenerationProvider? VideoProvider { get; set; }

    /// <summary>The default segmentation model the resolver answers; null = none is set up.</summary>
    public ResolvedImageAnalysisProvider? SegmentationProvider { get; set; }

    /// <summary>Wires the doubles part 2 reads: the engine's queue, the placeholder agent, the store's copy.</summary>
    private void WireRuns()
    {
        using (InferenceApiDbContext db = H.NewDb())
        {
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
            db.SaveChanges();
        }

        Providers.Setup(p => p.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => VideoProvider);
        Providers.Setup(p => p.ResolveSegmentationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => SegmentationProvider);

        Executions.Setup(e => e.QueueAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<ReelBolt.Inference.Api.Services.Styles.RunChoices?>(), It.IsAny<WorkflowDefinitionKind>(),
                It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
            .Returns((Guid projectId, Guid workflowId, Guid userId, string? _, ExecutionInitiator _, CancellationToken _, bool _,
                ReelBolt.Inference.Api.Services.Styles.RunChoices? _, WorkflowDefinitionKind kind, Guid? executionId, Func<CancellationToken, Task>? _) =>
            {
                if (RefuseQueues > 0)
                {
                    RefuseQueues--;
                    throw new PlanLimitException(new PlanGateViolation("PLAN_CONCURRENCY_LIMIT", "concurrency", "Too many runs at once.", 429, 2, 2, 1));
                }

                using InferenceApiDbContext db = H.NewDb();
                db.WorkflowExecutions.Add(new WorkflowExecution
                {
                    Id = executionId!.Value,
                    WorkflowDefinitionId = workflowId,
                    ProjectId = projectId,
                    OrganizationId = H.OrganizationId,
                    Status = ExecutionStatus.Queued,
                    CorrelationId = "c",
                    InitiatedByUserId = userId
                });
                db.SaveChanges();
                Queued.Add((workflowId, executionId.Value, kind));
                return Task.FromResult<ReelBolt.Inference.Api.Controllers.Dto.WorkflowExecutionResponse>(null!);
            });

        H.Storage.Setup(s => s.CopyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string from, string to, CancellationToken _) =>
            {
                H.Objects[to] = H.Objects[from];
                Uploads.Add(to);
                return Task.CompletedTask;
            });
    }

    /// <summary>The steps of a queued run's workflow, in order.</summary>
    public List<WorkflowStep> StepsOf(Guid workflowId)
    {
        using InferenceApiDbContext db = H.NewDb();
        return [.. db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == workflowId).OrderBy(s => s.StepOrder)];
    }

    /// <summary>
    /// Ends a queued run the way the engine does: one result per step given (a completed step stores
    /// <paramref name="output"/>'s bytes under its output key), then the run's status.
    /// </summary>
    public void FinishRun(Guid executionId, ExecutionStatus status, Func<int, byte[]>? output, params StepStatus[] steps)
    {
        using InferenceApiDbContext db = H.NewDb();
        WorkflowExecution execution = db.WorkflowExecutions.Single(e => e.Id == executionId);
        List<WorkflowStep> workflowSteps = [.. db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == execution.WorkflowDefinitionId).OrderBy(s => s.StepOrder)];
        for (int i = 0; i < steps.Length; i++)
        {
            WorkflowStep step = workflowSteps[i];
            string extension = step.StepType == StepType.VideoGenerate ? "mp4" : "png";
            string? key = steps[i] == StepStatus.Completed
                ? step.StepType == StepType.VideoGenerate
                    ? $"projects/{H.Project.Id}/agentFiles/generated/{executionId}/step-{i + 1}-v1.{extension}"
                    : $"projects/{H.Project.Id}/outputFiles/{executionId}/step-{i + 1}-frame.{extension}"
                : null;
            if (key is not null) H.Objects[key] = output?.Invoke(i + 1) ?? H.Png(640, 360, (byte)(60 + i));
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = step.Id,
                Status = steps[i],
                OutputStorageKey = key,
                FailureCode = steps[i] == StepStatus.Failed ? "VENDOR_FAILED" : null,
                ExecutedAt = DateTime.UtcNow.AddSeconds(i)
            });
        }

        execution.Status = status;
        db.SaveChanges();
    }

    /// <summary>
    /// The avatar controller with the build service behind it, serving a POST to <paramref name="path"/> (a route of its own
    /// when the media rule is asked). <paramref name="principal"/> stands for the caller's token; left out, the signed-in
    /// person's own session.
    /// </summary>
    public ProjectAvatarsController Controller(
        ICurrentUser? user = null, ClaimsPrincipal? principal = null, IMediaEgressCallPermission? permission = null, string? path = null)
    {
        InferenceApiDbContext db = H.NewDb();
        user ??= H.Owner();
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = path ?? $"/api/v1/projects/{H.Project.Id}/avatars";
        http.User = principal ?? new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.UserId.ToString())], "Bearer"));
        return new ProjectAvatarsController(
            db, user, TestTenancy.Access(db, user), Microsoft.Extensions.Options.Options.Create(H.Options),
            H.Checks(db), H.Deletion(db), NullLogger<ProjectAvatarsController>.Instance, H.Entitlements, Service(db), permission)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    /// <summary>Runs the job in a fresh unit of work, the way the worker does.</summary>
    public async Task RunAsync(Guid jobId)
    {
        await using InferenceApiDbContext db = H.NewDb();
        await Processor(db).RunAsync(jobId, CancellationToken.None);
    }

    public AvatarBuildJob Job(Guid jobId)
    {
        using InferenceApiDbContext db = H.NewDb();
        return db.AvatarBuildJobs.Single(j => j.Id == jobId);
    }

    public Project Project()
    {
        using InferenceApiDbContext db = H.NewDb();
        return db.Projects.Single(p => p.Id == H.Project.Id);
    }

    /// <summary>Records the project's media consent, the way the Avatars tab does.</summary>
    public void GrantMediaConsent()
    {
        using InferenceApiDbContext db = H.NewDb();
        Project project = db.Projects.Single(p => p.Id == H.Project.Id);
        project.SubjectMediaConsentAt = DateTime.UtcNow;
        project.SubjectMediaConsentByUserId = H.OwnerId;
        db.SaveChanges();
    }

    /// <summary>An object avatar "Car" with <paramref name="pictures"/> pictures (Front, then Angle), made through the controller.</summary>
    public async Task<(Guid AvatarId, List<ProjectFile> Files)> CarAsync(int pictures = 2, string name = "Car")
    {
        List<ProjectFile> files = [];
        for (int i = 0; i < pictures; i++) files.Add(H.AddImage($"{name}-{i}.png", salt: (byte)(10 + i + name.Length)));
        ReelBolt.Inference.Api.Controllers.Dto.CreateProjectAvatarRequest request =
            H.CreateObject([.. files.Select((f, i) => (f.Id, i == 0 ? "Front" : "Angle"))]) with { Name = name, Description = "A small city car" };
        ActionResult<ReelBolt.Inference.Api.Controllers.Dto.ProjectAvatarResponse> result =
            await H.Controller().Create(H.Project.Id, request, CancellationToken.None);
        if (result.Result is not CreatedAtActionResult created)
            throw new InvalidOperationException("create failed: " + System.Text.Json.JsonSerializer.Serialize((result.Result as ObjectResult)?.Value));
        return (((ReelBolt.Inference.Api.Controllers.Dto.ProjectAvatarResponse)created.Value!).Id, files);
    }

    /// <summary>A complete variant request body (the object statement in English).</summary>
    public static ReelBolt.Inference.Api.Controllers.Dto.CreateAvatarVariantRequest Body(
        string instruction = "paint it matte red", string label = "Red") => new(
        Instruction: instruction,
        VariantLabel: label,
        ConsentStatement: ReelBolt.Shared.Avatars.AvatarObjectStatement.English,
        ConsentLocale: "en");

    /// <summary>Starts a variant build through the controller and answers its job id.</summary>
    public async Task<Guid> StartAsync(Guid avatarId, ReelBolt.Inference.Api.Controllers.Dto.CreateAvatarVariantRequest? body = null)
    {
        ActionResult<ReelBolt.Inference.Api.Controllers.Dto.AvatarBuildStartedResponse> result =
            await Controller().CreateVariant(H.Project.Id, avatarId, body ?? Body(), CancellationToken.None);
        if (result.Result is not AcceptedResult accepted)
            throw new InvalidOperationException("start failed: " + System.Text.Json.JsonSerializer.Serialize((result.Result as ObjectResult)?.Value));
        return ((ReelBolt.Inference.Api.Controllers.Dto.AvatarBuildStartedResponse)accepted.Value!).JobId;
    }
}
