using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The project's "analyse videos when they are uploaded" switch must cover every way a video arrives, not only
/// the upload routes (<see cref="AnalyzeOnUploadTests"/>). A video from a synced folder or a repository sync, and
/// one whose upload request was lost, gets its derivatives from the coverage sweep or from the editor's own
/// request: both ask the same one place (<see cref="UploadDerivativeRequest.AnalyzeForWorkflowsAsync"/>).
/// </summary>
public sealed class AnalyzeOnArrivalTests
{
    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Member;
    }

    private sealed class FixedOptions(bool enabled) : IOptionsMonitor<UploadAnalysisOptions>
    {
        public UploadAnalysisOptions CurrentValue { get; } = new() { Enabled = enabled };
        public UploadAnalysisOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<UploadAnalysisOptions, string?> listener) => null;
    }

    private sealed class ThrowingSetting : IUploadAnalysisSetting
    {
        public Task<bool> IsOnAsync(Guid projectId, CancellationToken ct) => throw new InvalidOperationException("database down");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(bool analyzeOnUpload)
        {
            Db = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Arrival",
                OwnerId = UserId,
                OrganizationId = TestTenancy.OrgOf(UserId),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                AnalyzeOnUpload = analyzeOnUpload
            };
            Db.Projects.Add(Project);
            Db.SaveChanges();

            Storage
                .Setup(s => s.GetObjectETagAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("\"etag-1\"");
            Publisher
                .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
                .Callback<MediaDerivativesRequested, CancellationToken>((message, _) => Published.Add(message))
                .Returns(Task.CompletedTask);
        }

        public Guid UserId { get; } = Guid.NewGuid();

        public InferenceApiDbContext Db { get; }

        public Project Project { get; }

        public Mock<IFileStorageService> Storage { get; } = new();

        public Mock<IPublishEndpoint> Publisher { get; } = new();

        public List<MediaDerivativesRequested> Published { get; } = [];

        public UploadAnalysisSetting Setting(bool globalEnabled = true) => new(Db, new FixedOptions(globalEnabled));

        public ProjectFile AddFile(string fileName, string mimeType)
        {
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = fileName,
                StorageKey = $"projects/{Project.Id:D}/userFiles/{Guid.NewGuid():N}/{fileName}",
                StorageBucket = "reelforge",
                Category = "userFiles",
                MimeType = mimeType,
                SizeBytes = 1024,
                UploadedAt = DateTime.UtcNow,
                CsamStatus = CsamScanStatus.Clean
            };
            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        public MediaDerivativeCoverageService Sweep(IUploadAnalysisSetting? setting) => new(
            Db, Storage.Object, Publisher.Object, new TenantContext(new HttpContextAccessor()),
            NullLogger<MediaDerivativeCoverageService>.Instance, setting);

        public MediaController Editor(IUploadAnalysisSetting? setting)
        {
            MediaController controller = new(
                Db, TestTenancy.Access(Db, new FakeCurrentUser(UserId)), Storage.Object, new Mock<IAmazonS3>().Object,
                Publisher.Object, NullLogger<MediaController>.Instance, uploadAnalysis: setting);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        public void Dispose() => Db.Dispose();
    }

    // --- the coverage sweep (synced folders, repository sync, lost requests) ------------------------

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task The_sweep_asks_for_the_analysis_of_a_video_when_the_project_and_the_operator_say_so(
        bool projectOn, bool operatorOn, bool expected)
    {
        using Fixture fx = new(projectOn);
        ProjectFile video = fx.AddFile("synced.mp4", "video/mp4");

        MediaDerivativeSweepResult sweep = await fx.Sweep(fx.Setting(operatorOn))
            .SweepOnceAsync(MediaDerivativeRequests.RequestedBySweep, 50, CancellationToken.None);

        sweep.Requested.Should().Be(1);
        MediaDerivativesRequested request = fx.Published.Should().ContainSingle().Subject;
        request.SourceProjectFileId.Should().Be(video.Id);
        request.AnalyzeForWorkflows.Should().Be(expected);
        request.RequestedBy.Should().Be(MediaDerivativeRequests.RequestedBySweep);
        request.Kinds.Should().Equal(MediaDerivativeRequests.DefaultKinds);
    }

    [Fact]
    public async Task A_sweep_with_no_setting_or_one_that_fails_still_publishes_and_leaves_the_analysis_out()
    {
        using Fixture fx = new(analyzeOnUpload: true);
        fx.AddFile("synced.mp4", "video/mp4");

        (await fx.Sweep(new ThrowingSetting()).SweepOnceAsync(MediaDerivativeRequests.RequestedBySweep, 50, CancellationToken.None))
            .Requested.Should().Be(1, "a failed read of the switch never costs the file its derivatives");
        fx.Published.Should().ContainSingle().Which.AnalyzeForWorkflows.Should().BeFalse();

        using Fixture plain = new(analyzeOnUpload: true);
        plain.AddFile("synced.mp4", "video/mp4");
        await plain.Sweep(setting: null).SweepOnceAsync(MediaDerivativeRequests.RequestedBySweep, 50, CancellationToken.None);
        plain.Published.Should().ContainSingle().Which.AnalyzeForWorkflows.Should().BeFalse();
    }

    // --- the editor's own request -------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, "video/mp4", true)]
    [InlineData(false, true, "video/mp4", false)]
    [InlineData(true, false, "video/mp4", false)]
    [InlineData(true, true, "audio/wav", false)]
    public async Task The_editors_request_asks_for_the_analysis_of_a_video_when_the_project_and_the_operator_say_so(
        bool projectOn, bool operatorOn, string mimeType, bool expected)
    {
        using Fixture fx = new(projectOn);
        ProjectFile file = fx.AddFile("clip", mimeType);

        IActionResult result = await fx.Editor(fx.Setting(operatorOn)).RequestDerivatives(
            fx.Project.Id, new RequestMediaDerivativesRequest($"file:{file.Id:D}"), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        MediaDerivativesRequested request = fx.Published.Should().ContainSingle().Subject;
        request.SourceProjectFileId.Should().Be(file.Id);
        request.AnalyzeForWorkflows.Should().Be(expected);
    }

    [Fact]
    public async Task An_editors_request_with_no_setting_or_one_that_fails_is_published_as_before()
    {
        using Fixture fx = new(analyzeOnUpload: true);
        ProjectFile file = fx.AddFile("clip.mp4", "video/mp4");
        RequestMediaDerivativesRequest body = new($"file:{file.Id:D}");

        (await fx.Editor(new ThrowingSetting()).RequestDerivatives(fx.Project.Id, body, CancellationToken.None))
            .Should().BeOfType<AcceptedResult>();
        (await fx.Editor(setting: null).RequestDerivatives(fx.Project.Id, body, CancellationToken.None))
            .Should().BeOfType<AcceptedResult>();

        fx.Published.Should().HaveCount(2).And.OnlyContain(m => !m.AnalyzeForWorkflows);
    }
}
