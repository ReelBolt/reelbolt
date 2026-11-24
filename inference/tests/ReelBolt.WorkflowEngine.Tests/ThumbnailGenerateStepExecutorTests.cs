using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>StepType.ThumbnailGenerate</c> (work package I1). The claims this file holds down, in the
/// order they matter:
/// <list type="number">
/// <item>the moment is chosen from the <c>VideoAnalyze</c> artifact's OWN measurements, and the same
/// artifact always yields the same frame;</item>
/// <item>the written row is a real <c>media_derivatives</c> <c>Thumbnail</c> row — the first row this
/// table has ever been given, which is the headline of the whole work package;</item>
/// <item>the overlay text never reaches ffmpeg's filter string (only its own session file does), which
/// is the security property <c>OverlayTextSanitizer</c> is the SECOND line of;</item>
/// <item>every failure path returns a coded failure with valid JSON rather than throwing, because
/// <c>output_json</c> is <c>jsonb</c> and a throw would be refunded as a platform fault.</item>
/// </list>
/// </summary>
public class ThumbnailGenerateStepExecutorTests
{
    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid SourceFileId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceKey = "projects/p/userFiles/e/source.mp4";

    // ---------------------------------------------------------------------------------------
    // The selection rule, in isolation.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_selector_prefers_a_shots_own_take_quality_over_its_position()
    {
        // s0 is first but badly exposed and jittery; s1 is later, well exposed and sharp. A selector
        // that just took the first shot would fail this, and taking the first shot is exactly the
        // lazy implementation this test exists to prevent.
        VideoAnalysisArtifact artifact = Artifact(
            Shot("s0", 0, 4, motionStdDev: 0.5, brightness: 0.02, sharpness: 0.05),
            Shot("s1", 4, 8, motionStdDev: 0.01, brightness: 0.5, sharpness: 0.9));

        IReadOnlyList<ThumbnailCandidate> ranked = ThumbnailCandidateSelector.Rank(artifact, 24, 0.4, preferBestTake: true);

        ranked.Should().HaveCount(2);
        ranked[0].ShotId.Should().Be("s1");
        ranked[0].Score.Should().BeGreaterThan(ranked[1].Score);
    }

    [Fact]
    public void The_selector_drops_shots_shorter_than_the_floor()
    {
        VideoAnalysisArtifact artifact = Artifact(
            Shot("s0", 0, 0.2, 0.01, 0.5, 0.9),
            Shot("s1", 0.2, 4, 0.05, 0.5, 0.5));

        IReadOnlyList<ThumbnailCandidate> ranked = ThumbnailCandidateSelector.Rank(artifact, 24, 0.4, preferBestTake: true);

        ranked.Should().ContainSingle().Which.ShotId.Should().Be("s1");
    }

    [Fact]
    public void The_selector_demotes_the_other_members_of_a_duplicate_group()
    {
        // Same measured quality on both takes; only the group standing distinguishes them, so this
        // isolates the best-take rule rather than re-testing the quality terms.
        VideoAnalysisArtifact artifact = Artifact(
            Shot("s0", 0, 4, 0.05, 0.5, 0.5, groupId: "d0", groupRank: 1, isBestTake: false),
            Shot("s1", 4, 8, 0.05, 0.5, 0.5, groupId: "d0", groupRank: 0, isBestTake: true));

        IReadOnlyList<ThumbnailCandidate> ranked = ThumbnailCandidateSelector.Rank(artifact, 24, 0.4, preferBestTake: true);

        ranked[0].ShotId.Should().Be("s1");
        ranked[0].IsBestTake.Should().BeTrue();
    }

    [Fact]
    public void The_selector_is_a_pure_function_of_the_artifact()
    {
        VideoAnalysisArtifact artifact = Artifact(
            Shot("s0", 0, 4, 0.05, 0.5, 0.5),
            Shot("s1", 4, 8, 0.02, 0.5, 0.8, groupId: "d1", groupRank: 0, isBestTake: true));

        IReadOnlyList<ThumbnailCandidate> first = ThumbnailCandidateSelector.Rank(artifact, 24, 0.4, preferBestTake: true);
        IReadOnlyList<ThumbnailCandidate> second = ThumbnailCandidateSelector.Rank(artifact, 24, 0.4, preferBestTake: true);

        second.Select(c => (c.ShotId, c.AtSec, c.Score)).Should().Equal(first.Select(c => (c.ShotId, c.AtSec, c.Score)));
    }

    [Fact]
    public void The_selector_samples_a_shots_calm_window_rather_than_its_midpoint()
    {
        // The still window is deliberately NOT centred on the shot, so a midpoint sampler and a
        // calm-window sampler give different answers.
        var shot = new VideoAnalysisShot("s0", 0, 10, new VideoAnalysisShotVisual(
            MotionMean: 0.05, MotionPeak: 0.2, MotionStdDev: 0.05, MotionClass: "Still",
            CameraMove: "Static", CameraMoveConfidence: 0.9, HeadMotion: 0.05, TailMotion: 0.05,
            StillWindows: [new VideoAnalysisStillWindow(7, 9, 0.01)],
            BrightnessMean: 0.5, BrightnessStdDev: 0.1, ContrastRms: 0.2,
            ClippedHighlightRatio: 0, CrushedBlackRatio: 0, SaturationMean: 0.4,
            DominantColors: [], Regions: [], BestOverlayRegion: null, ActiveCrop: null,
            Sharpness: 0.8, KenBurnsCandidate: false, KenBurnsReason: null,
            DuplicateGroupId: null, GroupRank: null, IsBestTake: false));

        ThumbnailCandidate? best = ThumbnailCandidateSelector.Best(Artifact(shot), 24, 0.4, preferBestTake: true);

        best.Should().NotBeNull();
        best!.AtSec.Should().Be(8.0, "the middle of the shot's longest calm window, not the shot's midpoint");

        // KeyframeSelector owns that rule, and this asserts the two agree rather than restating it.
        KeyframeSelector.ChooseKeyframeSec(shot).Should().Be(best.AtSec);
    }

    [Fact]
    public void The_selector_still_answers_for_a_shot_with_no_visual_analysis_at_all()
    {
        // An artifact produced with AnalyzeVisuals off must still yield a usable thumbnail rather
        // than a failure — the step's job is to produce a cover, not to insist on measurements.
        VideoAnalysisArtifact artifact = Artifact(
            new VideoAnalysisShot("s0", 1, 5),
            new VideoAnalysisShot("s1", 5, 9));

        ThumbnailCandidate? best = ThumbnailCandidateSelector.Best(artifact, 24, 0.4, preferBestTake: true);

        best.Should().NotBeNull();
        best!.ShotId.Should().Be("s0", "with nothing measured, the tie-break toward the earlier shot decides");
    }

    [Fact]
    public void The_selector_answers_nothing_when_no_shot_is_long_enough()
    {
        VideoAnalysisArtifact artifact = Artifact(Shot("s0", 0, 0.1, 0.05, 0.5, 0.5));

        ThumbnailCandidateSelector.Rank(artifact, 24, 0.4, preferBestTake: true).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------
    // The executor, end to end over fake seams.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Auto_selection_publishes_a_frame_and_writes_the_first_media_derivatives_row_ever_written()
    {
        Fixture fixture = await Fixture.RunAsync();

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed);
        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
        output["status"]!.GetValue<string>().Should().Be("completed");
        output["shotId"]!.GetValue<string>().Should().Be("s1", "s1 is the better take");
        output["width"]!.GetValue<int>().Should().Be(1280);
        output["height"]!.GetValue<int>().Should().Be(720);

        // The object really was published, at a key under this project's own prefix.
        fixture.Session.Published.Should().ContainSingle();
        string key = output["thumbnailStorageKey"]!.GetValue<string>();
        key.Should().StartWith($"projects/{ProjectId}/outputFiles/");
        key.Should().EndWith("/thumbnail.jpg");

        // THE claim of work package I1: media_derivatives now has a writer.
        using IServiceScope scope = fixture.Scopes.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        MediaDerivative row = await db.MediaDerivatives.SingleOrDefaultAsync()
            ?? throw new Xunit.Sdk.XunitException(
                "no media_derivatives row. project_files in this store: " +
                await db.ProjectFiles.CountAsync()
                + " | executor log: " + string.Join(" || ", fixture.Log.Messages));
        row.Kind.Should().Be(MediaDerivativeKind.Thumbnail);
        row.ProjectId.Should().Be(ProjectId);
        row.SourceProjectFileId.Should().Be(SourceFileId);
        row.StorageKey.Should().Be(key);
        row.MimeType.Should().Be("image/jpeg");
        row.Width.Should().Be(1280);
        row.Height.Should().Be(720);
        row.SizeBytes.Should().BeGreaterThan(0);
        output["derivativeId"]!.GetValue<string>().Should().Be(row.Id.ToString());

        // And it is visible in the project's own file list, which is what makes it reachable.
        fixture.Workspace.RegisteredObjects.Should().ContainSingle();
        fixture.Workspace.RegisteredObjects[0].StorageKey.Should().Be(key);
        fixture.Workspace.RegisteredObjects[0].ContentType.Should().Be("image/jpeg");
        fixture.Workspace.RegisteredObjects[0].Category.Should().Be("outputFiles");
    }

    [Fact]
    public async Task The_extraction_asks_ffmpeg_for_the_chosen_moment_and_an_exact_canvas()
    {
        Fixture fixture = await Fixture.RunAsync();

        await fixture.ExecuteAsync();

        IReadOnlyList<string> argv = fixture.Session.FfmpegCalls.Single();
        int seek = argv.ToList().IndexOf("-ss");
        seek.Should().BeGreaterThan(-1);

        double atSec = double.Parse(argv[seek + 1], System.Globalization.CultureInfo.InvariantCulture);
        atSec.Should().Be(8.0, "s1's calm-window midpoint");

        string filter = argv[argv.ToList().IndexOf("-vf") + 1];
        filter.Should().Contain("scale=1280:720:force_original_aspect_ratio=increase");
        filter.Should().Contain("crop=1280:720");
        filter.Should().NotContain("drawtext", "no overlay was configured");
    }

    [Fact]
    public async Task AtTime_extracts_exactly_the_moment_it_was_given_and_can_omit_the_real_derivative_row()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            Selection = ThumbnailSelection.AtTime,
            AtTimeSec = 3.25,
            RegisterProjectFile = false
        });

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed);
        IReadOnlyList<string> argv = fixture.Session.FfmpegCalls.Single();
        double.Parse(argv[argv.ToList().IndexOf("-ss") + 1], System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(3.25);

        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
        output["selection"]!.GetValue<string>().Should().Be("AtTime");
        output["projectFileId"].Should().BeNull("RegisterProjectFile=false was asked for");

        fixture.Workspace.RegisteredObjects.Should().BeEmpty();
    }

    [Fact]
    public async Task The_overlay_text_is_written_to_its_own_session_file_and_never_enters_the_filter_string()
    {
        // The security property. The text below is chosen to be meaningful to ffmpeg's filter parser
        // if it ever reached it: a colon would end a drawtext option and a percent would open an
        // expansion. OverlayTextSanitizer strips both, but the REAL guarantee is that the text is
        // never in this string at all — which is what the assertions below actually check.
        const string hostile = "Sale: 50% off, call now";

        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            OverlayText = hostile,
            OverlayPosition = ThumbnailOverlayPosition.LowerThird
        });

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed);

        // Two stages: the frame extraction (no text), then the text overlay on that frame.
        fixture.Session.FfmpegCalls.Should().HaveCount(2);
        string[] extractCall = fixture.Session.FfmpegCalls[0];
        extractCall[extractCall.ToList().IndexOf("-vf") + 1].Should().NotContain("drawtext");
        string[] overlayCall = fixture.Session.FfmpegCalls[1];
        string filter = overlayCall[overlayCall.ToList().IndexOf("-vf") + 1];
        filter.Should().Contain("drawtext=");
        filter.Should().Contain("expansion=none", "without it, %{...} in the text would be evaluated");
        filter.Should().Contain("textfile=");
        filter.Should().NotContain("Sale", "the text itself must never reach the filtergraph");
        filter.Should().NotContain("50%");

        // The text landed in a session file, sanitized to the allowlist.
        fixture.Session.Files.Should().ContainKey("thumbnail-overlay.txt");
        string written = System.Text.Encoding.UTF8.GetString(fixture.Session.Files["thumbnail-overlay.txt"]);
        written.Should().Be("Sale 50 off call now");
    }

    [Fact]
    public async Task The_reported_1920x1080_quality_2_none_box_case_completes_and_never_hands_ffmpeg_boxcolor_none()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            Width = 1920,
            Height = 1080,
            JpegQuality = 2,
            OverlayText = "Launch day",
            OverlayPosition = ThumbnailOverlayPosition.LowerThird,
            OverlayBoxColor = "none"
        });

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed);
        fixture.Session.FfmpegCalls.SelectMany(c => c).Should().NotContain(a => a.Contains("boxcolor=none"));
    }

    [Fact]
    public async Task A_failed_extraction_names_the_exit_code_the_last_three_clean_stderr_lines_and_the_extraction_fields()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with { Width = 1920, Height = 1080, JpegQuality = 2 });
        fixture.Session.FfmpegExitCode = 234;
        fixture.Session.FfmpegStdErr =
            "first line that is dropped\n" +
            "[mov @ 0x55f1] error reading /srv/scratch/exec-9/source.mp4\n" +
            "\n" +
            "Invalid data found; key=AKIA" + "ABCDEFGHIJKLMNOPQRSTUV\n" +
            "Error opening output file thumbnail-raw.jpg.";

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("FRAME_EXTRACTION_FAILED");
        result.ErrorDetails.Should().Contain("exitCode=234");
        result.ErrorDetails.Should().NotContain("first line that is dropped", "only the last three non-empty lines are kept");
        result.ErrorDetails.Should().Contain("Invalid data found");
        result.ErrorDetails.Should().Contain("Error opening output file");
        result.ErrorDetails.Should().NotContain("/srv/scratch", "paths never leave the engine");
        result.ErrorDetails.Should().NotContain("AKIA", "key-shaped tokens are redacted");
        result.ErrorDetails.Should().Contain("Width").And.Contain("Height").And.Contain("JpegQuality");
        result.ErrorDetails.Should().NotContain("OverlayBoxColor", "the overlay stage never ran");
    }

    [Fact]
    public async Task A_failed_overlay_stage_is_its_own_code_and_names_the_overlay_fields()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            OverlayText = "Launch day",
            OverlayPosition = ThumbnailOverlayPosition.Center
        });
        fixture.Session.FfmpegExitCode = 1;
        fixture.Session.FailOnlyCall = 2;
        fixture.Session.FfmpegStdErr = "Cannot find color 'x'";

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("THUMBNAIL_OVERLAY_FAILED");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        result.ErrorDetails.Should().Contain("OverlayBoxColor").And.Contain("OverlayFontColor").And.Contain("OverlayPosition");
        result.ErrorDetails.Should().Contain("Cannot find color");
        fixture.Session.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_out_of_range_field_is_CONFIG_INVALID_naming_the_field_and_ffmpeg_never_runs()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with { Width = 99999 });

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CONFIG_INVALID");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.ErrorDetails.Should().Contain("Width");
        fixture.Session.FfmpegCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_analysis_artifact_is_the_named_failure_not_an_unexpected_error()
    {
        Fixture fixture = await Fixture.RunAsync();
        fixture.Session.Stage404Path = "analysis.json";

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("ANALYSIS_ARTIFACT_UNAVAILABLE");
        result.ErrorDetails.Should().NotContain("projects/");
    }

    // ---- AtTime on an assembled film / explicit source ----

    [Fact]
    public async Task AtTime_on_a_compiled_film_reads_that_steps_output_and_needs_no_analysis()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            AnalysisStepOrder = 0,
            Selection = ThumbnailSelection.AtTime,
            AtTimeSec = 42,
            Source = new VideoSourceRef(VideoSourceKind.StepOutput, StepOrder: 3)
        });
        fixture.History.Clear();
        fixture.History.Add(new StepOutputHistoryEntry(3, "Compile", "{}", OutputStorageKey: "projects/p/outputFiles/e/film.mp4"));

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        string[] argv = fixture.Session.FfmpegCalls.Single();
        double.Parse(argv[argv.ToList().IndexOf("-ss") + 1], System.Globalization.CultureInfo.InvariantCulture).Should().Be(42);
        JsonNode.Parse(result.Output)!["sourceKind"]!.GetValue<string>().Should().Be("StepOutput");
    }

    [Fact]
    public async Task Output_basis_adds_the_references_InSec_to_the_file_position()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            AnalysisStepOrder = 0,
            Selection = ThumbnailSelection.AtTime,
            AtTimeSec = 5,
            AtTimeBasis = ThumbnailAtTimeBasis.Output,
            Source = new VideoSourceRef(VideoSourceKind.StepOutput, StepOrder: 3, InSec: 10, OutSec: 30)
        });
        fixture.History.Clear();
        fixture.History.Add(new StepOutputHistoryEntry(3, "Compile", "{}", OutputStorageKey: "projects/p/outputFiles/e/film.mp4"));

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        string[] argv = fixture.Session.FfmpegCalls.Single();
        double.Parse(argv[argv.ToList().IndexOf("-ss") + 1], System.Globalization.CultureInfo.InvariantCulture).Should().Be(15);
    }

    [Fact]
    public async Task A_time_past_the_end_of_the_reference_window_is_refused_with_a_coded_user_input_failure()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            AnalysisStepOrder = 0,
            Selection = ThumbnailSelection.AtTime,
            AtTimeSec = 25,
            AtTimeBasis = ThumbnailAtTimeBasis.Output,
            Source = new VideoSourceRef(VideoSourceKind.StepOutput, StepOrder: 3, InSec: 10, OutSec: 30)
        });
        fixture.History.Clear();
        fixture.History.Add(new StepOutputHistoryEntry(3, "Compile", "{}", OutputStorageKey: "projects/p/outputFiles/e/film.mp4"));

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.FailureCode.Should().Be("AT_TIME_OUT_OF_RANGE");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        fixture.Session.FfmpegCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unresolvable_source_is_a_coded_user_input_failure()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            AnalysisStepOrder = 0,
            Selection = ThumbnailSelection.AtTime,
            Source = new VideoSourceRef(VideoSourceKind.StepOutput, StepOrder: 7)
        });
        fixture.History.Clear();

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.FailureCode.Should().Be("SOURCE_UNRESOLVED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
    }

    [Fact]
    public async Task Auto_with_a_project_file_source_ranks_only_that_analysed_source()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            Source = new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: SourceFileId)
        });

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        JsonNode.Parse(result.Output)!["shotId"]!.GetValue<string>().Should().Be("s1");
    }

    [Fact]
    public async Task Auto_with_a_source_filters_to_that_source_before_the_candidate_cap()
    {
        // Source 0 holds the single best-scoring shot; the chosen file is source 1 whose shots score lower.
        // With MaxCandidates = 1 the global cut keeps only source 0's shot, so filtering afterwards found nothing.
        VideoAnalysisShot weak = Shot("b0", 4, 12, 0.5, 0.02, 0.05) with { SourceIndex = 1 };
        VideoAnalysisArtifact artifact = Artifact(Shot("a0", 0, 4, 0.01, 0.5, 0.9), weak) with
        {
            Sources =
            [
                new VideoAnalysisSourceInfo(0, "projects/p/userFiles/e/other.mp4", new VideoAnalysisMedia(12, 30, 1, 1920, 1080)),
                new VideoAnalysisSourceInfo(1, SourceKey, new VideoAnalysisMedia(12, 30, 1, 1920, 1080))
            ]
        };

        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            MaxCandidates = 1,
            Source = new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: SourceFileId)
        }, artifact);

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        JsonNode.Parse(result.Output)!["shotId"]!.GetValue<string>().Should().Be("b0");
    }

    [Fact]
    public async Task Auto_with_a_source_the_analysis_never_saw_is_refused()
    {
        Guid other = Guid.NewGuid();
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            Source = new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: other)
        });
        await fixture.SeedProjectFileAsync(other, "other.mp4", "projects/p/userFiles/e/other.mp4", 100);

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.FailureCode.Should().Be("SOURCE_NOT_ANALYSED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
    }

    // ---- Brand font ----

    private static readonly Guid FontFileId = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    private const string FontKey = "projects/p/userFiles/e/brand.ttf";

    private static async Task<Fixture> BrandFontFixtureAsync(
        string fileName = "brand.ttf", long sizeBytes = 2048, byte[]? bytes = null, Guid? ownerProject = null)
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            OverlayText = "Launch day",
            OverlayPosition = ThumbnailOverlayPosition.LowerThird,
            FontProjectFileId = FontFileId
        });
        await fixture.SeedProjectFileAsync(FontFileId, fileName, FontKey, sizeBytes, ownerProject);
        fixture.Session.Files["brand-font"] = bytes ?? [0x00, 0x01, 0x00, 0x00, 0x00, 0x10];
        return fixture;
    }

    [Fact]
    public async Task A_brand_font_from_the_runs_project_is_staged_and_used_as_the_drawtext_fontfile()
    {
        Fixture fixture = await BrandFontFixtureAsync();

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        string[] overlayCall = fixture.Session.FfmpegCalls[1];
        string filter = overlayCall[overlayCall.ToList().IndexOf("-vf") + 1];
        filter.Should().Contain("fontfile='brand-font'");
        filter.Should().NotContain("DejaVu");
        fixture.Session.Staged.Should().Contain("brand-font");
    }

    [Fact]
    public async Task The_brand_font_is_staged_with_the_font_byte_cap_enforced_by_the_stage_itself()
    {
        Fixture fixture = await BrandFontFixtureAsync();

        await fixture.ExecuteAsync();

        fixture.Session.StageCaps.Should().ContainKey("brand-font").WhoseValue.Should().Be(ThumbnailGenerateStepConfig.MaxFontBytes);
    }

    [Fact]
    public async Task A_brand_font_whose_bytes_exceed_the_cap_though_its_row_says_small_is_FONT_INVALID()
    {
        byte[] huge = new byte[ThumbnailGenerateStepConfig.MaxFontBytes + 1];
        huge[1] = 0x01;
        Fixture fixture = await BrandFontFixtureAsync(sizeBytes: 2048, bytes: huge);

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("FONT_INVALID");
    }

    [Fact]
    public async Task In_a_remote_session_the_overlay_carries_no_engine_font_path()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            OverlayText = "Launch day",
            OverlayPosition = ThumbnailOverlayPosition.LowerThird
        });
        fixture.Session.IsRemoteSession = true;

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        string filter = fixture.Session.FfmpegCalls[1][fixture.Session.FfmpegCalls[1].ToList().IndexOf("-vf") + 1];
        filter.Should().Contain("drawtext=textfile=").And.NotContain("fontfile=").And.NotContain("/usr/share/fonts");
    }

    [Fact]
    public async Task In_a_remote_session_a_staged_brand_font_is_still_the_fontfile()
    {
        Fixture fixture = await BrandFontFixtureAsync();
        fixture.Session.IsRemoteSession = true;

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        string filter = fixture.Session.FfmpegCalls[1][fixture.Session.FfmpegCalls[1].ToList().IndexOf("-vf") + 1];
        filter.Should().Contain("fontfile='brand-font'");
    }

    [Fact]
    public async Task Without_a_brand_font_the_configured_VIDEO_FONT_FILE_is_used()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            OverlayText = "Launch day",
            OverlayPosition = ThumbnailOverlayPosition.LowerThird
        });

        await fixture.ExecuteAsync();

        string[] overlayCall = fixture.Session.FfmpegCalls[1];
        overlayCall[overlayCall.ToList().IndexOf("-vf") + 1].Should().Contain("fontfile='/usr/share/fonts/dejavu/DejaVuSans.ttf'");
    }

    [Fact]
    public async Task A_brand_font_from_another_project_is_not_found_and_nothing_is_staged()
    {
        Fixture fixture = await BrandFontFixtureAsync(ownerProject: Guid.NewGuid());

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.FailureCode.Should().Be("FONT_NOT_FOUND");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        fixture.Session.Staged.Should().NotContain("brand-font");
        fixture.Session.FfmpegCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("brand.exe", 2048, new byte[] { 0x00, 0x01, 0x00, 0x00 })]
    [InlineData("brand.ttf", 2048, new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]
    [InlineData("brand.otf", 11 * 1024 * 1024, new byte[] { 0x4F, 0x54, 0x54, 0x4F })]
    public async Task A_brand_font_that_is_not_a_small_ttf_or_otf_is_refused(string name, long size, byte[] bytes)
    {
        Fixture fixture = await BrandFontFixtureAsync(fileName: name, sizeBytes: size, bytes: bytes);

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.FailureCode.Should().Be("FONT_INVALID");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        fixture.Session.FfmpegCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_otf_brand_font_with_the_OTTO_magic_is_accepted()
    {
        Fixture fixture = await BrandFontFixtureAsync(fileName: "brand.OTF", bytes: [0x4F, 0x54, 0x54, 0x4F, 0x00]);

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
    }

    [Fact]
    public async Task A_colour_outside_the_allowlist_fails_the_step_instead_of_reaching_ffmpeg()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            OverlayText = "Launch day",
            OverlayPosition = ThumbnailOverlayPosition.Center,
            // A value that would terminate the drawtext option's value and inject another filter.
            OverlayBoxColor = "black@0.45,format=null"
        });

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("COLOR_NOT_ALLOWED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        fixture.Session.FfmpegCalls.Should().BeEmpty("nothing may be handed to ffmpeg with an unvalidated colour");
    }

    [Fact]
    public async Task A_missing_config_is_a_coded_failure_with_valid_json_rather_than_a_throw()
    {
        Fixture fixture = await Fixture.RunAsync();
        fixture.Step.ThumbnailGenerateConfigJson = null;

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CONFIG_INVALID");
        JsonNode.Parse(result.Output)!["status"]!.GetValue<string>().Should().Be("failed");
    }

    [Fact]
    public async Task Config_JSON_that_is_not_JSON_is_a_coded_failure()
    {
        Fixture fixture = await Fixture.RunAsync();
        fixture.Step.ThumbnailGenerateConfigJson = "{not json";

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CONFIG_INVALID");
    }

    [Fact]
    public async Task Footage_with_no_shot_long_enough_fails_with_a_code_the_author_can_act_on()
    {
        Fixture fixture = await Fixture.RunAsync(artifact: ShortShotArtifact());

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("NO_CANDIDATE_SHOT");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        fixture.Session.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_artifact_with_no_source_entry_fails_rather_than_guessing_which_file_to_decode()
    {
        Fixture fixture = await Fixture.RunAsync(artifact: ArtifactWithoutSources());

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("ANALYSIS_SOURCE_UNRESOLVED");
        fixture.Session.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unresolvable_analysis_artifact_fails_before_any_compute_is_spent()
    {
        Fixture fixture = await Fixture.RunAsync();
        fixture.History.Clear();

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("ANALYSIS_NOT_FOUND");
        fixture.Session.FfmpegCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_ffmpeg_failure_is_reported_as_a_coded_failure_with_no_publish()
    {
        Fixture fixture = await Fixture.RunAsync();
        fixture.Session.FfmpegExitCode = 1;

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("FRAME_EXTRACTION_FAILED");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        fixture.Session.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task The_derivative_row_is_not_written_when_no_project_file_matches_the_analysed_source()
    {
        // The foreign key is real, so an unresolvable source row must degrade to "no index row"
        // rather than to a fabricated id that would violate the constraint and lose the whole write.
        Fixture fixture = await Fixture.RunAsync(seedSourceProjectFile: false);

        StepExecutionResult result = await fixture.ExecuteAsync();

        // The thumbnail itself still succeeded — losing the index degrades discoverability, not the image.
        result.Status.Should().Be(StepStatus.Completed);
        JsonNode.Parse(result.Output)!["derivativeId"].Should().BeNull();

        using IServiceScope scope = fixture.Scopes.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        (await db.MediaDerivatives.CountAsync()).Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------

    private sealed class ChangeContext : IStepChangeRequestContext
    {
        public StepChangeRequestScope? Scope { get; set; }

        public Task<StepChangeRequestScope?> FindAsync(WorkflowExecution execution, WorkflowStep step, CancellationToken ct) =>
            Task.FromResult(Scope);
    }

    private sealed class Fixture
    {
        public required WorkflowStep Step { get; init; }
        public required WorkflowExecution Execution { get; init; }
        public required List<StepOutputHistoryEntry> History { get; init; }
        public required SessionWorkspace Session { get; init; }
        public required FakeProjectFileWorkspace Workspace { get; init; }
        public required ServiceProvider Scopes { get; init; }
        public required ThumbnailGenerateStepExecutor Executor { get; init; }
        public required CapturingLogger Log { get; init; }
        public required ChangeContext Changes { get; init; }

        public static async Task<Fixture> RunAsync(
            Func<ThumbnailGenerateStepConfig, ThumbnailGenerateStepConfig>? configure = null,
            VideoAnalysisArtifact? artifact = null,
            bool seedSourceProjectFile = true)
        {
            ThumbnailGenerateStepConfig config = (configure ?? (c => c))(new ThumbnailGenerateStepConfig(
                Version: 1,
                AnalysisStepOrder: 1));

            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 2,
                StepType = StepType.ThumbnailGenerate,
                ThumbnailGenerateConfigJson = JsonSerializer.Serialize(config, ConfigJson)
            };

            var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId };

            // The name is computed ONCE, outside the options callback: AddDbContext runs that
            // callback per scope, so a Guid.NewGuid() inside it would give every scope its own
            // private store — the seed would land in one and the assertions would read another.
            string databaseName = "thumbnails-" + Guid.NewGuid().ToString("N");
            var services = new ServiceCollection();
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(databaseName));
            ServiceProvider provider = services.BuildServiceProvider();

            if (seedSourceProjectFile)
            {
                using IServiceScope seedScope = provider.CreateScope();
                WorkflowEngineDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
                seedDb.ProjectFiles.Add(new ProjectFile
                {
                    Id = SourceFileId,
                    ProjectId = ProjectId,
                    OriginalFileName = "source.mp4",
                    StorageKey = SourceKey,
                    StorageFileName = "source.mp4",
                    StorageBucket = "test",
                    MimeType = "video/mp4",
                    Category = "userFiles",
                    UploadedAt = DateTime.UtcNow
                });
                await seedDb.SaveChangesAsync();
            }

            var logger = new CapturingLogger();
            var session = new SessionWorkspace();
            session.Files["analysis.json"] = System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(artifact ?? DefaultArtifact(), ArtifactJson));

            var workspace = new FakeProjectFileWorkspace();
            var changes = new ChangeContext();

            // The video tool runner is only reached when no workspace factory is supplied (it is what
            // builds the fallback LocalMediaWorkspace). This fixture always supplies the factory, so
            // the runner is a mock that would fail loudly if the executor ever fell back to it.
            var executor = new ThumbnailGenerateStepExecutor(
                new Mock<IVideoToolRunner>(MockBehavior.Strict).Object,
                workspace,
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new VideoEditingOptions()),
                logger,
                new StubWorkspaceFactory(session),
                changeRequests: changes);

            return new Fixture
            {
                Step = step,
                Execution = execution,
                History =
                [
                    new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey)
                ],
                Session = session,
                Workspace = workspace,
                Scopes = provider,
                Executor = executor,
                Log = logger,
                Changes = changes
            };
        }

        public async Task SeedProjectFileAsync(Guid id, string fileName, string storageKey, long sizeBytes, Guid? projectId = null)
        {
            using IServiceScope scope = Scopes.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = id,
                ProjectId = projectId ?? ProjectId,
                OriginalFileName = fileName,
                StorageKey = storageKey,
                StorageFileName = fileName,
                StorageBucket = "test",
                MimeType = "application/octet-stream",
                SizeBytes = sizeBytes,
                Category = "userFiles",
                UploadedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        public async Task<StepExecutionResult> ExecuteAsync(WorkflowExecution? execution = null) => await Executor.ExecuteAsync(new StepExecutionContext
        {
            Execution = execution ?? Execution,
            Step = Step,
            AllSteps = [Step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = History,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        });
    }

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static VideoAnalysisArtifact DefaultArtifact() => Artifact(
        Shot("s0", 0, 4, 0.5, 0.02, 0.05),
        Shot("s1", 4, 12, 0.01, 0.5, 0.9));

    private static VideoAnalysisArtifact ShortShotArtifact() => Artifact(Shot("s0", 0, 0.1, 0.01, 0.5, 0.9));

    private static VideoAnalysisArtifact ArtifactWithoutSources() => DefaultArtifact() with { Sources = [] };

    private static VideoAnalysisArtifact Artifact(params VideoAnalysisShot[] shots) => new(
        Version: 2,
        Media: new VideoAnalysisMedia(12, 30, 1, 1920, 1080),
        Shots: shots,
        SilenceSpans: [],
        Segments: [],
        Words: [],
        OfferedIds: [],
        Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
        Sources:
        [
            new VideoAnalysisSourceInfo(0, SourceKey, new VideoAnalysisMedia(12, 30, 1, 1920, 1080))
        ]);

    private static VideoAnalysisShot Shot(
        string id, double start, double end, double motionStdDev, double brightness, double sharpness,
        string? groupId = null, int? groupRank = null, bool isBestTake = false) =>
        new(id, start, end, new VideoAnalysisShotVisual(
            MotionMean: motionStdDev, MotionPeak: motionStdDev * 3, MotionStdDev: motionStdDev,
            MotionClass: motionStdDev > 0.2 ? "Heavy" : "Still",
            CameraMove: "Static", CameraMoveConfidence: 0.8,
            HeadMotion: motionStdDev, TailMotion: motionStdDev,
            StillWindows: [],
            BrightnessMean: brightness, BrightnessStdDev: 0.1, ContrastRms: 0.2,
            ClippedHighlightRatio: 0, CrushedBlackRatio: 0, SaturationMean: 0.4,
            DominantColors: [], Regions: [], BestOverlayRegion: null, ActiveCrop: null,
            Sharpness: sharpness, KenBurnsCandidate: false, KenBurnsReason: null,
            DuplicateGroupId: groupId, GroupRank: groupRank, IsBestTake: isBestTake));

    /// <summary>
    /// A workspace that behaves like a REMOTE session where it matters here: <c>MaterializeAsync</c>
    /// answers the virtual path UNCHANGED, so nothing this executor does can be satisfied by a path
    /// on this machine. A green run is therefore evidence that every read and write went through the
    /// session, not the local disk.
    /// </summary>
    private sealed class SessionWorkspace : IMediaWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Staged { get; } = [];
        public Dictionary<string, long> StageCaps { get; } = new(StringComparer.Ordinal);
        public List<string> Published { get; } = [];
        public List<string[]> FfmpegCalls { get; } = [];
        public int FfmpegExitCode { get; set; }
        /// <summary>When set, only the Nth (1-based) ffmpeg call fails with <see cref="FfmpegExitCode"/>; earlier calls succeed.</summary>
        public int FailOnlyCall { get; set; }
        public string FfmpegStdErr { get; set; } = "stderr";

        public string RootPath => "session:/";

        /// <summary>When true the fake stands in for a remote (runner) session (security T-326).</summary>
        public bool IsRemoteSession { get; set; }

        /// <summary>When set, staging this virtual path fails like the runner's stage op does on a missing object.</summary>
        public string? Stage404Path { get; set; }

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct)
        {
            if (virtualPath == Stage404Path)
                throw new RunnerSessionOpException(
                    "stage_failed", "stage '" + virtualPath + "' failed on the runner: the object store answered 404",
                    sessionLost: false, retryable: false);
            Staged.Add(virtualPath);
            // The real seam copies the object-store object into the session. This fixture pre-seeds
            // the session with exactly what each key stands for, so staging must NOT clobber it —
            // otherwise every test would decode an artifact of three arbitrary bytes.
            if (!Files.ContainsKey(virtualPath))
                Files[virtualPath] = [0x00, 0x01, 0x02];

            return Task.CompletedTask;
        }

        public async Task StageAsync(string storageKey, string virtualPath, long maxBytes, CancellationToken ct)
        {
            StageCaps[virtualPath] = maxBytes;
            await StageAsync(storageKey, virtualPath, ct);
            if (Files[virtualPath].Length > maxBytes)
            {
                Files.Remove(virtualPath);
                throw new MediaStageLimitExceededException(virtualPath, maxBytes);
            }
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
            => RunFfmpegAsync(argv, timeout, ct, null);

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        {
            FfmpegCalls.Add(argv.ToArray());
            int exit = FailOnlyCall == 0 || FailOnlyCall == FfmpegCalls.Count ? FfmpegExitCode : 0;
            if (exit == 0)
            {
                // A real encoder writes the output file; the executor stats it before publishing.
                Files[argv[^1]] = new byte[2048];
            }

            return Task.FromResult(new VideoToolResult(exit, string.Empty, exit == 0 ? string.Empty : FfmpegStdErr, false));
        }

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct) => Task.FromResult(virtualPath);

        public Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct)
        {
            Files[virtualPath] = content;
            return Task.CompletedTask;
        }

        public Task WriteTextAsync(string virtualPath, string content, System.Text.Encoding encoding, CancellationToken ct)
            => WriteBytesAsync(virtualPath, encoding.GetBytes(content), ct);

        public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) =>
            ReadAsync(virtualPath, int.MaxValue, ct);

        public Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct) =>
            Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? Task.FromResult(bytes)
                : throw new FileNotFoundException($"Media workspace file '{virtualPath}' does not exist.", virtualPath);

        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? new MediaWorkspaceStat(true, bytes.Length)
                : new MediaWorkspaceStat(false, 0));

        public Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            Published.Add(finalStorageKey);
            return Task.FromResult(finalStorageKey);
        }

        public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            Published.Add(finalStorageKey);
            return Task.FromResult(finalStorageKey);
        }
    }

    /// <summary>Captures every log line the executor emits, so a swallowed failure is visible in a test's message rather than silent.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<ThumbnailGenerateStepExecutor>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(logLevel + ": " + formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
    }

    private sealed class StubWorkspaceFactory(SessionWorkspace session) : IMediaWorkspaceFactory
    {
        public MediaWorkspaceRequest? Request { get; private set; }

        public Task<MediaWorkspaceOpenResult> OpenAsync(
            MediaWorkspaceRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                session,
                scratch: null,
                ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null,
                request.ExecutionId,
                request.StepOrder,
                NullLogger.Instance)));
        }
    }


    // ------------------------------------------------------------------ change requests (docs/design/change-requests.md)

    private static StepChangeRequestScope Request(string payloadJson, string? previousOutput = null) =>
        new(Guid.NewGuid(), ChangeRequestPayload.TryParse(payloadJson)!, previousOutput);

    [Fact]
    public async Task A_change_request_overlay_text_lands_in_the_textfile_never_the_filter_string_and_uses_a_new_output_key()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with
        {
            OverlayText = "Original title",
            OverlayPosition = ThumbnailOverlayPosition.LowerThird
        });
        StepExecutionResult first = await fixture.ExecuteAsync();
        first.Status.Should().Be(StepStatus.Completed);
        string firstKey = JsonNode.Parse(first.Output!)!["thumbnailStorageKey"]!.GetValue<string>();
        byte[] firstBytes = fixture.Session.Files["thumbnail-raw.jpg"];
        fixture.Session.FfmpegCalls.Clear();

        fixture.Changes.Scope = Request("""{"overrides":{"thumbnail":{"overlayText":"Sale: 50% off, now"}}}""", first.Output);
        var second = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId };
        StepExecutionResult result = await fixture.ExecuteAsync(second);

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        string[] overlayCall = fixture.Session.FfmpegCalls.Last();
        string filter = overlayCall[overlayCall.ToList().IndexOf("-vf") + 1];
        filter.Should().Contain("textfile=").And.NotContain("Sale").And.NotContain("50%");
        System.Text.Encoding.UTF8.GetString(fixture.Session.Files["thumbnail-overlay.txt"]).Should().Be("Sale 50 off now");

        string newKey = JsonNode.Parse(result.Output!)!["thumbnailStorageKey"]!.GetValue<string>();
        newKey.Should().NotBe(firstKey);
        newKey.Should().Contain(second.Id.ToString("D"));
        fixture.Session.Published.Should().Contain(firstKey, "the earlier object was published once and never rewritten").And.Contain(newKey);
        fixture.Session.Published.Count(k => k == firstKey).Should().Be(1);
        JsonNode.Parse(result.Output!)!["changeRequest"]!["changed"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("overlayText");
        firstBytes.Should().NotBeNull();
    }

    [Fact]
    public async Task A_change_request_atTimeSec_selects_that_frame()
    {
        Fixture fixture = await Fixture.RunAsync(config => config with { RegisterProjectFile = false });
        fixture.Changes.Scope = Request("""{"overrides":{"*":{"atTimeSec":3.5}}}""");

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.Status.Should().Be(StepStatus.Completed, result.ErrorDetails);
        JsonNode.Parse(result.Output!)!["selection"]!.GetValue<string>().Should().Be("AtTime");
        JsonNode.Parse(result.Output!)!["atSec"]!.GetValue<double>().Should().Be(3.5);
    }

    [Fact]
    public async Task A_malformed_stored_request_is_CHANGE_NOT_APPLIED_and_publishes_nothing()
    {
        Fixture fixture = await Fixture.RunAsync();
        fixture.Changes.Scope = Request("""{"overrides":{"thumbnail":{"overlayText":"x"}}}""") with { Malformed = true };

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        fixture.Session.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_change_request_with_nothing_this_step_accepts_is_CHANGE_NOT_APPLIED()
    {
        Fixture fixture = await Fixture.RunAsync();
        fixture.Changes.Scope = Request("""{"overrides":{"thumbnail":{"colour":"red"}}}""");

        StepExecutionResult result = await fixture.ExecuteAsync();

        result.FailureCode.Should().Be("CHANGE_NOT_APPLIED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        fixture.Session.Published.Should().BeEmpty();
    }

    [Fact]
    public void Thumbnail_registry_validator_and_ListItems()
    {
        const string output = """{"status":"completed","thumbnailStorageKey":"projects/p/outputFiles/e/thumbnail.jpg","thumbnailFileName":"thumbnail.jpg"}""";
        StepChangeCapabilities.ListItems(StepType.ThumbnailGenerate, output).Should().Equal(new StepItem("thumbnail", "completed", null, "thumbnail.jpg"));
        foreach (string? garbage in new string?[] { null, "", "nope", "[]", """{"status":"failed"}""" })
            StepChangeCapabilities.ListItems(StepType.ThumbnailGenerate, garbage).Should().BeEmpty();
        StepChangeCapabilities.For(StepType.ThumbnailGenerate).Overrides.Select(o => o.Key).Should().Equal(
            "overlayText", "atTimeSec", "overlayPosition", "overlayFontColor", "overlayBoxColor");

        string[] Paths(string json) => ChangeRequestValidator.Validate(StepType.ThumbnailGenerate, ChangeRequestPayload.TryParse(json), output).Select(v => v.Path).ToArray();
        Paths("""{"overrides":{"thumbnail":{"overlayText":"Hi","atTimeSec":2,"overlayPosition":"center","overlayFontColor":"yellow","overlayBoxColor":"none"}}}""").Should().BeEmpty();
        Paths("""{"overrides":{"thumbnail":{"overlayPosition":"Sideways"}}}""").Should().Contain("overrides.thumbnail.overlayPosition");
        Paths("""{"overrides":{"thumbnail":{"overlayFontColor":"#ff0000; rm"}}}""").Should().Contain("overrides.thumbnail.overlayFontColor");
        Paths("""{"overrides":{"thumbnail":{"overlayBoxColor":5}}}""").Should().Contain("overrides.thumbnail.overlayBoxColor");
        Paths("""{"overrides":{"thumbnail":{"atTimeSec":-1}}}""").Should().Contain("overrides.thumbnail.atTimeSec");
        Paths("""{"overrides":{"thumbnail":{"overlayText":"bad\u0007"}}}""").Should().Contain("overrides.thumbnail.overlayText");
        Paths("""{"overrides":{"frame":{"atTimeSec":1}}}""").Should().Contain("overrides.frame");
        Paths("""{"itemIds":["thumbnail"],"overrides":{"thumbnail":{"fontProjectFileId":"x"}}}""").Should().Contain("overrides.thumbnail.fontProjectFileId");
    }
}
