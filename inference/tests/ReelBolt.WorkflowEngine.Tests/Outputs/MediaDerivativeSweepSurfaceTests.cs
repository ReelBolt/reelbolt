#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>The derivative sweep requests nothing for a source the project keeps back, and everything for a project that keeps nothing.</summary>
public sealed class MediaDerivativeSweepSurfaceTests
{
    private static async Task<List<string>> Sweep(SurfaceWorld w, InferenceApiDbContext? db = null)
    {
        List<string> requested = [];
        Mock<IPublishEndpoint> publish = new();
        publish.Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativesRequested, CancellationToken>((m, _) => requested.Add(m.SourceStorageKey))
            .Returns(Task.CompletedTask);
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.GetObjectETagAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("\"etag\"");
        MediaDerivativeCoverageService service = new(
            db ?? w.Db, storage.Object, publish.Object, new TenantContext(new HttpContextAccessor()), NullLogger<MediaDerivativeCoverageService>.Instance);
        await service.SweepOnceAsync("test", 20, CancellationToken.None);
        return requested;
    }

    private static string UniqueRenderKey(SurfaceWorld w, WorkflowStepResult result, string name)
    {
        // A render no file row names, so the sweep takes it from the step result.
        result.OutputStorageKey = $"projects/{w.Project.Id}/outputFiles/{name}.mp4";
        w.Db.SaveChanges();
        return result.OutputStorageKey;
    }

    [Fact]
    public async Task An_earlier_output_is_not_requested_and_a_later_output_or_an_upload_is()
    {
        SurfaceWorld w = new();
        string earlierRender = UniqueRenderKey(w, w.EarlierResult, "sweep-earlier");
        string laterRender = UniqueRenderKey(w, w.LaterResult, "sweep-later");
        w.In(SurfaceState.Held);
        List<string> requested = await Sweep(w);
        requested.Should().Contain([w.LaterFile.StorageKey, w.Upload.StorageKey, laterRender]);
        requested.Should().NotContain([w.EarlierFile.StorageKey, earlierRender]);
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Every_source_is_requested_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new();
        string earlierRender = UniqueRenderKey(w, w.EarlierResult, "sweep-earlier");
        w.In(state);
        List<string> requested = await Sweep(w);
        requested.Should().Contain([w.EarlierFile.StorageKey, w.Upload.StorageKey, earlierRender]);
    }

    [Fact]
    public async Task Nothing_is_requested_when_the_marker_cannot_be_read()
    {
        SurfaceWorld w = new();
        UniqueRenderKey(w, w.LaterResult, "sweep-later");
        w.In(SurfaceState.Held);
        using MarkerReadFailsContext failing = w.MarkerFailingDb();
        (await Sweep(w, failing)).Should().BeEmpty();
    }
}
