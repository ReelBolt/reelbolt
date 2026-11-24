using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// D10b — "Persist the intermediate extract artefact".
///
/// <para>
/// D10a split <c>VideoAnalyze</c> into extraction and inference and handed the extract across the
/// boundary in memory. This file pins the property D10b adds on top of that: the extraction's
/// result survives the attempt, and a later attempt starts from it instead of decoding the source
/// again.
/// </para>
/// <para>
/// <b>Two things this file is careful NOT to overclaim.</b> First, "reuses the extract" means the
/// extraction phase does not run — it does not mean literally zero ffmpeg processes in every case,
/// because D10a deliberately does not publish the caption keyframes and a session that is gone
/// therefore cannot serve them; <c>Extraction_is_reused_even_when_the_session_is_gone</c> covers
/// that case and asserts exactly which work is and is not repeated. Second, the "session survived
/// the failed attempt" fixture used by
/// <c>A_retry_after_a_caption_failure_reuses_the_extract_and_runs_no_ffmpeg</c> has to put the
/// session files back itself, because in the local (in-place) configuration a failed attempt tears
/// its scratch directory down — that precondition is what a remote session (D11) provides, and the
/// test says so where it manufactures it rather than pretending the local case has it for free.
/// </para>
/// </remarks>
public class VideoAnalyzeExtractArtefactTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    // Fixed, so a control run and the run it is compared against report the same music candidate.
    private static readonly Guid MusicFileId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime MusicUploadedAt = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);

    // =======================================================================
    // The document itself
    // =======================================================================

    [Fact]
    public async Task The_extraction_survives_a_round_trip_through_its_persisted_document()
    {
        Fixture fixture = CreateFixture();

        VideoAnalysisExtract extract = await ExtractAsync(fixture);

        VideoAnalysisExtractArtifact document = VideoAnalysisExtractArtifact.FromExtract(
            extract, fixture.Context.Execution.Id, fixture.Context.Step.StepOrder);
        string json = document.ToJson();

        VideoAnalysisExtractArtifact.TryDeserialize(
            json, fixture.Context.Execution.Id, fixture.Context.Step.StepOrder, out VideoAnalysisExtractArtifact? parsed, out string? reason)
            .Should().BeTrue(reason);
        parsed!.SchemaVersion.Should().Be(VideoAnalysisExtractArtifact.CurrentSchemaVersion);

        VideoAnalysisExtract rebuilt = parsed.ToExtract(
            extract.Sources
                .Select(s => new VideoAnalysisExtractMedia(s.VideoVirtualPath, s.LocalVideoPath))
                .ToList());

        // The silence spans are the member that would be silently lost by serializing the record
        // itself (System.Text.Json writes a ValueTuple as {}), so they are asserted first and by
        // value rather than by count.
        rebuilt.Sources[0].SilenceSpans.Should().HaveCount(extract.Sources[0].SilenceSpans.Count);
        rebuilt.Sources[0].SilenceSpans[0].Should().Be(extract.Sources[0].SilenceSpans[0]);
        rebuilt.Sources[0].SilenceSpans[0].StartSec.Should().BeApproximately(3.0, 1e-9);
        rebuilt.Sources[0].SilenceSpans[0].EndSec.Should().BeApproximately(3.5, 1e-9);

        // Everything else the inference phase reads, member by member.
        rebuilt.Sources[0].StorageKey.Should().Be(extract.Sources[0].StorageKey);
        rebuilt.Sources[0].ResolvedStorageKey.Should().Be(SourceVideoKey);
        rebuilt.Sources[0].AudioVirtualPath.Should().Be("audio-full.wav");
        rebuilt.Sources[0].AudioStorageKey.Should().Be(extract.Sources[0].AudioStorageKey);
        rebuilt.Sources[0].AudioBytes.Should().Be(100);
        rebuilt.Sources[0].Media.Should().Be(extract.Sources[0].Media);
        rebuilt.Sources[0].Provenance.VisualAnalysisApplied.Should().BeTrue();
        rebuilt.Deterministic.SourceInfos.Should().HaveCount(1);
        rebuilt.Keyframes.Should().Equal(extract.Keyframes);
        rebuilt.KeyframeFailures.Should().Equal(extract.KeyframeFailures);

        // And, member for member, nothing else moved either. Compared as JSON rather than with
        // record equality because these are records over LISTS, and a list member's Equals is
        // reference equality — a deserialized document would "differ" from its source on every
        // collection no matter how faithful it was.
        Json(rebuilt).Should().Be(Json(extract));

        // A LocalVideoPath never leaves the machine that produced it.
        json.Should().NotContain("video-analyze", because: "no host path may reach object storage");
    }

    [Fact]
    public void The_persisted_source_mirrors_the_extract_source_member_for_member()
    {
        // The document is a hand-written DTO, so "a member was added to the record and forgotten
        // here" is a real failure mode — and a silent one, because the missing member simply
        // deserializes to its default on resume. This is the guard: the two member sets must differ
        // by exactly the session-scoped members the document deliberately does not carry —
        // the host path (meaningless on another machine), the virtual path (meaningful only inside
        // the session that was open at the time) and the version of the bytes that session staged
        // (a resume stages the bytes again, and they may be another version by then).
        HashSet<string> record = typeof(VideoAnalysisExtractSource)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> dto = typeof(VideoAnalysisExtractArtifactSource)
            .GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        record.Should().Contain("LocalVideoPath");
        dto.Should().NotContain("LocalVideoPath",
            because: "a host-local absolute path must never be persisted");
        record.Should().Contain("VideoVirtualPath");
        dto.Should().NotContain("VideoVirtualPath",
            because: "a session-relative name only means something inside the session it was staged in");

        record.Should().Contain("PassSourceVersion");
        dto.Should().NotContain("PassSourceVersion",
            because: "the version belongs to the bytes one attempt staged, never to the document");

        record.Remove("LocalVideoPath");
        record.Remove("VideoVirtualPath");
        record.Remove("PassSourceVersion");
        dto.Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task The_analysis_copy_decision_survives_the_document_and_an_older_document_reads_as_the_source()
    {
        Fixture fixture = CreateFixture();
        VideoAnalysisExtract extract = await ExtractAsync(fixture);
        Guid executionId = fixture.Context.Execution.Id;
        int stepOrder = fixture.Context.Step.StepOrder;
        List<VideoAnalysisExtractMedia> media = extract.Sources
            .Select(s => new VideoAnalysisExtractMedia(s.VideoVirtualPath, s.LocalVideoPath))
            .ToList();
        extract.Sources[0].AnalysisCopy.Should().BeNull("a short 1080p source is analysed as it is");

        VideoAnalysisExtract copied = extract with
        {
            Sources = extract.Sources.Select(s => s with { AnalysisCopy = new VideoAnalysisCopyPlan("appleLog") }).ToList()
        };
        VideoAnalysisExtractArtifact.TryDeserialize(
            VideoAnalysisExtractArtifact.FromExtract(copied, executionId, stepOrder).ToJson(), executionId, stepOrder,
            out VideoAnalysisExtractArtifact? parsed, out string? reason).Should().BeTrue(reason);
        parsed!.ToExtract(media).Sources[0].AnalysisCopy.Should().Be(new VideoAnalysisCopyPlan("appleLog"));

        // A document written before the member existed: it reads, and the event pass reads the source.
        string older = VideoAnalysisExtractArtifact.FromExtract(extract, executionId, stepOrder).ToJson()
            .Replace(",\"analysisCopy\":null", string.Empty, StringComparison.Ordinal);
        older.Should().NotContain("analysisCopy");
        VideoAnalysisExtractArtifact.TryDeserialize(older, executionId, stepOrder, out VideoAnalysisExtractArtifact? old, out reason)
            .Should().BeTrue(reason);
        old!.ToExtract(media).Sources[0].AnalysisCopy.Should().BeNull();
    }

    [Fact]
    public async Task The_pass_cache_version_is_never_in_the_document_and_comes_from_the_bytes_this_attempt_staged()
    {
        Fixture fixture = CreateFixture();
        VideoAnalysisExtract extract = await ExtractAsync(fixture);
        Guid executionId = fixture.Context.Execution.Id;
        int stepOrder = fixture.Context.Step.StepOrder;
        VideoAnalysisExtract versioned = extract with
        {
            Sources = extract.Sources.Select(s => s with { PassSourceVersion = "etag-then" }).ToList()
        };

        string json = VideoAnalysisExtractArtifact.FromExtract(versioned, executionId, stepOrder).ToJson();
        json.Should().NotContain("etag-then").And.NotContainEquivalentOf("passSourceVersion");
        VideoAnalysisExtractArtifact.TryDeserialize(json, executionId, stepOrder, out VideoAnalysisExtractArtifact? parsed, out string? reason)
            .Should().BeTrue(reason);

        // A resume stages the bytes again. The version is the one read around that staging, or none.
        parsed!.ToExtract(extract.Sources.Select(s => new VideoAnalysisExtractMedia(s.VideoVirtualPath, s.LocalVideoPath)).ToList())
            .Sources[0].PassSourceVersion.Should().BeNull();
        parsed.ToExtract(extract.Sources.Select(s => new VideoAnalysisExtractMedia(s.VideoVirtualPath, s.LocalVideoPath, "etag-now")).ToList())
            .Sources[0].PassSourceVersion.Should().Be("etag-now");
    }

    [Theory]
    [InlineData("analysis-extract.v2")]
    [InlineData("")]
    public void A_document_this_build_does_not_understand_is_refused(string schemaVersion)
    {
        VideoAnalysisExtractArtifact document = SampleDocument() with { SchemaVersion = schemaVersion };
        Guid executionId = document.ExecutionId;

        VideoAnalysisExtractArtifact.TryDeserialize(
            document.ToJson(), executionId, document.StepOrder, out VideoAnalysisExtractArtifact? parsed, out string? reason)
            .Should().BeFalse();
        parsed.Should().BeNull();
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_document_written_for_another_execution_or_step_is_refused()
    {
        VideoAnalysisExtractArtifact document = SampleDocument();

        VideoAnalysisExtractArtifact.TryDeserialize(
            document.ToJson(), Guid.NewGuid(), document.StepOrder, out _, out string? wrongExecution)
            .Should().BeFalse();
        wrongExecution.Should().Contain("belongs to execution");

        VideoAnalysisExtractArtifact.TryDeserialize(
            document.ToJson(), document.ExecutionId, document.StepOrder + 1, out _, out string? wrongStep)
            .Should().BeFalse();
        wrongStep.Should().Contain("belongs to execution");
    }

    [Fact]
    public void Malformed_json_is_refused_rather_than_thrown()
    {
        VideoAnalysisExtractArtifact.TryDeserialize(
            "{ not json", Guid.NewGuid(), 1, out VideoAnalysisExtractArtifact? parsed, out string? reason)
            .Should().BeFalse();
        parsed.Should().BeNull();
        reason.Should().Contain("not valid JSON");
    }

    // =======================================================================
    // The resume: the acceptance bar
    // =======================================================================

    [Fact]
    public async Task A_retry_after_a_caption_failure_reuses_the_extract_and_runs_no_ffmpeg()
    {
        Fixture fixture = CreateFixture();

        // ---- Attempt 1: the media work succeeds, the LLM half fails. ----
        fixture.Captioner
            .Setup(c => c.CaptionAsync(It.IsAny<ResolvedInferenceProvider>(), It.IsAny<ShotCaptionRequest>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vision provider is having a bad day"));

        StepExecutionResult first = await fixture.Executor.ExecuteAsync(fixture.Context);
        first.Status.Should().Be(StepStatus.Failed);
        JsonNode.Parse(first.Output)!["error"]!["code"]!.GetValue<string>().Should().Be("VISION_FAILED");
        fixture.Store.Should().ContainKey(fixture.ExtractKey, "the extraction was persisted before inference ran");

        // The precondition the local configuration cannot supply for free: the session the failed
        // attempt used is torn down with it, so a resume that could still read the caption keyframes
        // is the REMOTE-SESSION case (D11's workspace), not today's local one. Put the session back
        // so this test measures the resume path rather than the keyframe re-solve path — the next
        // test covers the case where the session is genuinely gone.
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ScratchPathFor("x"))!);
        foreach (string keyframe in new[] { "keyframe-s0.jpg", "keyframe-s1.jpg" })
            File.WriteAllBytes(fixture.ScratchPathFor(keyframe), new byte[] { 0xFF, 0xD8, 0xFF });

        // ---- Attempt 2: same execution, same step, a working provider. ----
        fixture.Captioner.Invocations.Clear();
        fixture.Probe.Invocations.Clear();
        fixture.Silence.Invocations.Clear();
        fixture.ShotDetector.Invocations.Clear();
        fixture.AudioExtractor.Invocations.Clear();
        fixture.FrameGridSampler.Invocations.Clear();
        fixture.KeyframeExtractor.Invocations.Clear();
        fixture.VideoToolRunner.Invocations.Clear();
        fixture.Captioner
            .Setup(c => c.CaptionAsync(It.IsAny<ResolvedInferenceProvider>(), It.IsAny<ShotCaptionRequest>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Caption());

        StepExecutionResult second = await fixture.Executor.ExecuteAsync(fixture.Context);

        second.Status.Should().Be(StepStatus.Completed, because: second.ErrorDetails ?? second.Output);

        // ---- The bar: the extraction is not re-run. ----
        fixture.Probe.Invocations.Should().BeEmpty("the probe is extraction work the persisted document already answered");
        fixture.Silence.Invocations.Should().BeEmpty();
        fixture.ShotDetector.Invocations.Should().BeEmpty();
        fixture.AudioExtractor.Invocations.Should().BeEmpty("the WAV is re-staged from its published key, never re-decoded");
        fixture.FrameGridSampler.Invocations.Should().BeEmpty();
        fixture.KeyframeExtractor.Invocations.Should().BeEmpty("the session survived, so its keyframes are simply read");
        fixture.VideoToolRunner.Invocations.Should().BeEmpty("no ffmpeg runs at all on a resume whose session is intact");

        // ---- And the output is what a first-time run produces. ----
        (string controlEnvelope, string controlArtifact) =
            await RunUnresumedControlAsync(fixture.Context.Execution.Id);
        Normalize(second.Output).Should().Be(controlEnvelope);
        Normalize(fixture.ArtifactJson!).Should().Be(controlArtifact);
    }

    // =======================================================================
    // What a resume can and cannot recover
    // =======================================================================

    [Fact]
    public async Task Extraction_is_reused_even_when_the_session_is_gone()
    {
        // The case the in-place configuration actually produces today: the failed attempt's scratch
        // directory is deleted, so nothing the extraction wrote into it survives. The document is
        // still worth having — every decode, probe, silence/shot pass, frame grid, chroma track and
        // audio-level sample is not repeated — but the caption keyframes are, because D10a
        // deliberately does not publish them and re-solving one is a single seek plus a single
        // frame. This test is the honest boundary of the claim, not an embarrassment to be hidden.
        Fixture fixture = CreateFixture();
        StepExecutionResult first = await fixture.Executor.ExecuteAsync(fixture.Context);
        first.Status.Should().Be(StepStatus.Completed, because: first.ErrorDetails ?? first.Output);

        fixture.ScratchMustBeGone().Should().BeTrue("ExecuteAsync tears its scratch directory down");

        fixture.Probe.Invocations.Clear();
        fixture.Silence.Invocations.Clear();
        fixture.ShotDetector.Invocations.Clear();
        fixture.AudioExtractor.Invocations.Clear();
        fixture.FrameGridSampler.Invocations.Clear();
        fixture.KeyframeExtractor.Invocations.Clear();
        fixture.VideoToolRunner.Invocations.Clear();

        StepExecutionResult second = await fixture.Executor.ExecuteAsync(fixture.Context);

        second.Status.Should().Be(StepStatus.Completed, because: second.ErrorDetails ?? second.Output);

        fixture.Probe.Invocations.Should().BeEmpty();
        fixture.Silence.Invocations.Should().BeEmpty();
        fixture.ShotDetector.Invocations.Should().BeEmpty();
        fixture.AudioExtractor.Invocations.Should().BeEmpty();
        fixture.FrameGridSampler.Invocations.Should().BeEmpty();

        // Exactly the bounded exception, and nothing else: one re-solve per caption keyframe.
        fixture.KeyframeExtractor.Invocations.Should().HaveCount(2, "one per captioned shot, and only because keyframes are unpublished");

        // The source was re-staged from storage (a download) rather than re-decoded, and the WAV was
        // re-staged from its published key.
        fixture.StagedKeys.Should().Contain(SourceVideoKey);
        fixture.StagedKeys.Should().Contain(k => k.EndsWith(".wav", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_source_whose_extracted_wav_was_never_published_refuses_the_resume()
    {
        // D10a left the WAV publish best-effort, with a comment saying D11 would have to make it the
        // failure it had become. D10b's resume is the first reader of that key, so the dependency is
        // made explicit here: an unpublished WAV means the audio exists nowhere a second attempt can
        // reach, and the honest answer is to extract again rather than to resume into a silently
        // transcript-less artifact.
        Fixture fixture = CreateFixture();
        fixture.FailWavPublish = true;

        StepExecutionResult first = await fixture.Executor.ExecuteAsync(fixture.Context);
        first.Status.Should().Be(StepStatus.Completed, because: first.ErrorDetails ?? first.Output);

        fixture.Probe.Invocations.Clear();
        fixture.AudioExtractor.Invocations.Clear();

        StepExecutionResult second = await fixture.Executor.ExecuteAsync(fixture.Context);

        second.Status.Should().Be(StepStatus.Completed, because: second.ErrorDetails ?? second.Output);
        fixture.Probe.Invocations.Should().NotBeEmpty("the resume was refused, so extraction ran");
        fixture.AudioExtractor.Invocations.Should().NotBeEmpty("and it re-derived the WAV it could not stage");
    }

    [Fact]
    public async Task A_source_that_resolves_somewhere_else_now_refuses_the_resume()
    {
        // The document's address is stable across attempts AND across a ReviewLoop loop-back, so
        // "it is still there" is not the same question as "it is still valid". This is the guard.
        Fixture fixture = CreateFixture();
        StepExecutionResult first = await fixture.Executor.ExecuteAsync(fixture.Context);
        first.Status.Should().Be(StepStatus.Completed, because: first.ErrorDetails ?? first.Output);

        // A re-executed upstream step produced a different render, so the document's claim about
        // what it analysed no longer matches what the source reference resolves to.
        fixture.RewritePersistedResolvedSourceKey("projects/p/outputFiles/e/render-v2.mp4");

        fixture.Probe.Invocations.Clear();
        fixture.AudioExtractor.Invocations.Clear();

        StepExecutionResult second = await fixture.Executor.ExecuteAsync(fixture.Context);

        second.Status.Should().Be(StepStatus.Completed, because: second.ErrorDetails ?? second.Output);
        fixture.Probe.Invocations.Should().NotBeEmpty("a stale extraction must never be replayed against new bytes");
        fixture.AudioExtractor.Invocations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_first_attempt_that_cannot_persist_the_extract_still_completes()
    {
        // The document only ever saves a later attempt work. Losing it costs a re-extraction and
        // never correctness, so a storage hiccup on the way out must not fail a step whose media
        // work and inference both succeeded.
        Fixture fixture = CreateFixture();
        fixture.FailDocumentPublish = true;

        StepExecutionResult result = await fixture.Executor.ExecuteAsync(fixture.Context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Store.Should().NotContainKey(fixture.ExtractKey);
    }

    // =======================================================================
    // Driving the phases the way ExecuteAsync does
    // =======================================================================

    private static async Task<VideoAnalysisExtract> ExtractAsync(Fixture fixture)
    {
        using VideoScratchSpace scratch = VideoScratchSpace.Create(
            fixture.Options, fixture.Context.Execution.Id, fixture.Context.Step.Id, NullLogger<VideoAnalyzeStepExecutor>.Instance);

        VideoAnalysisExtractOutcome outcome = await fixture.Executor.ExtractAsync(
            fixture.Context,
            LocalMediaWorkspace.Create(
                scratch, new Mock<IVideoToolRunner>().Object, fixture.Workspace.Object, ProjectId,
                NullLogger<VideoAnalyzeStepExecutor>.Instance),
            fixture.Config,
            fixture.Sources,
            new VideoAnalyzeProgressPlan(Enum.GetValues<VideoAnalyzeProgressPlan.Stage>(), 1),
            CreateSharpnessBudget(fixture.Config),
            32,
            18,
            fixture.Context.CancellationToken);

        outcome.Extract.Should().NotBeNull(outcome.ErrorMessage);
        return outcome.Extract!;
    }

    /// <summary>
    /// The same fixture with nothing resumed: this is what the step produces when the extraction
    /// genuinely runs, so the resumed run can be compared against it rather than against itself.
    /// </summary>
    private static async Task<(string Envelope, string Artifact)> RunUnresumedControlAsync(Guid executionId)
    {
        // Same execution id as the run under test, because the execution id is part of the artifact
        // storage key that the envelope reports — the comparison is about what the step produced,
        // not about which execution produced it.
        Fixture control = CreateFixture(executionId);
        StepExecutionResult result = await control.Executor.ExecuteAsync(control.Context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        return (Normalize(result.Output), Normalize(control.ArtifactJson!));
    }

    private static VideoAnalyzeStepExecutor.StepBudget CreateSharpnessBudget(VideoAnalyzeStepConfig config) =>
        new() { Remaining = Math.Max(0, config.MaxSharpnessShots) };

    /// <summary>
    /// Drops the two genuinely non-deterministic fields — the provenance's analysis timestamp, and
    /// nothing else — so a resumed run and a control run can be compared. Everything else, every id
    /// and every numeric descriptor included, must match exactly.
    /// </summary>
    private static string Normalize(string json)
    {
        JsonNode node = JsonNode.Parse(json)!;
        NormalizeTimestamps(node);
        return node.ToJsonString();
    }

    private static void NormalizeTimestamps(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(kv => kv.Key).ToList())
                {
                    if (string.Equals(key, "analyzedAt", StringComparison.Ordinal))
                        obj[key] = "normalized";
                    else
                        NormalizeTimestamps(obj[key]);
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                    NormalizeTimestamps(item);
                break;
        }
    }

    /// <summary>The same options the document and the analysis artifact are written with.</summary>
    private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    });

    private static VideoShotCaption Caption() => new(
        "s0", "a caption", [], "action", "setting", "mood", "Medium", "Eye level", [], [],
        "Unknown", "flat", "neutral", "centered", []);

    /// <summary>A document with no interesting content, for the version/identity refusals.</summary>
    private static VideoAnalysisExtractArtifact SampleDocument() => VideoAnalysisExtractArtifact.FromExtract(
        new VideoAnalysisExtract(
            Sources: [],
            Deterministic: new VideoAnalysisExtractDeterministic(
                Shots: [], Silences: [], Placements: [], DuplicateGroups: [], InsertRegions: [], SourceInfos: [],
                Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, TranscriptionApplied: false, TranscriptionDegraded: false),
                Pacing: default,
                LookGroups: [],
                LookGroupingApplied: false, LookUniform: false, SharpnessShotsMeasured: 0, TotalDurationSec: 0),
            MusicCandidates: [], SfxCandidates: [], Music: null, Keyframes: [], KeyframeFailures: []),
        executionId: Guid.NewGuid(),
        stepOrder: 3);

    // =======================================================================
    // Fixture
    // =======================================================================

    private sealed class Fixture
    {
        public required VideoAnalyzeStepExecutor Executor { get; init; }
        public required StepExecutionContext Context { get; init; }
        public required VideoAnalyzeStepConfig Config { get; init; }
        public required IReadOnlyList<VideoSourceRef> Sources { get; init; }
        public required VideoEditingOptions Options { get; init; }
        public required Mock<IProjectFileWorkspace> Workspace { get; init; }
        public required Mock<IInferenceProviderResolver> ProviderResolver { get; init; }
        public required Mock<IShotCaptioner> Captioner { get; init; }
        public required Mock<IMediaProbe> Probe { get; init; }
        public required Mock<ISilenceDetector> Silence { get; init; }
        public required Mock<IShotDetector> ShotDetector { get; init; }
        public required Mock<IAudioExtractor> AudioExtractor { get; init; }
        public required Mock<IFrameGridSampler> FrameGridSampler { get; init; }
        public required Mock<IKeyframeExtractor> KeyframeExtractor { get; init; }
        public required Mock<IVideoToolRunner> VideoToolRunner { get; init; }

        /// <summary>The fake object store, keyed exactly as the workspace lays keys out.</summary>
        public required Dictionary<string, byte[]> Store { get; init; }

        /// <summary>Every storage key a resume re-staged, in order.</summary>
        public required List<string> StagedKeys { get; init; }

        /// <summary>The analysis artifact the last run published, captured by name.</summary>
        public string? ArtifactJson { get; set; }

        public bool FailWavPublish { get; set; }
        public bool FailDocumentPublish { get; set; }

        public string ExtractKey => VideoAnalysisExtractArtifact.BuildStorageKey(
            ProjectId, Context.Execution.Id, Context.Step.StepOrder);

        public string ScratchPathFor(string name) => Path.Combine(
            Path.GetFullPath(Options.ScratchPath), Context.Execution.Id.ToString("D"), Context.Step.Id.ToString("D"), name);

        public bool ScratchMustBeGone() => !Directory.Exists(Path.GetDirectoryName(ScratchPathFor("x"))!);

        /// <summary>
        /// Rewrites the persisted document to claim it analysed a different source than the step's
        /// source reference resolves to now — exactly the state a ReviewLoop loop-back leaves behind.
        /// </summary>
        public void RewritePersistedResolvedSourceKey(string storageKey)
        {
            JsonNode document = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(Store[ExtractKey]))!;
            document["sources"]![0]!["resolvedStorageKey"] = storageKey;
            Store[ExtractKey] = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString());
        }
    }

    private static Fixture CreateFixture(Guid? executionId = null)
    {
        var store = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var stagedKeys = new List<string>();
        var workspace = new Mock<IProjectFileWorkspace>();
        var probe = new Mock<IMediaProbe>();
        var silence = new Mock<ISilenceDetector>();
        var shotDetector = new Mock<IShotDetector>();
        var audioExtractor = new Mock<IAudioExtractor>();
        var frameGridSampler = new Mock<IFrameGridSampler>();
        var keyframeExtractor = new Mock<IKeyframeExtractor>();
        var videoToolRunner = new Mock<IVideoToolRunner>();
        var transcriptionFactory = new Mock<ITranscriptionClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();
        var captioner = new Mock<IShotCaptioner>();

        VideoAnalyzeStepConfig config = new VideoAnalyzeStepConfig(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)) with
        {
            DetectSilence = true,
            DetectShots = true,
            Transcription = VideoTranscriptionMode.Required,
            AnalyzeAudioLevels = true,
            AnalyzeVisuals = true,
            DetectNearDuplicates = true,
            EmitOverlayPlacements = true,
            Vision = VideoVisionMode.Required,
            MaxCaptionedShots = 10,
            MinCaptionShotSeconds = 0.0,
            OfferMusicTracks = true,
            OfferSfxClips = true
        };

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() }
            })
        };

        var options = new VideoEditingOptions
        {
            ScratchPath = Path.Combine(Path.GetTempPath(), "video-analyze-extract-tests", Guid.NewGuid().ToString("N")),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        };

        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = executionId ?? Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null)],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        // ---- The object store, laid out exactly as ProjectFileWorkspace lays it out: the fake
        // answers HEAD from what has been uploaded, so a resume is a real hit only when the first
        // run genuinely published a document. ----
        workspace
            .Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string key, CancellationToken _) => store.ContainsKey(key));
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, destPath, _) =>
            {
                stagedKeys.Add(key);
                File.WriteAllBytes(destPath, store.TryGetValue(key, out byte[]? bytes) ? bytes : new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(MusicFileId, ProjectId, "bed.mp3", null, "userFiles", "projects/p/userFiles/bed.mp3", "audio/mpeg", 4096, MusicUploadedAt, null)
            });

        Fixture? fixture = null;

        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns<Guid, string, string, string, CancellationToken, string>((projectId, path, fileName, _, _, category) =>
            {
                if (fixture!.FailWavPublish && fileName.EndsWith(".wav", StringComparison.Ordinal))
                    throw new InvalidOperationException("object store said no");

                if (fixture.FailDocumentPublish && fileName.EndsWith("-extract.v1.json", StringComparison.Ordinal))
                    throw new InvalidOperationException("object store said no");

                string key = $"projects/{projectId}/{category}/{fileName}";
                byte[] bytes = File.ReadAllBytes(path);
                store[key] = bytes;

                if (fileName.EndsWith("-analysis.json", StringComparison.Ordinal))
                    fixture.ArtifactJson = System.Text.Encoding.UTF8.GetString(bytes);

                return Task.FromResult(key);
            });

        // ---- Extraction's own stages. ----
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(4, 30, 1, 32, 18, "h264", "aac", 48000));
        silence.Setup(s => s.DetectAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { (3.0, 3.5) });
        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(0.0, 1.0), (1.0, 2.0)]);
        audioExtractor
            .Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, _, outPath, ct) =>
            {
                await ws.WriteBytesAsync(outPath, new byte[100], ct);
            });
        frameGridSampler
            .Setup(g => g.SampleAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildGrid(32, 18, frameCount: 8, fps: 2.0));

        // ---- Inference's provider calls. ----
        var visionProvider = new ResolvedInferenceProvider(
            Guid.NewGuid(), "vision-test", InferenceProviderKind.OpenAICompatible, "http://localhost:9999", "vision-1", "", 30);
        providerResolver
            .Setup(r => r.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(visionProvider);
        providerResolver
            .Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedTranscriptionProvider(
                ProviderId: Guid.NewGuid(), Name: "whisper-local", Kind: InferenceProviderKind.OpenAICompatible,
                Endpoint: "http://localhost:9999", ModelName: "whisper-1", ApiKey: "", TimeoutSeconds: 30));

        captioner
            .Setup(c => c.CaptionAsync(It.IsAny<ResolvedInferenceProvider>(), It.IsAny<ShotCaptionRequest>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedInferenceProvider _, ShotCaptionRequest r, int _, CancellationToken _) => Caption() with { ShotId = r.ShotId });

        var transcriptionClient = new Mock<ITranscriptionClient>();
        transcriptionClient
            .Setup(c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptResult(
                "hello world",
                [new TranscriptSegment("hello world", 0.5, 1.5)],
                [new TranscriptWord("hello", 0.5, 1.0)],
                "en"));
        transcriptionFactory
            .Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>()))
            .Returns(transcriptionClient.Object);

        var executor = new VideoAnalyzeStepExecutor(
            probe.Object,
            silence.Object,
            shotDetector.Object,
            audioExtractor.Object,
            frameGridSampler.Object,
            keyframeExtractor.Object,
            new Mock<ISharpnessSampler>().Object,
            captioner.Object,
            workspace.Object,
            transcriptionFactory.Object,
            providerResolver.Object,
            Options.Create(options),
            NullLogger<VideoAnalyzeStepExecutor>.Instance,
            videoToolRunner: videoToolRunner.Object);

        fixture = new Fixture
        {
            Executor = executor,
            Context = context,
            Config = config,
            Sources = [config.Source],
            Options = options,
            Workspace = workspace,
            ProviderResolver = providerResolver,
            Captioner = captioner,
            Probe = probe,
            Silence = silence,
            ShotDetector = shotDetector,
            AudioExtractor = audioExtractor,
            FrameGridSampler = frameGridSampler,
            KeyframeExtractor = keyframeExtractor,
            VideoToolRunner = videoToolRunner,
            Store = store,
            StagedKeys = stagedKeys
        };

        return fixture;
    }

    /// <summary>A synthetic RGB grid with genuinely varying content, so the visual stage produces real descriptors.</summary>
    private static FrameGridResult BuildGrid(int gridWidth, int gridHeight, int frameCount, double fps)
    {
        int frameSize = gridWidth * gridHeight * 3;
        var pixels = new byte[frameCount * frameSize];
        var rnd = new Random(42);

        for (int f = 0; f < frameCount; f++)
        {
            int brightRow = f % gridHeight;
            for (int y = 0; y < gridHeight; y++)
            {
                for (int x = 0; x < gridWidth; x++)
                {
                    int idx = (f * frameSize) + (y * gridWidth + x) * 3;
                    byte baseVal = (byte)(40 + rnd.Next(0, 10));
                    byte val = y == brightRow ? (byte)220 : baseVal;
                    pixels[idx] = val;
                    pixels[idx + 1] = val;
                    pixels[idx + 2] = val;
                }
            }
        }

        return new FrameGridResult(pixels, gridWidth, gridHeight, fps, frameCount);
    }
}