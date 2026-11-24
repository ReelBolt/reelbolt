#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The picture editor's per-job capabilities: one capability per tool, so an admin points each job at a
/// provider and model built for it, and the admin boundary, the capability table, metering and the fal.ai
/// client all agree on what each one means.
/// </summary>
public class ImageJobCapabilityTests
{
    private static readonly InferenceProviderCapability[] SplitOut =
    [
        InferenceProviderCapability.ImageErase,
        InferenceProviderCapability.ImageFill,
        InferenceProviderCapability.ImageExpand,
        InferenceProviderCapability.ImageBackgroundReplace,
        InferenceProviderCapability.ImageRelight,
        InferenceProviderCapability.ImageUpscale
    ];

    // ================================================================ the mapping

    [Fact]
    public void Every_editor_job_has_exactly_one_capability_and_back()
    {
        foreach (FalImageJob job in Enum.GetValues<FalImageJob>())
        {
            InferenceProviderCapability capability = ImageJobCapabilities.CapabilityFor(job);
            ImageJobCapabilities.JobFor(capability).Should().Be(job);
            ImageJobCapabilities.All.Should().Contain(capability);
        }

        ImageJobCapabilities.All.Should().OnlyHaveUniqueItems().And.HaveCount(Enum.GetValues<FalImageJob>().Length);
        ImageJobCapabilities.JobFor(InferenceProviderCapability.Segmentation).Should().BeNull();
        ImageJobCapabilities.JobFor(InferenceProviderCapability.Chat).Should().BeNull();
    }

    [Fact]
    public void Only_the_six_split_out_capabilities_fall_back_to_ImageEdit()
    {
        foreach (InferenceProviderCapability capability in Enum.GetValues<InferenceProviderCapability>())
        {
            ImageJobCapabilities.FallsBackToImageEdit(capability).Should().Be(SplitOut.Contains(capability), capability.ToString());
        }
    }

    [Fact]
    public void The_web_mirror_of_the_fal_catalogue_matches_it()
    {
        // The admin form suggests these endpoints per capability; a stale list would suggest a model the
        // API then refuses (or hide the new default). Read from the web source, one list per line.
        System.IO.DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string? path = null;
        while (dir is not null && path is null)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, "web", "lib", "inference", "image-capabilities.ts");
            if (System.IO.File.Exists(candidate)) path = candidate;
            dir = dir.Parent;
        }

        path.Should().NotBeNull("web/lib/inference/image-capabilities.ts must be findable from the test run");
        string source = System.IO.File.ReadAllText(path!);
        string block = source[source.IndexOf("FALAI_IMAGE_MODELS", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("};", StringComparison.Ordinal)];

        foreach (InferenceProviderCapability capability in ImageJobCapabilities.All)
        {
            FalImageJob job = ImageJobCapabilities.JobFor(capability)!.Value;
            System.Text.RegularExpressions.Match line = System.Text.RegularExpressions.Regex.Match(
                block, @"^\s*" + capability + @": \[(?<ids>[^\]]*)\]", System.Text.RegularExpressions.RegexOptions.Multiline);
            line.Success.Should().BeTrue(capability + " must have a line in FALAI_IMAGE_MODELS");

            string[] web = [.. System.Text.RegularExpressions.Regex.Matches(line.Groups["ids"].Value, "'([^']+)'").Select(m => m.Groups[1].Value)];
            string[] catalogue = [FalImageCatalog.Instance.DefaultFor(job).Id, .. FalImageCatalog.Instance.AlternativesFor(job).Select(e => e.Id)];
            web.Should().Equal(catalogue, capability + ": default first, then the catalogue's alternatives in order");
        }
    }

    // ================================================================ the admin boundary

    [Theory]
    [InlineData(InferenceProviderKind.FalAi, true, true)]
    [InlineData(InferenceProviderKind.OpenAICompatible, true, false)]
    [InlineData(InferenceProviderKind.BlackForestLabs, true, false)]
    [InlineData(InferenceProviderKind.MiniMax, false, false)]
    [InlineData(InferenceProviderKind.AzureOpenAI, false, false)]
    [InlineData(InferenceProviderKind.Anthropic, false, false)]
    [InlineData(InferenceProviderKind.FishAudio, false, false)]
    public void Mask_jobs_go_to_kinds_that_document_a_mask_and_the_transforms_to_fal_alone(
        InferenceProviderKind kind, bool servesMaskJobs, bool servesTransforms)
    {
        foreach (InferenceProviderCapability capability in SplitOut)
        {
            bool expected = capability is InferenceProviderCapability.ImageErase or InferenceProviderCapability.ImageFill
                ? servesMaskJobs
                : servesTransforms;

            InferenceProvidersController.IsUnsupportedCombination(kind, capability, out string error)
                .Should().Be(!expected, kind + "/" + capability);
            ProviderCapabilityUsage.ServableByKind[kind].Contains(capability).Should().Be(expected, kind + "/" + capability);
            ImageJobCapabilities.KindServes(kind, capability).Should().Be(expected);
            if (!expected) error.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Every_split_out_job_is_metered_as_an_image_edit_so_its_per_endpoint_price_still_applies()
    {
        foreach (InferenceProviderCapability capability in SplitOut)
        {
            ProviderCapabilityUsage.ByCapability[capability].Should().Equal(UsageKind.ImageEditImages);
        }

        ProviderCapabilityUsage.RequiredCards().Should().Contain((InferenceProviderKind.FalAi, UsageKind.ImageEditImages));
    }

    // ================================================================ which model a row sends

    [Fact]
    public void A_fal_row_sends_its_own_model_only_for_its_own_job()
    {
        ImageJobCapabilities.FalModelFor(FalImageJob.Upscale, "topaz/upscale/image/precision")
            .Should().Be("topaz/upscale/image/precision");
        ImageJobCapabilities.FalModelFor(FalImageJob.Erase, "google/nano-banana-2.1/edit")
            .Should().BeEmpty("an instruction-edit model is never sent to the erase job; the catalogue default is");
        ImageJobCapabilities.FalModelFor(FalImageJob.Relight, "not-a-catalogued-endpoint").Should().BeEmpty();
        ImageJobCapabilities.FalModelFor(FalImageJob.Relight, null).Should().BeEmpty();
    }

    [Theory]
    [InlineData(InferenceProviderCapability.ImageUpscale, "fal-ai/seedvr/upscale/image", true)]
    [InlineData(InferenceProviderCapability.ImageUpscale, "topaz/upscale/image/generative", true)]
    [InlineData(InferenceProviderCapability.ImageUpscale, "fal-ai/image-apps-v2/relighting", false)]
    [InlineData(InferenceProviderCapability.ImageErase, "fal-ai/object-removal/mask", true)]
    [InlineData(InferenceProviderCapability.ImageErase, "fal-ai/flux-pro/v1/fill", false)]
    [InlineData(InferenceProviderCapability.ImageGeneration, "ideogram/v4.5", true)]
    [InlineData(InferenceProviderCapability.ImageGeneration, "google/nano-banana-2.1/edit", false)]
    [InlineData(InferenceProviderCapability.ImageEdit, "google/nano-banana-2.1/edit", true)]
    [InlineData(InferenceProviderCapability.ImageEdit, "fal-ai/image-apps-v2/relighting", true)]
    [InlineData(InferenceProviderCapability.ImageEdit, "ideogram/v4.5", false)]
    [InlineData(InferenceProviderCapability.ImageRelight, "fal-ai/not-read", false)]
    public void A_fal_image_row_must_name_an_endpoint_for_its_job(InferenceProviderCapability capability, string model, bool accepted)
    {
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, capability, model, out string error)
            .Should().Be(!accepted, error);
        if (!accepted) error.Should().ContainEquivalentOf("use one of");
    }

    [Fact]
    public void The_model_rule_leaves_every_other_kind_and_capability_alone()
    {
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.BlackForestLabs, InferenceProviderCapability.ImageFill, "flux-pro-1.0-fill", out _)
            .Should().BeFalse();
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/sam-3-1/image", out _)
            .Should().BeFalse();
    }

    // ================================================================ the connection test

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    private static (FalAiImageClient Client, StubHandler Api) Probe(string model, InferenceProviderCapability capability)
    {
        StubHandler api = new(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("{\"detail\":\"image_url is required\"}")
        });
        return (new FalAiImageClient(
            new HttpClient(api) { BaseAddress = new Uri("https://queue.fal.run") }, "k",
            probeModel: model, probeCapability: capability), api);
    }

    [Fact]
    public async Task A_job_row_pointing_at_another_jobs_endpoint_fails_its_test_without_a_request()
    {
        (FalAiImageClient client, StubHandler api) = Probe("fal-ai/image-apps-v2/relighting", InferenceProviderCapability.ImageUpscale);

        ImageProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.IsOk.Should().BeFalse();
        probe.Reason.Should().Contain("Relight endpoint").And.Contain("fal-ai/seedvr/upscale/image");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_job_row_is_probed_against_its_own_endpoint()
    {
        (FalAiImageClient client, StubHandler api) = Probe("topaz/upscale/image/precision", InferenceProviderCapability.ImageUpscale);

        ImageProbeResult probe = await client.ProbeAsync(CancellationToken.None);

        probe.IsOk.Should().BeTrue(probe.Reason);
        api.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/topaz/upscale/image/precision");
    }

    [Fact]
    public async Task A_legacy_ImageEdit_row_with_any_editing_endpoint_still_tests_green()
    {
        (FalAiImageClient client, _) = Probe("fal-ai/image-apps-v2/relighting", InferenceProviderCapability.ImageEdit);

        (await client.ProbeAsync(CancellationToken.None)).IsOk.Should().BeTrue();
    }
}

/// <summary>
/// The PNG reading and writing the editor uses to keep a cut-out's transparency through an AI edit. The
/// decoder is exercised on PNGs built here byte by byte, in every colour type and with every filter, so it
/// is not only ever checked against the encoder it ships with.
/// </summary>
public class PngAlphaTests
{
    /// <summary>A PNG from raw (already filtered) scanlines: each row is its filter byte followed by its samples.</summary>
    private static byte[] RawPng(int width, int height, int bitDepth, int colorType, byte[][] rows,
        byte[]? palette = null, byte[]? transparency = null, int interlace = 0)
    {
        using System.IO.MemoryStream compressed = new();
        using (System.IO.Compression.ZLibStream z = new(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            foreach (byte[] row in rows) z.Write(row);
        }

        using System.IO.MemoryStream file = new();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        ihdr[12] = (byte)interlace;
        Chunk(file, "IHDR", ihdr);
        if (palette is not null) Chunk(file, "PLTE", palette);
        if (transparency is not null) Chunk(file, "tRNS", transparency);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
        return file.ToArray();

        static void Chunk(System.IO.Stream s, string type, byte[] data)
        {
            byte[] length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            s.Write(length);
            s.Write(System.Text.Encoding.ASCII.GetBytes(type));
            s.Write(data);
            s.Write(new byte[4]); // the decoder does not verify CRCs
        }
    }

    [Fact]
    public void An_rgb_png_with_no_alpha_reads_as_fully_opaque()
    {
        // 2x2 RGB, filter None and Sub (the second row's samples are deltas).
        byte[] png = RawPng(2, 2, 8, 2,
        [
            [0, 10, 20, 30, 40, 50, 60],
            [1, 5, 5, 5, 1, 1, 1]
        ]);

        PngAlpha.DecodeAlpha(png)!.HasTransparency.Should().BeFalse();
        var rgba = PngAlpha.DecodeRgba(png)!.Value;
        rgba.Rgba.Should().Equal(10, 20, 30, 255, 40, 50, 60, 255, 5, 5, 5, 255, 6, 6, 6, 255);
    }

    [Fact]
    public void Up_average_and_paeth_filters_are_undone()
    {
        // 2x4 grayscale+alpha: a None row, then rows filtered Up, Average and Paeth.
        byte[] png = RawPng(2, 4, 8, 4,
        [
            [0, 100, 255, 50, 0],
            [2, 1, 0, 1, 0],    // Up: 101,255 51,0
            [3, 0, 0, 0, 0],    // Average: (left+up)/2 -> 50,127 50,63
            [4, 1, 1, 1, 1]     // Paeth -> 51,128 52,64
        ]);

        var rgba = PngAlpha.DecodeRgba(png)!.Value;
        rgba.Rgba.Skip(8).Take(8).Should().Equal(101, 101, 101, 255, 51, 51, 51, 0);
        rgba.Rgba.Skip(16).Take(8).Should().Equal(50, 50, 50, 127, 50, 50, 50, 63);
        rgba.Rgba.Skip(24).Take(8).Should().Equal(51, 51, 51, 128, 52, 52, 52, 64);
        PngAlpha.DecodeAlpha(png)!.Values.Should().Equal(255, 0, 255, 0, 127, 63, 128, 64);
    }

    [Fact]
    public void A_palette_png_takes_its_alpha_from_tRNS()
    {
        byte[] png = RawPng(4, 1, 2, 3, [[0, 0b_00_01_10_11]],
            palette: [255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9],
            transparency: [0, 128]);

        PngAlpha.DecodeAlpha(png)!.Values.Should().Equal(0, 128, 255, 255);
        PngAlpha.DecodeRgba(png)!.Value.Rgba.Take(8).Should().Equal(255, 0, 0, 0, 0, 255, 0, 128);
    }

    [Fact]
    public void Sixteen_bit_rgba_and_a_grey_tRNS_key_are_read()
    {
        byte[] rgba16 = RawPng(1, 1, 16, 6, [[0, 0xFF, 0xFF, 0x80, 0x00, 0x00, 0x10, 0x7F, 0xFF]]);
        PngAlpha.DecodeRgba(rgba16)!.Value.Rgba.Should().Equal(255, 128, 0, 127);

        byte[] keyed = RawPng(2, 1, 8, 0, [[0, 7, 8]], transparency: [0, 7]);
        PngAlpha.DecodeAlpha(keyed)!.Values.Should().Equal(0, 255);
    }

    [Fact]
    public void Interlaced_or_foreign_bytes_are_declined_not_misread()
    {
        PngAlpha.DecodeAlpha(RawPng(1, 1, 8, 6, [[0, 1, 2, 3, 4]], interlace: 1)).Should().BeNull();
        PngAlpha.DecodeAlpha([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]).Should().BeNull();
        PngAlpha.ApplyAlpha([0xFF, 0xD8, 0xFF], new AlphaPlane(1, 1, [0])).Should().BeNull();
    }

    [Fact]
    public void The_encoder_round_trips_through_the_decoder_for_a_noisy_picture()
    {
        Random random = new(7);
        byte[] rgba = new byte[37 * 23 * 4];
        random.NextBytes(rgba);

        byte[] png = PngAlpha.EncodeRgba(37, 23, rgba);

        PngAlpha.IsPng(png).Should().BeTrue();
        PngAlpha.DecodeRgba(png)!.Value.Rgba.Should().Equal(rgba);
    }

    [Fact]
    public void ApplyAlpha_multiplies_the_alpha_by_the_resampled_plane_and_keeps_the_colour()
    {
        byte[] opaque = RawPng(4, 2, 8, 2,
        [
            [0, 1, 2, 3, 1, 2, 3, 1, 2, 3, 1, 2, 3],
            [0, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9]
        ]);
        // A 2x1 plane: left transparent, right opaque — stretched over the 4x2 picture.
        AlphaPlane plane = new(2, 1, [0, 255]);

        var result = PngAlpha.DecodeRgba(PngAlpha.ApplyAlpha(opaque, plane)!)!.Value;

        result.Rgba.Where((_, i) => i % 4 == 3).Should().Equal(0, 64, 191, 255, 0, 64, 191, 255);
        result.Rgba.Take(3).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void A_mask_reads_its_brightness_and_planes_combine_by_product_and_maximum()
    {
        byte[] mask = PngAlpha.EncodeRgba(2, 1, [255, 255, 255, 255, 0, 0, 0, 255]);
        AlphaPlane painted = PngAlpha.DecodeMask(mask)!;
        painted.Values.Should().Equal(255, 0);

        AlphaPlane subject = new(2, 1, [0, 200]);
        subject.Max(painted).Values.Should().Equal(255, 200);
        subject.Multiply(new AlphaPlane(2, 1, [255, 128])).Values.Should().Equal(0, 100);
    }

    [Fact]
    public void A_matte_is_white_with_the_plane_as_its_alpha()
    {
        var matte = PngAlpha.DecodeRgba(PngAlpha.EncodeMatte(new AlphaPlane(2, 1, [0, 77])))!.Value;
        matte.Rgba.Should().Equal(255, 255, 255, 0, 255, 255, 255, 77);
    }
}

/// <summary>
/// The admin side of the split: a fal.ai image row must name an endpoint for its job, and one click sets
/// up every picture tool from one fal.ai row's key.
/// </summary>
public class ImageToolProviderAdminTests
{
    private static DbContextOptions<InferenceApiDbContext> Options() =>
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private sealed class Admin : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "admin@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => true;
        public Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }

    private static InferenceProvidersController Controller(InferenceApiDbContext db)
    {
        Mock<ISecretProtector> protector = new();
        protector.Setup(p => p.Protect(It.IsAny<string>())).Returns((string key) => "enc:" + key);
        return new InferenceProvidersController(
            db, new Admin(),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object, new Mock<IVideoGenerationClientFactory>().Object,
            new Mock<ISpeechSynthesisClientFactory>().Object, protector.Object,
            new Mock<ILogger<InferenceProvidersController>>().Object);
    }

    private static CreateProviderRequest Fal(string name, string capability, string model, bool isDefault = false) =>
        ProviderTree.OneModel(Name: name, Kind: "FalAi", Endpoint: "https://queue.fal.run", ModelName: model, ApiKey: "fal-secret-1234",
            IsDefault: isDefault, IsEnabled: true, TimeoutSeconds: null, Capability: capability);

    private static CreateProviderRequest BflGeneration() =>
        ProviderTree.OneModel(Name: "bfl", Kind: "BlackForestLabs", Endpoint: "https://api.bfl.ai", ModelName: "flux-2-pro", ApiKey: "k",
            IsDefault: true, IsEnabled: true, TimeoutSeconds: null, Capability: "ImageGeneration");

    [Fact]
    public async Task Creating_a_fal_upscale_row_with_a_relighting_endpoint_is_400_and_the_right_one_is_created()
    {
        using InferenceApiDbContext db = new(Options());
        InferenceProvidersController controller = Controller(db);

        ActionResult<ProviderResponse> wrong =
            await controller.Create(Fal("up", "ImageUpscale", "fal-ai/image-apps-v2/relighting"), CancellationToken.None);
        wrong.Result.Should().BeOfType<BadRequestObjectResult>();

        ActionResult<ProviderResponse> right =
            await controller.Create(Fal("up", "ImageUpscale", "fal-ai/seedvr/upscale/image", isDefault: true), CancellationToken.None);
        right.Result.Should().BeOfType<CreatedAtActionResult>();
        db.FlatProviders().Single().Capability.Should().Be(InferenceProviderCapability.ImageUpscale);
    }

    [Fact]
    public async Task One_click_adds_a_row_per_missing_picture_tool_with_the_same_key_and_never_steals_a_default()
    {
        using InferenceApiDbContext db = new(Options());
        InferenceProvidersController controller = Controller(db);
        await controller.Create(Fal("fal", "Segmentation", "fal-ai/sam-3-1/image", isDefault: true), CancellationToken.None);
        await controller.Create(Fal("fal edit", "ImageEdit", "google/nano-banana-2.1/edit", isDefault: true), CancellationToken.None);
        await controller.Create(BflGeneration(), CancellationToken.None);
        Guid source = db.FlatProviders().Single(p => p.Name == "fal").ProviderId;

        ActionResult<List<ModelResponse>> result = await controller.CreateImageTools(source, CancellationToken.None);

        List<ModelResponse> created = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<List<ModelResponse>>().Subject;
        created.Select(m => m.Capabilities.Single().Capability).Should().BeEquivalentTo(
            ["ImageGeneration", "ImageErase", "ImageFill", "ImageExpand", "ImageBackgroundReplace", "ImageRelight", "ImageUpscale"],
            "ImageEdit already has a fal.ai row");

        List<Guid> ids = [.. created.Select(m => m.Capabilities.Single().Id)];
        List<ModelCapabilityView> rows = [.. db.FlatProviders().Where(p => ids.Contains(p.Id))];
        rows.Should().OnlyContain(r => r.Kind == InferenceProviderKind.FalAi && r.ApiKeyEncrypted == "enc:fal-secret-1234");
        rows.Should().OnlyContain(r => r.ProviderId == source, "the picture tools are models under the SAME fal.ai account, no key copied");
        rows.Single(r => r.Capability == InferenceProviderCapability.ImageUpscale).ModelName.Should().Be("fal-ai/seedvr/upscale/image");
        rows.Single(r => r.Capability == InferenceProviderCapability.ImageErase).IsDefault.Should().BeTrue();
        rows.Single(r => r.Capability == InferenceProviderCapability.ImageGeneration).IsDefault.Should().BeFalse("BFL stays the generation default");
        db.FlatProviders().Single(p => p.Name == "bfl").IsDefault.Should().BeTrue();

        ActionResult<List<ModelResponse>> again = await controller.CreateImageTools(source, CancellationToken.None);
        ((List<ModelResponse>)((OkObjectResult)again.Result!).Value!).Should().BeEmpty("a second click has nothing left to add");
    }

    [Fact]
    public async Task One_click_needs_a_fal_row()
    {
        using InferenceApiDbContext db = new(Options());
        InferenceProvidersController controller = Controller(db);
        await controller.Create(BflGeneration(), CancellationToken.None);

        (await controller.CreateImageTools(db.FlatProviders().Single().ProviderId, CancellationToken.None)).Result
            .Should().BeOfType<BadRequestObjectResult>();
        (await controller.CreateImageTools(Guid.NewGuid(), CancellationToken.None)).Result
            .Should().BeOfType<NotFoundResult>();
    }
}
