#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Moq;
using MediaDerivativeRequests = ReelBolt.Inference.Api.Controllers.Dto.MediaDerivativeRequests;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// d2 N6 / P2-LAZY-04: the browser uploads through the direct (presigned) path, and that path never
/// asked for derivatives — five uploaded videos had no proxy, no filmstrip and no probe until each was
/// opened in the editor. A committed media upload now publishes the same
/// <see cref="MediaDerivativesRequested"/> (<c>requestedBy:"upload"</c>) the multipart form upload does.
/// </summary>
/// <remarks>
/// <b>The request is made by the route's one post-add path, not by the upload service.</b>
/// <see cref="DirectUploadService.CompleteAsync"/> only commits the row; every caller then applies
/// <see cref="ReelBolt.Inference.Api.Services.Uploads.IProjectFileAddedEffects"/> — the single path the
/// form upload, the direct-upload completion and the assistant's upload tool share — and that path is
/// what asks for a media row's derivative set, through
/// <see cref="UploadDerivativeRequest"/>. These tests drive the completion the way a route does
/// (<see cref="DirectUploadHarness.CompleteThroughRouteAsync"/>), so they pin the whole path's outcome
/// rather than one mechanism inside it.
/// </remarks>
public class DirectUploadDerivativeRequestTests
{
    [Fact]
    public async Task A_completed_media_upload_requests_its_derivative_set_once()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 9, fileName: "clip.mp4", contentType: "video/mp4");
        h.Storage.Put(ticket.Key, 9);

        ProjectFile row = await h.CompleteThroughRouteAsync(ticket);

        MediaDerivativesRequested request = h.Published.Should().ContainSingle().Subject;
        request.ProjectId.Should().Be(h.Project.Id);
        request.SourceRef.Should().Be(new MediaSourceRef(MediaSourceKind.ProjectFile, row.Id));
        request.SourceProjectFileId.Should().Be(row.Id);
        request.SourceStorageKey.Should().Be(row.StorageKey);
        request.MimeType.Should().Be("video/mp4");
        request.RequestedBy.Should().Be("upload");
        request.Kinds.Should().Equal(MediaDerivativeRequests.DefaultKinds);
    }

    [Fact]
    public async Task An_audio_upload_requests_derivatives_too()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 9, fileName: "voice.wav", contentType: "audio/wav");
        h.Storage.Put(ticket.Key, 9);

        await h.CompleteThroughRouteAsync(ticket);

        h.Published.Should().ContainSingle().Which.MimeType.Should().Be("audio/wav");
    }

    [Fact]
    public async Task A_text_upload_requests_nothing()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 12, fileName: "notes.txt", contentType: "text/plain");
        h.Storage.Put(ticket.Key, 12);

        await h.CompleteThroughRouteAsync(ticket);

        h.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retried_completion_does_not_request_a_second_time()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 9, fileName: "clip.mp4", contentType: "video/mp4");
        h.Storage.Put(ticket.Key, 9);

        ProjectFile first = await h.CompleteThroughRouteAsync(ticket);
        ProjectFile retried = await h.CompleteThroughRouteAsync(ticket);

        // The retry answers the row the first call wrote, and the route's "already recorded" guard keeps
        // it from applying the post-add effects — and so from asking for the set — a second time.
        retried.Id.Should().Be(first.Id);
        h.Db.ProjectFiles.Should().ContainSingle();
        h.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task A_broker_failure_never_fails_an_upload_whose_row_is_committed()
    {
        DirectUploadHarness h = new();
        h.Publisher
            .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker down"));
        DirectUploadTicket ticket = await h.Create(sizeBytes: 9, fileName: "clip.mp4", contentType: "video/mp4");
        h.Storage.Put(ticket.Key, 9);

        ProjectFile row = await h.CompleteThroughRouteAsync(ticket);

        row.Id.Should().Be(ticket.FileId);
        h.Db.ProjectFiles.Should().ContainSingle();
    }
}
