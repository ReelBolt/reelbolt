using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// docs/csam.md: no derivative, preview or model call is made from a file before its CSAM verdict has settled.
/// A row a writer did not scan itself (cloud and GitHub sync, an assistant copy) is requested by the sweeper
/// after the sweeper's own verdict, and never when the verdict is a quarantine.
/// </summary>
public class CsamDerivativeOrderTests
{
    [Theory]
    [InlineData(CsamScanStatus.Pending, false)]
    [InlineData(CsamScanStatus.Scanning, false)]
    [InlineData(CsamScanStatus.Quarantined, false)]
    [InlineData(CsamScanStatus.Clean, true)]
    [InlineData(CsamScanStatus.NotScanned, true)]
    public async Task The_gate_allows_a_file_only_once_its_verdict_has_settled_and_it_is_not_held(CsamScanStatus status, bool allowed)
    {
        Guid projectId = Guid.NewGuid();
        string database = $"csam-derivative-gate-{Guid.NewGuid():N}";
        ProjectFile file = Row(projectId, status, "video/mp4");
        using (InferenceApiDbContext seed = CsamTestSupport.NewDb(database))
        {
            seed.ProjectFiles.Add(file);
            await seed.SaveChangesAsync();
        }

        CsamScanGate.AllowsDerivatives(file).Should().Be(allowed);
        using InferenceApiDbContext db = CsamTestSupport.NewDb(database);
        (await CsamScanGate.AllowsDerivativesAsync(db, projectId, file.Id, CancellationToken.None)).Should().Be(allowed);
    }

    [Fact]
    public async Task A_failed_quarantine_move_that_still_names_the_quarantine_key_is_not_allowed()
    {
        Guid projectId = Guid.NewGuid();
        string database = $"csam-derivative-gate-key-{Guid.NewGuid():N}";
        ProjectFile file = Row(projectId, CsamScanStatus.NotScanned, "video/mp4");
        file.CsamQuarantineKey = $"projects/{projectId}/quarantine/{file.Id}/x.mp4";
        using (InferenceApiDbContext seed = CsamTestSupport.NewDb(database))
        {
            seed.ProjectFiles.Add(file);
            await seed.SaveChangesAsync();
        }

        using InferenceApiDbContext db = CsamTestSupport.NewDb(database);
        (await CsamScanGate.AllowsDerivativesAsync(db, projectId, file.Id, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_source_that_is_not_a_project_file_is_not_this_gates_business()
    {
        using InferenceApiDbContext db = CsamTestSupport.NewDb($"csam-derivative-gate-none-{Guid.NewGuid():N}");
        (await CsamScanGate.AllowsDerivativesAsync(db, Guid.NewGuid(), null, CancellationToken.None)).Should().BeTrue();
        (await CsamScanGate.AllowsDerivativesAsync(db, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task The_sweeper_requests_the_derivative_set_after_a_clean_verdict_and_not_after_a_quarantine()
    {
        Fixture f = await Fixture.NewAsync();
        ProjectFile clean = f.AddPending("clean.mp4", "video/mp4");
        ProjectFile hit = f.AddPending("hit.mp4", "video/mp4");
        ProjectFile image = f.AddPending("pic.jpg", "image/jpeg");
        f.Matcher.Verdicts[hit.Id] = CsamScanStatus.Quarantined;
        await f.SaveAsync();

        (await f.Sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(3);

        f.Matcher.Scanned.Should().Contain([clean.Id, hit.Id, image.Id]);
        MediaDerivativesRequested sent = f.Published.Should().ContainSingle().Subject;
        sent.SourceRef.SourceId.Should().Be(clean.Id);
        sent.RequestedBy.Should().Be("upload");
        f.PublishedAfterVerdict.Should().BeTrue("the request follows the scan, never precedes it");
    }

    [Fact]
    public async Task A_disabled_control_settles_pending_media_and_the_sweeper_then_requests_its_derivatives()
    {
        Fixture f = await Fixture.NewAsync(enabled: false);
        ProjectFile video = f.AddPending("disabled.mp4", "video/mp4");
        await f.SaveAsync();

        (await f.Sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        f.Matcher.Scanned.Should().BeEmpty("a disabled control never scans");
        f.Published.Should().ContainSingle().Which.SourceRef.SourceId.Should().Be(video.Id);
    }

    [Fact]
    public async Task A_requested_project_scan_with_the_control_off_settles_its_rows_at_once_and_requests_derivatives()
    {
        Fixture f = await Fixture.NewAsync(enabled: false);
        ProjectFile video = f.AddPending("fresh.mp4", "video/mp4");
        video.UploadedAt = DateTime.UtcNow;
        await f.SaveAsync();

        (await f.Sweeper.ScanProjectAsync(f.ProjectId, CancellationToken.None)).Should().Be(1);

        f.Published.Should().ContainSingle().Which.SourceRef.SourceId.Should().Be(video.Id);
    }

    [Theory]
    [InlineData(CsamScanStatus.Pending, 0)]
    [InlineData(CsamScanStatus.Clean, 1)]
    public async Task A_still_is_requested_only_for_a_file_whose_verdict_has_settled(CsamScanStatus status, int expected)
    {
        Guid projectId = Guid.NewGuid();
        ProjectFile file = Row(projectId, status, "video/mp4");
        using InferenceApiDbContext db = CsamTestSupport.NewDb($"csam-derivative-stills-{Guid.NewGuid():N}");
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();
        Mock<IPublishEndpoint> publish = new();
        Mock<IFileStorageService> storage = new();
        ReelBolt.Inference.Api.Services.Outputs.OutputDescriber describer = new(
            db, storage.Object,
            new ReelBolt.Inference.Api.Services.Outputs.OutputLinkService(
                ReelBolt.Inference.Api.Services.Assistant.ConfirmationTokenService.CreateEphemeral(),
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System),
            publish.Object);

        await describer.RequestStillsAsync(
            projectId,
            new ReelBolt.Inference.Api.Services.Outputs.OutputSource(
                file.Id, "file", file.OriginalFileName, "video/mp4", 4, file.StorageKey,
                new MediaSourceRef(MediaSourceKind.ProjectFile, file.Id), file.Id),
            [1000], CancellationToken.None);

        publish.Verify(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()), Times.Exactly(expected));
    }

    private static ProjectFile Row(Guid projectId, CsamScanStatus status, string mime)
    {
        Guid id = Guid.NewGuid();
        return new ProjectFile
        {
            Id = id,
            ProjectId = projectId,
            OriginalFileName = $"{id:N}.bin",
            Category = "userFiles",
            StorageKey = $"projects/{projectId}/userFiles/{id:N}.bin",
            MimeType = mime,
            SizeBytes = 4,
            UploadedAt = DateTime.UtcNow.AddMinutes(-5),
            CsamStatus = status
        };
    }

    private sealed class Fixture
    {
        private readonly ServiceProvider _provider;
        private readonly Project _project;
        private readonly List<ProjectFile> _added = [];

        public SettlingMatcher Matcher { get; } = new();
        public List<MediaDerivativesRequested> Published { get; } = [];
        public bool PublishedAfterVerdict { get; private set; } = true;
        public CsamScanSweeper Sweeper { get; }
        public Guid ProjectId => _project.Id;

        private Fixture(bool enabled)
        {
            Guid org = Guid.NewGuid();
            CsamOptions options = CsamTestSupport.EnabledOptions(o => o.Enabled = enabled);
            options.Sweeper.IntervalMinutes = 15;

            Mock<IPublishEndpoint> publish = new();
            publish.Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
                .Callback<MediaDerivativesRequested, CancellationToken>((m, _) =>
                {
                    if (!Matcher.Scanned.Contains(m.SourceRef.SourceId) && enabled) PublishedAfterVerdict = false;
                    Published.Add(m);
                })
                .Returns(Task.CompletedTask);
            Mock<IFileStorageService> storage = new();
            storage.Setup(s => s.GetObjectETagAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("etag");

            string database = $"csam-derivative-order-{Guid.NewGuid():N}";
            ServiceCollection services = new();
            services.AddLogging();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(database));
            services.AddSingleton<ITenantContext>(new CsamFixedTenant(org));
            services.AddSingleton<ICsmaHashMatcher>(Matcher);
            services.AddSingleton(Options.Create(options));
            services.AddSingleton(publish.Object);
            services.AddSingleton(storage.Object);
            _provider = services.BuildServiceProvider();

            _project = new Project { Id = Guid.NewGuid(), Name = "P", OrganizationId = org, OwnerId = Guid.NewGuid() };
            Sweeper = new CsamScanSweeper(
                _provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<CsamScanSweeper>.Instance);
        }

        public static Task<Fixture> NewAsync(bool enabled = true) => Task.FromResult(new Fixture(enabled));

        public ProjectFile AddPending(string name, string mime)
        {
            ProjectFile row = Row(_project.Id, CsamScanStatus.Pending, mime);
            row.OriginalFileName = name;
            _added.Add(row);
            return row;
        }

        public async Task SaveAsync()
        {
            using IServiceScope scope = _provider.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Projects.Add(_project);
            db.ProjectFiles.AddRange(_added);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Settles the row it is given the way the real matcher does: the verdict is written on the entity.</summary>
    private sealed class SettlingMatcher : ICsmaHashMatcher
    {
        public Dictionary<Guid, CsamScanStatus> Verdicts { get; } = [];
        public List<Guid> Scanned { get; } = [];

        public Task<CsamScanOutcome> ScanAsync(ProjectFile file, Guid? actorUserId, CancellationToken ct)
        {
            Scanned.Add(file.Id);
            CsamScanStatus status = Verdicts.GetValueOrDefault(file.Id, CsamScanStatus.Clean);
            file.CsamStatus = status;
            return Task.FromResult(new CsamScanOutcome(status, null, null, status == CsamScanStatus.Quarantined, null));
        }
    }
}
