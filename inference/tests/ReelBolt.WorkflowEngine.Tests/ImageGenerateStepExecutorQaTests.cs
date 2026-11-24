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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// QA probes for <see cref="ImageGenerateStepExecutor"/> edge cases (PR #1281): partial success, out-of-range and
/// mode-mismatched configs, foreign sources, retry/resume, reuse and pricing. Offline fakes only, no live fal call.
/// </summary>
public sealed class ImageGenerateStepExecutorQaTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private sealed class Rig
    {
        public Guid ProjectId { get; } = Guid.NewGuid();
        public WorkflowExecution Execution { get; }
        public SharedInMemoryDbFactory Db { get; } = new();
        public ResolvedImageProvider Provider { get; }
        public Mock<IProjectFileWorkspace> Workspace { get; } = new();
        public Mock<IImageClient> Client { get; } = new();
        public Mock<IPresignedUrlService> Presigner { get; } = new();
        public List<ImageGenerationRequest> Generated { get; } = [];
        public List<ImageEditRequest> Edited { get; } = [];
        public List<ProjectWorkspaceFile> Files { get; } = [];
        public Dictionary<string, string> Stored { get; } = [];
        public List<UsageRecordRequest> Usage { get; } = [];

        /// <summary>Per vendor call (1-based across the rig's life): an exception to throw, or null to answer a picture.</summary>
        public Func<int, Exception?> CallOutcome { get; set; } = _ => null;

        /// <summary>Per vendor call: the set to answer (defaults to one PNG).</summary>
        public Func<int, GeneratedImageSet>? Answer { get; set; }

        /// <summary>Upload of a picture (not the manifest) fails on these 1-based upload numbers.</summary>
        public HashSet<int> FailUploads { get; } = [];

        private int _calls;
        private int _pictureUploads;

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
                    return Next();
                });
            Client.Setup(c => c.EditAsync(It.IsAny<ImageEditRequest>(), It.IsAny<CancellationToken>()))
                .Returns((ImageEditRequest r, CancellationToken _) =>
                {
                    Edited.Add(r);
                    return Next();
                });

            Presigner.Setup(p => p.CreateGetAsync(ProjectId, It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync("https://signed.example/source.png");

            Workspace.Setup(w => w.UploadArtifactAsync(ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string path, string name, string mime, CancellationToken _, string category) =>
                {
                    if (!name.EndsWith(".json", StringComparison.Ordinal) && FailUploads.Contains(++_pictureUploads))
                        throw new IOException("storage down");
                    string stored = $"projects/{p}/{category}/{name}";
                    if (name.EndsWith(".json", StringComparison.Ordinal))
                        Stored[stored] = File.ReadAllText(path);
                    return stored;
                });
            Workspace.Setup(w => w.RegisterStoredObjectAsync(ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<long>(), It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string key, string name, string mime, long size, SummaryStatus _, FileIndexingStatus _, CancellationToken _, string category) =>
                {
                    var file = new ProjectWorkspaceFile(Guid.NewGuid(), p, name, null, category, key, mime, size, DateTime.UtcNow, null);
                    Files.Add(file);
                    return file;
                });
            Workspace.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(() => Files.ToList());
            Workspace.Setup(w => w.ArtifactExistsAsync(ProjectId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        }

        private Task<GeneratedImageSet> Next()
        {
            int n = ++_calls;
            Exception? failure = CallOutcome(n);
            if (failure is not null)
                return Task.FromException<GeneratedImageSet>(failure);
            return Task.FromResult(Answer?.Invoke(n) ?? new GeneratedImageSet([new GeneratedImage(Png, "image/png") { Width = 1024, Height = 1024 }], $"fal-req-{n}"));
        }

        public ImageGenerateStepExecutor Executor(IServiceScopeFactory? scopes = null)
        {
            var resolver = new Mock<IInferenceProviderResolver>();
            resolver.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Provider);
            resolver.Setup(r => r.ResolveImageEditAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Provider with { Capability = InferenceProviderCapability.ImageEdit });

            var factory = new Mock<IImageClientFactory>();
            factory.Setup(f => f.Get(It.IsAny<ResolvedImageProvider>())).Returns(Client.Object);
            factory.Setup(f => f.GetUnmetered(It.IsAny<ResolvedImageProvider>())).Returns(Client.Object);

            return new ImageGenerateStepExecutor(
                new VideoGenerationBudgetGate(Db, Microsoft.Extensions.Options.Options.Create(new VideoGenerationOptions())),
                resolver.Object, factory.Object, Workspace.Object, Db, NullLogger<ImageGenerateStepExecutor>.Instance,
                prices: Prices().Object, presignedUrls: Presigner.Object, scopeFactory: scopes, usageRecorder: Recorder());
        }

        private IUsageRecorder Recorder()
        {
            var recorder = new Mock<IUsageRecorder>();
            recorder.Setup(r => r.RecordAsync(It.IsAny<UsageRecordRequest>(), It.IsAny<CancellationToken>()))
                .Returns((UsageRecordRequest r, CancellationToken _) => { Usage.Add(r); return new ValueTask<long>(12_500L); });
            return recorder.Object;
        }

        /// <summary>0.06 USD vendor cost (10 credits on the ledger) and 12_500 millicredits to the tenant, per picture.</summary>
        public static Mock<IModelPriceService> Prices(Exception? failure = null)
        {
            var prices = new Mock<IModelPriceService>();
            prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .Returns((PriceRequest _, CancellationToken _) =>
                    failure is not null ? throw failure : new ValueTask<PriceQuote>(new PriceQuote(12_500, 0.06m, Guid.NewGuid())));
            return prices;
        }

        public Task<StepExecutionResult> RunAsync(string configJson, IServiceScopeFactory? scopes = null, Guid? stepResultId = null) =>
            Executor(scopes).ExecuteAsync(Context(configJson, stepResultId ?? Guid.NewGuid()));

        public StepExecutionContext Context(string configJson, Guid stepResultId, List<StepOutputHistoryEntry>? history = null)
        {
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepOrder = 2,
                StepType = StepType.ImageGenerate,
                ImageGenerateConfigJson = configJson
            };
            return new StepExecutionContext
            {
                StepResultId = stepResultId,
                Execution = Execution,
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                CurrentStepIndex = 1,
                IterationCount = 0,
                CorrelationId = "qa",
                StepOutputHistory = history ?? [],
                CancellationToken = CancellationToken.None
            };
        }

        public List<ExternalGenerationJob> Rows()
        {
            using var db = Db.CreateDbContext();
            return [.. db.ExternalGenerationJobs.AsNoTracking().OrderBy(j => j.CreatedAt).ThenBy(j => j.ClipId)];
        }

        public IServiceScopeFactory Scopes()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => Db.CreateDbContext());
            return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }
    }

    private static JsonObject Output(StepExecutionResult result) => JsonNode.Parse(result.Output)!.AsObject();

    private static HttpRequestException Http(HttpStatusCode status) => new($"fal.ai returned HTTP {(int)status}", null, status);

    private static string Gen(int variations, string extra = "") =>
        $$"""{"mode":"Generate","prompt":"A red fox in snow","variations":{{variations}}{{extra}}}""";

    // ================================================================ partial success

    [Fact]
    public async Task Partial_success_completes_with_the_failed_variation_released_and_only_delivered_pictures_registered()
    {
        var rig = new Rig { CallOutcome = n => n == 2 ? Http(HttpStatusCode.UnprocessableEntity) : null };

        StepExecutionResult result = await rig.RunAsync(Gen(3));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        result.FailureClass.Should().Be(FailureClass.None);
        rig.Rows().Select(r => (r.ClipId, r.Status)).Should().BeEquivalentTo(new[]
        {
            ("img-1", ExternalGenerationJobStatus.Succeeded),
            ("img-2", ExternalGenerationJobStatus.Failed),
            ("img-3", ExternalGenerationJobStatus.Succeeded),
        });
        rig.Files.Should().HaveCount(2);

        JsonObject output = Output(result);
        JsonArray images = output["images"]!.AsArray();
        images.Select(i => i!["status"]!.GetValue<string>()).Should().Equal("succeeded", "failed", "succeeded");
        images[1]!["failureReason"]!.GetValue<string>().Should().Be("IMAGE_VENDOR_REJECTED");
        images[1]!["projectFileId"].Should().BeNull();
        output["meta"]!["totalCost"]!.GetValue<decimal>().Should().Be(20m, "the released picture costs nothing");
        output["meta"]!["imagesFailed"]!.GetValue<int>().Should().Be(1);

        // The manifest lists all three, with storage keys only for the delivered ones.
        JsonArray manifest = JsonNode.Parse(rig.Stored[result.ArtifactStorageKey!])!["images"]!.AsArray();
        manifest.Should().HaveCount(3);
        manifest[1]!["storageKey"].Should().BeNull();
        manifest[2]!["storageKey"]!.GetValue<string>().Should().EndWith("step-2-i3.png");
    }

    [Fact]
    public async Task When_the_first_variation_fails_the_step_output_points_at_the_first_delivered_picture()
    {
        var rig = new Rig { CallOutcome = n => n == 1 ? Http(HttpStatusCode.BadGateway) : null };

        StepExecutionResult result = await rig.RunAsync(Gen(2));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        result.OutputStorageKey.Should().EndWith("step-2-i2.png");
        rig.Rows()[0].Status.Should().Be(ExternalGenerationJobStatus.Reserved, "an unknown outcome stays charged");
        Output(result)["meta"]!["totalCost"]!.GetValue<decimal>().Should().Be(20m, "the unknown picture is counted at its estimate");
    }

    [Fact]
    public async Task All_variations_failing_with_different_codes_is_ALL_IMAGES_FAILED_and_refundable()
    {
        var rig = new Rig { CallOutcome = n => n == 1 ? Http(HttpStatusCode.UnprocessableEntity) : Http(HttpStatusCode.TooManyRequests) };

        StepExecutionResult result = await rig.RunAsync(Gen(2));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("ALL_IMAGES_FAILED");
        result.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        rig.Rows().Should().OnlyContain(r => r.Status == ExternalGenerationJobStatus.Failed);
        result.ArtifactStorageKey.Should().NotBeNull("a failed step still writes its manifest");
    }

    /// <summary>
    /// FINDING (low): on the organization's OWN (BYO) row every vendor code alone is a ByoProviderError, but two different
    /// vendor codes collapse to ALL_IMAGES_FAILED, which is not in VendorCodes, so the step blames the PLATFORM's vendor.
    /// This pins the current behaviour; flip the expectation when ClassFor treats a vendor-only mix as BYO.
    /// </summary>
    [Fact]
    public async Task Byo_row_mixed_vendor_failures_are_classified_as_byo_provider_errors()
    {
        var single = new Rig(providerOrganizationId: Guid.NewGuid()) { CallOutcome = _ => Http(HttpStatusCode.TooManyRequests) };
        (await single.RunAsync(Gen(2))).FailureClass.Should().Be(FailureClass.ByoProviderError);

        var mixed = new Rig(providerOrganizationId: Guid.NewGuid())
        {
            CallOutcome = n => n == 1 ? Http(HttpStatusCode.UnprocessableEntity) : Http(HttpStatusCode.TooManyRequests)
        };
        StepExecutionResult result = await mixed.RunAsync(Gen(2));

        result.FailureCode.Should().Be("ALL_IMAGES_FAILED");
        result.FailureClass.Should().Be(FailureClass.ByoProviderError, "a mix of vendor failures on the organization's own row stays a BYO error");
    }

    // ================================================================ empty provider answer, delivery failure

    [Fact]
    public async Task An_empty_provider_answer_is_IMAGE_EMPTY_released_and_refundable()
    {
        var rig = new Rig { Answer = n => new GeneratedImageSet([], $"fal-req-{n}") };

        StepExecutionResult result = await rig.RunAsync(Gen(1));

        result.FailureCode.Should().Be("IMAGE_EMPTY");
        result.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        rig.Rows().Should().ContainSingle().Which.Status.Should().Be(ExternalGenerationJobStatus.Failed);
        rig.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task A_zero_byte_picture_is_treated_as_empty()
    {
        var rig = new Rig { Answer = n => new GeneratedImageSet([new GeneratedImage([], "image/png")], $"fal-req-{n}") };

        StepExecutionResult result = await rig.RunAsync(Gen(1));

        result.FailureCode.Should().Be("IMAGE_EMPTY");
        rig.Rows().Single().Status.Should().Be(ExternalGenerationJobStatus.Failed);
    }

    /// <summary>The credit-side record carries the plain model code and the tier as the variant, never the "code#tier" key.</summary>
    [Fact]
    public async Task A_tier_priced_picture_records_the_model_code_and_its_tier_as_the_variant()
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(Gen(1));

        result.Status.Should().Be(StepStatus.Completed);
        UsageRecordRequest usage = rig.Usage.Should().ContainSingle().Subject;
        usage.ModelName.Should().Be("ideogram/v4.5");
        usage.Variant.Should().Be("medium");
    }

    /// <summary>
    /// Since d34408e1f: a picture the vendor delivered but storage refused is released (Failed), costs nothing in the
    /// output, and records no tenant usage; the delivered one records exactly one unit keyed to its ledger row.
    /// </summary>
    [Fact]
    public async Task A_delivered_but_unstored_picture_is_abandoned_and_records_no_usage()
    {
        var rig = new Rig();
        rig.FailUploads.Add(2);

        StepExecutionResult result = await rig.RunAsync(Gen(2));

        result.Status.Should().Be(StepStatus.Completed, "picture 1 was delivered");
        JsonObject failed = Output(result)["images"]![1]!.AsObject();
        failed["failureReason"]!.GetValue<string>().Should().Be("DELIVERY_FAILED");
        failed["cost"]!.GetValue<decimal>().Should().Be(0m);
        failed["costBasis"]!.GetValue<string>().Should().Be("released");
        List<ExternalGenerationJob> rows = rig.Rows();
        rows.Single(r => r.ClipId == "img-2").Status.Should().Be(ExternalGenerationJobStatus.Abandoned);
        rig.Usage.Should().ContainSingle().Which.IdempotencyKey.Should().Be($"imagegen:{rows.Single(r => r.ClipId == "img-1").Id:N}");
    }

    [Fact]
    public async Task Usage_is_one_unit_per_delivered_picture_and_none_for_released_or_reused_ones()
    {
        var rig = new Rig { CallOutcome = n => n == 2 ? Http(HttpStatusCode.UnprocessableEntity) : null };

        (await rig.RunAsync(Gen(3))).Status.Should().Be(StepStatus.Completed);
        rig.Usage.Should().HaveCount(2).And.OnlyContain(u => u.Quantity == 1m && u.Kind == UsageKind.ImageGenImages);

        rig.CallOutcome = _ => null;
        (await rig.RunAsync(Gen(3))).Status.Should().Be(StepStatus.Completed);
        rig.Usage.Should().HaveCount(3, "the retry bought only the released picture; the reused ones record nothing");
        rig.Usage.Select(u => u.IdempotencyKey).Distinct().Should().HaveCount(3);
    }

    // ================================================================ config bounds

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public async Task Variations_out_of_range_are_refused_before_any_price_or_ledger_row(int variations)
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(Gen(variations));

        result.FailureCode.Should().Be("CONFIG_INVALID");
        result.ErrorDetails.Should().Contain("Variations must be from 1 to 4");
        rig.Rows().Should().BeEmpty();
        rig.Generated.Should().BeEmpty();
    }

    [Fact]
    public async Task Four_variations_make_four_distinct_requests_and_four_rows()
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(Gen(4));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Generated.Should().HaveCount(4);
        List<ExternalGenerationJob> rows = rig.Rows();
        rows.Select(r => r.ClipId).Should().BeEquivalentTo(["img-1", "img-2", "img-3", "img-4"]);
        rows.Select(r => r.RequestHash).Distinct().Should().HaveCount(4, "each variation is its own request");
    }

    /// <summary>
    /// FINDING (high, spec divergence): the brief asks to "edit an existing image with n variations"; the config
    /// forces Edit to exactly one picture (ImageGenerateStepConfig.cs:81 and :117-118).
    /// </summary>
    [Fact]
    public async Task Edit_with_variations_makes_one_edited_picture_each_from_the_same_source()
    {
        var rig = new Rig();
        Guid fileId = Guid.NewGuid();
        rig.Files.Add(new ProjectWorkspaceFile(fileId, rig.ProjectId, "photo.png", null, "inputFiles",
            $"projects/{rig.ProjectId}/inputFiles/photo.png", "image/png", 10, DateTime.UtcNow, null));

        StepExecutionResult result = await rig.RunAsync(
            $$$"""{"mode":"Edit","instruction":"Make it night","variations":3,"allowSourceMediaEgress":true,"source":{"kind":"ProjectFile","projectFileId":"{{{fileId}}}"}}""");

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Edited.Should().HaveCount(3);
        rig.Edited.Select(e => e.SourceImageUrl).Distinct().Should().ContainSingle();
        rig.Rows().Select(r => r.ClipId).Should().Equal("img-1", "img-2", "img-3");
    }

    [Theory]
    [InlineData("""{"mode":"Edit","instruction":"x","allowSourceMediaEgress":true}""", "Source is required for Edit")]
    [InlineData("""{"mode":"Edit","instruction":"x","allowSourceMediaEgress":true,"source":{"kind":"ProjectFile"}}""", "requires ProjectFileId")]
    [InlineData("""{"mode":"Edit","instruction":"x","allowSourceMediaEgress":true,"source":{"kind":"StepOutput"}}""", "requires StepOrder")]
    [InlineData("""{"mode":"Edit","allowSourceMediaEgress":true,"source":{"kind":"PreviousStepOutput"}}""", "Instruction is required")]
    [InlineData("""{"mode":"Generate","prompt":"x","source":{"kind":"PreviousStepOutput"}}""", "Source is for Edit only")]
    [InlineData("""{"mode":"Generate","prompt":"x","maxSpendCredits":0}""", "MaxSpendCredits must be a positive")]
    [InlineData("""{"mode":"Generate","prompt":"x","providerId":"00000000-0000-0000-0000-000000000000"}""", "ProviderId must not be the empty id")]
    [InlineData("""{"mode":"Paint","prompt":"x"}""", "not valid JSON")]
    public async Task Malformed_configs_are_refused_with_no_ledger_row(string json, string expected)
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(json);

        result.FailureCode.Should().Be("CONFIG_INVALID");
        result.ErrorDetails.Should().Contain(expected);
        rig.Rows().Should().BeEmpty();
        rig.Generated.Should().BeEmpty();
        rig.Edited.Should().BeEmpty();
    }

    // ================================================================ sources

    private static async Task<Guid> SeedStepResultAsync(Rig rig, Guid projectId, string outputKey)
    {
        await using WorkflowEngineDbContext db = rig.Db.CreateDbContext();
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = ExecutionStatus.Passed,
            FirstStartedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        var step = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.ImageGenerate };
        var result = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = outputKey,
            ExecutedAt = DateTime.UtcNow.AddMinutes(-4)
        };
        db.WorkflowExecutions.Add(execution);
        db.WorkflowSteps.Add(step);
        db.WorkflowStepResults.Add(result);
        await db.SaveChangesAsync();
        return result.Id;
    }

    private static string EditStepResult(Guid id) =>
        $$$"""{"mode":"Edit","instruction":"Make it night","allowSourceMediaEgress":true,"source":{"kind":"StepResult","stepResultId":"{{{id}}}"}}""";

    [Fact]
    public async Task Edit_of_a_same_project_step_result_picture_is_sent_to_the_vendor()
    {
        var rig = new Rig();
        Guid id = await SeedStepResultAsync(rig, rig.ProjectId, $"projects/{rig.ProjectId}/outputFiles/{Guid.NewGuid():D}/step-1-i1.png");

        StepExecutionResult result = await rig.RunAsync(EditStepResult(id), rig.Scopes());

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Edited.Should().ContainSingle();
    }

    [Fact]
    public async Task Edit_of_another_projects_step_result_is_SOURCE_NOT_FOUND_and_nothing_is_presigned_or_reserved()
    {
        var rig = new Rig();
        Guid other = Guid.NewGuid();
        Guid id = await SeedStepResultAsync(rig, other, $"projects/{other}/outputFiles/{Guid.NewGuid():D}/step-1-i1.png");

        StepExecutionResult result = await rig.RunAsync(EditStepResult(id), rig.Scopes());

        result.FailureCode.Should().Be("SOURCE_NOT_FOUND");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Edited.Should().BeEmpty();
        rig.Rows().Should().BeEmpty();
        rig.Presigner.Verify(p => p.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact]
    public async Task Edit_of_a_same_project_step_result_that_is_a_video_is_SOURCE_NOT_IMAGE()
    {
        var rig = new Rig();
        Guid id = await SeedStepResultAsync(rig, rig.ProjectId, $"projects/{rig.ProjectId}/outputFiles/{Guid.NewGuid():D}/clip.mp4");

        StepExecutionResult result = await rig.RunAsync(EditStepResult(id), rig.Scopes());

        result.FailureCode.Should().Be("SOURCE_NOT_IMAGE");
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_of_a_step_output_key_with_traversal_is_refused()
    {
        var rig = new Rig();
        var context = rig.Context(
            """{"mode":"Edit","instruction":"x","allowSourceMediaEgress":true,"source":{"kind":"StepOutput","stepOrder":1}}""", Guid.NewGuid(),
            [new StepOutputHistoryEntry(1, "s", "{}", $"projects/{rig.ProjectId}/../{Guid.NewGuid()}/x.png")]);

        StepExecutionResult result = await rig.Executor().ExecuteAsync(context);

        result.FailureCode.Should().Be("SOURCE_NOT_FOUND");
        rig.Edited.Should().BeEmpty();
    }

    // ================================================================ retry / resume

    [Fact]
    public async Task A_retry_after_partial_success_reuses_the_delivered_picture_and_buys_only_the_released_one()
    {
        var rig = new Rig { CallOutcome = n => n == 2 ? Http(HttpStatusCode.TooManyRequests) : null };
        (await rig.RunAsync(Gen(2))).Status.Should().Be(StepStatus.Completed);
        rig.Generated.Should().HaveCount(2);

        StepExecutionResult retry = await rig.RunAsync(Gen(2));

        retry.Status.Should().Be(StepStatus.Completed, retry.ErrorDetails);
        rig.Generated.Should().HaveCount(3, "only the released picture was bought again");
        JsonArray images = Output(retry)["images"]!.AsArray();
        images[0]!["reused"]!.GetValue<bool>().Should().BeTrue();
        images[1]!["reused"]!.GetValue<bool>().Should().BeFalse();
        Output(retry)["meta"]!["totalCost"]!.GetValue<decimal>().Should().Be(10m);
        rig.Rows().Count(r => r.Status == ExternalGenerationJobStatus.Succeeded).Should().Be(2);
    }

    [Fact]
    public async Task A_retry_after_a_delivery_failure_buys_the_released_picture_again()
    {
        var rig = new Rig();
        rig.FailUploads.Add(1);
        (await rig.RunAsync(Gen(1))).FailureCode.Should().Be("DELIVERY_FAILED");
        rig.Usage.Should().BeEmpty();

        StepExecutionResult retry = await rig.RunAsync(Gen(1));

        retry.Status.Should().Be(StepStatus.Completed, retry.ErrorDetails);
        rig.Generated.Should().HaveCount(2);
        rig.Usage.Should().ContainSingle();
    }

    private static async Task<(Guid Step, Guid First)> SeedAttemptAsync(Rig rig)
    {
        Guid first = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        await using WorkflowEngineDbContext db = rig.Db.CreateDbContext();
        db.WorkflowExecutions.Add(rig.Execution);
        db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = Guid.NewGuid(), StepOrder = 2, StepType = StepType.ImageGenerate });
        db.WorkflowStepResults.Add(new WorkflowStepResult { Id = first, WorkflowExecutionId = rig.Execution.Id, WorkflowStepId = stepId, Status = StepStatus.Running });
        await db.SaveChangesAsync();
        return (stepId, first);
    }

    private static async Task<Guid> SupersedeAsync(Rig rig, Guid stepId, Guid earlierId)
    {
        Guid next = Guid.NewGuid();
        await using WorkflowEngineDbContext db = rig.Db.CreateDbContext();
        WorkflowStepResult earlier = await db.WorkflowStepResults.SingleAsync(r => r.Id == earlierId);
        earlier.Status = StepStatus.Failed;
        earlier.SupersededAt = DateTime.UtcNow;
        db.WorkflowStepResults.Add(new WorkflowStepResult { Id = next, WorkflowExecutionId = rig.Execution.Id, WorkflowStepId = stepId, Status = StepStatus.Running });
        await db.SaveChangesAsync();
        return next;
    }

    /// <summary>
    /// FINDING (medium): the engine's real retry supersedes the earlier step result; the shared gate then abandons the
    /// earlier attempt's Reserved row (unknown outcome) and KEEPS it in the step total (decision 2b). With no
    /// MaxSpendCredits the executor sets the step cap to the priced total of THIS attempt only
    /// (ImageGenerateStepExecutor.cs:246), so the retry is always refused BUDGET_EXCEEDED (UserInput) with a
    /// "video generation ... clips" sentence and a "spending limit" the author never set. Same pattern as AudioGenerate:300.
    /// </summary>
    [Fact]
    public async Task On_an_engine_retry_after_an_unknown_outcome_the_default_cap_leaves_room_to_buy_again()
    {
        var rig = new Rig { CallOutcome = n => n == 1 ? Http(HttpStatusCode.BadGateway) : null };
        (Guid stepId, Guid first) = await SeedAttemptAsync(rig);
        (await rig.RunAsync(Gen(1), stepResultId: first)).FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        Guid second = await SupersedeAsync(rig, stepId, first);

        StepExecutionResult retry = await rig.RunAsync(Gen(1), stepResultId: second);

        retry.Status.Should().Be(StepStatus.Completed, retry.ErrorDetails);
        rig.Generated.Should().HaveCount(2, "the unknown outcome was abandoned once and the picture bought again");
    }

    [Fact]
    public async Task On_an_engine_retry_with_room_under_an_explicit_cap_the_unknown_outcome_is_abandoned_once_and_bought_again()
    {
        var rig = new Rig { CallOutcome = n => n == 1 ? Http(HttpStatusCode.BadGateway) : null };
        (Guid stepId, Guid first) = await SeedAttemptAsync(rig);
        const string capped = ""","maxSpendCredits":40""";
        (await rig.RunAsync(Gen(1, capped), stepResultId: first)).FailureCode.Should().Be("SUBMIT_OUTCOME_UNKNOWN");
        Guid second = await SupersedeAsync(rig, stepId, first);

        StepExecutionResult retry = await rig.RunAsync(Gen(1, capped), stepResultId: second);

        retry.Status.Should().Be(StepStatus.Completed, retry.ErrorDetails);
        rig.Generated.Should().HaveCount(2, "the earlier unknown outcome was abandoned and bought again once");
        rig.Rows().Select(r => r.Status).Should().BeEquivalentTo(new[] { ExternalGenerationJobStatus.Abandoned, ExternalGenerationJobStatus.Succeeded });
    }

    // ================================================================ reuse and registration

    /// <summary>
    /// FINDING (low): the request hash ignores RegisterProjectFile, and a reused picture is only ever LINKED to an existing
    /// project file (ImageGenerateStepExecutor.cs:590-598, "else if (!reused)"). A picture first made with
    /// registerProjectFile=false is reused by a later run that asks for registration, and never becomes a project file.
    /// </summary>
    [Fact]
    public async Task A_reused_picture_is_not_registered_even_when_the_later_run_asks_for_a_project_file()
    {
        var rig = new Rig();
        (await rig.RunAsync(Gen(1, ""","registerProjectFile":false"""))).Status.Should().Be(StepStatus.Completed);
        rig.Files.Should().BeEmpty();

        StepExecutionResult second = await rig.RunAsync(Gen(1, ""","registerProjectFile":true"""));

        second.Status.Should().Be(StepStatus.Completed);
        JsonObject image = Output(second)["images"]![0]!.AsObject();
        image["reused"]!.GetValue<bool>().Should().BeTrue();
        image["projectFileId"].Should().BeNull("current behaviour: the reused picture is not registered");
        rig.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task A_different_prompt_size_or_aspect_is_never_reused()
    {
        var rig = new Rig();
        foreach (string json in new[] { Gen(1), Gen(1, ",\"aspectRatio\":\"16:9\""), Gen(1, ""","width":1024,"height":768"""), """{"mode":"Generate","prompt":"A blue fox in snow"}""" })
        {
            StepExecutionResult r = await rig.RunAsync(json);
            r.Status.Should().Be(StepStatus.Completed, json + " -> " + r.ErrorDetails);
        }

        rig.Generated.Should().HaveCount(4);
    }

    // ================================================================ hold vs settle

    [Theory]
    [InlineData(1, 12_500L)]
    [InlineData(4, 50_000L)]
    public async Task The_hold_is_variations_times_the_tenant_price_and_matches_the_ledger_count(int variations, long expected)
    {
        var rig = new Rig();
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(rig.Provider);

        long? hold = await ImageGenerateCreditPricing.TryHoldMillisAsync(Gen(variations), resolver.Object, Rig.Prices().Object);

        hold.Should().Be(expected);
        (await rig.RunAsync(Gen(variations))).Status.Should().Be(StepStatus.Completed);
        rig.Rows().Should().HaveCount(variations).And.OnlyContain(r => r.CostEstimate == 10m);
    }

    [Fact]
    public async Task The_hold_is_zero_on_an_organizations_own_row_and_null_when_unpriced_or_invalid()
    {
        var byo = new Rig(providerOrganizationId: Guid.NewGuid());
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(byo.Provider);

        (await ImageGenerateCreditPricing.TryHoldMillisAsync(Gen(3), resolver.Object, Rig.Prices().Object)).Should().Be(0);
        (await ImageGenerateCreditPricing.TryHoldMillisAsync(Gen(3), resolver.Object,
            Rig.Prices(new UnpricedUsageException(UsageKind.ImageGenImages, InferenceProviderKind.FalAi, "m")).Object)).Should().BeNull();
        (await ImageGenerateCreditPricing.TryHoldMillisAsync(Gen(9), resolver.Object, Rig.Prices().Object)).Should().BeNull();

        var none = new Mock<IInferenceProviderResolver>();
        (await ImageGenerateCreditPricing.TryHoldMillisAsync(Gen(1), none.Object, Rig.Prices().Object)).Should().BeNull("no provider is never a free hold");
    }

    [Fact]
    public async Task The_step_cap_admits_exactly_the_priced_total_and_counts_the_ledger_cost_not_the_tenant_price()
    {
        // 2 x 10 ledger credits = 20: a cap of exactly 20 passes, although the tenant is charged 2 x 12.5 = 25 credits.
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(Gen(2, ""","maxSpendCredits":20"""));

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        rig.Generated.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_cap_too_small_for_one_picture_reserves_nothing()
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(Gen(1, ""","maxSpendCredits":9.99"""));

        result.FailureCode.Should().Be("BUDGET_EXCEEDED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task An_unpriced_model_never_reaches_the_vendor_even_with_no_price_service()
    {
        var rig = new Rig();
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(rig.Provider);
        var factory = new Mock<IImageClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedImageProvider>())).Returns(rig.Client.Object);
        factory.Setup(f => f.GetUnmetered(It.IsAny<ResolvedImageProvider>())).Returns(rig.Client.Object);
        var executor = new ImageGenerateStepExecutor(
            new VideoGenerationBudgetGate(rig.Db, Microsoft.Extensions.Options.Options.Create(new VideoGenerationOptions())),
            resolver.Object, factory.Object, rig.Workspace.Object, rig.Db, NullLogger<ImageGenerateStepExecutor>.Instance, prices: null);

        StepExecutionResult result = await executor.ExecuteAsync(rig.Context(Gen(1), Guid.NewGuid()));

        result.FailureCode.Should().Be("MODEL_UNPRICED");
        rig.Generated.Should().BeEmpty();
        rig.Rows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_zero_vendor_price_is_refused_rather_than_free()
    {
        var rig = new Rig();
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(rig.Provider);
        var factory = new Mock<IImageClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedImageProvider>())).Returns(rig.Client.Object);
        factory.Setup(f => f.GetUnmetered(It.IsAny<ResolvedImageProvider>())).Returns(rig.Client.Object);
        var prices = new Mock<IModelPriceService>();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<PriceQuote>(new PriceQuote(0, 0m, Guid.NewGuid())));
        var executor = new ImageGenerateStepExecutor(
            new VideoGenerationBudgetGate(rig.Db, Microsoft.Extensions.Options.Options.Create(new VideoGenerationOptions())),
            resolver.Object, factory.Object, rig.Workspace.Object, rig.Db, NullLogger<ImageGenerateStepExecutor>.Instance, prices: prices.Object);

        StepExecutionResult result = await executor.ExecuteAsync(rig.Context(Gen(1), Guid.NewGuid()));

        result.FailureCode.Should().Be("CONFIG_INVALID");
        rig.Generated.Should().BeEmpty();
        rig.Rows().Should().BeEmpty();
    }

    // ================================================================ daily caps do not apply (credits only)

    [Fact]
    public async Task Image_rows_never_count_toward_the_video_daily_sums()
    {
        var rig = new Rig();
        (await rig.RunAsync(Gen(4))).Status.Should().Be(StepStatus.Completed);

        await using WorkflowEngineDbContext db = rig.Db.CreateDbContext();
        var today = await VideoBudgetAccounting
            .TodayRows(db.ExternalGenerationJobs, ExternalGenerationJob.CreditsUnit, DateTime.UtcNow.Date).ToListAsync();
        today.Should().BeEmpty();
    }
}
