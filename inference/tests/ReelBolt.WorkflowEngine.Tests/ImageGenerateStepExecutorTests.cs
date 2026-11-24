#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="ImageGenerateStepExecutor"/> over a FAKE image client (no live fal call, no key), the real budget gate on an
/// in-memory ledger, and a mocked workspace, price book and presigner.
/// </summary>
public sealed class ImageGenerateStepExecutorTests
{
    private const string GenerateJson = """{"mode":"Generate","prompt":"A red fox in snow","variations":2}""";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private sealed class Rig
    {
        public Guid ProjectId { get; } = Guid.NewGuid();
        public WorkflowExecution Execution { get; }
        public SharedInMemoryDbFactory Db { get; } = new();
        public ResolvedImageProvider? Provider { get; set; }
        public Mock<IProjectFileWorkspace> Workspace { get; } = new();
        public Mock<IImageClient> Client { get; } = new();
        public Mock<IPresignedUrlService> Presigner { get; } = new();
        public List<ImageGenerationRequest> Generated { get; } = [];
        public List<ImageEditRequest> Edited { get; } = [];
        public List<(string Key, string Name, string Mime, long Size)> Registered { get; } = [];
        public List<(string FileName, string Category, string Mime)> Uploaded { get; } = [];
        public List<ProjectWorkspaceFile> Files { get; } = [];
        public Dictionary<string, string> Stored { get; } = [];
        public List<PriceRequest> Priced { get; } = [];
        public List<UsageRecordRequest> Usage { get; } = [];
        public Func<string, bool> FailUpload { get; set; } = _ => false;
        public Exception? PriceFailure { get; set; }
        public Exception? ClientFailure { get; set; }
        public bool PresignerAvailable { get; set; } = true;
        public IContentModerationGate? Moderation { get; set; }
        public List<StepOutputHistoryEntry> History { get; } = [];
        public Mock<ReelBolt.WorkflowEngine.Services.Video.IVideoToolRunner>? Runner { get; set; }
        public (int W, int H) DeliveredSize { get; set; } = (1024, 1024);

        public Rig(Guid? providerOrganizationId = null)
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId, Status = ExecutionStatus.Running };
            Provider = new ResolvedImageProvider(
                Guid.NewGuid(), "fal", InferenceProviderKind.FalAi, InferenceProviderCapability.ImageGeneration,
                "", "", "test", 60, OrganizationId: providerOrganizationId, IsPlatformManaged: providerOrganizationId is null,
                AccountId: Guid.NewGuid());

            Client.Setup(c => c.Describe(It.IsAny<string>()))
                .Returns((string m) => new ImageModelCapabilities(m, m.EndsWith("/edit", StringComparison.Ordinal), false, 4, 4000, "test"));
            Client.Setup(c => c.GenerateAsync(It.IsAny<ImageGenerationRequest>(), It.IsAny<CancellationToken>()))
                .Returns((ImageGenerationRequest r, CancellationToken _) =>
                {
                    Generated.Add(r);
                    return ClientFailure is null ? Task.FromResult(Set()) : Task.FromException<GeneratedImageSet>(ClientFailure);
                });
            Client.Setup(c => c.EditAsync(It.IsAny<ImageEditRequest>(), It.IsAny<CancellationToken>()))
                .Returns((ImageEditRequest r, CancellationToken _) =>
                {
                    Edited.Add(r);
                    return ClientFailure is null ? Task.FromResult(Set()) : Task.FromException<GeneratedImageSet>(ClientFailure);
                });

            Presigner.Setup(p => p.CreateGetAsync(ProjectId, It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync("https://signed.example/source.png");

            Workspace.Setup(w => w.UploadArtifactAsync(ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string path, string name, string mime, CancellationToken _, string category) =>
                {
                    File.Exists(path).Should().BeTrue();
                    if (FailUpload(name)) throw new IOException("the object store refused the write");
                    Uploaded.Add((name, category, mime));
                    string stored = $"projects/{p}/{category}/{name}";
                    if (name.EndsWith(".json", StringComparison.Ordinal))
                        Stored[stored] = File.ReadAllText(path);
                    return stored;
                });
            Workspace.Setup(w => w.RegisterStoredObjectAsync(ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<long>(), It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string key, string name, string mime, long size, SummaryStatus _, FileIndexingStatus _, CancellationToken _, string category) =>
                {
                    Registered.Add((key, name, mime, size));
                    var file = new ProjectWorkspaceFile(Guid.NewGuid(), p, name, null, category, key, mime, size, DateTime.UtcNow, null);
                    Files.Add(file);
                    return file;
                });
            Workspace.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(() => Files.ToList());
            Workspace.Setup(w => w.ArtifactExistsAsync(ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        }

        private GeneratedImageSet Set() =>
            new([new GeneratedImage(Png, "image/png") { Width = DeliveredSize.W, Height = DeliveredSize.H }], "fal-req-1");

        /// <summary>A runner whose "ffmpeg" writes a PNG of the size the scale filter asked for (or fails).</summary>
        public void UseRunner(bool succeed = true)
        {
            Runner = new Mock<ReelBolt.WorkflowEngine.Services.Video.IVideoToolRunner>();
            Runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyList<string> args, TimeSpan _, CancellationToken _) =>
                {
                    string filter = args[args.ToList().IndexOf("-vf") + 1];
                    var m = System.Text.RegularExpressions.Regex.Match(filter, @"crop=(\d+):(\d+)");
                    if (succeed)
                        File.WriteAllBytes(args[^1], PngOf(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
                    return Task.FromResult(new ReelBolt.WorkflowEngine.Services.Video.VideoToolResult(succeed ? 0 : 1, "", succeed ? "" : "boom", false));
                });
        }

        public static byte[] PngOf(int w, int h)
        {
            byte[] b = new byte[33];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
            return b;
        }

        public ImageGenerateStepExecutor Executor()
        {
            var resolver = new Mock<IInferenceProviderResolver>();
            resolver.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Provider);
            resolver.Setup(r => r.ResolveImageEditAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Provider is null ? null : Provider with { Capability = InferenceProviderCapability.ImageEdit });

            var factory = new Mock<IImageClientFactory>();
            factory.Setup(f => f.Get(It.IsAny<ResolvedImageProvider>())).Returns(Client.Object);
            factory.Setup(f => f.GetUnmetered(It.IsAny<ResolvedImageProvider>())).Returns(Client.Object);

            var usage = new Mock<IUsageRecorder>();
            usage.Setup(u => u.RecordAsync(It.IsAny<UsageRecordRequest>(), It.IsAny<CancellationToken>()))
                .Callback<UsageRecordRequest, CancellationToken>((r, _) => Usage.Add(r))
                .Returns(new ValueTask<long>(1L));

            // 0.06 USD of vendor cost = 10 credits a picture; 12.5 credits (12_500 millicredits) charged to the tenant.
            var prices = new Mock<IModelPriceService>();
            prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .Returns((PriceRequest r, CancellationToken _) =>
                {
                    Priced.Add(r);
                    if (PriceFailure is not null) throw PriceFailure;
                    return new ValueTask<PriceQuote>(new PriceQuote(12_500, 0.06m, Guid.NewGuid()));
                });

            return new ImageGenerateStepExecutor(
                new VideoGenerationBudgetGate(Db, Microsoft.Extensions.Options.Options.Create(new VideoGenerationOptions())),
                resolver.Object, factory.Object, Workspace.Object, Db, NullLogger<ImageGenerateStepExecutor>.Instance,
                prices: prices.Object, presignedUrls: PresignerAvailable ? Presigner.Object : null, moderation: Moderation, usageRecorder: usage.Object, videoToolRunner: Runner?.Object);
        }

        public Task<StepExecutionResult> RunAsync(string configJson, int stepOrder = 2, CancellationToken ct = default)
        {
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepOrder = stepOrder,
                StepType = StepType.ImageGenerate,
                ImageGenerateConfigJson = configJson
            };
            return Executor().ExecuteAsync(new StepExecutionContext
            {
                StepResultId = Guid.NewGuid(),
                Execution = Execution,
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                CurrentStepIndex = 1,
                IterationCount = 0,
                CorrelationId = "t",
                StepOutputHistory = History,
                CancellationToken = ct
            });
        }

        public List<ExternalGenerationJob> Rows()
        {
            using var db = Db.CreateDbContext();
            return [.. db.ExternalGenerationJobs.AsNoTracking().OrderBy(j => j.ClipId)];
        }

        public ProjectWorkspaceFile AddFile(string name, string mime)
        {
            var file = new ProjectWorkspaceFile(Guid.NewGuid(), ProjectId, name, null, "inputFiles", $"projects/{ProjectId}/inputFiles/{name}", mime, 10, DateTime.UtcNow, null);
            Files.Add(file);
            return file;
        }
    }

    private static JsonObject Output(StepExecutionResult result) => JsonNode.Parse(result.Output)!.AsObject();

    private static string EditJson(Guid fileId, bool consent = true) =>
        $$$"""{"mode":"Edit","instruction":"Make it night","allowSourceMediaEgress":{{{(consent ? "true" : "false")}}},"source":{"kind":"ProjectFile","projectFileId":"{{{fileId}}}"}}""";

    // ================================================================ generate

    [Fact]
    public async Task Generate_makes_one_priced_ledger_row_and_one_file_per_variation()
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(GenerateJson);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Generated.Should().HaveCount(2);
        rig.Generated.Should().OnlyContain(r => r.Prompt == "A red fox in snow" && r.Count == 1 && r.Model == "ideogram/v4.5");
        rig.Edited.Should().BeEmpty();

        List<ExternalGenerationJob> rows = rig.Rows();
        rows.Select(r => r.ClipId).Should().Equal("img-1", "img-2");
        rows.Should().OnlyContain(r => r.Status == ExternalGenerationJobStatus.Succeeded && r.CostEstimate == 10m && r.ActualCost == 10m
            && r.StorageKey != null && r.RemoteJobId == "fal-req-1");

        rig.Uploaded.Where(u => u.Category == "outputFiles").Select(u => u.FileName)
            .Should().Equal($"{rig.Execution.Id:D}/step-2-i1.png", $"{rig.Execution.Id:D}/step-2-i2.png");
        rig.Uploaded.Should().Contain(u => u.FileName.EndsWith("step-2-images.json", StringComparison.Ordinal));
        rig.Registered.Should().HaveCount(2).And.OnlyContain(f => f.Mime == "image/png");

        JsonObject output = Output(result);
        output["status"]!.GetValue<string>().Should().Be("completed");
        output["images"]!.AsArray().Should().HaveCount(2);
        output["images"]![0]!["projectFileId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        output["meta"]!["totalCost"]!.GetValue<decimal>().Should().Be(20m);
        result.OutputStorageKey.Should().EndWith("step-2-i1.png");
        result.ArtifactStorageKey.Should().NotBeNull();

        // The price came from the price book as ImageGenImages for one picture, at the platform rate.
        rig.Priced.Should().OnlyContain(p => p.Unit == UsageKind.ImageGenImages && p.Capability == InferenceProviderCapability.ImageGeneration
            && p.BillingMode == UsageBillingMode.Platform && p.Quantity == 1m);
    }

    [Fact]
    public async Task RegisterProjectFile_false_stores_the_picture_without_a_project_file()
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"x","registerProjectFile":false}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Registered.Should().BeEmpty();
        Output(result)["images"]![0]!["projectFileId"].Should().BeNull();
    }

    [Fact]
    public async Task An_identical_picture_is_reused_from_the_ledger_and_buys_nothing()
    {
        var rig = new Rig();
        (await rig.RunAsync(GenerateJson)).Status.Should().Be(StepStatus.Completed);

        StepExecutionResult second = await rig.RunAsync(GenerateJson);

        second.Status.Should().Be(StepStatus.Completed, second.ErrorDetails);
        rig.Generated.Should().HaveCount(2, "the second run reused both pictures");
        rig.Rows().Should().HaveCount(2);
        Output(second)["images"]![0]!["reused"]!.GetValue<bool>().Should().BeTrue();
        Output(second)["meta"]!["totalCost"]!.GetValue<decimal>().Should().Be(0m);
    }

    // ================================================================ output size

    private const string Nano = "google/nano-banana-2.1";

    [Fact]
    public async Task OutputSize_sends_the_planned_tier_and_aspect_then_resizes_to_the_exact_size()
    {
        var rig = new Rig();
        rig.Provider = rig.Provider! with { ModelName = Nano };
        rig.UseRunner();
        rig.DeliveredSize = (2048, 2048);

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"x","outputWidth":2000,"outputHeight":2000}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        ImageGenerationRequest sent = rig.Generated.Should().ContainSingle().Which;
        sent.AspectRatio.Should().Be("1:1");
        sent.Params!["resolution"].GetString().Should().Be("2K");
        rig.Priced.Should().OnlyContain(p => p.Variant == "2K");
        rig.Usage.Should().OnlyContain(u => u.Variant == "2K");
        JsonNode image = Output(result)["images"]![0]!;
        image["width"]!.GetValue<int>().Should().Be(2000);
        image["height"]!.GetValue<int>().Should().Be(2000);
        image["requested"]!["width"]!.GetValue<int>().Should().Be(2000);
        image["tier"]!.GetValue<string>().Should().Be("2K");
        image["resized"]!.GetValue<bool>().Should().BeTrue();
        Output(result)["warnings"].Should().BeNull();
    }

    [Fact]
    public async Task OutputSize_already_exact_png_is_delivered_without_ffmpeg()
    {
        var rig = new Rig();
        rig.Provider = rig.Provider! with { ModelName = Nano };
        rig.UseRunner();
        rig.DeliveredSize = (2000, 2000);

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"x","outputWidth":2000,"outputHeight":2000}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Runner!.Invocations.Should().BeEmpty();
        Output(result)["images"]![0]!["resized"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task OutputSize_smaller_picture_is_still_scaled_with_an_image_upscaled_warning()
    {
        var rig = new Rig();
        rig.Provider = rig.Provider! with { ModelName = Nano };
        rig.UseRunner();
        rig.DeliveredSize = (1024, 1024);

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"x","outputWidth":2000,"outputHeight":2000}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        JsonNode warning = Output(result)["warnings"]!.AsArray().Should().ContainSingle().Which!;
        warning["code"]!.GetValue<string>().Should().Be("image_upscaled");
        warning["message"]!.GetValue<string>().Should().Contain("upscaled from 1024x1024 to 2000x2000");
        Output(result)["images"]![0]!["resized"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task OutputSize_resize_failure_delivers_the_original_with_a_resize_failed_warning()
    {
        var rig = new Rig();
        rig.Provider = rig.Provider! with { ModelName = Nano };
        rig.UseRunner(succeed: false);
        rig.DeliveredSize = (2048, 2048);

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"x","outputWidth":2000,"outputHeight":2000}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        Output(result)["warnings"]![0]!["code"]!.GetValue<string>().Should().Be("resize_failed");
        Output(result)["images"]![0]!["resized"]!.GetValue<bool>().Should().BeFalse();
        Output(result)["images"]![0]!["projectFileId"]!.GetValue<string>().Should().NotBeNullOrEmpty("a paid picture is never lost");
        rig.Rows().Should().OnlyContain(r => r.Status == ExternalGenerationJobStatus.Succeeded);
    }

    [Fact]
    public async Task OutputSize_without_a_runner_still_delivers_the_original()
    {
        var rig = new Rig();
        rig.Provider = rig.Provider! with { ModelName = Nano };
        rig.DeliveredSize = (2048, 2048);

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"x","outputWidth":2000,"outputHeight":2000}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        Output(result)["warnings"]![0]!["code"]!.GetValue<string>().Should().Be("resize_failed");
    }

    [Fact]
    public async Task OutputSize_on_ideogram_sends_the_nearest_supported_size()
    {
        var rig = new Rig();
        rig.UseRunner();
        rig.DeliveredSize = (2048, 2048);

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"x","outputWidth":2000,"outputHeight":2000}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        ImageGenerationRequest sent = rig.Generated.Should().ContainSingle().Which;
        (sent.Width, sent.Height, sent.AspectRatio).Should().Be((2048, 2048, null));
        sent.Params.Should().BeNull();
    }

    [Fact]
    public void RequestHash_is_unchanged_without_the_output_fields_and_changes_with_them()
    {
        var plain = new ImageGenerateStepConfig(ImageGenerateMode.Generate, "x");
        var sized = plain with { OutputWidth = 2000, OutputHeight = 2000 };
        var tiered = plain with { Resolution = "2K" };

        string a = ImageGenerateStepExecutor.RequestHash(plain, InferenceProviderKind.FalAi, Nano, 1, null);
        ImageGenerateStepExecutor.RequestHash(plain with { Resolution = null }, InferenceProviderKind.FalAi, Nano, 1, null).Should().Be(a);
        ImageGenerateStepExecutor.RequestHash(sized, InferenceProviderKind.FalAi, Nano, 1, null).Should().NotBe(a);
        ImageGenerateStepExecutor.RequestHash(tiered, InferenceProviderKind.FalAi, Nano, 1, null).Should().NotBe(a);
    }

    // ================================================================ edit

    [Fact]
    public async Task Edit_without_consent_is_refused_before_anything_is_read_priced_reserved_or_uploaded()
    {
        var rig = new Rig();
        ProjectWorkspaceFile file = rig.AddFile("photo.png", "image/png");

        StepExecutionResult result = await rig.RunAsync(EditJson(file.Id, consent: false));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("EGRESS_REFUSED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Edited.Should().BeEmpty();
        rig.Rows().Should().BeEmpty();
        rig.Priced.Should().BeEmpty();
        rig.Presigner.Verify(p => p.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact]
    public async Task Edit_sends_the_presigned_source_and_the_instruction_to_the_edit_provider()
    {
        var rig = new Rig();
        ProjectWorkspaceFile file = rig.AddFile("photo.png", "image/png");

        StepExecutionResult result = await rig.RunAsync(EditJson(file.Id));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        ImageEditRequest request = rig.Edited.Should().ContainSingle().Subject;
        request.Model.Should().Be("google/nano-banana-2.1/edit");
        request.Prompt.Should().Be("Make it night");
        request.SourceImageUrl.Should().Be("https://signed.example/source.png");
        rig.Presigner.Verify(p => p.CreateGetAsync(rig.ProjectId, file.StorageKey, ImageGenerateStepExecutor.SourceUrlTtl), Times.Once);
        rig.Priced.Should().OnlyContain(p => p.Unit == UsageKind.ImageEditImages && p.Capability == InferenceProviderCapability.ImageEdit);
        rig.Rows().Should().ContainSingle().Which.ClipId.Should().Be("img-1");
    }

    [Fact]
    public async Task Edit_variations_are_priced_and_held_per_picture_and_each_has_its_own_row()
    {
        var rig = new Rig();
        ProjectWorkspaceFile file = rig.AddFile("photo.png", "image/png");
        string json = EditJson(file.Id).Replace("\"mode\":\"Edit\"", "\"mode\":\"Edit\",\"variations\":2");

        StepExecutionResult result = await rig.RunAsync(json);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Edited.Should().HaveCount(2);
        rig.Rows().Select(r => r.ClipId).Should().Equal("img-1", "img-2");
        Output(result)["images"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task Without_a_cap_the_default_leaves_room_for_one_abandon_per_picture_and_the_budget_message_is_about_pictures()
    {
        var rig = new Rig();

        StepExecutionResult over = await rig.RunAsync("""{"mode":"Generate","prompt":"x","maxSpendCredits":9}""");

        over.FailureCode.Should().Be("BUDGET_EXCEEDED");
        Output(over)["error"]!["message"]!.GetValue<string>().Should().Contain("pictures").And.NotContain("video");
    }

    [Fact]
    public async Task Edit_refuses_a_source_that_is_not_a_picture_or_not_in_this_project()
    {
        var rig = new Rig();
        ProjectWorkspaceFile clip = rig.AddFile("clip.mp4", "video/mp4");

        StepExecutionResult notImage = await rig.RunAsync(EditJson(clip.Id));
        notImage.FailureCode.Should().Be("SOURCE_NOT_IMAGE");

        StepExecutionResult missing = await rig.RunAsync(EditJson(Guid.NewGuid()));
        missing.FailureCode.Should().Be("SOURCE_NOT_FOUND");

        rig.History.Add(new StepOutputHistoryEntry(1, "s", "{}", $"projects/{Guid.NewGuid()}/outputFiles/other.png"));
        StepExecutionResult foreign = await rig.RunAsync(
            """{"mode":"Edit","instruction":"x","allowSourceMediaEgress":true,"source":{"kind":"PreviousStepOutput"}}""");
        foreign.FailureCode.Should().Be("SOURCE_NOT_FOUND");

        rig.Edited.Should().BeEmpty();
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_reads_the_previous_step_output_when_it_is_a_picture_of_this_project()
    {
        var rig = new Rig();
        string key = $"projects/{rig.ProjectId}/outputFiles/{rig.Execution.Id:D}/step-1-i1.png";
        rig.History.Add(new StepOutputHistoryEntry(1, "s", "{}", key));

        StepExecutionResult result = await rig.RunAsync(
            """{"mode":"Edit","instruction":"x","allowSourceMediaEgress":true,"source":{"kind":"PreviousStepOutput"}}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Presigner.Verify(p => p.CreateGetAsync(rig.ProjectId, key, It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task Edit_without_a_presigner_fails_before_reserving()
    {
        var rig = new Rig { PresignerAvailable = false };
        ProjectWorkspaceFile file = rig.AddFile("photo.png", "image/png");

        StepExecutionResult result = await rig.RunAsync(EditJson(file.Id));

        result.FailureCode.Should().Be("IMAGE_EGRESS_UNAVAILABLE");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        rig.Rows().Should().BeEmpty();
    }

    // ================================================================ refusals before the gate

    [Fact]
    public async Task No_provider_is_a_user_input_failure_with_no_ledger_row()
    {
        var rig = new Rig { Provider = null };

        StepExecutionResult result = await rig.RunAsync(GenerateJson);

        result.FailureCode.Should().Be("NO_IMAGE_PROVIDER");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_config_and_a_missing_config_fail_with_CONFIG_INVALID()
    {
        var rig = new Rig();

        (await rig.RunAsync("""{"mode":"Generate","prompt":"","variations":9}""")).FailureCode.Should().Be("CONFIG_INVALID");
        (await rig.RunAsync("not json")).FailureCode.Should().Be("CONFIG_INVALID");
        (await rig.RunAsync("")).FailureCode.Should().Be("CONFIG_INVALID");
        rig.Rows().Should().BeEmpty();
        rig.Generated.Should().BeEmpty();
    }

    [Fact]
    public async Task A_model_with_no_price_is_refused_as_MODEL_UNPRICED_and_refunded()
    {
        var rig = new Rig { PriceFailure = new UnpricedUsageException(UsageKind.ImageGenImages, InferenceProviderKind.FalAi, "ideogram/v4.5") };

        StepExecutionResult result = await rig.RunAsync(GenerateJson);

        result.FailureCode.Should().Be("MODEL_UNPRICED");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        rig.Generated.Should().BeEmpty();
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task The_step_cap_is_enforced_by_the_gate_before_any_call()
    {
        var rig = new Rig();

        // 2 pictures x 10 credits = 20 credits, over a 15-credit cap.
        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"x","variations":2,"maxSpendCredits":15}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("BUDGET_EXCEEDED");
        rig.Generated.Should().BeEmpty();
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_wrong_model_for_the_mode_is_refused_before_the_gate()
    {
        var rig = new Rig();
        rig.Client.Setup(c => c.Describe(It.IsAny<string>())).Returns((string m) => new ImageModelCapabilities(m, true, false, 4, 4000, "t"));

        StepExecutionResult result = await rig.RunAsync(GenerateJson);

        result.FailureCode.Should().Be("CONFIG_INVALID");
        rig.Rows().Should().BeEmpty();
    }

    // ================================================================ moderation

    [Theory]
    [InlineData(ModerationAction.Allow, true, "Allow")]
    [InlineData(ModerationAction.Review, true, "Review")]
    [InlineData(ModerationAction.Allow, false, null)]
    public async Task The_manifest_keeps_the_verdict_the_prompt_was_given_and_nothing_after_an_outage(ModerationAction action, bool available, string? expected)
    {
        var rig = new Rig { Moderation = Gate(action, available) };

        StepExecutionResult result = await rig.RunAsync(GenerateJson);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        JsonObject image = JsonNode.Parse(rig.Stored[result.ArtifactStorageKey!])!["images"]![0]!.AsObject();
        image["promptCheck"]?.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public async Task A_blocked_prompt_buys_nothing()
    {
        var rig = new Rig { Moderation = Gate(ModerationAction.Block, true) };

        StepExecutionResult result = await rig.RunAsync(GenerateJson);

        result.FailureCode.Should().Be("CONTENT_BLOCKED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Rows().Should().BeEmpty();
        rig.Generated.Should().BeEmpty();
    }

    private static IContentModerationGate Gate(ModerationAction action, bool available)
    {
        var gate = new Mock<IContentModerationGate>();
        gate.Setup(g => g.ModerateAsync(It.IsAny<ModerationInput>(), It.IsAny<ModerationAttribution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModerationDecision(action, available, false, null, "blocked", "h"));
        return gate.Object;
    }

    // ================================================================ vendor outcomes

    private static HttpRequestException Http(HttpStatusCode status) => new($"fal.ai returned HTTP {(int)status}", null, status);

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "IMAGE_VENDOR_REJECTED", FailureClass.UserInput)]
    [InlineData(HttpStatusCode.TooManyRequests, "IMAGE_VENDOR_RETRYABLE", FailureClass.PlatformProviderError)]
    [InlineData(HttpStatusCode.Unauthorized, "IMAGE_PROVIDER_CONFIG", FailureClass.PlatformInternal)]
    [InlineData(HttpStatusCode.PaymentRequired, "IMAGE_PROVIDER_CONFIG", FailureClass.PlatformInternal)]
    public async Task A_definite_vendor_refusal_releases_the_reservation(HttpStatusCode status, string code, FailureClass failureClass)
    {
        var rig = new Rig { ClientFailure = Http(status) };

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"x"}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be(code);
        result.FailureClass.Should().Be(failureClass);
        rig.Rows().Should().ContainSingle().Which.Status.Should().Be(ExternalGenerationJobStatus.Failed);
        rig.Registered.Should().BeEmpty();
        if (code == "IMAGE_PROVIDER_CONFIG")
            Output(result)["error"]!["message"]!.GetValue<string>().Should().Be(ImageGenerateStepExecutor.PlatformAccountSentence);
    }

    [Fact]
    public async Task An_outcome_the_step_cannot_know_stays_charged_and_a_retry_never_resubmits_it()
    {
        var rig = new Rig { ClientFailure = Http(HttpStatusCode.BadGateway) };

        StepExecutionResult first = await rig.RunAsync("""{"mode":"Generate","prompt":"x"}""");

        first.FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        first.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        rig.Rows().Should().ContainSingle().Which.Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        Output(first)["error"]!["message"]!.GetValue<string>().Should().NotContain("fal.ai", "the vendor's text is never shown to the tenant");

        rig.ClientFailure = null;
        StepExecutionResult retry = await rig.RunAsync("""{"mode":"Generate","prompt":"x"}""");

        retry.FailureCode.Should().Be("RESERVATION_ORPHANED");
        rig.Generated.Should().HaveCount(1, "the retry did not call the vendor again");
    }

    [Fact]
    public async Task A_timeout_stays_charged_and_a_bad_request_shape_is_released()
    {
        var timeout = new Rig { ClientFailure = new TimeoutException("fal.ai did not finish") };
        (await timeout.RunAsync("""{"mode":"Generate","prompt":"x"}""")).FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        timeout.Rows().Single().Status.Should().Be(ExternalGenerationJobStatus.Reserved);

        var invalid = new Rig { ClientFailure = new ArgumentException("Give a size as Width/Height OR as an AspectRatio") };
        StepExecutionResult result = await invalid.RunAsync("""{"mode":"Generate","prompt":"x"}""");
        result.FailureCode.Should().Be("IMAGE_REQUEST_INVALID");
        invalid.Rows().Single().Status.Should().Be(ExternalGenerationJobStatus.Failed);
    }

    [Fact]
    public async Task A_vendor_failure_on_the_organizations_own_row_is_a_BYO_error()
    {
        var rig = new Rig(providerOrganizationId: Guid.NewGuid()) { ClientFailure = Http(HttpStatusCode.InternalServerError) };

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"x"}""");

        result.FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        result.FailureClass.Should().Be(FailureClass.ByoProviderError);
    }

    // ================================================================ metering follows delivery

    [Fact]
    public async Task Usage_is_recorded_once_per_delivered_picture_and_never_for_one_lost_in_delivery()
    {
        var rig = new Rig { FailUpload = name => name.Contains("-i2.", StringComparison.Ordinal) };

        StepExecutionResult result = await rig.RunAsync(GenerateJson);

        result.Status.Should().Be(StepStatus.Completed, "one picture was delivered");
        List<ExternalGenerationJob> rows = rig.Rows();
        rows.Single(r => r.ClipId == "img-1").Status.Should().Be(ExternalGenerationJobStatus.Succeeded);
        rows.Single(r => r.ClipId == "img-2").Status.Should().Be(ExternalGenerationJobStatus.Abandoned, "not stuck Submitted, and still counted against the step cap");

        UsageRecordRequest usage = rig.Usage.Should().ContainSingle("only the delivered picture is charged").Subject;
        usage.Kind.Should().Be(UsageKind.ImageGenImages);
        usage.Quantity.Should().Be(1m);
        usage.IdempotencyKey.Should().Be($"imagegen:{rows.Single(r => r.ClipId == "img-1").Id:N}");

        JsonObject output = Output(result);
        output["images"]![1]!["status"]!.GetValue<string>().Should().Be("failed");
        output["images"]![1]!["failureReason"]!.GetValue<string>().Should().Be("DELIVERY_FAILED");
        output["images"]![1]!["costBasis"]!.GetValue<string>().Should().Be("released");
        output["meta"]!["totalCost"]!.GetValue<decimal>().Should().Be(10m, "only the delivered picture counts");
    }

    [Fact]
    public async Task A_step_whose_every_picture_is_lost_in_delivery_records_no_usage_and_is_refunded()
    {
        var rig = new Rig { FailUpload = name => name.EndsWith(".png", StringComparison.Ordinal) };

        StepExecutionResult result = await rig.RunAsync(GenerateJson);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("DELIVERY_FAILED");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        rig.Usage.Should().BeEmpty();
        rig.Rows().Should().OnlyContain(r => r.Status == ExternalGenerationJobStatus.Abandoned);
    }

    // ================================================================ quarantine and reuse

    private static async Task SetMarkerAsync(Rig rig, DateTime marker)
    {
        await using var db = rig.Db.CreateDbContext();
        db.Projects.Add(new Project { Id = rig.ProjectId, Name = "p", OrganizationId = Guid.NewGuid(), ContentQuarantinedAt = marker });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task With_a_quarantine_marker_a_picture_made_before_it_is_not_reused()
    {
        var rig = new Rig();
        await using (var db = rig.Db.CreateDbContext())
        {
            db.WorkflowExecutions.Add(rig.Execution);
            rig.Execution.FirstStartedAt = DateTime.UtcNow.AddHours(-2);
            await db.SaveChangesAsync();
        }

        (await rig.RunAsync("""{"mode":"Generate","prompt":"x"}""")).Status.Should().Be(StepStatus.Completed);
        rig.Generated.Should().HaveCount(1);

        await SetMarkerAsync(rig, DateTime.UtcNow.AddMinutes(-30));
        StepExecutionResult second = await rig.RunAsync("""{"mode":"Generate","prompt":"x"}""");

        // Not reused: the identical request was bought again.
        rig.Generated.Should().HaveCount(2, "the older picture predates the quarantine marker, so it was not reused");
        Output(second)["images"]![0]!["reused"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task With_no_marker_the_same_picture_is_reused()
    {
        var rig = new Rig();
        await using (var db = rig.Db.CreateDbContext())
        {
            db.Projects.Add(new Project { Id = rig.ProjectId, Name = "p", OrganizationId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        (await rig.RunAsync("""{"mode":"Generate","prompt":"x"}""")).Status.Should().Be(StepStatus.Completed);
        StepExecutionResult second = await rig.RunAsync("""{"mode":"Generate","prompt":"x"}""");

        second.Status.Should().Be(StepStatus.Completed, second.ErrorDetails);
        Output(second)["images"]![0]!["reused"]!.GetValue<bool>().Should().BeTrue();
        rig.Generated.Should().HaveCount(1);
    }

    // ================================================================ batch (ItemList / BriefFiles)

    private const string BatchJson =
        """{"mode":"Generate","prompt":"A {item} sofa","itemList":"Red, blue\nred; Green","variations":2,"fileNamePrefix":"sofa"}""";

    [Fact]
    public async Task An_item_list_makes_one_picture_per_item_and_variation_each_with_its_own_text_row_and_file()
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(BatchJson);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Generated.Select(g => g.Prompt).Should().Equal("A Red sofa", "A Red sofa", "A blue sofa", "A blue sofa", "A Green sofa", "A Green sofa");
        rig.Rows().Select(r => r.ClipId).Should().BeEquivalentTo("img-1", "img-2", "img-3", "img-4", "img-5", "img-6");
        rig.Rows().Select(r => r.RequestHash).Distinct().Should().HaveCount(6, "the item index and text are part of the hash");

        JsonArray images = Output(result)["images"]!.AsArray();
        images.Select(i => i!["item"]!.GetValue<string>()).Should().Equal("Red", "Red", "blue", "blue", "Green", "Green");
        images.Select(i => i!["itemIndex"]!.GetValue<int>()).Should().Equal(1, 1, 2, 2, 3, 3);
        images.Select(i => i!["variation"]!.GetValue<int>()).Should().Equal(1, 2, 1, 2, 1, 2);
        string exec8 = rig.Execution.Id.ToString("N")[..8];
        images.Select(i => i!["fileName"]!.GetValue<string>()).Should().Equal(
            $"sofa-{exec8}-01-red-v1.png", $"sofa-{exec8}-01-red-v2.png", $"sofa-{exec8}-02-blue-v1.png",
            $"sofa-{exec8}-02-blue-v2.png", $"sofa-{exec8}-03-green-v1.png", $"sofa-{exec8}-03-green-v2.png");
        Output(result)["meta"]!["items"]!.AsArray().Select(i => i!.GetValue<string>()).Should().Equal("Red", "blue", "Green");
        rig.Registered.Should().HaveCount(6);
        rig.Stored.Values.Single()!.Should().Contain("\"item\":\"blue\"");
    }

    [Fact]
    public async Task A_batch_run_again_reuses_every_picture_and_never_double_submits()
    {
        var rig = new Rig();
        (await rig.RunAsync(BatchJson)).Status.Should().Be(StepStatus.Completed);

        StepExecutionResult second = await rig.RunAsync(BatchJson);

        second.Status.Should().Be(StepStatus.Completed, second.ErrorDetails);
        rig.Generated.Should().HaveCount(6);
        rig.Rows().Should().HaveCount(6);
    }

    [Fact]
    public async Task The_step_cap_covers_the_whole_batch()
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(
            """{"mode":"Generate","prompt":"A {item} sofa","itemList":"a,b,c","maxSpendCredits":25}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("BUDGET_EXCEEDED");
        rig.Generated.Should().BeEmpty("all pictures or none");
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task One_failing_picture_of_a_batch_leaves_the_others_delivered()
    {
        var rig = new Rig { FailUpload = name => name.Contains("-i2.", StringComparison.Ordinal) };

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"A {item} sofa","itemList":"a,b,c"}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        JsonArray images = Output(result)["images"]!.AsArray();
        images.Select(i => i!["status"]!.GetValue<string>()).Should().Equal("succeeded", "failed", "succeeded");
        images[1]!["item"]!.GetValue<string>().Should().Be("b");
        Output(result)["meta"]!["imagesFailed"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Without_an_item_list_the_item_token_stays_literal_and_the_output_has_no_item_fields()
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync("""{"mode":"Generate","prompt":"Keep {item} as written"}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Generated.Single().Prompt.Should().Be("Keep {item} as written");
        Output(result)["images"]![0]!.AsObject().ContainsKey("item").Should().BeFalse();
        Output(result)["meta"]!.AsObject().ContainsKey("items").Should().BeFalse();
    }

    [Fact]
    public async Task Edit_with_an_item_list_edits_the_one_source_once_per_item()
    {
        var rig = new Rig();
        ProjectWorkspaceFile source = rig.AddFile("sofa.png", "image/png");

        StepExecutionResult result = await rig.RunAsync(
            $$$"""{"mode":"Edit","instruction":"Recolour it {item}","itemList":"red,blue","allowSourceMediaEgress":true,"source":{"kind":"ProjectFile","projectFileId":"{{{source.Id}}}"}}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Edited.Select(e => e.Prompt).Should().Equal("Recolour it red", "Recolour it blue");
    }

    [Fact]
    public async Task Edit_with_brief_files_makes_one_edit_per_chosen_picture_and_labels_it_by_file_name()
    {
        var rig = new Rig();
        ProjectWorkspaceFile a = rig.AddFile("Cuscino Rosso.png", "image/png");
        ProjectWorkspaceFile b = rig.AddFile("cuscino blu.jpg", "image/jpeg");
        var brief = new RunBriefState(new StoredRunBrief(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Image, [a.Id, b.Id])]));
        const string json = """{"mode":"Edit","instruction":"Make it night","allowSourceMediaEgress":true,"source":{"kind":"BriefFiles","role":"Image"}}""";

        StepExecutionResult result;
        using (RunBriefAmbient.Enter(brief))
            result = await rig.RunAsync(json);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Edited.Should().HaveCount(2);
        JsonArray images = Output(result)["images"]!.AsArray();
        images.Select(i => i!["item"]!.GetValue<string>()).Should().Equal("Cuscino Rosso", "cuscino blu");
        rig.Rows().Select(r => r.ClipId).Should().Equal("img-1", "img-2");
    }

    [Fact]
    public async Task Edit_with_brief_files_and_none_chosen_fails_with_a_plain_code_and_no_ledger_row()
    {
        var rig = new Rig();
        const string json = """{"mode":"Edit","instruction":"Make it night","allowSourceMediaEgress":true,"source":{"kind":"BriefFiles","role":"Image"}}""";

        StepExecutionResult result;
        using (RunBriefAmbient.Enter(new RunBriefState(new StoredRunBrief())))
            result = await rig.RunAsync(json);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("INPUT_MISSING");
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_with_brief_files_still_needs_egress_consent()
    {
        var rig = new Rig();
        const string json = """{"mode":"Edit","instruction":"x","source":{"kind":"BriefFiles","role":"Image"}}""";

        StepExecutionResult result = await rig.RunAsync(json);

        result.FailureCode.Should().Be("EGRESS_REFUSED");
    }

    [Fact]
    public void Slug_keeps_letters_and_digits_and_never_comes_back_empty()
    {
        ImageGenerateStepExecutor.Slug("Rosso Fuoco!").Should().Be("rosso-fuoco");
        ImageGenerateStepExecutor.Slug("Blù/Cielo").Should().Be("blu-cielo");
        ImageGenerateStepExecutor.Slug("???").Should().Be("item");
        ImageGenerateStepExecutor.Slug(new string('a', 80)).Should().HaveLength(30);
    }

    [Fact]
    public void The_step_cache_key_carries_the_brief_picture_files_an_edit_reads_and_nothing_for_other_configs()
    {
        const string briefEdit = """{"mode":"Edit","instruction":"x","allowSourceMediaEgress":true,"source":{"kind":"BriefFiles","role":"Image"}}""";
        Guid one = Guid.NewGuid();
        Guid two = Guid.NewGuid();
        RunBriefState With(params Guid[] ids) => new(new StoredRunBrief(SourceFiles: [new RunBriefSourceFiles(RunBriefFileRole.Image, ids)]));

        string? a = With(one).CacheKeyComponent(StepType.ImageGenerate, null, null, briefEdit);
        string? b = With(one, two).CacheKeyComponent(StepType.ImageGenerate, null, null, briefEdit);

        a.Should().NotBeNull().And.NotBe(b);
        With(one).CacheKeyComponent(StepType.ImageGenerate, null, null, GenerateJson).Should().BeNull("a Generate reads no brief file");
    }
}
