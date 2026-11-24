using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary><c>StageProjectFile</c> adds a <c>brand</c> block and advisory warnings for logo images; it never refuses.</summary>
public class StageProjectFileBrandTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static byte[] Png(byte r, byte g, byte b)
    {
        var rgba = new byte[8 * 8 * 4];
        for (int i = 0; i < 64; i++)
        {
            rgba[i * 4] = r; rgba[(i * 4) + 1] = g; rgba[(i * 4) + 2] = b; rgba[(i * 4) + 3] = 255;
        }

        return PngAlpha.EncodeRgba(8, 8, rgba);
    }

    private static ProjectWorkspaceFile File(string name, string mime) => new(
        Guid.NewGuid(), ProjectId, name, null, "userFiles",
        $"projects/{ProjectId}/userFiles/{Guid.NewGuid()}", mime, 400, DateTime.UtcNow, null);

    private static async Task<JsonObject> Stage(
        ProjectWorkspaceFile file, byte[]? bytes, DesignSystemRunState? state = null, bool failDownload = false)
    {
        var session = new Mock<ISandboxSession>();
        session
            .Setup(s => s.StageFromUrlAsync(It.IsAny<SandboxStageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SandboxTransfer(4, null));

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync([file]);
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string _, string path, long _, CancellationToken _) =>
            {
                if (failDownload || bytes is null)
                    throw new IOException("storage unavailable");
                System.IO.File.WriteAllBytes(path, bytes);
                return Task.CompletedTask;
            });

        var accessor = new WorkflowExecutionContextAccessor();
        accessor.BeginScope(Guid.NewGuid(), ProjectId, "correlation", null);
        var factory = new Mock<ISandboxSessionFactory>();
        factory.Setup(f => f.Create(It.IsAny<Guid>())).Returns(session.Object);

        var tool = new ReactRemotionSandboxTools(
            Mock.Of<IHttpClientFactory>(),
            accessor,
            workspace.Object,
            new Mock<IAmazonS3>(MockBehavior.Strict).Object,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            Mock.Of<IServiceScopeFactory>(),
            sandboxSessions: factory.Object,
            presignedUrls: new FakePresignedUrlService());

        using IDisposable ambient = DesignSystemAmbient.Enter(state);
        return JsonNode.Parse(await tool.StageProjectFile(file.Id.ToString()))!.AsObject();
    }

    private static DesignSystemRunState StateFor(JsonObject design) =>
        new(new LoadedProfile(design, 1, null, 0), new DesignSystemAmbientOptions());

    [Fact]
    public async Task Dark_ink_product_logo_gets_a_brand_block_and_a_dark_ink_warning()
    {
        ProjectWorkspaceFile file = File("acme-logo.png", "image/png");
        DesignSystemRunState state = StateFor(new JsonObject { ["name"] = "Acme" });

        JsonObject result = await Stage(file, Png(5, 5, 5), state);

        result["ok"]!.GetValue<bool>().Should().BeTrue();
        JsonObject brand = result["brand"]!.AsObject();
        brand["subject"]!.GetValue<string>().Should().Be("product");
        brand["tone"]!.GetValue<string>().Should().Be("dark");
        brand["bestOn"]!.GetValue<string>().Should().Be("light");
        result["warnings"]!.AsArray().Should().ContainSingle()
            .Which!.GetValue<string>().Should().Be(ReactRemotionSandboxTools.DarkInkWarning);
    }

    [Fact]
    public async Task Publisher_mark_warns_that_it_is_not_the_product_logo()
    {
        ProjectWorkspaceFile file = File("globex-group-logo.png", "image/png");
        DesignSystemRunState state = StateFor(new JsonObject
        {
            ["name"] = "Acme",
            ["voice"] = new JsonObject { ["summary"] = "Acme is published by Globex Group Srl." },
        });

        JsonObject result = await Stage(file, Png(0, 80, 200), state);

        result["brand"]!["subject"]!.GetValue<string>().Should().Be("publisher");
        result["warnings"]!.AsArray().Select(w => w!.GetValue<string>())
            .Should().Contain(ReactRemotionSandboxTools.PublisherMarkWarning);
    }

    [Fact]
    public async Task Catalog_entry_supplies_the_brand_block_without_reading_storage()
    {
        ProjectWorkspaceFile file = File("anything.png", "image/png");
        var design = new JsonObject
        {
            ["assets"] = new JsonObject
            {
                ["logo"] = new JsonObject
                {
                    ["fileRef"] = "project-file:" + file.Id,
                    ["subject"] = "product",
                    ["form"] = "mark",
                    ["tone"] = "light",
                    ["readableOn"] = new JsonArray("dark"),
                    ["luminance"] = 0.9,
                },
            },
        };

        JsonObject result = await Stage(file, null, StateFor(design), failDownload: true);

        result["brand"]!["bestOn"]!.GetValue<string>().Should().Be("dark");
        result["warnings"]!.AsArray().Single()!.GetValue<string>().Should().Be(ReactRemotionSandboxTools.LightInkWarning);
    }

    [Fact]
    public async Task Unmeasurable_or_non_image_files_stage_exactly_as_before()
    {
        JsonObject failing = await Stage(File("logo.png", "image/png"), null);
        failing["ok"]!.GetValue<bool>().Should().BeTrue();
        failing.ContainsKey("brand").Should().BeFalse();
        failing.ContainsKey("warnings").Should().BeFalse();

        JsonObject font = await Stage(File("brand.woff2", "font/woff2"), Png(0, 0, 0));
        font.ContainsKey("brand").Should().BeFalse();
    }

    [Fact]
    public async Task Non_logo_images_get_no_brand_block_or_warnings()
    {
        DesignSystemRunState state = StateFor(new JsonObject { ["name"] = "Acme" });

        JsonObject shot = await Stage(File("screenshot-2026.png", "image/png"), Png(5, 5, 5), state);
        shot["ok"]!.GetValue<bool>().Should().BeTrue();
        shot.ContainsKey("brand").Should().BeFalse();
        shot.ContainsKey("warnings").Should().BeFalse();

        JsonObject bg = await Stage(File("hero-background.svg", "image/svg+xml"), System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0L9 9\"/></svg>"), state);
        bg.ContainsKey("brand").Should().BeFalse();
    }

    [Fact]
    public async Task Logo_like_name_with_unknown_subject_warns_only_when_a_design_system_is_pinned()
    {
        JsonObject pinned = await Stage(
            File("zeta-logo.png", "image/png"), Png(0, 80, 200), StateFor(new JsonObject { ["name"] = "Acme" }));
        pinned["brand"]!["subject"]!.GetValue<string>().Should().Be("unknown");
        pinned["warnings"]!.AsArray().Select(w => w!.GetValue<string>())
            .Should().Contain(ReactRemotionSandboxTools.UnknownMarkWarning);

        JsonObject unpinned = await Stage(File("zeta-logo.png", "image/png"), Png(5, 5, 5));
        unpinned["brand"]!["subject"]!.GetValue<string>().Should().Be("unknown");
        unpinned["warnings"]!.AsArray().Select(w => w!.GetValue<string>())
            .Should().NotContain(ReactRemotionSandboxTools.UnknownMarkWarning)
            .And.Contain(ReactRemotionSandboxTools.DarkInkWarning);
    }

    [Fact]
    public async Task Design_system_assets_path_counts_as_logo_like_even_with_a_neutral_name()
    {
        ProjectWorkspaceFile file = File("photo.png", "image/png") with { OriginalPath = "design-system/assets/photo.png" };
        JsonObject result = await Stage(file, Png(0, 80, 200), StateFor(new JsonObject { ["name"] = "Acme" }));
        result.ContainsKey("brand").Should().BeTrue();
    }

    [Fact]
    public async Task Legacy_design_name_slug_classifies_the_product_logo()
    {
        JsonObject result = await Stage(
            File("acmehr-logo.png", "image/png"), Png(0, 80, 200),
            StateFor(new JsonObject { ["name"] = "Acme-HR Design System" }));
        result["brand"]!["subject"]!.GetValue<string>().Should().Be("product");
    }
}
