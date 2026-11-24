using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The project's "analyse videos when they are uploaded" switch: the read DTO and the update route (tenancy and
/// role), and the one upload seam that turns it, with the global switch, into
/// <see cref="MediaDerivativesRequested.AnalyzeForWorkflows"/>. SQLite in memory with the real tenant filter, so
/// another organization's project really is invisible.
/// </summary>
public sealed class AnalyzeOnUploadTests : IDisposable
{
    private static readonly Guid OrgA = Guid.Parse("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("22222222-0000-0000-0000-0000000000b2");
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;
    private readonly TestTenancy.MutableTenant _tenant = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Mock<IPublishEndpoint> _publisher = new();
    private readonly Mock<IFileStorageService> _storage = new();
    private readonly List<MediaDerivativesRequested> _published = [];

    public AnalyzeOnUploadTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options, _tenant);
        _db.Database.EnsureCreated();

        _publisher
            .Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativesRequested, CancellationToken>((message, _) => _published.Add(message))
            .Returns(Task.CompletedTask);
        _storage
            .Setup(s => s.GetObjectETagAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("etag-1");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // --- harness ----------------------------------------------------------------------------------

    private sealed class FakeCurrentUser(Guid userId, Guid organizationId, OrganizationRole role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
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

    /// <summary>
    /// A caller who may see the organization's projects and change none of them. No shipped role is view-only,
    /// so this stands in for one: it answers View like the real service and Forbidden for anything stronger.
    /// </summary>
    private sealed class ViewOnlyAccess(IProjectAccessService real) : IProjectAccessService
    {
        public List<ProjectPermission> Asked { get; } = [];

        public async Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct)
        {
            Asked.Add(permission);
            return Narrow(await real.GetAsync(projectId, ProjectPermission.View, ct), permission);
        }

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission)
        {
            Asked.Add(permission);
            return Narrow(real.Get(projectId, ProjectPermission.View), permission);
        }

        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) =>
            permission == ProjectPermission.View ? real.AuthorizeOrganization(permission) : ProjectAccessOutcome.Forbidden;

        private static ProjectAccessResult Narrow(ProjectAccessResult seen, ProjectPermission permission) =>
            !seen.IsFound || permission == ProjectPermission.View
                ? seen
                : new ProjectAccessResult(ProjectAccessOutcome.Forbidden, null);
    }

    private ProjectsController Act(Guid org, OrganizationRole role = OrganizationRole.Owner)
    {
        _tenant.ActAsOrganization(org);
        _db.ChangeTracker.Clear();
        FakeCurrentUser user = new(_userId, org, role);
        return new ProjectsController(_db, user, new ProjectAccessService(_db, user),
            new WorkflowTemplateProvisioningService(_db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()));
    }

    private Project SeedProject(Guid org, bool analyzeOnUpload = false)
    {
        Project p = new()
        {
            Id = Guid.NewGuid(),
            Name = "p",
            OwnerId = _userId,
            OrganizationId = org,
            CreatedAt = Old,
            UpdatedAt = Old,
            AnalyzeOnUpload = analyzeOnUpload
        };
        _db.Projects.Add(p);
        _db.SaveChanges();
        return p;
    }

    private static ProjectFile FileOf(Project p, string mimeType) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = p.Id,
        OriginalFileName = "clip",
        StorageKey = $"projects/{p.Id}/userFiles/{Guid.NewGuid():N}",
        MimeType = mimeType,
        SummaryStatus = SummaryStatus.Done,
        UploadedAt = Old
    };

    private bool Stored(Guid projectId)
    {
        _db.ChangeTracker.Clear();
        return _db.Projects.IgnoreQueryFilters().AsNoTracking().Single(p => p.Id == projectId).AnalyzeOnUpload;
    }

    private UploadAnalysisSetting Setting(bool globalEnabled = true) => new(_db, new FixedOptions(globalEnabled));

    private async Task<MediaDerivativesRequested> PublishAsync(Project p, string mimeType, IUploadAnalysisSetting? setting)
    {
        _published.Clear();
        await UploadDerivativeRequest.PublishAsync(
            _publisher.Object, _storage.Object, setting, FileOf(p, mimeType), mimeType, NullLogger.Instance, CancellationToken.None);
        return _published.Should().ContainSingle().Subject;
    }

    // --- the read DTO and the update route --------------------------------------------------------

    [Fact]
    public async Task A_new_project_reads_as_off_and_the_update_route_turns_it_on_and_off()
    {
        Project p = SeedProject(OrgA);
        ProjectsController projects = Act(OrgA);

        ProjectResponse read = (ProjectResponse)((OkObjectResult)(await projects.Get(p.Id, default)).Result!).Value!;
        read.AnalyzeOnUpload.Should().BeFalse();

        ProjectResponse on = (ProjectResponse)((OkObjectResult)
            (await projects.Update(p.Id, new UpdateProjectRequest("p", null, AnalyzeOnUpload: true), default)).Result!).Value!;
        on.AnalyzeOnUpload.Should().BeTrue();
        Stored(p.Id).Should().BeTrue();

        projects = Act(OrgA);
        List<ProjectResponse> listed = (List<ProjectResponse>)((OkObjectResult)(await projects.List(null, null, false, default)).Result!).Value!;
        listed.Single(x => x.Id == p.Id).AnalyzeOnUpload.Should().BeTrue();

        ProjectResponse off = (ProjectResponse)((OkObjectResult)
            (await projects.Update(p.Id, new UpdateProjectRequest("p", null, AnalyzeOnUpload: false), default)).Result!).Value!;
        off.AnalyzeOnUpload.Should().BeFalse();
        Stored(p.Id).Should().BeFalse();
    }

    [Fact]
    public async Task An_update_that_leaves_the_field_out_keeps_the_stored_choice()
    {
        Project p = SeedProject(OrgA, analyzeOnUpload: true);

        ProjectResponse renamed = (ProjectResponse)((OkObjectResult)
            (await Act(OrgA).Update(p.Id, new UpdateProjectRequest("renamed", "d"), default)).Result!).Value!;

        renamed.Name.Should().Be("renamed");
        renamed.AnalyzeOnUpload.Should().BeTrue();
        Stored(p.Id).Should().BeTrue();
    }

    [Fact]
    public void The_update_body_binds_the_camel_case_field_and_reads_a_missing_one_as_unchanged()
    {
        System.Text.Json.JsonSerializerOptions web = new(System.Text.Json.JsonSerializerDefaults.Web);

        System.Text.Json.JsonSerializer.Deserialize<UpdateProjectRequest>("""{"name":"p","description":null,"analyzeOnUpload":true}""", web)!
            .AnalyzeOnUpload.Should().BeTrue();
        System.Text.Json.JsonSerializer.Deserialize<UpdateProjectRequest>("""{"name":"p","description":null}""", web)!
            .AnalyzeOnUpload.Should().BeNull();
        System.Text.Json.JsonSerializer.Serialize(new ProjectResponse(Guid.Empty, "p", null, "Active", Old, Old, AnalyzeOnUpload: true), web)
            .Should().Contain("\"analyzeOnUpload\":true");
    }

    [Fact]
    public async Task Another_organizations_project_is_not_found_and_stays_unchanged()
    {
        Project other = SeedProject(OrgB);
        ProjectsController projects = Act(OrgA);

        (await projects.Get(other.Id, default)).Result.Should().BeOfType<NotFoundResult>();
        (await projects.Update(other.Id, new UpdateProjectRequest("p", null, AnalyzeOnUpload: true), default)).Result
            .Should().BeOfType<NotFoundResult>();

        Stored(other.Id).Should().BeFalse();
    }

    [Fact]
    public async Task A_role_without_edit_rights_cannot_change_it_but_a_member_can()
    {
        Project p = SeedProject(OrgA);

        IActionResult? denied = (await Act(OrgA, OrganizationRole.Billing)
            .Update(p.Id, new UpdateProjectRequest("p", null, AnalyzeOnUpload: true), default)).Result;
        (denied is ForbidResult || denied is StatusCodeResult { StatusCode: 403 } || denied is ObjectResult { StatusCode: 403 })
            .Should().BeTrue($"got {denied?.GetType().Name}");
        Stored(p.Id).Should().BeFalse();

        (await Act(OrgA, OrganizationRole.Member)
            .Update(p.Id, new UpdateProjectRequest("p", null, AnalyzeOnUpload: true), default)).Result
            .Should().BeOfType<OkObjectResult>();
        Stored(p.Id).Should().BeTrue();
    }

    [Fact]
    public async Task A_view_only_caller_can_read_the_choice_and_cannot_change_it()
    {
        Project p = SeedProject(OrgA, analyzeOnUpload: true);
        _tenant.ActAsOrganization(OrgA);
        _db.ChangeTracker.Clear();
        FakeCurrentUser user = new(_userId, OrgA, OrganizationRole.Member);
        ViewOnlyAccess access = new(new ProjectAccessService(_db, user));
        ProjectsController projects = new(_db, user, access,
            new WorkflowTemplateProvisioningService(_db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()));

        ProjectResponse read = (ProjectResponse)((OkObjectResult)(await projects.Get(p.Id, default)).Result!).Value!;
        read.AnalyzeOnUpload.Should().BeTrue();

        access.Asked.Clear();
        (await projects.Update(p.Id, new UpdateProjectRequest("p", null, AnalyzeOnUpload: false), default)).Result
            .Should().BeOfType<ForbidResult>();
        access.Asked.Should().Equal(ProjectPermission.Edit);
        Stored(p.Id).Should().BeTrue();
    }

    // --- the upload seam --------------------------------------------------------------------------

    [Fact]
    public async Task A_video_of_a_project_that_opted_in_asks_for_the_analysis()
    {
        Project p = SeedProject(OrgA, analyzeOnUpload: true);
        _tenant.ActAsOrganization(null);

        MediaDerivativesRequested request = await PublishAsync(p, "video/mp4", Setting());

        request.AnalyzeForWorkflows.Should().BeTrue();
        request.RequestedBy.Should().Be(MediaDerivativeRequests.RequestedByUpload);
        request.Kinds.Should().Equal(MediaDerivativeRequests.DefaultKinds);
        request.SourceEtag.Should().Be("etag-1");
    }

    [Fact]
    public async Task A_video_of_a_project_that_did_not_opt_in_is_published_as_before()
    {
        Project p = SeedProject(OrgA);

        MediaDerivativesRequested with = await PublishAsync(p, "video/mp4", Setting());
        MediaDerivativesRequested without = await PublishAsync(p, "video/mp4", setting: null);

        with.AnalyzeForWorkflows.Should().BeFalse();
        without.AnalyzeForWorkflows.Should().BeFalse();
        MediaDerivativesRequested sameFile = with with
        {
            SourceRef = without.SourceRef,
            SourceProjectFileId = without.SourceProjectFileId,
            SourceStorageKey = without.SourceStorageKey,
            RequestedAt = without.RequestedAt,
            Kinds = without.Kinds
        };
        sameFile.Should().Be(without);
    }

    [Theory]
    [InlineData("audio/wav")]
    [InlineData("audio/mpeg")]
    public async Task An_audio_file_never_asks_for_the_analysis(string mimeType)
    {
        Project p = SeedProject(OrgA, analyzeOnUpload: true);

        (await PublishAsync(p, mimeType, Setting())).AnalyzeForWorkflows.Should().BeFalse();
    }

    [Fact]
    public async Task The_global_switch_off_wins_over_the_projects_choice()
    {
        Project p = SeedProject(OrgA, analyzeOnUpload: true);

        (await PublishAsync(p, "video/mp4", Setting(globalEnabled: false))).AnalyzeForWorkflows.Should().BeFalse();
        (await PublishAsync(p, "video/mp4", Setting(globalEnabled: true))).AnalyzeForWorkflows.Should().BeTrue();
    }

    [Fact]
    public async Task The_setting_is_read_for_the_files_own_project_whatever_the_tenant()
    {
        Project a = SeedProject(OrgA, analyzeOnUpload: true);
        Project b = SeedProject(OrgB);
        // A background path (sync, unpack) has no tenant; an upload route acts as the caller's organization.
        _tenant.ActAsOrganization(OrgB);
        UploadAnalysisSetting setting = Setting();

        (await PublishAsync(a, "video/mp4", setting)).AnalyzeForWorkflows.Should().BeTrue();
        (await PublishAsync(b, "video/mp4", setting)).AnalyzeForWorkflows.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_read_publishes_the_request_without_the_analysis()
    {
        Project p = SeedProject(OrgA, analyzeOnUpload: true);
        UploadAnalysisSetting setting = Setting();
        _connection.Close();

        (await setting.IsOnAsync(p.Id, CancellationToken.None)).Should().BeFalse();
        _published.Clear();
        await UploadDerivativeRequest.PublishAsync(
            _publisher.Object, _storage.Object, setting, FileOf(p, "video/mp4"), "video/mp4", NullLogger.Instance, CancellationToken.None);
        _published.Should().ContainSingle().Which.AnalyzeForWorkflows.Should().BeFalse();
        _connection.Open();
    }

    [Fact]
    public async Task A_setting_that_throws_never_fails_the_upload()
    {
        Project p = SeedProject(OrgA, analyzeOnUpload: true);
        _published.Clear();

        Func<Task> act = () => UploadDerivativeRequest.PublishAsync(
            _publisher.Object, _storage.Object, new ThrowingSetting(), FileOf(p, "video/mp4"), "video/mp4", NullLogger.Instance, CancellationToken.None);

        await act.Should().NotThrowAsync();
        // The derivatives are still asked for; only the analysis is left out.
        _published.Should().ContainSingle().Which.AnalyzeForWorkflows.Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_read_of_the_setting_is_not_swallowed()
    {
        Project p = SeedProject(OrgA, analyzeOnUpload: true);
        _published.Clear();
        using CancellationTokenSource cancelled = new();
        Mock<IUploadAnalysisSetting> setting = new();
        setting
            .Setup(s => s.IsOnAsync(p.Id, It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>((_, token) =>
            {
                cancelled.Cancel();
                return Task.FromCanceled<bool>(token);
            });

        Func<Task> act = () => UploadDerivativeRequest.PublishAsync(
            _publisher.Object, _storage.Object, setting.Object, FileOf(p, "video/mp4"), "video/mp4", NullLogger.Instance, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task One_scoped_setting_reads_a_project_once_however_many_files_it_publishes()
    {
        Project p = SeedProject(OrgA, analyzeOnUpload: true);
        UploadAnalysisSetting setting = Setting();

        (await PublishAsync(p, "video/mp4", setting)).AnalyzeForWorkflows.Should().BeTrue();
        // The stored value changes behind the instance: the answer it already gave for this project stands.
        _db.Projects.IgnoreQueryFilters().Single(x => x.Id == p.Id).AnalyzeOnUpload = false;
        _db.SaveChanges();
        (await PublishAsync(p, "video/quicktime", setting)).AnalyzeForWorkflows.Should().BeTrue();

        (await PublishAsync(p, "video/mp4", Setting())).AnalyzeForWorkflows.Should().BeFalse();
    }

    [Fact]
    public async Task A_project_that_does_not_exist_reads_as_off()
    {
        (await Setting().IsOnAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task The_post_add_path_every_upload_shares_carries_the_projects_choice()
    {
        Project p = SeedProject(OrgA, analyzeOnUpload: true);
        _tenant.ActAsOrganization(OrgA);
        ProjectFile video = FileOf(p, "video/mp4");
        ProjectFile audio = FileOf(p, "audio/wav");

        ProjectFileAddedEffects effects = new(
            _publisher.Object, new ChannelBackgroundTaskQueue<FileSummarizationTask>(10), _storage.Object, uploadAnalysis: Setting());
        await effects.RunAsync(video, _userId, CancellationToken.None);
        await effects.RunAsync(audio, _userId, CancellationToken.None);

        _published.Should().HaveCount(2);
        _published.Single(m => m.SourceProjectFileId == video.Id).AnalyzeForWorkflows.Should().BeTrue();
        _published.Single(m => m.SourceProjectFileId == audio.Id).AnalyzeForWorkflows.Should().BeFalse();
    }

    [Fact]
    public void The_app_registers_the_setting_and_the_switch_defaults_to_on()
    {
        new UploadAnalysisOptions().Enabled.Should().BeTrue();
        UploadAnalysisOptions.SectionName.Should().Be("MediaAnalysis:OnUpload");

        IConfiguration off = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MediaAnalysis:OnUpload:Enabled"] = "false" })
            .Build();
        ServiceCollection services = new();
        services.Configure<UploadAnalysisOptions>(off.GetSection(UploadAnalysisOptions.SectionName));
        services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<UploadAnalysisOptions>>().CurrentValue.Enabled.Should().BeFalse();
    }
}
