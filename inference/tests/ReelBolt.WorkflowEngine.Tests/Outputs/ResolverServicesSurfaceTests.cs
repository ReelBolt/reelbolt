#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using Xunit;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>Services that turn an id or a key into a project asset: an output the project keeps back does not resolve.</summary>
public sealed class ResolverServicesSurfaceTests
{
    public enum Which { EarlierFile, LaterFile, Upload }

    private static ProjectFile FileOf(SurfaceWorld w, Which which) => which switch
    {
        Which.EarlierFile => w.EarlierFile,
        Which.LaterFile => w.LaterFile,
        _ => w.Upload,
    };

    private static SurfaceWorld TextWorld()
    {
        SurfaceWorld w = new();
        foreach (ProjectFile f in new[] { w.EarlierFile, w.LaterFile, w.Upload })
        {
            f.MimeType = "text/plain";
            f.OriginalFileName = f.OriginalFileName.Replace(".mp4", ".txt");
        }
        w.Db.SaveChanges();
        return w;
    }

    private static async Task<BriefResolution> Brief(SurfaceWorld w, Which which, InferenceApiDbContext? db = null)
    {
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("a brief")));
        return await new BriefFileResolver(db ?? w.Db, storage.Object).ResolveAsync(w.Project.Id, FileOf(w, which).Id, null, CancellationToken.None);
    }

    [Fact]
    public async Task A_brief_file_that_is_an_earlier_output_is_refused_like_a_missing_file()
    {
        SurfaceWorld w = TextWorld().In(SurfaceState.Held);
        BriefResolution r = await Brief(w, Which.EarlierFile);
        r.Text.Should().BeNull();
        r.Error.Should().Be("briefFileId must be a file of this project; none was found.");
    }

    [Theory]
    [InlineData(Which.LaterFile)]
    [InlineData(Which.Upload)]
    public async Task A_brief_file_that_is_a_later_output_or_an_upload_is_read_while_earlier_outputs_are_kept(Which which)
    {
        SurfaceWorld w = TextWorld().In(SurfaceState.Held);
        (await Brief(w, which)).Text.Should().Be("a brief");
    }

    [Theory]
    [InlineData(SurfaceState.Clear)]
    [InlineData(SurfaceState.ModerationOnly)]
    public async Task A_brief_file_is_read_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = TextWorld().In(state);
        (await Brief(w, Which.EarlierFile)).Text.Should().Be("a brief");
    }

    [Fact]
    public async Task A_brief_file_on_a_failed_read_is_refused()
    {
        SurfaceWorld w = TextWorld().In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = w.MarkerFailingDb();
        (await Brief(w, Which.LaterFile, failing)).Text.Should().BeNull();
    }

    // ---- seed assets -------------------------------------------------------------------------

    [Fact]
    public async Task An_earlier_output_does_not_resolve_as_a_seed_asset()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        SeedAssetResolver seed = await SeedAssetResolver.LoadAsync(w.Db, w.Project.Id, CancellationToken.None);
        seed.ResolveKey(w.EarlierFile.StorageKey).Should().BeNull();
        seed.ResolveIdentity(Source(file: w.EarlierFile.Id)).Should().BeNull();
        seed.ResolveIdentity(Source(result: w.EarlierResult.Id)).Should().BeNull();
    }

    [Fact]
    public async Task A_later_output_and_an_upload_resolve_while_earlier_outputs_are_kept()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        SeedAssetResolver seed = await SeedAssetResolver.LoadAsync(w.Db, w.Project.Id, CancellationToken.None);
        seed.ResolveKey(w.LaterFile.StorageKey).Should().NotBeNull();
        seed.ResolveKey(w.Upload.StorageKey).Should().NotBeNull();
        seed.ResolveIdentity(Source(file: w.LaterFile.Id)).Should().NotBeNull();
        seed.ResolveIdentity(Source(result: w.LaterResult.Id)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(SurfaceState.Clear)]
    [InlineData(SurfaceState.ModerationOnly)]
    public async Task The_earlier_output_resolves_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        SeedAssetResolver seed = await SeedAssetResolver.LoadAsync(w.Db, w.Project.Id, CancellationToken.None);
        seed.ResolveKey(w.EarlierFile.StorageKey).Should().NotBeNull();
        seed.ResolveIdentity(Source(file: w.EarlierFile.Id)).Should().NotBeNull();
        seed.ResolveIdentity(Source(result: w.EarlierResult.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task No_output_resolves_as_a_seed_asset_when_the_marker_cannot_be_read()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = w.MarkerFailingDb();
        SeedAssetResolver seed = await SeedAssetResolver.LoadAsync(failing, w.Project.Id, CancellationToken.None);
        seed.ResolveKey(w.LaterFile.StorageKey).Should().BeNull();
        seed.ResolveIdentity(Source(result: w.LaterResult.Id)).Should().BeNull();
    }

    private static TimelineSource Source(Guid? file = null, Guid? result = null) =>
        new(0, "projects/x/outputFiles/y.mp4", 1, 1, 1, 30, ProjectFileId: file, StepResultId: result);
}
