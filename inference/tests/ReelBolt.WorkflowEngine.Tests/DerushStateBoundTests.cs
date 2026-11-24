using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.Guardrails;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-206 — the derush pre-pass's outbound <see cref="DecisionState"/> is bounded by
/// <c>WorkflowEngine:Guardrails:StateMaxChars</c>, not by <c>DerushChunkSize</c> ids alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The finding this closes.</b> Each asked <c>s{n}</c>/<c>t{n}</c> id carried its whole view row
/// plus both immediate neighbours, and a chunk was <see cref="VideoAnalyzeStepExecutor.DerushChunkSize"/>
/// (8) of them — so the outbound state was bounded by id COUNT and not by characters, and a view row
/// with a long caption or transcript pushed a single call well past the intended bound. The sibling
/// guardrail path already honoured <c>WorkflowEngine:Guardrails:StateMaxChars</c> (default 4000); the
/// derush pre-pass did not. The fix reuses that ONE bound (plan §1 D3's stated preference: no new
/// config record where an existing one already defines the value) and shrinks the effective chunk —
/// fewer ids per call, more calls — instead of dropping ids.
/// </para>
/// <para>
/// <b>The failure mode these tests exist to prevent (plan §7 review focus #2).</b> Making the state
/// fit by removing an id from the question set, or from the view, would leave the model's legal
/// answer set disagreeing with the set the downstream compile step accepts. Every test here asserts
/// set-equality and ORDER-equality of the asked/offered ids across the change, at several bounds.
/// </para>
/// <para>
/// <b>Two distinct guarantees, asserted separately.</b> (1) A chunk carrying two or more ids is
/// within the bound — enforced by construction, because a chunk is closed the moment the next id
/// would not fit. (2) A chunk carrying one id may sit at that row's own minimal state when its JSON
/// skeleton alone is bigger than the bound, because an id may not be split across calls and may not be
/// dropped; measured, that skeleton is ~195 characters for a shot row and ~268 for a transcript-span
/// row, so it is unreachable at the shipped 4000 and only shows up below ~300.
/// </para>
/// <para>
/// <b>Byte-identity (plan §1 D2).</b> With no Decision provider configured — this install's real
/// state — the emitted <c>{view, meta}</c> envelope is unchanged, asserted by sha256 over the
/// serialized output, at the tight bound and at the default one.
/// </para>
/// </remarks>
public class DerushStateBoundTests
{
    /// <summary>A bound small enough that this fixture's rows cannot all share one chunk.</summary>
    private const int TightBound = 900;

    [Fact]
    public async Task Every_outbound_derush_state_is_bounded_by_the_configured_StateMaxChars()
    {
        // Long-ish transcript text — the shape the finding describes (a row whose TEXT, not whose
        // count, is what makes the state large). MaxSegmentTextChars is raised alongside it so the
        // view really carries rows this size rather than the 160-character default.
        RunResult result = await RunAsync(Shape, DefaultPKeep, TightBound, transcriptChars: 300);

        result.ClientCalls.Should().BeGreaterThan(
            1, "a 900-character bound over rows this size cannot fit every asked id in one call — otherwise this test proves nothing");

        result.CapturedStates.Should().NotBeEmpty();
        foreach (string state in result.CapturedStates)
        {
            state.Length.Should().BeLessThanOrEqualTo(
                TightBound, "every outbound DecisionState must honour WorkflowEngine:Guardrails:StateMaxChars");
        }

        // The id ceiling is still a ceiling: the character bound shrinks the effective chunk, and it
        // never raises the id ceiling.
        foreach (List<string> chunk in result.CapturedChunkIds)
        {
            chunk.Should().NotBeEmpty("a chunk with no ids would be a call asking nothing");
            chunk.Count.Should().BeLessThanOrEqualTo(
                VideoAnalyzeStepExecutor.DerushChunkSize,
                "the character bound shrinks the effective chunk; it never raises the id ceiling");
        }

        // Chunking decides how many calls are made, never which ids are asked.
        result.CapturedChunkIds.SelectMany(c => c).Should().Equal(
            Shape.ScoredIds,
            "the concatenated chunks are exactly the asked ids, in offered order — set-equal AND order-equal");
    }

    [Fact]
    public async Task The_pre_pass_never_drops_or_reorders_an_offered_id_at_any_bound()
    {
        // Plan §7 item 2's failure mode, swept across bounds, so a pass that happened to agree at one
        // bound cannot hide a reordering at another.
        RunResult off = await RunAsync(Shape, DefaultPKeep, GuardrailConfig.DefaultStateMaxChars, transcriptChars: 300, derushPrePass: false);

        // Guard against a vacuous pass: the fixture must really offer every id the assertions below
        // then account for, or a silently shrunken view would make them trivially true.
        off.ViewIds.Should().Equal(Shape.ScoredIds, "the fixture must offer every s{n} and t{n} id, in offered order");

        foreach (int bound in new[] { TightBound, 2000, GuardrailConfig.DefaultStateMaxChars })
        {
            RunResult armed = await RunAsync(Shape, DefaultPKeep, bound, transcriptChars: 300);

            armed.ViewIds.Should().Equal(
                off.ViewIds, $"bound {bound}: the view's offered-id set and order are untouched by the pre-pass");
            armed.Meta.GetProperty("offeredIdCount").GetInt32().Should().Be(
                off.Meta.GetProperty("offeredIdCount").GetInt32(), $"bound {bound}");

            JsonElement derush = armed.Meta.GetProperty("derushPrePass");
            derush.GetProperty("questionCount").GetInt32().Should().Be(
                Shape.ScoredIds.Count, $"bound {bound}: every offered s{{n}}/t{{n}} id is still asked exactly once");

            string[] asked = derush.GetProperty("pKeep").EnumerateObject().Select(p => p.Name).ToArray();
            asked.Should().Equal(Shape.ScoredIds, $"bound {bound}: asked ids stay order-equal to the offered ids");

            armed.RowPKeep.Keys.Should().Equal(Shape.ScoredIds, $"bound {bound}: every row is still stamped");

            foreach (string state in armed.CapturedStates)
                state.Length.Should().BeLessThanOrEqualTo(bound, $"bound {bound}: the outbound state honours the bound");

            armed.CapturedChunkIds.SelectMany(c => c).Should().Equal(Shape.ScoredIds, $"bound {bound}");
        }
    }

    [Fact]
    public async Task The_bound_comes_from_the_guardrail_config_and_not_from_a_second_knob()
    {
        // The point of reusing one bound rather than inventing a derush-specific one: the very same
        // setting that bounds the guardrail screening state bounds this state too, and lowering it
        // buys more, smaller calls rather than fewer questions.
        RunResult tight = await RunAsync(Shape, DefaultPKeep, TightBound, transcriptChars: 300);
        RunResult loose = await RunAsync(Shape, DefaultPKeep, GuardrailConfig.DefaultStateMaxChars, transcriptChars: 300);

        tight.ClientCalls.Should().BeGreaterThan(
            loose.ClientCalls, "lowering WorkflowEngine:Guardrails:StateMaxChars must buy more, smaller derush calls");

        loose.Meta.GetProperty("derushPrePass").GetProperty("questionCount").GetInt32().Should().Be(
            tight.Meta.GetProperty("derushPrePass").GetProperty("questionCount").GetInt32(),
            "more calls, never more or fewer questions");
    }

    [Fact]
    public async Task A_single_row_larger_than_the_bound_is_truncated_but_its_id_is_never_dropped()
    {
        // The defined behaviour for one oversized row (ticket acceptance). A row cannot be split
        // across calls — that would mean asking about half an id — so its TEXT is truncated in the
        // outbound copy while the id, the question and the stamped row all survive.
        RunResult result = await RunAsync(Shape, DefaultPKeep, TightBound, transcriptChars: 5_000);

        result.ClientCalls.Should().BeGreaterThan(0);
        foreach (string state in result.CapturedStates)
            state.Length.Should().BeLessThanOrEqualTo(TightBound, "even a several-thousand-character row is bounded");

        result.CapturedStates.Should().Contain(
            state => state.Contains(VideoAnalyzeStepExecutor.DerushTruncatedMarker, StringComparison.Ordinal),
            "the truncated copy is marked as truncated, so a model is never shown a silently cut sentence");

        result.CapturedChunkIds.SelectMany(c => c).Should().Equal(
            Shape.ScoredIds,
            "a row that does not fit is still asked — as a smaller chunk, never as a missing question");

        JsonElement derush = result.Meta.GetProperty("derushPrePass");
        derush.GetProperty("questionCount").GetInt32().Should().Be(Shape.ScoredIds.Count);
        result.RowPKeep.Keys.Should().Equal(Shape.ScoredIds);
    }

    [Fact]
    public async Task A_bound_below_a_single_rows_own_skeleton_sends_that_row_whole_and_never_drops_it()
    {
        // The granularity floor, asserted explicitly rather than left implicit: at 256 characters a
        // transcript-span context (~268) cannot be reached by truncation alone, because its own JSON
        // skeleton is already larger. The invariant that must still hold is that the floor applies to
        // chunks of ONE only — a chunk carrying two or more ids is within the bound, always.
        RunResult result = await RunAsync(Shape, DefaultPKeep, VideoAnalyzeStepExecutor.MinDerushStateChars, transcriptChars: 300);

        result.CapturedChunkIds.Should().HaveCount(result.CapturedStates.Count);

        for (int i = 0; i < result.CapturedStates.Count; i++)
        {
            int length = result.CapturedStates[i].Length;
            int idCount = result.CapturedChunkIds[i].Count;

            if (idCount >= 2)
            {
                length.Should().BeLessThanOrEqualTo(
                    VideoAnalyzeStepExecutor.MinDerushStateChars,
                    "the granularity floor applies to a chunk of one; a two-or-more-id chunk is always within the bound");
            }
        }

        result.CapturedChunkIds.SelectMany(c => c).Should().Equal(
            Shape.ScoredIds, "the floor costs more calls; it never costs an id");
        result.RowPKeep.Keys.Should().Equal(Shape.ScoredIds);
    }

    [Fact]
    public void The_chunk_helper_keeps_every_id_in_order_at_any_bound()
    {
        // The pure layer, asserted directly: a bound small enough to force one id per call must still
        // produce a partition of the input, not a filtered prefix.
        List<string> ids = Enumerable.Range(0, 12).Select(i => $"s{i}").ToList();
        var raw = ids.ToDictionary(
            id => id,
            id => new JsonObject { ["id"] = id, ["text"] = new string('x', 400) },
            StringComparer.Ordinal);

        foreach (int bound in new[] { VideoAnalyzeStepExecutor.MinDerushStateChars, 512, 1024, 4096 })
        {
            // The contract is that contexts arrive already fitted — ChunkDerushIds measures, it does
            // not fit. So fit first, exactly as ResolveDerushPKeepAsync does.
            var fitted = ids.ToDictionary(
                id => id,
                id => VideoAnalyzeStepExecutor.FitDerushItemContext(id, (JsonObject)raw[id].DeepClone(), bound),
                StringComparer.Ordinal);

            IReadOnlyList<IReadOnlyList<string>> chunks = VideoAnalyzeStepExecutor.ChunkDerushIds(ids, fitted, bound);

            chunks.SelectMany(c => c).Should().Equal(ids, $"bound {bound}: a partition, in order, of the input");
            chunks.Should().OnlyContain(c => c.Count <= VideoAnalyzeStepExecutor.DerushChunkSize);

            foreach (IReadOnlyList<string> chunk in chunks)
            {
                VideoAnalyzeStepExecutor.DerushStateJson(chunk, fitted).Length.Should().BeLessThanOrEqualTo(
                    bound, $"bound {bound}: the helper measures exactly the string the client is handed");
            }
        }
    }

    [Fact]
    public void The_fit_helper_truncates_text_and_never_the_id()
    {
        // The sacrosanct fields, stated as an assertion: the id the question is keyed by, the
        // neighbours' ids and the numeric descriptors all survive the fit, because a dropped id is
        // the one outcome the ticket forbids.
        JsonObject context = new()
        {
            ["id"] = "s3",
            ["prev"] = new JsonObject { ["id"] = "s2", ["text"] = new string('p', 5_000) },
            ["item"] = new JsonObject { ["id"] = "s3", ["text"] = new string('i', 50_000), ["startSec"] = 1.5, ["score"] = 0.25 },
            ["next"] = new JsonObject { ["id"] = "s4", ["text"] = new string('n', 5_000) }
        };

        int before = StateLength("s3", context);
        before.Should().BeGreaterThan(VideoAnalyzeStepExecutor.MinDerushStateChars);

        JsonObject fitted = VideoAnalyzeStepExecutor.FitDerushItemContext(
            "s3", context, VideoAnalyzeStepExecutor.MinDerushStateChars);

        fitted["id"]!.GetValue<string>().Should().Be("s3", "the id the question is keyed by is never shortened away");
        fitted["prev"]!["id"]!.GetValue<string>().Should().Be("s2");
        fitted["next"]!["id"]!.GetValue<string>().Should().Be("s4");
        fitted["item"]!["startSec"]!.GetValue<double>().Should().Be(1.5);
        fitted["item"]!["score"]!.GetValue<double>().Should().Be(0.25);

        fitted["item"]!["text"]!.GetValue<string>().Length.Should().BeLessThan(50_000, "the oversized text is what gets cut");

        int after = StateLength("s3", fitted);
        after.Should().BeLessThanOrEqualTo(VideoAnalyzeStepExecutor.MinDerushStateChars, "the fitted context fits the bound it was fitted to");
        after.Should().BeLessThan(before);
    }

    [Fact]
    public async Task With_no_decision_provider_the_envelope_is_still_byte_identical()
    {
        // Plan §1 D2, the absolute contract. Zero Decision provider rows is this install's real
        // state, and this change must not move a single byte of what is emitted then — including when
        // an operator has moved StateMaxChars.
        RunResult off = await RunAsync(Shape, DefaultPKeep, TightBound, transcriptChars: 300, derushPrePass: false);
        RunResult priorNoProvider = await RunAsync(Shape, DefaultPKeep, TightBound, transcriptChars: 300, decisionProvider: false);
        RunResult priorNoProviderDefaultBound = await RunAsync(
            Shape, DefaultPKeep, GuardrailConfig.DefaultStateMaxChars, transcriptChars: 300, decisionProvider: false);

        priorNoProvider.CapturedStates.Should().BeEmpty("no provider means no outbound call at all");

        Sha256(priorNoProvider.Output).Should().Be(
            Sha256(off.Output), "DerushPrePass=Prior with no Decision provider must emit the same bytes as Off");
        Sha256(priorNoProviderDefaultBound.Output).Should().Be(
            Sha256(off.Output), "and the bound must not leak into that path either — a different StateMaxChars, the same bytes");

        priorNoProvider.Meta.TryGetProperty("derushPrePass", out _).Should().BeFalse();
        priorNoProvider.RowPKeep.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_guardrail_configuration_at_all_the_shipped_default_bound_applies()
    {
        // The bound must not be removable by omitting configuration: an executor constructed without
        // IOptions<GuardrailConfig> has to fall back to the shipped default, exactly as
        // AgentToolProvider.ResolveStateMaxChars does for the wrapper. Otherwise a wiring change would
        // silently turn the derush path back into an unbounded one.
        RunResult explicitDefault = await RunAsync(Shape, DefaultPKeep, GuardrailConfig.DefaultStateMaxChars, transcriptChars: 300);
        RunResult unconfigured = await RunAsync(Shape, DefaultPKeep, stateMaxChars: null, transcriptChars: 300);

        unconfigured.CapturedStates.Should().Equal(
            explicitDefault.CapturedStates,
            "an absent GuardrailConfig resolves to GuardrailConfig.DefaultStateMaxChars, not to 'unbounded'");

        foreach (string state in unconfigured.CapturedStates)
            state.Length.Should().BeLessThanOrEqualTo(GuardrailConfig.DefaultStateMaxChars);
    }

    // =======================================================================
    // Fixture and harness
    // =======================================================================

    private static readonly Guid ProjectId = Guid.NewGuid();
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    /// <summary>3 shots (<c>s0</c>-<c>s2</c>) + 6 transcript segments (<c>t0</c>-<c>t5</c>), in the
    /// view's own offered order. No silence gaps: this file is about the STATE bound, not about the
    /// drop order, which <c>DerushBudgetDropOrderTests</c> owns.</summary>
    private static readonly FixtureShape Shape = new(Shots: 3, Segments: 6);

    private static readonly Dictionary<string, double> DefaultPKeep =
        Shape.ScoredIds.ToDictionary(id => id, id => 0.75, StringComparer.Ordinal);

    private sealed record FixtureShape(int Shots, int Segments)
    {
        public IReadOnlyList<string> ShotIds { get; } = Enumerable.Range(0, Shots).Select(i => $"s{i}").ToList();

        public IReadOnlyList<string> SegmentIds { get; } = Enumerable.Range(0, Segments).Select(i => $"t{i}").ToList();

        /// <summary>The ids the pre-pass asks about, in offered order (shots, then segments).</summary>
        public IReadOnlyList<string> ScoredIds => ShotIds.Concat(SegmentIds).ToList();
    }

    private sealed record RunResult(
        string Output,
        JsonElement Meta,
        IReadOnlyList<string> ViewIds,
        IReadOnlyDictionary<string, double> RowPKeep,
        IReadOnlyList<string> CapturedStates,
        IReadOnlyList<List<string>> CapturedChunkIds,
        int ClientCalls);

    private static Task<RunResult> RunAsync(
        FixtureShape shape,
        IReadOnlyDictionary<string, double> pKeep,
        int? stateMaxChars,
        int transcriptChars,
        bool derushPrePass = true,
        bool decisionProvider = true)
        => RunAsync(shape, pKeep, stateMaxChars, transcriptChars, derushPrePass, decisionProvider, Guid.NewGuid());

    /// <param name="stateMaxChars">
    /// The configured bound, or <c>null</c> to construct the executor with NO
    /// <c>IOptions&lt;GuardrailConfig&gt;</c> at all — which is how the fallback to
    /// <see cref="GuardrailConfig.DefaultStateMaxChars"/> is asserted.
    /// </param>
    private static async Task<RunResult> RunAsync(
        FixtureShape shape,
        IReadOnlyDictionary<string, double> pKeep,
        int? stateMaxChars,
        int transcriptChars,
        bool derushPrePass,
        bool decisionProvider,
        Guid executionId)
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        var probe = new Mock<IMediaProbe>();
        var silence = new Mock<ISilenceDetector>();
        var shotDetector = new Mock<IShotDetector>();
        var transcriptionFactory = new Mock<ITranscriptionClientFactory>();
        var providerResolver = new Mock<IInferenceProviderResolver>();

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-1-analysis.json");

        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(60, 60, 1, 1920, 1080, "h264", "aac", 48000));

        // No silence detection at all: this file is not about the drop rule.
        silence.Setup(s => s.DetectAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());

        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, shape.Shots)
                .Select(i => ((double)(i * 8), (double)((i * 8) + 6)))
                .ToArray());

        SetupTranscription(transcriptionFactory, providerResolver, shape, transcriptChars);

        var capturer = new CapturingDecisionClient(pKeep);
        IDecisionClientFactory? clientFactory = null;
        if (decisionProvider)
        {
            clientFactory = new StubDecisionClientFactory(capturer);
            providerResolver
                .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedDecisionProvider(
                    ProviderId: Guid.NewGuid(), Name: "stub-decision", Kind: InferenceProviderKind.OpenAICompatible,
                    Endpoint: "http://localhost:9999", ModelName: "stub", ApiKey: "", TimeoutSeconds: 30));
        }
        else
        {
            // No provider at all: the pre-pass must decline before it ever asks.
            providerResolver
                .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ResolvedDecisionProvider?)null);
        }

        var audioExtractor = new Mock<IAudioExtractor>();
        audioExtractor
            .Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, _, outPath, ct) =>
            {
                await ws.WriteBytesAsync(outPath, new byte[100], ct);
            });

        string scratchRoot = Path.Combine(Path.GetTempPath(), "derush-state-bound-tests", Guid.NewGuid().ToString("N"));
        var executor = new VideoAnalyzeStepExecutor(
            probe.Object,
            silence.Object,
            shotDetector.Object,
            audioExtractor.Object,
            new Mock<IFrameGridSampler>().Object,
            new Mock<IKeyframeExtractor>().Object,
            new Mock<ISharpnessSampler>().Object,
            new Mock<IShotCaptioner>().Object,
            workspace.Object,
            transcriptionFactory.Object,
            providerResolver.Object,
            Options.Create(new VideoEditingOptions
            {
                ScratchPath = scratchRoot,
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 30,
                CompileTimeoutSeconds = 30
            }),
            NullLogger<VideoAnalyzeStepExecutor>.Instance,
            decisionMemoizer: derushPrePass && decisionProvider ? new DecisionQuestionMemoizer() : null,
            decisionClientFactory: clientFactory,
            inputSpanScreener: null,
            guardrailConfig: stateMaxChars is int bound
                ? Options.Create(new GuardrailConfig { StateMaxChars = bound })
                : null);

        StepExecutionResult executed = await executor.ExecuteAsync(
            CreateContext(shape, executionId, derushPrePass, transcriptChars));
        executed.Status.Should().Be(StepStatus.Completed, because: executed.ErrorDetails ?? executed.Output);

        using JsonDocument doc = JsonDocument.Parse(executed.Output);
        JsonElement root = doc.RootElement;
        List<string> viewIds = ViewIds(root.GetProperty("view"));

        var rowPKeep = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string arrayName in new[] { "shots", "silences", "segments" })
        {
            foreach (JsonElement row in root.GetProperty("view").GetProperty(arrayName).EnumerateArray())
            {
                if (row.TryGetProperty("pKeep", out JsonElement value))
                    rowPKeep[row.GetProperty("id").GetString()!] = value.GetDouble();
            }
        }

        return new RunResult(
            executed.Output,
            root.GetProperty("meta").Clone(),
            viewIds,
            rowPKeep,
            capturer.States,
            capturer.ChunkIds,
            capturer.Calls);
    }

    private static List<string> ViewIds(JsonElement view)
    {
        List<string> ids = [];
        foreach (string arrayName in new[] { "shots", "silences", "segments" })
        {
            foreach (JsonElement row in view.GetProperty(arrayName).EnumerateArray())
                ids.Add(row.GetProperty("id").GetString()!);
        }

        return ids;
    }

    private static void SetupTranscription(
        Mock<ITranscriptionClientFactory> factory,
        Mock<IInferenceProviderResolver> providerResolver,
        FixtureShape shape,
        int transcriptChars)
    {
        providerResolver
            .Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedTranscriptionProvider(
                ProviderId: Guid.NewGuid(), Name: "whisper-local", Kind: InferenceProviderKind.OpenAICompatible,
                Endpoint: "http://localhost:9999", ModelName: "whisper-1", ApiKey: "", TimeoutSeconds: 30));

        // Each segment's text is exactly transcriptChars long, so the fixture's per-row size is a
        // parameter of the test rather than an accident of the stub.
        string TextFor(int i) => new((char)('a' + (i % 26)), transcriptChars);

        var client = new Mock<ITranscriptionClient>();
        client
            .Setup(c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptResult(
                string.Join(" ", Enumerable.Range(0, shape.Segments).Select(TextFor)),
                Enumerable.Range(0, shape.Segments)
                    .Select(i => new TranscriptSegment(TextFor(i), 20.0 + (i * 5), 24.0 + (i * 5)))
                    .ToArray(),
                Array.Empty<TranscriptWord>(),
                "en"));

        factory.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(client.Object);
    }

    private static StepExecutionContext CreateContext(
        FixtureShape shape, Guid executionId, bool derushPrePass, int transcriptChars)
    {
        VideoAnalyzeStepConfig config = new VideoAnalyzeStepConfig(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput))
        with
        {
            DetectSilence = false,
            DetectShots = true,
            Transcription = VideoTranscriptionMode.Required,
            AnalyzeVisuals = false,
            AnalyzeAudioLevels = false,
            // 1000, not shape.Segments: MaxViewSegments caps the COMBINED offered list (shots +
            // segments), so a small value would silently truncate the fixture and make every
            // id-equality assertion below trivially true.
            MaxViewSegments = 1000,
            // Raised alongside the stub's text so the VIEW really carries rows this large. Left at
            // the 160-character default the fixture could not produce an oversized row at all, and
            // the bound would look satisfied for the wrong reason.
            MaxSegmentTextChars = Math.Max(160, transcriptChars),
            // No budget pressure: this file is about the OUTBOUND state bound, and the budget path
            // would otherwise drop items and route the pre-pass through its pre-resolved branch.
            // 200_000 is MaxOutputChars' own clamp ceiling — the largest value that can be asked for,
            // and comfortably above every view this fixture builds.
            MaxOutputChars = 200_000,
            DerushPrePass = derushPrePass ? VideoDerushPrePassMode.Prior : VideoDerushPrePassMode.Off
        };

        string configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = configJson
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = executionId, ProjectId = ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory =
            [
                new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null)
            ],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    /// <summary>The state string carrying exactly one id — the same measurement the pre-pass makes.</summary>
    private static int StateLength(string id, JsonObject context) =>
        VideoAnalyzeStepExecutor.DerushStateJson(
            [id], new Dictionary<string, JsonObject>(StringComparer.Ordinal) { [id] = context }).Length;

    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Answers every <c>keep &lt;id&gt;</c> question from the test's map and records the exact
    /// <see cref="DecisionState.Content"/> of every outbound call — the string the bound is a claim
    /// about — plus which ids travelled in it.
    /// </summary>
    private sealed class CapturingDecisionClient(IReadOnlyDictionary<string, double> pKeepById) : IDecisionClient
    {
        private const string QuestionPrefix = "keep ";

        public int Calls { get; private set; }

        public List<string> States { get; } = [];

        public List<List<string>> ChunkIds { get; } = [];

        public Task<DecisionResult> DecideAsync(
            DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct)
        {
            Calls++;
            States.Add(state.Content);

            var answered = new List<string>(questions.Count);
            var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);

            foreach (DecisionQuestion question in questions)
            {
                string id = question.Name.StartsWith(QuestionPrefix, StringComparison.Ordinal)
                    ? question.Name[QuestionPrefix.Length..]
                    : question.Name;
                answered.Add(id);

                double pKeep = pKeepById.TryGetValue(id, out double configured) ? configured : 0.5;

                answers[question.Name] = new DecisionAnswer(
                    question.Name,
                    DecisionKind.Noul,
                    Choice: null,
                    Score: null,
                    Noul: pKeep,
                    Confidence: null,
                    Probabilities: new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        ["yes"] = pKeep,
                        ["no"] = 1.0 - pKeep
                    });
            }

            ChunkIds.Add(answered);

            return Task.FromResult(new DecisionResult("stub", answers, InputTokens: 1, OutputTokens: 1));
        }
    }

    private sealed class StubDecisionClientFactory(IDecisionClient client) : IDecisionClientFactory
    {
        public IDecisionClient Get(ResolvedDecisionProvider provider) => client;
    }
}