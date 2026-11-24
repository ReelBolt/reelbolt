using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Product decision 6: AudioGenerate shares the generation ledger but never the video-generation daily caps. Its rows are
/// recognised by their clip id (<c>music-{k}</c> / <c>sfx-{k}</c>), the video sums leave them out, and an audio
/// reservation is limited only by its step cap (MaxSpendCredits) and the credit hold.
/// </summary>
public class VideoGenerationBudgetGateAudioTests
{
    private static VideoGenerationBudgetGate NewGate(SharedInMemoryDbFactory factory, decimal projectCap = 100m, bool videoGenAllowed = true, decimal globalCap = 100m)
        => new(
            factory,
            Options.Create(new VideoGenerationOptions { DailyBudgetCredits = projectCap, GlobalDailyBudgetCredits = globalCap }),
            new ConfigOrganizationEntitlements(new TenancyOptions
            {
                DefaultEntitlements = new OrganizationEntitlementSet { VideoGenAllowed = videoGenAllowed }
            }));

    private static async Task<(Guid Project, Guid Org)> SeedProjectAsync(SharedInMemoryDbFactory factory)
    {
        Guid project = Guid.NewGuid(), org = Guid.NewGuid();
        await using var db = factory.CreateDbContext();
        db.Projects.Add(new Project { Id = project, Name = "p", OrganizationId = org });
        await db.SaveChangesAsync();
        return (project, org);
    }

    private static async Task AddRowAsync(SharedInMemoryDbFactory factory, Guid project, Guid org, string clipId, decimal cost)
    {
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Add(new ExternalGenerationJob
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            StepResultId = Guid.NewGuid(),
            ProjectId = project,
            OrganizationId = org,
            ClipId = clipId,
            RequestHash = Guid.NewGuid().ToString("N"),
            ProviderId = Guid.NewGuid(),
            Status = ExternalGenerationJobStatus.Succeeded,
            CostEstimate = cost,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static VideoGenerationReservationRequest Video(Guid project, decimal cost)
        => new(project, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1000m,
            [new VideoGenerationTakeRequest("v1", Guid.NewGuid().ToString("N"), cost)]);

    private static VideoGenerationReservationRequest Audio(Guid project, decimal cost, decimal stepCap = 1000m, string clipId = "music-1")
        => new(project, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), stepCap,
            [new VideoGenerationTakeRequest(clipId, Guid.NewGuid().ToString("N"), cost)],
            Scope: VideoGenerationBudgetScope.Audio);

    [Fact]
    public async Task Audio_rows_consume_no_video_cap()
    {
        var factory = new SharedInMemoryDbFactory();
        var (project, org) = await SeedProjectAsync(factory);
        await AddRowAsync(factory, project, org, "music-1", 90m);
        await AddRowAsync(factory, project, org, "sfx-2", 90m);

        // 180 credits of audio today; a 60-credit video take still fits the 100-credit project, org and global caps.
        var result = await NewGate(factory).ReserveAsync(Video(project, 60m), CancellationToken.None);

        result.Allowed.Should().BeTrue(result.DenyReason);
        result.ProjectSpentToday.Should().Be(0m);
        result.GlobalSpentToday.Should().Be(0m);
    }

    [Fact]
    public async Task An_exhausted_video_cap_does_not_block_audio()
    {
        var factory = new SharedInMemoryDbFactory();
        var (project, org) = await SeedProjectAsync(factory);
        await AddRowAsync(factory, project, org, "v1", 100m); // every video cap is exactly used up

        var video = await NewGate(factory).ReserveAsync(Video(project, 1m), CancellationToken.None);
        video.Allowed.Should().BeFalse("the video caps are exhausted");

        var audio = await NewGate(factory).ReserveAsync(Audio(project, 50m), CancellationToken.None);
        audio.Allowed.Should().BeTrue(audio.DenyReason);
        audio.Takes.Should().ContainSingle();
    }

    [Fact]
    public async Task Audio_is_not_refused_by_a_plan_without_a_daily_video_budget()
    {
        var factory = new SharedInMemoryDbFactory();
        var (project, _) = await SeedProjectAsync(factory);

        var audio = await NewGate(factory, videoGenAllowed: false).ReserveAsync(Audio(project, 50m), CancellationToken.None);

        audio.Allowed.Should().BeTrue(audio.DenyReason);
    }

    [Fact]
    public async Task Audio_is_still_bounded_by_its_step_cap()
    {
        var factory = new SharedInMemoryDbFactory();
        var (project, _) = await SeedProjectAsync(factory);

        var audio = await NewGate(factory).ReserveAsync(Audio(project, 50m, stepCap: 40m), CancellationToken.None);

        audio.Allowed.Should().BeFalse();
        audio.DenyReason.Should().StartWith("BUDGET_EXCEEDED:");
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Any().Should().BeFalse();
    }

    [Theory]
    [InlineData(VideoGenerationBudgetScope.Audio, "v1")]
    [InlineData(VideoGenerationBudgetScope.Video, "music-1")]
    [InlineData(VideoGenerationBudgetScope.Video, "sfx-3")]
    public async Task A_clip_id_outside_its_scope_is_refused_so_no_row_can_escape_the_video_caps(VideoGenerationBudgetScope scope, string clipId)
    {
        var factory = new SharedInMemoryDbFactory();
        var (project, _) = await SeedProjectAsync(factory);
        var request = new VideoGenerationReservationRequest(project, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1000m,
            [new VideoGenerationTakeRequest(clipId, "h", 10m)], Scope: scope);

        var result = await NewGate(factory).ReserveAsync(request, CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.DenyReason.Should().StartWith("CONFIG_INVALID:");
    }

    [Fact]
    public void The_accounting_counts_no_audio_row()
    {
        DateTime today = DateTime.UtcNow.Date;
        ExternalGenerationJob Row(string clip) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            ClipId = clip,
            RequestHash = "h",
            Status = ExternalGenerationJobStatus.Succeeded,
            CostEstimate = 10m,
            CreatedAt = DateTime.UtcNow,
        };

        var rows = new[] { Row("v1"), Row("v2t1"), Row("music-1"), Row("sfx-4") }.AsQueryable();

        VideoBudgetAccounting.TodayJobs(rows, ExternalGenerationJob.CreditsUnit, today).Sum(VideoBudgetAccounting.ChargeOf).Should().Be(20m);
        VideoBudgetAccounting.IsAudioClipId("music-1").Should().BeTrue();
        VideoBudgetAccounting.IsAudioClipId("sfx-1").Should().BeTrue();
        VideoBudgetAccounting.IsAudioClipId("v1").Should().BeFalse();
        VideoBudgetAccounting.AudioClipId(AudioGenerationKind.Music, 2).Should().Be("music-2");
        VideoBudgetAccounting.AudioClipId(AudioGenerationKind.SoundEffect, 3).Should().Be("sfx-3");
    }
}
