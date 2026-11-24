using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Avatars;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// A <c>VideoGenerate</c> executor over fakes with FIXED ids, for the tests of several subjects per clip
/// (docs/design/avatar-objects.md "Subjects in a video generation step") and for the pins of the legacy single-avatar
/// step. No vendor is reachable: the client is a mock whose declarations each test chooses, and every take "fails at
/// the vendor" after its submit, so no download or ffmpeg is wired.
/// </summary>
internal sealed class SubjectsTestHarness
{
    public static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ExecutionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid StepResultId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid ProviderId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly DateTime Attested = new(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc);

    public static readonly byte[] Picture = Png(1024, 1024);
    public static readonly byte[] Speech = [1, 2, 3, 4, 5, 6, 7, 8];
    public static readonly byte[] Motion = [9, 8, 7, 6, 5, 4, 3, 2, 1];

    /// <summary>The fake resolver: every avatar the "project" holds, by id, changeable between takes.</summary>
    public sealed class FakeSubjects : IAvatarResolver
    {
        public Dictionary<Guid, ResolvedAvatar> Avatars { get; } = [];
        public List<Guid> Resolved { get; } = [];
        public int KindReads { get; private set; }

        public Task<ResolvedAvatar> ResolveAsync(Guid projectId, Guid avatarId, CancellationToken ct)
        {
            Resolved.Add(avatarId);
            return Task.FromResult(Avatars.TryGetValue(avatarId, out ResolvedAvatar? a) ? a : ResolvedAvatar.NotFound(avatarId));
        }

        public async Task<ResolvedAvatar> ResolveRequiredAsync(Guid projectId, Guid avatarId, CancellationToken ct)
        {
            ResolvedAvatar r = await ResolveAsync(projectId, avatarId, ct);
            return r.Usable ? r : throw new AvatarUnusableException(avatarId, r.Reason!);
        }

        public Task<IReadOnlyList<ResolvedAvatar>> ListSelectableAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ResolvedAvatar>>([.. Avatars.Values.Where(a => a.Usable)]);

        public Task<AvatarSubjectKind?> SubjectKindAsync(Guid projectId, Guid avatarId, CancellationToken ct)
        {
            KindReads++;
            return Task.FromResult(Avatars.TryGetValue(avatarId, out ResolvedAvatar? a) ? a.SubjectKind : (AvatarSubjectKind?)null);
        }

        public void Revoke(Guid id) =>
            Avatars[id] = Avatars[id] with { Usable = false, Reason = ProjectAvatar.ReasonConsentRevoked, References = [] };
    }

    public required VideoGenerateStepExecutor Executor { get; init; }
    public required StepExecutionContext Context { get; init; }
    public required Mock<IVideoGenerationClient> Client { get; init; }
    public required SharedInMemoryDbFactory Factory { get; init; }
    public required FakeSubjects Subjects { get; init; }
    public required Mock<IVideoGenerationBudgetGate> Gate { get; init; }
    public required List<VideoGenerationRequest> Sent { get; init; }
    public required List<VideoGenerationReservationRequest> Reservations { get; init; }
    public required Mock<IProjectFileWorkspace> Workspace { get; init; }

    /// <summary>A person with <paramref name="pictures"/> Face pictures, one speech sample and (optionally) one motion video.</summary>
    public static ResolvedAvatar Person(int n, int pictures = 1, bool speech = true, bool motion = false, string name = "Anna presenter")
    {
        var references = new List<ResolvedAvatarReference>();
        int ordinal = 0;
        for (int i = 0; i < pictures; i++)
        {
            references.Add(new ResolvedAvatarReference(Id(n, 0x10 + i), Id(n, 0x50 + i), AvatarReferenceKind.Image,
                i == 0 ? AvatarReferenceRole.Face : AvatarReferenceRole.HalfBody, ordinal++,
                Sha(Picture), $"projects/{ProjectId}/userFiles/s{n}-pic{i}.png", "image/png"));
        }

        if (speech)
        {
            references.Add(new ResolvedAvatarReference(Id(n, 0x20), Id(n, 0x60), AvatarReferenceKind.Audio, AvatarReferenceRole.Speech,
                ordinal++, Sha(Speech), $"projects/{ProjectId}/userFiles/s{n}.wav", "audio/wav", 4_000));
        }

        if (motion)
        {
            references.Add(new ResolvedAvatarReference(Id(n, 0x30), Id(n, 0x70), AvatarReferenceKind.Video, AvatarReferenceRole.Motion,
                ordinal, Sha(Motion), $"projects/{ProjectId}/userFiles/s{n}.mp4", "video/mp4", 5_000));
        }

        return new ResolvedAvatar(Id(n, 1), name, true, null, references, null, AvatarDisclosure.Visible, Attested);
    }

    /// <summary>An object with <paramref name="pictures"/> pictures (Front, then Angle) and (optionally) one motion video.</summary>
    public static ResolvedAvatar Thing(int n, int pictures = 1, bool motion = false, string name = "Espresso machine", string? description = null)
    {
        var references = new List<ResolvedAvatarReference>();
        int ordinal = 0;
        for (int i = 0; i < pictures; i++)
        {
            references.Add(new ResolvedAvatarReference(Id(n, 0x10 + i), Id(n, 0x50 + i), AvatarReferenceKind.Image,
                i == 0 ? AvatarReferenceRole.Front : AvatarReferenceRole.Angle, ordinal++,
                Sha(Picture), $"projects/{ProjectId}/userFiles/s{n}-pic{i}.png", "image/png"));
        }

        if (motion)
        {
            references.Add(new ResolvedAvatarReference(Id(n, 0x30), Id(n, 0x70), AvatarReferenceKind.Video, AvatarReferenceRole.Motion,
                ordinal, Sha(Motion), $"projects/{ProjectId}/userFiles/s{n}.mp4", "video/mp4", 5_000));
        }

        return new ResolvedAvatar(Id(n, 1), name, true, null, references, null, AvatarDisclosure.MetadataOnly, Attested,
            AvatarSubjectKind.Object, description, CoversPictures: true);
    }

    /// <summary>A stable id: subject <paramref name="n"/>, part <paramref name="part"/>.</summary>
    public static Guid Id(int n, int part) => Guid.Parse($"aaaaaaaa-{n:x4}-4000-8000-{part:x12}");

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static SubjectsTestHarness Build(
        string configJson,
        IReadOnlyList<ResolvedAvatar> avatars,
        VideoReferenceInputSupport? references = null,
        VideoImageInputSupport? images = null,
        InferenceProviderKind kind = InferenceProviderKind.MiniMax,
        AvatarGenerationOptions? options = null,
        Action<FakeSubjects, int>? afterSubmit = null,
        FakeSceneStills? stills = null,
        IReadOnlyList<StepOutputHistoryEntry>? history = null)
    {
        var factory = new SharedInMemoryDbFactory();
        var execution = new WorkflowExecution
        {
            Id = ExecutionId,
            ProjectId = ProjectId,
            Status = ExecutionStatus.Running,
            InitiatedByUserId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
        };

        var subjects = new FakeSubjects();
        foreach (ResolvedAvatar avatar in avatars) subjects.Avatars[avatar.Id] = avatar;

        var real = new MiniMaxVideoGenerationClient(new System.Net.Http.HttpClient(), "key");
        var client = new Mock<IVideoGenerationClient>();
        client.Setup(c => c.Describe(It.IsAny<string>())).Returns((string m) => real.Describe("MiniMax-H3"));
        VideoReferenceInputSupport declared = references ?? MiniMaxVideoGenerationClient.ReferenceSupport;
        client.As<IVideoReferenceInputSupport>().Setup(c => c.ReferenceInputSupport(It.IsAny<string>())).Returns(declared);
        client.As<IVideoReferenceInputSupport>()
            .Setup(c => c.InputMaterialCostUsd(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<VideoReferenceMaterial>>()))
            .Returns((string m, string r, IReadOnlyList<VideoReferenceMaterial> x) => real.InputMaterialCostUsd("MiniMax-H3", r, x));
        client.As<IVideoImageInputSupport>().Setup(c => c.ImageInputSupport(It.IsAny<string>()))
            .Returns(images ?? VideoImageInputSupport.FirstAndLastFrame);

        var sent = new List<VideoGenerationRequest>();
        client.Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Callback((VideoGenerationRequest r, CancellationToken _) =>
            {
                sent.Add(r);
                afterSubmit?.Invoke(subjects, sent.Count);
            })
            .ReturnsAsync(new ExternalJobHandle("remote-1"));
        client.Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobStatus(ExternalJobState.Failed, null, "fake vendor"));

        var reservations = new List<VideoGenerationReservationRequest>();
        var gate = new Mock<IVideoGenerationBudgetGate>();
        VideoGenerationBudgetGate realGate = VideoGenerateStepExecutorTests.RealGate(factory);
        gate.Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .Returns((VideoGenerationReservationRequest r, CancellationToken ct) =>
            {
                reservations.Add(r);
                return realGate.ReserveAsync(r, ct);
            });

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => subjects.Avatars.Values.SelectMany(a => a.References).Select(r => new ProjectWorkspaceFile(
                r.ProjectFileId, ProjectId, "f", null, "userFiles", r.StorageKey, r.MimeType, BytesOf(r.StorageKey).Length, DateTime.UtcNow, null)).ToList());
        workspace.Setup(w => w.DownloadStorageKeyToFileAsync(
                ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string key, string path, long _, CancellationToken _) => File.WriteAllBytesAsync(path, BytesOf(key)));
        workspace.Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), "agentFiles"))
            .ReturnsAsync("stored/manifest.json");

        var provider = new ResolvedVideoGenerationProvider(
            ProviderId, kind.ToString(), kind, "https://vendor.example.invalid", "MiniMax-H3", "key123", 1800);
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveVideoGenerationAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(provider);
        var clientFactory = new Mock<IVideoGenerationClientFactory>();
        clientFactory.Setup(f => f.Get(provider)).Returns(client.Object);

        var parts = new ExecutorParts(
            gate.Object, resolver.Object, clientFactory.Object, workspace.Object, factory, subjects,
            options ?? new AvatarGenerationOptions { Enabled = true, Generation = AvatarGenerationMode.On, ObjectsEnabled = true });
        VideoGenerateStepExecutor executor = MakeExecutor(parts, stills);

        var step = new WorkflowStep
        {
            Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
            WorkflowDefinitionId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = configJson,
        };
        var context = new StepExecutionContext
        {
            StepResultId = StepResultId,
            Execution = execution,
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "t",
            StepOutputHistory = history ?? [],
            CancellationToken = CancellationToken.None,
        };

        return new SubjectsTestHarness
        {
            Executor = executor,
            Context = context,
            Client = client,
            Factory = factory,
            Subjects = subjects,
            Gate = gate,
            Sent = sent,
            Reservations = reservations,
            Workspace = workspace,
        };
    }

    /// <summary>The executor's collaborators.</summary>
    private sealed record ExecutorParts(
        IVideoGenerationBudgetGate Gate, IInferenceProviderResolver Resolver, IVideoGenerationClientFactory Clients,
        IProjectFileWorkspace Workspace, SharedInMemoryDbFactory Factory, IAvatarResolver Avatars, AvatarGenerationOptions Options);

    private static VideoGenerateStepExecutor MakeExecutor(ExecutorParts p, FakeSceneStills? stills) =>
        new(
            p.Gate, p.Resolver, p.Clients, p.Workspace, p.Factory,
            new GeneratedClipNormalizer(new Mock<IVideoToolRunner>().Object,
                Microsoft.Extensions.Options.Options.Create(new VideoEditingOptions()), NullLogger<GeneratedClipNormalizer>.Instance),
            Microsoft.Extensions.Options.Options.Create(new VideoGenerationOptions()), NullLogger<VideoGenerateStepExecutor>.Instance,
            new Mock<IMediaWorkspaceFactory>(MockBehavior.Strict).Object, new Mock<IKeyframeExtractor>(MockBehavior.Strict).Object,
            avatars: p.Avatars,
            avatarOptions: Microsoft.Extensions.Options.Options.Create(p.Options),
            avatarStills: stills);

    /// <summary>
    /// A still maker that makes nothing: it records what it was asked for and answers a fixed picture (or a refusal).
    /// The talking head's recipe is not this harness's subject, so <see cref="MakeAsync"/> refuses.
    /// </summary>
    public sealed class FakeSceneStills : ISubjectStillMaker
    {
        public static readonly byte[] StillBytes = Png(768, 1344);

        public List<SubjectSceneStillRequest> Requests { get; } = [];
        public AvatarStillOutcome? Answer { get; set; }
        public bool Reused { get; set; }
        public Action? AfterMake { get; set; }

        public Task<AvatarStillOutcome> MakeAsync(AvatarStillRequest request, CancellationToken ct) =>
            Task.FromResult(AvatarStillOutcome.Refused(AvatarStillCodes.StillFailed, "not a talking head test"));

        public Task<AvatarStillOutcome> MakeSceneAsync(SubjectSceneStillRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            AfterMake?.Invoke();
            return Task.FromResult(Answer ?? new AvatarStillOutcome(new AvatarStill(
                StillBytes, "image/png", Sha(StillBytes), "google/nano-banana-2.1/edit", Reused,
                AvatarStillStorage.ObjectPrefix(request.ProjectId, request.OwnerAvatarId) + "scene.img")));
        }
    }

    private static byte[] BytesOf(string storageKey) =>
        storageKey.EndsWith(".png", StringComparison.Ordinal) ? Picture
        : storageKey.EndsWith(".wav", StringComparison.Ordinal) ? Speech
        : Motion;

    public static byte[] Png(int width, int height)
    {
        byte[] png = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(png, 0);
        png[11] = 13;
        png[12] = (byte)'I'; png[13] = (byte)'H'; png[14] = (byte)'D'; png[15] = (byte)'R';
        png[16] = (byte)(width >> 24); png[17] = (byte)(width >> 16); png[18] = (byte)(width >> 8); png[19] = (byte)width;
        png[20] = (byte)(height >> 24); png[21] = (byte)(height >> 16); png[22] = (byte)(height >> 8); png[23] = (byte)height;
        png[24] = 8; png[25] = 6;
        return png;
    }
}
