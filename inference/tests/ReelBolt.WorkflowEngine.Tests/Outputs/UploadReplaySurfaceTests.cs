#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using DirectUploadTicket = ReelBolt.Inference.Api.Services.Uploads.DirectUploadTicket;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>A completion that names a file row already recorded rewrites that row, so a row the project keeps back is not touched.</summary>
public sealed class UploadReplaySurfaceTests
{
    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public Mock<IDirectUploadService> Uploads { get; } = new();

        public async Task<IActionResult?> Complete(ProjectFile row, InferenceApiDbContext? db = null)
        {
            InferenceApiDbContext d = db ?? W.Db;
            string key = DirectUploadService.DeriveKey(W.Project.Id, row.Id, row.OriginalFileName, null)!;
            DirectUploadTicket ticket = new(
                "token", DirectUploadMode.SinglePut, W.Project.Id, row.Id, key, row.OriginalFileName, null, "video/mp4", 8,
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), null, null, 0, 0, new Dictionary<string, string>(), null, null, null, null);
            Uploads.Setup(u => u.Verify(It.IsAny<DirectUploadTicketEcho>(), It.IsAny<string>())).Returns(ticket);
            Uploads.Setup(u => u.CompleteAsync(It.IsAny<DirectUploadTicket>(), It.IsAny<DirectUploadCompletion>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(row);
            ProjectFilesController controller = new(
                d, W.User, W.Access(), Mock.Of<IFileStorageService>(), Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(),
                Mock.Of<IPublishEndpoint>(), new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()),
                directUploads: Uploads.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
            ActionResult<ProjectFileResponse> result = await controller.CompleteUpload(
                W.Project.Id,
                new CompleteDirectUploadRequest("token", key, row.Id, row.OriginalFileName, 8, ticket.ExpiresAtUnix, ContentType: "video/mp4"),
                CancellationToken.None);
            return result.Result;
        }
    }

    private static bool Refused(IActionResult? r) =>
        r is ObjectResult { StatusCode: 404, Value: { } v } && v.ToString()!.Contains("OUTPUT_UNAVAILABLE");

    [Fact]
    public async Task A_replayed_completion_of_an_earlier_output_row_is_refused_and_writes_nothing()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Complete(r.W.EarlierFile)).Should().BeTrue();
        r.Uploads.Verify(u => u.CompleteAsync(It.IsAny<DirectUploadTicket>(), It.IsAny<DirectUploadCompletion>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_replayed_completion_of_a_later_output_row_or_an_upload_is_not_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused(await r.Complete(r.W.LaterFile)).Should().BeFalse();
        Refused(await r.Complete(r.W.Upload)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task A_replayed_completion_of_the_earlier_row_is_not_refused_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Refused(await r.Complete(r.W.EarlierFile)).Should().BeFalse();
    }

    [Fact]
    public async Task A_replayed_completion_on_a_failed_read_is_refused()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        Refused(await r.Complete(r.W.LaterFile, failing)).Should().BeTrue();
    }
}
