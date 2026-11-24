using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// <see cref="BrandAssetCatalog"/>: persisted keys win, a legacy document (no metadata) is measured from the
/// stored file, and a storage failure never throws.
/// </summary>
public class BrandAssetCatalogTests
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

    private static byte[] Svg(string inner) =>
        Encoding.UTF8.GetBytes($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">{inner}</svg>");

    private static ProjectWorkspaceFile File(Guid id, string name, string mime, long size = 500) => new(
        id, ProjectId, name, null, "userFiles", $"projects/{ProjectId}/userFiles/{id}", mime, size, DateTime.UtcNow, null);

    private static JsonObject Legacy(params (string Slot, Guid Id)[] variants)
    {
        var list = new JsonArray();
        foreach ((_, Guid id) in variants.Skip(1))
            list.Add(new JsonObject { ["name"] = "publisher", ["fileRef"] = "project-file:" + id });

        return new JsonObject
        {
            ["name"] = "Acme App",
            ["voice"] = new JsonObject { ["summary"] = "Acme App is published by Globex Group Srl." },
            ["assets"] = new JsonObject
            {
                ["logo"] = new JsonObject { ["fileRef"] = "project-file:" + variants[0].Id, ["variants"] = list },
            },
        };
    }

    private static Mock<IProjectFileWorkspace> Storage(
        IReadOnlyList<ProjectWorkspaceFile> files, Dictionary<Guid, byte[]> bytes, bool failDownload = false)
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>())).ReturnsAsync(files);
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                ProjectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string key, string path, long _, CancellationToken _) =>
            {
                if (failDownload)
                    throw new IOException("storage unavailable");
                Guid id = Guid.Parse(key[(key.LastIndexOf('/') + 1)..]);
                System.IO.File.WriteAllBytes(path, bytes[id]);
                return Task.CompletedTask;
            });
        return workspace;
    }

    [Fact]
    public void Persisted_keys_are_used_without_any_storage_access()
    {
        Guid id = Guid.NewGuid();
        var design = new JsonObject
        {
            ["assets"] = new JsonObject
            {
                ["logo"] = new JsonObject
                {
                    ["fileRef"] = "project-file:" + id,
                    ["file"] = "mark-dark.svg",
                    ["subject"] = "product",
                    ["form"] = "mark",
                    ["tone"] = "dark",
                    ["readableOn"] = new JsonArray("light"),
                    ["luminance"] = 0.0,
                },
            },
        };

        BrandAssetCatalog catalog = BrandAssetCatalog.FromDocument(design);

        BrandAssetEntry entry = catalog.Entries.Should().ContainSingle().Subject;
        entry.Path.Should().Be("assets.logo");
        entry.Subject.Should().Be("product");
        entry.Tone.Should().Be("dark");
        entry.ReadableOn.Should().Equal("light");
        entry.BestOn.Should().Be("light");
        entry.Source.Should().Be("persisted");
        entry.UseFor.Should().Contain("product logo").And.Contain("light backgrounds only");
        catalog.ForFile(id).Should().BeSameAs(entry);
    }

    [Theory]
    [InlineData("acme-hr-design")]
    [InlineData("Acme-HR Design System")]
    public void Slug_fallback_strips_generic_design_words_from_the_design_name(string name)
    {
        var design = new JsonObject
        {
            ["name"] = name,
            ["assets"] = new JsonObject
            {
                ["logo"] = new JsonObject { ["fileRef"] = "project-file:" + Guid.NewGuid(), ["file"] = "acmehr-logo.png" },
            },
        };

        BrandAssetCatalog.FromDocument(design).Entries.Should().ContainSingle().Which.Subject.Should().Be("product");
    }

    [Fact]
    public async Task Legacy_document_is_measured_and_classified_from_the_stored_files()
    {
        Guid darkPng = Guid.NewGuid(), noFillSvg = Guid.NewGuid(), violetSvg = Guid.NewGuid(), company = Guid.NewGuid();
        JsonObject design = Legacy(("logo", darkPng), ("v", noFillSvg), ("v", violetSvg), ("v", company));
        var files = new[]
        {
            File(darkPng, "acme-app-logo.png", "image/png"),
            File(noFillSvg, "acme-icon.svg", "image/svg+xml"),
            File(violetSvg, "acme-icon-light.svg", "image/svg+xml"),
            File(company, "globex-group-logo.png", "image/png"),
        };
        var bytes = new Dictionary<Guid, byte[]>
        {
            [darkPng] = Png(10, 10, 10),
            [noFillSvg] = Svg("<path d=\"M0 0h10v10H0z\"/>"),
            [violetSvg] = Svg("<path fill=\"#7e6fff\" d=\"M0 0h10v10H0z\"/>"),
            [company] = Png(0, 80, 200),
        };

        BrandAssetCatalog catalog = await BrandAssetCatalog.BuildAsync(
            design, "Acme App", Storage(files, bytes).Object, ProjectId, CancellationToken.None);

        catalog.Entries.Should().HaveCount(4);
        BrandAssetEntry png = catalog.ForFile(darkPng)!;
        png.Subject.Should().Be("product");
        png.Tone.Should().Be("dark");
        png.ReadableOn.Should().Equal("light");
        png.Source.Should().Be("measured");

        BrandAssetEntry svg = catalog.ForFile(noFillSvg)!;
        svg.Path.Should().Be("assets.logo.variants[0]");
        svg.Tone.Should().Be("dark");
        svg.BestOn.Should().Be("light");

        BrandAssetEntry violet = catalog.ForFile(violetSvg)!;
        violet.Tone.Should().NotBe("dark");
        violet.ReadableOn.Should().Contain("dark");

        BrandAssetEntry publisher = catalog.ForFile(company)!;
        publisher.Subject.Should().Be("publisher");
        publisher.UseFor.Should().Contain("not the product logo").And.Contain("by <company>");
    }

    [Fact]
    public async Task Storage_failure_gives_entries_without_a_tone_and_never_throws()
    {
        Guid id = Guid.NewGuid();
        JsonObject design = Legacy(("logo", id));
        var files = new[] { File(id, "acme-app-logo.png", "image/png") };

        BrandAssetCatalog failing = await BrandAssetCatalog.BuildAsync(
            design, "Acme App", Storage(files, [], failDownload: true).Object, ProjectId, CancellationToken.None);
        failing.Entries.Should().ContainSingle().Which.Tone.Should().BeNull();

        BrandAssetCatalog noStorage = await BrandAssetCatalog.BuildAsync(design, "Acme App", null, ProjectId, CancellationToken.None);
        noStorage.Entries.Should().ContainSingle().Which.Tone.Should().BeNull();

        var broken = new Mock<IProjectFileWorkspace>();
        broken.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        BrandAssetCatalog listFails = await BrandAssetCatalog.BuildAsync(design, "Acme App", broken.Object, ProjectId, CancellationToken.None);
        listFails.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Oversized_and_non_image_files_are_not_read_and_the_catalog_is_bounded_to_twelve()
    {
        var assets = new JsonObject();
        var files = new List<ProjectWorkspaceFile>();
        for (int i = 0; i < 20; i++)
        {
            Guid id = Guid.NewGuid();
            assets["slot" + i] = new JsonObject { ["fileRef"] = "project-file:" + id };
            files.Add(File(id, "f" + i + ".jpg", "image/jpeg"));
        }

        Guid big = Guid.NewGuid();
        files[0] = File(big, "big.png", "image/png", BrandAssetCatalog.MaxProfileBytes + 1);
        assets["slot0"] = new JsonObject { ["fileRef"] = "project-file:" + big };
        Mock<IProjectFileWorkspace> workspace = Storage(files, []);

        BrandAssetCatalog catalog = await BrandAssetCatalog.BuildAsync(
            new JsonObject { ["assets"] = assets }, null, workspace.Object, ProjectId, CancellationToken.None);

        catalog.Entries.Count.Should().Be(BrandAssetCatalog.MaxEntries);
        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
