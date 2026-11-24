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
/// The permanent byte-identity and offered-set coverage for the opt-in derush pre-pass and input
/// screening on <see cref="VideoAnalyzeStepExecutor"/> — the guarantees the quality bar names and that
/// had no permanent test of their own (QA-1).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this file deliberately does NOT re-test.</b> <c>DerushBudgetDropOrderTests</c> already
/// owns the drop ORDER (R1: scored before unscored, ties earliest-first, the positional fallback
/// when there is no map) and the <c>meta.derushPrePass</c> asked-vs-stamped telemetry on a
/// one-drop run (R2), plus the one pinned cross-revision sha256. None of that is duplicated here.
/// This file covers what was missing: the <i>no-Decision-provider</i> path that actually executes
/// on this install (plan §0: zero <c>Decision</c> provider rows), the fail-open polarity of the
/// derush pre-pass, the add-only/never-remove discipline on the offered id set, and the
/// per-id <c>pKeep</c> contract.
/// </para>
/// <para>
/// <b>How byte-identity is established here, and why it is stronger than a pinned hash.</b> Every
/// test below compares two runs of the SAME fixture on THIS revision — the mode under test against
/// <see cref="VideoDerushPrePassMode.Off"/> — and asserts their serialized envelopes hash equal.
/// That needs no second worktree and cannot rot: the <c>Off</c> arm is the control, computed live.
/// The pinned literal in <c>DerushBudgetDropOrderTests</c> anchors <c>Off</c> to
/// <c>origin/master</c>; this file anchors every other mode to <c>Off</c>. Together they carry the
/// whole claim without either depending on a magic constant.
/// </para>
/// </remarks>
public class DerushPrePassContractTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    // =======================================================================
    // 1. Off is inert — it does not even consult the decision resolver
    // =======================================================================

    [Fact]
    public async Task Off_does_not_consult_the_decision_resolver_and_emits_no_decision_derived_keys()
    {
        // "Off must be a no-op" is not the same claim as "Off produces the right bytes": a run that
        // resolved a provider and opened a network call before declining would still emit the same
        // envelope. The install-side cost, and the failure surface, are what this pins down.
        Harness harness = Harness.Build(shape: Shape, derushMode: VideoDerushPrePassMode.Off, decision: DecisionStub.Unreachable());

        RunResult result = await harness.RunAsync();

        harness.Resolver.Verify(
            r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "DerushPrePass=Off must not resolve a Decision provider at all");
        harness.Screener.Verify(
            s => s.ScreenAsync(It.IsAny<IReadOnlyList<InputSpan>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "ScreenInstructionLikeSpans=false (the default) must not screen anything");

        var keys = result.ViewAndMetaKeys();
        keys.Should().NotContain("derushPrePass");
        keys.Should().NotContain("screenedSpanCount");
        keys.Should().NotContain("screening");
        result.RowsCarryingPKeep().Should().BeEmpty();
    }

    // =======================================================================
    // 2. No Decision provider configured ⇒ byte-identical, for every mode
    // =======================================================================

    /// <summary>
    /// The path that ACTUALLY runs on this install. Plan §0 says it plainly: this company's
    /// database has zero <c>Decision</c> provider rows, so <c>ResolveDecisionAsync</c> returns
    /// <c>null</c> and <c>Prior</c> is a no-op that must be indistinguishable from <c>Off</c> in the
    /// emitted bytes — not "equivalent", the same bytes.
    /// </summary>
    [Theory]
    [InlineData(VideoDerushPrePassMode.Prior)]
    // Fast is declared for wave 2 and unimplemented in wave 1; it must degrade exactly like Prior
    // rather than being an accidental half-built skip.
    [InlineData(VideoDerushPrePassMode.Fast)]
    public async Task A_mode_other_than_Off_with_no_decision_provider_is_byte_identical_to_Off(VideoDerushPrePassMode mode)
    {
        RunResult off = await Harness.Build(Shape, VideoDerushPrePassMode.Off, DecisionStub.Unreachable()).RunAsync();
        Harness armed = Harness.Build(Shape, mode, DecisionStub.Unreachable());
        RunResult none = await armed.RunAsync();

        // Guard against a vacuous pass: the mode gate really did engage and really did reach the
        // provider resolution that then came back empty. Without this, an early `return` that
        // skipped the whole feature would satisfy the byte-identity claim for the wrong reason.
        armed.Resolver.Verify(
            r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the pre-pass must have run and resolved (and failed to find) a provider");

        Sha256(none.Output).Should().Be(Sha256(off.Output),
            $"{mode} with no Decision provider must emit the same bytes as Off");
        none.ViewAndMetaKeys().Should().NotContain("derushPrePass");
        none.RowsCarryingPKeep().Should().BeEmpty();
    }

    // =======================================================================
    // 3. Fail OPEN on the derush path: a throw or a partial answer is a no-op
    // =======================================================================

    [Fact]
    public async Task Prior_degrades_to_byte_identical_Off_when_the_decision_client_throws()
    {
        RunResult off = await Harness.Build(Shape, VideoDerushPrePassMode.Off, DecisionStub.Unreachable()).RunAsync();

        Harness armed = Harness.Build(Shape, VideoDerushPrePassMode.Prior, DecisionStub.Throwing());
        RunResult degraded = await armed.RunAsync();

        armed.Client.Calls.Should().BeGreaterThan(0, "the call was attempted — the degradation is a caught failure, not a skipped one");

        Sha256(degraded.Output).Should().Be(Sha256(off.Output),
            "a Decision-model outage must never change a byte of the emitted envelope, let alone fail the step");
        degraded.Status.Should().Be(StepStatus.Completed);
        degraded.ViewAndMetaKeys().Should().NotContain("derushPrePass");
        degraded.RowsCarryingPKeep().Should().BeEmpty();
    }

    [Theory]
    // One of the five asked ids has no answer at all.
    [InlineData("missing")]
    // The answer came back with the wrong kind for a Noul question.
    [InlineData("wrong-kind")]
    // The answer is outside [0,1] — not a probability, so not a pKeep.
    [InlineData("out-of-range")]
    // The answer is NaN, which passes a naive range check if it is written as `p < 0 || p > 1`.
    [InlineData("not-finite")]
    public async Task Prior_degrades_to_byte_identical_Off_on_a_partial_or_malformed_answer(string shape)
    {
        // All-or-nothing is the contract: stamping only the ids that came back would leave a view
        // in which the UN-answered rows are the ones the downstream "drop the lowest pKeep" rule
        // cannot see — i.e. the failure would silently prefer them for the wrong reason.
        RunResult off = await Harness.Build(Shape, VideoDerushPrePassMode.Off, DecisionStub.Unreachable()).RunAsync();

        Harness armed = Harness.Build(Shape, VideoDerushPrePassMode.Prior, DecisionStub.Malformed(shape));
        RunResult degraded = await armed.RunAsync();

        armed.Client.Calls.Should().BeGreaterThan(0);

        Sha256(degraded.Output).Should().Be(Sha256(off.Output),
            $"a '{shape}' answer must leave the envelope byte-identical to Off, never half-stamped");
        degraded.RowsCarryingPKeep().Should().BeEmpty("no row may carry a pKeep when the answer set was incomplete");
        degraded.ViewAndMetaKeys().Should().NotContain("derushPrePass");
    }

    // =======================================================================
    // 4. Input screening: no provider ⇒ byte-identical (and never drops a span)
    // =======================================================================

    [Fact]
    public async Task Input_screening_with_the_real_screener_and_no_provider_is_byte_identical_to_disabled()
    {
        // The REAL InputSpanScreener, not a stub: the no-provider short-circuit is exactly the code
        // path under test, and a hand-written fake would assume the answer instead of measuring it.
        RunResult disabled = await Harness
            .Build(Shape, VideoDerushPrePassMode.Off, DecisionStub.Unreachable(), screenSpans: false)
            .RunAsync();

        Harness armed = Harness.Build(Shape, VideoDerushPrePassMode.Off, DecisionStub.Unreachable(), screenSpans: true);
        RunResult noProvider = await armed.RunAsync();

        armed.Resolver.Verify(
            r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "screening was on and there were transcript spans to screen, so the provider was resolved once");

        Sha256(noProvider.Output).Should().Be(Sha256(disabled.Output),
            "screening on with no Decision provider must be indistinguishable from screening off");
        noProvider.ViewAndMetaKeys().Should().NotContain("screening");
        noProvider.ViewAndMetaKeys().Should().NotContain("screenedSpanCount");
    }

    [Fact]
    public async Task Input_screening_flags_and_quotes_a_span_without_removing_a_single_view_id()
    {
        // The brief's rule, from the additive side: on-screen text and transcribed speech are
        // routinely legitimate content, so screening points at the suspicious spans and NEVER
        // censors the view. Every offered id the compile step will later validate against must
        // still be there, in the same order.
        RunResult disabled = await Harness
            .Build(Shape, VideoDerushPrePassMode.Off, DecisionStub.Unreachable(), screenSpans: false)
            .RunAsync();

        // Flag exactly one ASR span, by id, through the real InputSpanScreener. A Decision provider
        // row must exist for the screen to ask anything at all — and with DerushPrePass=Off the
        // derush path never touches this client, so only screening can consume it.
        Harness armed = Harness.Build(
            Shape,
            VideoDerushPrePassMode.Off,
            DecisionStub.Uniform(DefaultPKeep),
            screenSpans: true,
            flagSpans: new[] { "t0" });
        RunResult flagged = await armed.RunAsync();

        JsonElement screening = flagged.View.GetProperty("screening");

        List<string> flaggedIds = screening.GetProperty("flaggedSpanIds").EnumerateArray()
            .Select(v => v.GetString()!)
            .ToList();
        flaggedIds.Should().Equal(
            new List<string> { "t0" }, "only the span the model actually flagged may be listed");

        string quoted = screening.GetProperty("flagged").GetProperty("t0").GetString()!;
        quoted.Should().Be(armed.SpanTextById("t0"), "the flagged span is QUOTED — its own text, verbatim");
        screening.GetProperty("block").GetString().Should().NotBeNullOrWhiteSpace(
            "a flagged span carries the untrusted-content note into the prompt");

        flagged.Meta.GetProperty("screenedSpanCount").GetInt32()
            .Should().Be(armed.ScreenableSpanCount, "the count describes what was screened, not what was flagged");

        // The load-bearing half: not one offered id moved.
        flagged.ViewIds().Should().Equal(disabled.ViewIds(),
            "screening is add-only — flagging a span must not add, remove or reorder an offered id");
        flagged.Meta.GetProperty("offeredIdCount").GetInt32()
            .Should().Be(disabled.Meta.GetProperty("offeredIdCount").GetInt32());
    }

    // =======================================================================
    // 5. The per-id pKeep contract and the offered-set invariant (plan §7 item 2)
    // =======================================================================

    [Fact]
    public async Task Every_offered_shot_and_transcript_id_is_asked_and_carries_a_pKeep_in_the_unit_interval()
    {
        var pKeep = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["s0"] = 0.0,   // the boundary values are legal probabilities
            ["s1"] = 1.0,
            ["s2"] = 0.42,
            ["t0"] = 0.87,
            ["t1"] = 0.13
        };

        Harness harness = Harness.Build(Shape, VideoDerushPrePassMode.Prior, DecisionStub.Answering(pKeep), budget: NoBudget);
        RunResult result = await harness.RunAsync();

        JsonElement derush = result.Meta.GetProperty("derushPrePass");
        string[] asked = derush.GetProperty("pKeep").EnumerateObject().Select(p => p.Name).ToArray();

        asked.Should().BeEquivalentTo(Shape.ScoredIds,
            "exactly one 'keep <id>' question per offered s{n}/t{n} id — every one, and nothing else");
        derush.GetProperty("questionCount").GetInt32().Should().Be(Shape.ScoredIds.Count);

        // The g{n} silence gaps are deliberately OUTSIDE this pre-pass's vocabulary: it has no
        // evidence to ask about, and an invented pKeep would make the drop rule trust a number
        // nothing produced.
        asked.Should().NotContain(Shape.SilenceIds);

        foreach (string id in Shape.ScoredIds)
        {
            double stamped = result.RowPKeep(id);
            stamped.Should().BeInRange(0.0, 1.0, $"{id} must carry a real probability");
            stamped.Should().Be(pKeep[id]);
        }

        result.RowsCarryingPKeep().Should().BeEquivalentTo(Shape.ScoredIds,
            "pKeep is stamped on the scored rows only");
    }

    [Fact]
    public async Task The_pre_pass_neither_adds_removes_nor_reorders_an_offered_id()
    {
        // Plan §7 item 2's failure mode, stated positively: a silently dropped offered id makes the
        // model's legal answer set disagree with the set the compile step will accept. Run the SAME
        // fixture twice under a budget that cannot bind, so the two runs differ in nothing but the
        // pre-pass, and compare the views id-by-id and array-by-array.
        RunResult off = await Harness
            .Build(Shape, VideoDerushPrePassMode.Off, DecisionStub.Unreachable(), budget: NoBudget)
            .RunAsync();

        RunResult prior = await Harness
            .Build(Shape, VideoDerushPrePassMode.Prior, DecisionStub.Uniform(DefaultPKeep), budget: NoBudget)
            .RunAsync();

        off.Meta.GetProperty("truncated").GetBoolean().Should().BeFalse("the comparison is only meaningful with no budget pressure");

        prior.ViewIds().Should().Equal(off.ViewIds(),
            "OfferedIds is set-equal AND order-equal before and after the pre-pass");
        prior.ViewIds().Should().Equal(Shape.AllIds,
            "and the offered order is the fixture's own: shots, then silence gaps, then transcript spans");

        prior.Meta.GetProperty("offeredIdCount").GetInt32()
            .Should().Be(off.Meta.GetProperty("offeredIdCount").GetInt32());
        prior.Meta.GetProperty("droppedItems").GetInt32().Should().Be(0);

        // The ONLY structural difference is the additive pKeep key on the scored rows.
        prior.RowsCarryingPKeep().Should().BeEquivalentTo(Shape.ScoredIds);
        prior.RowKeys("s0").Should().BeEquivalentTo(off.RowKeys("s0").Concat(["pKeep"]),
            "a stamped row gains pKeep and nothing else — no renamed, reordered or dropped key");
        prior.RowKeys("g0").Should().Equal(off.RowKeys("g0"),
            "an unscored row is untouched, key for key");
    }

    [Fact]
    public async Task The_asked_question_count_never_tracks_the_survivors_at_any_cut_depth()
    {
        // R2's invariant, generalized off the single calibrated one-drop budget that
        // DerushBudgetDropOrderTests uses: across a whole sweep of budgets, `questionCount` stays
        // pinned at the number of offered s/t ids the pre-pass asked about BEFORE the budget path
        // dropped anything, while `stampedCount` falls. That gap is the whole point — reporting the
        // survivors as the asked count is telemetry that lies about what the run cost.
        RunResult baseline = await Harness
            .Build(Shape, VideoDerushPrePassMode.Off, DecisionStub.Unreachable(), budget: NoBudget)
            .RunAsync();
        int full = baseline.Meta.GetProperty("outputChars").GetInt32();

        var interesting = new List<(int Budget, int QuestionCount, int Stamped, int Dropped)>();
        int[] cuts = [1, 2, 4, 8, 16, 32, 64, 128];

        foreach (int cut in cuts)
        {
            int budget = Math.Max(256, full - cut);
            RunResult result = await Harness
                .Build(Shape, VideoDerushPrePassMode.Prior, DecisionStub.Uniform(DefaultPKeep), budget: budget)
                .RunAsync();

            // A cut deep enough to consume every scored item leaves nothing to stamp and the block
            // legitimately disappears; that is not the invariant under test here.
            if (!result.Meta.TryGetProperty("derushPrePass", out JsonElement derush))
                continue;

            derush.GetProperty("questionCount").GetInt32().Should().Be(
                Shape.ScoredIds.Count,
                $"budget {budget}: the asked count describes what was asked, not what survived");
            derush.GetProperty("stampedCount").GetInt32()
                .Should().Be(result.RowsCarryingPKeep().Count, "stampedCount is the survivor count");
            derush.GetProperty("droppedCount").GetInt32().Should().Be(
                derush.GetProperty("questionCount").GetInt32() - derush.GetProperty("stampedCount").GetInt32());

            interesting.Add((
                budget,
                derush.GetProperty("questionCount").GetInt32(),
                derush.GetProperty("stampedCount").GetInt32(),
                derush.GetProperty("droppedCount").GetInt32()));
        }

        interesting.Should().NotBeEmpty("the sweep must have produced at least one droppable run");

        interesting.Where(r => r.Dropped > 0).Should().NotBeEmpty(
            "at least one budget must actually have dropped an ASKED item, or the invariant above proved nothing");
        interesting.Where(r => r.Dropped > 1).Should().NotBeEmpty(
            "and at least one must have dropped SEVERAL, which is where questionCount != stampedCount stops being a coincidence");
    }

    // =======================================================================
    // Fixture
    // =======================================================================

    /// <summary>
    /// A MIXED fixture — scored <c>s{n}</c> shots, unscored <c>g{n}</c> silence gaps and scored
    /// <c>t{n}</c> transcript spans (with real text, so input screening has something to screen) —
    /// because the offered-set and drop-order claims are only meaningful when both halves exist.
    /// </summary>
    private sealed record FixtureShape(int Shots, int Silences, int Segments)
    {
        public IReadOnlyList<string> ShotIds { get; } = Enumerable.Range(0, Shots).Select(i => $"s{i}").ToList();

        public IReadOnlyList<string> SilenceIds { get; } = Enumerable.Range(0, Silences).Select(i => $"g{i}").ToList();

        public IReadOnlyList<string> SegmentIds { get; } = Enumerable.Range(0, Segments).Select(i => $"t{i}").ToList();

        /// <summary>The ids the pre-pass asks about, in offered order.</summary>
        public IReadOnlyList<string> ScoredIds => ShotIds.Concat(SegmentIds).ToList();

        /// <summary>Every offered id, in the view's own offered order.</summary>
        public IReadOnlyList<string> AllIds => ShotIds.Concat(SilenceIds).Concat(SegmentIds).ToList();
    }

    private static readonly FixtureShape Shape = new(Shots: 3, Silences: 4, Segments: 2);

    private const int NoBudget = 200_000;
    private const double DefaultPKeep = 0.5;

    private static string SpanText(string id) => $"Sentence number {id[1..]}.";

    // =======================================================================
    // Run result
    // =======================================================================

    private sealed record RunResult(StepExecutionResult Executed, JsonElement Root)
    {
        public string Output => Executed.Output;

        public StepStatus Status => Executed.Status;

        public JsonElement View => Root.GetProperty("view");

        public JsonElement Meta => Root.GetProperty("meta");

        /// <summary>Every offered id in the view, array by array, in the view's own order.</summary>
        public List<string> ViewIds()
        {
            List<string> ids = [];
            foreach (string arrayName in new[] { "shots", "silences", "segments" })
            {
                foreach (JsonElement row in View.GetProperty(arrayName).EnumerateArray())
                    ids.Add(row.GetProperty("id").GetString()!);
            }

            return ids;
        }

        /// <summary>Every <c>view</c>/<c>meta</c> key, so an added annotation is observable.</summary>
        public List<string> ViewAndMetaKeys()
        {
            List<string> keys = [];
            foreach (JsonProperty p in View.EnumerateObject())
                keys.Add(p.Name);
            foreach (JsonProperty p in Meta.EnumerateObject())
                keys.Add(p.Name);
            return keys;
        }

        public List<string> RowsCarryingPKeep()
        {
            List<string> ids = [];
            foreach (string arrayName in new[] { "shots", "silences", "segments" })
            {
                foreach (JsonElement row in View.GetProperty(arrayName).EnumerateArray())
                {
                    if (row.TryGetProperty("pKeep", out _))
                        ids.Add(row.GetProperty("id").GetString()!);
                }
            }

            return ids;
        }

        public double RowPKeep(string id)
        {
            foreach (string arrayName in new[] { "shots", "silences", "segments" })
            {
                foreach (JsonElement row in View.GetProperty(arrayName).EnumerateArray())
                {
                    if (row.GetProperty("id").GetString() == id)
                    {
                        row.TryGetProperty("pKeep", out JsonElement stamped)
                            .Should().BeTrue($"{id} must carry a pKeep");
                        return stamped.GetDouble();
                    }
                }
            }

            throw new InvalidOperationException($"{id} is not in the view");
        }

        public List<string> RowKeys(string id)
        {
            foreach (string arrayName in new[] { "shots", "silences", "segments" })
            {
                foreach (JsonElement row in View.GetProperty(arrayName).EnumerateArray())
                {
                    if (row.GetProperty("id").GetString() == id)
                        return row.EnumerateObject().Select(p => p.Name).ToList();
                }
            }

            throw new InvalidOperationException($"{id} is not in the view");
        }
    }

    // =======================================================================
    // Decision client doubles
    // =======================================================================

    /// <summary>
    /// Answers the derush pre-pass's <c>keep &lt;id&gt;</c> questions from a fixed map, counts
    /// outbound calls, and can be told to throw or to answer malformed — the two failure polarities
    /// the fail-open contract is about.
    /// </summary>
    private sealed class StubDecisionClient : IDecisionClient
    {
        private const string QuestionPrefix = "keep ";

        private readonly IReadOnlyDictionary<string, double>? _pKeepById;
        private readonly double _defaultPKeep;
        private readonly string? _malformedShape;
        private readonly bool _throw;

        private StubDecisionClient(
            IReadOnlyDictionary<string, double>? pKeepById,
            double defaultPKeep,
            string? malformedShape,
            bool expectedToThrow)
        {
            _pKeepById = pKeepById;
            _defaultPKeep = defaultPKeep;
            _malformedShape = malformedShape;
            _throw = expectedToThrow;
        }

        public static StubDecisionClient Answering(IReadOnlyDictionary<string, double> pKeep) =>
            new(pKeep, DefaultPKeep, null, false);

        public static StubDecisionClient Answering(IReadOnlyDictionary<string, double> pKeep, double defaultPKeep) =>
            new(pKeep, defaultPKeep, null, false);

        public static StubDecisionClient Throwing() => new(null, DefaultPKeep, null, true);

        public static StubDecisionClient Malformed(string shape) => new(null, DefaultPKeep, shape, false);

        public int Calls { get; private set; }

        public Task<DecisionResult> DecideAsync(
            DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct)
        {
            Calls++;

            if (_throw)
                throw new TimeoutException("decision provider is down");

            var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            foreach (DecisionQuestion question in questions)
            {
                string id = question.Name.StartsWith(QuestionPrefix, StringComparison.Ordinal)
                    ? question.Name[QuestionPrefix.Length..]
                    : question.Name;

                switch (_malformedShape)
                {
                    case "missing" when id == "s1":
                        continue;
                    case "wrong-kind":
                        answers[question.Name] = new DecisionAnswer(
                            question.Name, DecisionKind.Choice, "yes", null, null, null,
                            new Dictionary<string, double>());
                        continue;
                    case "out-of-range":
                        answers[question.Name] = Noul(question.Name, 1.4);
                        continue;
                    case "not-finite":
                        answers[question.Name] = Noul(question.Name, double.NaN);
                        continue;
                }

                double pKeep = _pKeepById is not null && _pKeepById.TryGetValue(id, out double configured)
                    ? configured
                    : _defaultPKeep;
                answers[question.Name] = Noul(question.Name, pKeep);
            }

            return Task.FromResult(new DecisionResult("stub", answers, InputTokens: 1, OutputTokens: 1));

            static DecisionAnswer Noul(string name, double p) => new(
                name, DecisionKind.Noul, Choice: null, Score: null, Noul: p, Confidence: null,
                Probabilities: new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["yes"] = double.IsFinite(p) ? p : 0.5,
                    ["no"] = double.IsFinite(p) ? 1.0 - p : 0.5
                });
        }
    }

    private sealed class StubDecisionClientFactory(IDecisionClient client) : IDecisionClientFactory
    {
        public IDecisionClient Get(ResolvedDecisionProvider provider) => client;
    }

    /// <summary>How the fixture's Decision provider behaves.</summary>
    private readonly record struct DecisionStub(bool ProviderConfigured, StubDecisionClient Client)
    {
        /// <summary>This install's real state: no <c>Decision</c> provider row at all.</summary>
        public static DecisionStub Unreachable() =>
            new(false, StubDecisionClient.Answering(new Dictionary<string, double>()));

        public static DecisionStub Answering(IReadOnlyDictionary<string, double> pKeep) =>
            new(true, StubDecisionClient.Answering(pKeep));

        /// <summary>A provider that answers every asked id with the same probability.</summary>
        public static DecisionStub Uniform(double pKeep) =>
            new(true, StubDecisionClient.Answering(new Dictionary<string, double>(), pKeep));

        public static DecisionStub Throwing() => new(true, StubDecisionClient.Throwing());

        public static DecisionStub Malformed(string shape) => new(true, StubDecisionClient.Malformed(shape));
    }

    // =======================================================================
    // Harness
    // =======================================================================

    /// <summary>
    /// Builds a <see cref="VideoAnalyzeStepExecutor"/> over the fixture with stubbed media
    /// dependencies (zero ffmpeg, zero network) and a controlled Decision surface, and runs it once.
    /// </summary>
    private sealed class Harness
    {
        private readonly FixtureShape _shape;
        private readonly VideoAnalyzeStepConfig _config;
        private readonly Guid _executionId;
        private readonly Mock<IInputSpanScreener> _screener;
        private readonly IInputSpanScreener? _realScreener;
        private readonly List<InputSpan> _screenedSpans = [];

        private Harness(
            FixtureShape shape,
            VideoAnalyzeStepConfig config,
            Guid executionId,
            Mock<IInferenceProviderResolver> resolver,
            Mock<IInputSpanScreener> screener,
            IInputSpanScreener? realScreener,
            StubDecisionClient client)
        {
            _shape = shape;
            _config = config;
            _executionId = executionId;
            Resolver = resolver;
            _screener = screener;
            _realScreener = realScreener;
            Client = client;
        }

        public Mock<IInferenceProviderResolver> Resolver { get; }

        public Mock<IInputSpanScreener> Screener => _screener;

        public StubDecisionClient Client { get; }

        public int ScreenableSpanCount => _screenedSpans.Count;

        public string SpanTextById(string id) =>
            _screenedSpans.Single(s => s.SpanId == id).Text;

        public static Harness Build(
            FixtureShape shape,
            VideoDerushPrePassMode derushMode,
            DecisionStub decision,
            bool screenSpans = false,
            IReadOnlyCollection<string>? flagSpans = null,
            int budget = NoBudget)
        {
            var resolver = new Mock<IInferenceProviderResolver>();

            resolver
                .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(decision.ProviderConfigured
                    ? new ResolvedDecisionProvider(
                        ProviderId: Guid.NewGuid(), Name: "stub-decision", Kind: InferenceProviderKind.OpenAICompatible,
                        Endpoint: "http://localhost:9999", ModelName: "stub", ApiKey: "", TimeoutSeconds: 30)
                    : null);

            VideoAnalyzeStepConfig config = BuildConfig(shape, derushMode, screenSpans, budget);

            // A Mock in every case, so "was the screener consulted at all?" is always verifiable;
            // when screening is on it is backed by a REAL InputSpanScreener over the same resolver,
            // so the no-provider short-circuit is the code under test rather than a restatement.
            var screenerMock = new Mock<IInputSpanScreener>();
            IInputSpanScreener? real = null;
            if (screenSpans)
            {
                var flag = flagSpans ?? [];
                real = new InputSpanScreener(
                    resolver.Object,
                    new StubDecisionClientFactory(flag.Count == 0
                        ? StubDecisionClient.Answering(new Dictionary<string, double>())
                        : new SpanFlaggingClient(flag)),
                    Options.Create(new GuardrailConfig()),
                    NullLogger<InputSpanScreener>.Instance);

                screenerMock
                    .Setup(s => s.ScreenAsync(It.IsAny<IReadOnlyList<InputSpan>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
                    .Returns<IReadOnlyList<InputSpan>, DecisionGateConfig, CancellationToken>(
                        (spans, cfg, ct) => real.ScreenAsync(spans, cfg, ct));
            }

            return new Harness(
                shape,
                config,
                Guid.NewGuid(),
                resolver,
                screenerMock,
                real,
                decision.Client);
        }

        private static VideoAnalyzeStepConfig BuildConfig(
            FixtureShape shape, VideoDerushPrePassMode mode, bool screenSpans, int budget) =>
            new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput))
            with
            {
                DetectSilence = shape.Silences > 0,
                DetectShots = shape.Shots > 0,
                Transcription = VideoTranscriptionMode.Required,
                // Visual/audio analysis off: this file is about the decision surfaces, and a richer
                // view would only add noise (and more drops) between the fixture and the contract.
                AnalyzeVisuals = false,
                AnalyzeAudioLevels = false,
                MaxViewSegments = 1000,
                MaxOutputChars = budget,
                DerushPrePass = mode,
                ScreenInstructionLikeSpans = screenSpans
            };

        public async Task<RunResult> RunAsync()
        {
            var workspace = new Mock<IProjectFileWorkspace>();
            var probe = new Mock<IMediaProbe>();
            var silence = new Mock<ISilenceDetector>();
            var shotDetector = new Mock<IShotDetector>();
            var transcriptionFactory = new Mock<ITranscriptionClientFactory>();

            workspace
                .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
                {
                    File.WriteAllBytes(destPath, [0x00, 0x01, 0x02]);
                    return Task.CompletedTask;
                });
            workspace
                .Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-1-analysis.json");

            probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(30, 30, 1, 1920, 1080, "h264", "aac", 48000));

            // 30s source; shots every 2s so each is comfortably inside the media and each silence gap
            // sits between two of them.
            silence.Setup(s => s.DetectAsync(
                    It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Enumerable.Range(0, _shape.Silences)
                    .Select(i => (10.0 + (i * 2), 10.5 + (i * 2)))
                    .ToArray());

            shotDetector
                .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Enumerable.Range(0, _shape.Shots)
                    .Select(i => ((double)(i * 2), (double)((i * 2) + 1)))
                    .ToArray());

            Resolver
                .Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedTranscriptionProvider(
                    ProviderId: Guid.NewGuid(), Name: "whisper-local", Kind: InferenceProviderKind.OpenAICompatible,
                    Endpoint: "http://localhost:9999", ModelName: "whisper-1", ApiKey: "", TimeoutSeconds: 30));

            var transcriptionClient = new Mock<ITranscriptionClient>();
            transcriptionClient
                .Setup(c => c.TranscribeAsync(
                    It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TranscriptResult(
                    string.Join(" ", Enumerable.Range(0, _shape.Segments).Select(i => $"Sentence number {i}.")),
                    Enumerable.Range(0, _shape.Segments)
                        .Select(i => new TranscriptSegment(SpanText($"t{i}"), 12.0 + (i * 2), 13.0 + (i * 2)))
                        .ToArray(),
                    Array.Empty<TranscriptWord>(),
                    "en"));

            transcriptionFactory.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(transcriptionClient.Object);

            var audioExtractor = new Mock<IAudioExtractor>();
            audioExtractor
                .Setup(a => a.ExtractWavAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<IMediaWorkspace, string, string, CancellationToken>(async (ws, _, outPath, ct) =>
                {
                    await ws.WriteBytesAsync(outPath, new byte[100], ct);
                });

            string scratchRoot = Path.Combine(Path.GetTempPath(), "derush-contract-tests", Guid.NewGuid().ToString("N"));

            // The screener under test records the spans it was actually handed, so a test can assert
            // the quoted text is the view's own text rather than the model's echo of it.
            _screener
                .Setup(s => s.ScreenAsync(It.IsAny<IReadOnlyList<InputSpan>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyList<InputSpan>, DecisionGateConfig, CancellationToken>((spans, _, _) =>
                {
                    _screenedSpans.Clear();
                    _screenedSpans.AddRange(spans);
                })
                .Returns<IReadOnlyList<InputSpan>, DecisionGateConfig, CancellationToken>(
                    (spans, cfg, ct) => _realScreener is null
                        ? Task.FromResult(InputScreeningResult.Empty)
                        : _realScreener.ScreenAsync(spans, cfg, ct));

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
                Resolver.Object,
                Options.Create(new VideoEditingOptions
                {
                    ScratchPath = scratchRoot,
                    MaxConcurrentJobs = 1,
                    AnalyzeTimeoutSeconds = 30,
                    CompileTimeoutSeconds = 30
                }),
                NullLogger<VideoAnalyzeStepExecutor>.Instance,
                decisionMemoizer: new DecisionQuestionMemoizer(),
                decisionClientFactory: new StubDecisionClientFactory(Client),
                inputSpanScreener: _screener.Object);

            StepExecutionResult executed = await executor.ExecuteAsync(CreateContext(_config, _executionId, _shape));
            executed.Status.Should().Be(StepStatus.Completed, because: executed.ErrorDetails ?? executed.Output);

            using JsonDocument doc = JsonDocument.Parse(executed.Output);
            return new RunResult(executed, doc.RootElement.Clone());
        }

        /// <summary>Flags exactly the named spans and nothing else — P(instruction-like) = 1.0 vs 0.0.</summary>
        private sealed class SpanFlaggingClient(IReadOnlyCollection<string> flagIds) : IDecisionClient
        {
            public Task<DecisionResult> DecideAsync(
                DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct)
            {
                var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
                foreach (DecisionQuestion question in questions)
                {
                    string spanId = question.Name["instruction_like:".Length..];
                    bool flag = flagIds.Contains(spanId);
                    double p = flag ? 1.0 : 0.0;

                    answers[question.Name] = new DecisionAnswer(
                        question.Name, DecisionKind.Noul, Choice: null, Score: null, Noul: p, Confidence: null,
                        Probabilities: new Dictionary<string, double>(StringComparer.Ordinal) { ["yes"] = p, ["no"] = 1.0 - p });
                }

                return Task.FromResult(new DecisionResult("stub", answers, 1, 1));
            }
        }
    }

    private static StepExecutionContext CreateContext(VideoAnalyzeStepConfig config, Guid executionId, FixtureShape shape)
    {
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

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}