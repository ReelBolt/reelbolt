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
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The program-lead rulings R1 (drop order: scored items before unscored ones) and R2 (the
/// <c>meta.derushPrePass</c> asked-vs-stamped telemetry contract) on the shipped derush pre-pass and
/// budget-aware drop, driven end to end through <see cref="VideoAnalyzeStepExecutor"/>.
/// </summary>
/// <remarks>
/// <para>
/// The fixture is deliberately MIXED: every run offers scored <c>s{n}</c> shots, scored <c>t{n}</c>
/// transcript spans AND unscored <c>g{n}</c> silence gaps, because the whole point of R1 is the
/// partition between those two groups. That is why silence detection and transcription are both on
/// with stubbed detectors — a shots-only fixture could not tell the two rules apart.
/// </para>
/// <para>
/// <b>Why the budget is calibrated against the fixture rather than hard-coded.</b> The
/// degrade/drop loop drops whole items until the serialized VIEW fits, so "drop exactly one item" is
/// <c>MaxOutputChars = (full-view length) - 1</c>: every item is worth more than one character, so
/// exactly one drop has to happen and no second one can. Each test therefore first runs the same
/// fixture with a 200k budget to read <c>meta.outputChars</c> — the off-budget path, which performs
/// no drop and no pre-pass resolution, so it cannot contaminate the run under test — and then runs
/// the real execution with that budget minus one (or, where a much deeper cut is wanted, a fraction
/// of it). The one test that pins a literal sha256 cannot do that (the fixture has to be
/// reproducible on another revision), so it hard-codes its budget.
/// </para>
/// <para>
/// <b>Scope (ruling R4).</b> These tests cover R1 and R2, plus the one pinned off-path sha256 the
/// ticket asks for. Permanent derush byte-identity coverage belongs to QA and is deliberately NOT
/// duplicated here.
/// </para>
/// </remarks>
public class DerushBudgetDropOrderTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    /// <summary>The pKeep a stub returns for an id the test did not name — deliberately mid-range.</summary>
    private const double DefaultPKeep = 0.5;

    // =======================================================================
    // R1 — the drop-order partition
    // =======================================================================

    [Fact]
    public async Task Scored_item_is_dropped_before_any_unscored_one_and_every_silence_gap_survives()
    {
        // R1 point 1: under pressure that needs exactly ONE drop, the victim is the lowest-pKeep
        // SCORED id. The fixture's lowest-pKeep id is s1 (0.05) while the trailing positional item —
        // what the pre-T-205 rule would have dropped — is the last transcript segment t1.
        var shape = new FixtureShape(Shots: 3, Silences: 4, Segments: 2);
        var pKeep = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["s0"] = 0.90,
            ["s1"] = 0.05,
            ["s2"] = 0.80,
            ["t0"] = 0.70,
            ["t1"] = 0.60
        };

        RunResult result = await RunWithOneDropAsync(shape, pKeep);

        result.Dropped.Should().Equal(
            ["s1"], "the lowest-pKeep SCORED item goes first — never a g{n} silence gap, and never the trailing t1");

        // The unscored half is untouched: no g{n} left the view. `meta.offeredIdCount` is the
        // offered-set counterpart of the view (meta carries a count, not an id list), so it must
        // agree with "exactly one item went".
        shape.SilenceIds.Should().BeSubsetOf(result.PresentIds());
        result.OfferedIdCount.Should().Be(shape.AllIds.Count - 1);

        // The scored ids that were not chosen survive, so this is a *partition* and not "drop
        // everything unscored-looking".
        new[] { "s0", "s2", "t0", "t1" }.Should().BeSubsetOf(result.PresentIds());
    }

    [Fact]
    public async Task Once_the_scored_set_is_exhausted_unscored_gaps_go_in_original_positional_order()
    {
        // R1 point 2, under escalating pressure: with far fewer scored ids than the budget needs to
        // shed, the scored set is consumed first and only THEN do unscored gaps start leaving, in
        // their original positional order (earliest first), one per iteration.
        var shape = new FixtureShape(Shots: 1, Silences: 9, Segments: 1);
        var pKeep = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["s0"] = 0.30,
            ["t0"] = 0.20
        };

        RunResult result = await RunWithBudgetFractionAsync(shape, pKeep, fractionOfFullBudget: 0.5);

        int scoredCount = shape.ScoredIds.Count;
        result.Dropped.Count.Should().BeGreaterThan(
            scoredCount,
            "this test only asserts the second branch if the budget actually pushed past the scored set");

        // The whole expected DROP SEQUENCE, computed from the RULE rather than from the
        // implementation: every scored id ascending by pKeep (stable, so ties go earliest-first),
        // then the unscored ids in their original positional order. The first `drops` entries of that
        // sequence are exactly the victims — which is what "one per iteration, earliest first"
        // means once the loop has run to completion.
        List<string> dropSequence = shape.ScoredIds.OrderBy(id => pKeep[id]).Concat(shape.SilenceIds).ToList();
        var expectedDropped = dropSequence.Take(result.Dropped.Count).ToHashSet(StringComparer.Ordinal);

        // `result.Dropped` is in the VIEW's positional order, so compare like with like.
        result.Dropped.Should().Equal(shape.AllIds.Where(expectedDropped.Contains));

        // ...and the survivors are exactly what is left, still in positional order, with the latest
        // gaps — not the earliest ones — at the tail.
        result.PresentIds().Should().Equal(shape.AllIds.Where(id => !expectedDropped.Contains(id)));
        result.PresentIds().Should().OnlyContain(id => shape.SilenceIds.Contains(id));
        int gapsDropped = expectedDropped.Count(id => shape.SilenceIds.Contains(id));
        result.PresentIds().Should().Equal(shape.SilenceIds.Skip(gapsDropped));
    }

    [Fact]
    public async Task Equal_pKeep_drops_the_earliest_positioned_item_and_is_deterministic_across_runs()
    {
        // R1 point 1's tie rule: a stable ASCENDING sort whose head is the victim, so the EARLIEST
        // of the tied items goes first. Two unrelated runs of the same input must agree.
        var shape = new FixtureShape(Shots: 3, Silences: 4, Segments: 2);
        var pKeep = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["s0"] = 0.11,
            ["s1"] = 0.11,
            ["s2"] = 0.95,
            ["t0"] = 0.90,
            ["t1"] = 0.85
        };

        RunResult first = await RunWithOneDropAsync(shape, pKeep);
        RunResult second = await RunWithOneDropAsync(shape, pKeep);

        first.Dropped.Should().Equal(["s0"], "the tied pair is s0/s1 and the earliest-positioned one goes first");
        second.Dropped.Should().Equal(first.Dropped, "the same input must give the same victim every time");
        second.PresentIds().Should().Equal(first.PresentIds());
    }

    // =======================================================================
    // R2 — asked-vs-stamped telemetry
    // =======================================================================

    [Fact]
    public async Task Question_count_describes_what_was_asked_not_the_stamped_survivors()
    {
        // R2: the pre-pass resolves its probabilities BEFORE the budget's first drop (that is how
        // "drop the lowest pKeep" is decidable at all), so on a dropping run the surviving subset is
        // strictly smaller than the asked subset. Reporting the survivors as the asked count is
        // telemetry that lies about the cost of the run.
        var shape = new FixtureShape(Shots: 3, Silences: 4, Segments: 2);
        var pKeep = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["s0"] = 0.90,
            ["s1"] = 0.05,
            ["s2"] = 0.80,
            ["t0"] = 0.70,
            ["t1"] = 0.60
        };

        RunResult result = await RunWithOneDropAsync(shape, pKeep);

        result.Dropped.Count.Should().Be(1, "the fixture is calibrated to need exactly one drop");

        JsonElement derush = result.DerushPrePass();
        int questionCount = derush.GetProperty("questionCount").GetInt32();
        int chunkCount = derush.GetProperty("chunkCount").GetInt32();
        int stampedCount = derush.GetProperty("stampedCount").GetInt32();
        int droppedCount = derush.GetProperty("droppedCount").GetInt32();

        // Asked = every offered s/t id at the moment the questions were issued, i.e. BEFORE the drop.
        questionCount.Should().Be(
            shape.ScoredIds.Count,
            "one 'keep <id>' question per offered s{n}/t{n} id, however many items the budget then dropped");

        // Every asked id was answered in ONE chunk (DerushChunkSize is 8 and the fixture is smaller),
        // and that chunk was a real outbound call.
        chunkCount.Should().Be(1, "5 asked ids fit one chunk, and that chunk was not answered from the memo");
        result.ClientCalls.Should().Be(1, "one outbound DecideAsync per non-memoized chunk");

        // Survivor information lives under DISTINCT keys and never overloads the asked counts.
        stampedCount.Should().Be(questionCount - 1, "exactly the dropped s1 is missing from the stamped rows");
        stampedCount.Should().BeLessThan(questionCount);
        droppedCount.Should().Be(questionCount - stampedCount);
        stampedCount.Should().BeGreaterThan(0);

        // The pKeep node keeps its current meaning: one entry per STAMPED id, not per asked id.
        derush.GetProperty("pKeep").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(shape.ScoredIds.Except(result.Dropped));

        // The keys the ticket says keep their meaning must be untouched.
        derush.GetProperty("chunkSize").GetInt32().Should().Be(8);
        derush.GetProperty("mode").GetString().Should().Be("Prior");
        derush.TryGetProperty("acceptAt", out _).Should().BeTrue();
        derush.TryGetProperty("minMargin", out _).Should().BeTrue();
        derush.TryGetProperty("escalatedIdCount", out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_chunk_answered_wholly_from_the_memo_is_not_counted_as_an_outbound_batch()
    {
        // R2: chunkCount counts outbound DecideAsync batches ACTUALLY ISSUED. A DecisionQuestionMemoizer
        // call answered entirely from its memo (DecisionResult.Model == "memo") sends nothing and must
        // not be counted; a second identical pre-pass round inside one execution is exactly that case.
        var shape = new FixtureShape(Shots: 1, Silences: 9, Segments: 1);
        var pKeep = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["s0"] = 0.30,
            ["t0"] = 0.20
        };

        // A REAL memoizer, shared between the two runs, keyed by the SAME execution id — that is what
        // makes the second round a pure memo hit. The budget is the calibrated one-drop budget, which
        // is what keeps at least one asked id in the final view (a cut deep enough to drop every
        // scored id would leave the stamping pass with nothing to ask about, and no derush block).
        var memoizer = new DecisionQuestionMemoizer();
        Guid executionId = Guid.NewGuid();

        int budget = await BudgetForAsync(shape);
        RunResult first = await RunAsync(shape, pKeep, budget, executionId, memoizer);
        RunResult second = await RunAsync(shape, pKeep, budget, executionId, memoizer);

        first.ClientCalls.Should().Be(1, "the first round has to ask");
        first.DerushPrePass().GetProperty("chunkCount").GetInt32().Should().Be(1);

        second.ClientCalls.Should().Be(0, "every question of the second round is already answered in the memo");
        second.DerushPrePass().GetProperty("chunkCount").GetInt32()
            .Should().Be(0, "a fully memoized chunk issues no outbound call, so it contributes no chunkCount");
        second.DerushPrePass().GetProperty("questionCount").GetInt32()
            .Should().Be(first.DerushPrePass().GetProperty("questionCount").GetInt32(),
                "under-counting must never be fixed by under-reporting the questions either");

        // The answers are the memo's, so the view is unchanged by the second round.
        second.PresentIds().Should().Equal(first.PresentIds());
    }

    // =======================================================================
    // The off-path is untouched — pinned sha256
    // =======================================================================

    /// <summary>
    /// The sha256 of the emitted envelope for <see cref="OffPathFixture"/> with
    /// <c>DerushPrePass=Off</c> (the default) and a budget that forces drops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Provenance — how this literal was produced, verbatim, because a pinned magic hash with no
    /// provenance is not evidence:</b>
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     A detached worktree of the base revision was created:
    ///     <c>git worktree add --detach &lt;scratch&gt; 49661e61fd39eea7d2f5ec1a39f6578479aecc22</c>
    ///     — that commit is <c>origin/master</c> as of this ticket
    ///     ("Merge pull request #186"), i.e.
    ///     the pre-derush base: T-202/T-203 are on this branch only.
    ///   </description></item>
    ///   <item><description>
    ///     The fixture below (<see cref="OffPathFixture"/> — 3 shots, 4 silence gaps, 2 transcript
    ///     segments, <c>DetectSilence</c>/<c>DetectShots</c>/<c>Transcription=Required</c> on, visual
    ///     and audio analysis off, <c>MaxViewSegments = 1000</c>, <c>MaxOutputChars = 400</c>, stubbed
    ///     probe/detectors/transcription/workspace) was transcribed into that worktree as a
    ///     standalone xunit test class, adapted in exactly two ways: no derush config fields exist on
    ///     that revision, and its executor constructor takes no decision memoizer/client factory.
    ///   </description></item>
    ///   <item><description>
    ///     The test was run there with
    ///     <c>dotnet test --filter "FullyQualifiedName~T205OffPathHashProbe"</c>, and it wrote
    ///     <c>SHA256(result.Output)</c> — computed over the UTF-8 bytes of
    ///     <c>StepExecutionResult.Output</c>, exactly the <see cref="Sha256"/> below — to a file. It
    ///     reported <c>HASH=5a3e5f35…</c>, <c>TRUNCATED=True</c>, <c>DROPPED=5</c>,
    ///     <c>OFFERED=4</c>, <c>OUTPUTCHARS=346</c>, <c>LENGTH=1220</c>.
    ///   </description></item>
    ///   <item><description>
    ///     The literal was pasted here unchanged, no normalization of any kind. This branch produces
    ///     the identical hash for the same fixture — that equality IS the byte-identity claim.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The revision is named in the comment rather than hard-coded into the fixture, because
    /// <c>origin/master</c> moves: <c>49661e61</c> was it at the time T-205 was worked, which is the
    /// pre-derush base for this fixture (the fixture sets <c>DerushPrePass=Off</c>, so it exercises
    /// the unchanged path in both revisions).
    /// </para>
    /// </remarks>
    private const string OffPathSha256 = "5a3e5f35f559ad7e84e8ccaf7b6ba57ff88ddb409773892f377ccbc0f1e127cb";

    [Fact]
    public async Task Off_path_envelope_is_byte_identical_and_carries_no_derush_block()
    {
        RunResult first = await RunAsync(OffPathFixture, OffPathPKeep, OffPathMaxOutputChars, Guid.NewGuid(), null);
        RunResult second = await RunAsync(OffPathFixture, OffPathPKeep, OffPathMaxOutputChars, Guid.NewGuid(), null);

        first.Meta.GetProperty("truncated").GetBoolean()
            .Should().BeTrue("the fixture's budget must actually force drops, or this proves nothing");
        first.Meta.GetProperty("droppedItems").GetInt32().Should().BeGreaterThan(0);

        // The pre-pass must not have run at all: no resolution, no question, no outbound call.
        first.ClientCalls.Should().Be(0);
        first.Meta.TryGetProperty("derushPrePass", out _)
            .Should().BeFalse("DerushPrePass=Off emits no derush block at all, not an empty one");

        // ...and no view row carries a pKeep either.
        first.PresentIds().Should().NotBeEmpty();
        first.ScoreRows.Should().BeEmpty("the pre-pass is the only writer of pKeep, and it did not run");

        Sha256(first.Output).Should().Be(OffPathSha256,
            "the DerushPrePass=Off envelope must be byte-identical to the pre-derush revision");
        Sha256(second.Output).Should().Be(OffPathSha256, "and deterministic across runs");
    }

    // =======================================================================
    // R1 as an INVARIANT across escalating pressure (QA-1 extension)
    // =======================================================================

    /// <summary>
    /// The three tests above each pin ONE calibrated budget. This one sweeps a range of them and
    /// asserts R1 as a property of every depth rather than of one fixture point: the victims are
    /// always the next entries of the canonical rule order, a deeper cut never resurrects a dropped
    /// id, and no <c>g{n}</c> gap leaves the view while a scored item is still present.
    /// </summary>
    /// <remarks>
    /// This is the shape that a per-call accident cannot satisfy. A drop rule that consulted the
    /// remaining list wrongly, or that fell back to the trailing rule under some state, would agree
    /// with the single-point tests at their one budget and disagree here.
    /// </remarks>
    [Fact]
    public async Task Escalating_budget_pressure_always_drops_a_monotone_prefix_of_the_rule_order()
    {
        var shape = new FixtureShape(Shots: 3, Silences: 4, Segments: 2);
        var pKeep = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["s0"] = 0.31,
            ["s1"] = 0.12,
            ["s2"] = 0.77,
            ["t0"] = 0.55,
            ["t1"] = 0.44
        };

        // The R1 sequence computed from the RULE, not from the implementation: scored ids ascending
        // (stable, so ties go earliest-positioned first), then the unscored gaps in positional order.
        List<string> ruleOrder = shape.ScoredIds.OrderBy(id => pKeep[id]).Concat(shape.SilenceIds).ToList();

        int full = await FullViewBudgetAsync(shape);

        HashSet<string>? shallowerCut = null;
        int runsWithAnyDrop = 0;

        foreach (int cut in new[] { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48 })
        {
            RunResult result = await RunAsync(
                shape, pKeep, Math.Max(256, full - cut), Guid.NewGuid(), new DecisionQuestionMemoizer());

            HashSet<string> dropped = result.Dropped.ToHashSet(StringComparer.Ordinal);

            // 1. Every victim is a candidate the rule names at all — never an id the rule has no
            //    opinion about (the pre-T-205 "unscored sorts below everything" artefact).
            dropped.Should().BeSubsetOf(ruleOrder);

            // 2. The victims are exactly the FIRST |dropped| entries of the rule order, at every depth.
            ruleOrder.Take(dropped.Count).Should().BeEquivalentTo(dropped);

            // 3. Monotone: raising the pressure never brings an id back.
            if (shallowerCut is not null)
                shallowerCut.IsSubsetOf(dropped).Should().BeTrue("a deeper cut must never resurrect a dropped id");

            // 4. No unscored gap goes while any scored item remains — the partition, at every depth.
            if (shape.SilenceIds.Any(dropped.Contains))
                shape.ScoredIds.All(dropped.Contains).Should().BeTrue(
                    "an unscored g{n} gap may only be dropped once no scored item is left");

            // 5. The survivors are the rule order's complement, still in positional order.
            result.PresentIds().Should().Equal(shape.AllIds.Where(id => !dropped.Contains(id)));

            if (dropped.Count > 0)
                runsWithAnyDrop++;

            shallowerCut = dropped;
        }

        runsWithAnyDrop.Should().BeGreaterThan(2, "the sweep must actually have forced drops at several depths");
        shallowerCut.Should().NotBeNull();
    }

    // =======================================================================
    // The drop-order helper, asserted directly (the cheaper second layer)
    // =======================================================================

    [Fact]
    public void Drop_order_helper_drops_the_lowest_scored_item_then_the_earliest_unscored_one()
    {
        // Offered order is shots, then silence gaps, then transcript segments (BuildOfferedItems).
        List<VideoAnalyzeStepExecutor.OfferedItem> offered = OfferedOrder(3, 2, 1);
        var pKeep = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["s0"] = 0.15,
            ["s1"] = 0.20,
            ["s2"] = 0.40,
            ["t0"] = 0.60
        };

        // Scored first, ascending, ties earliest-first: s0, s1, s2, t0 — a g{n} gap (which has no
        // evidence at all) is never preferred over any of them.
        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, pKeep).Should().Be(0); // s0
        offered.RemoveAt(0);
        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, pKeep).Should().Be(0); // s1
        offered.RemoveAt(0);
        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, pKeep).Should().Be(0); // s2
        offered.RemoveAt(0);

        offered.Select(i => i.Id).Should().Equal("g0", "g1", "t0");
        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, pKeep).Should().Be(2, "t0 is the last scored item left");
        offered.RemoveAt(2);

        // Scored evidence exhausted: the unscored gaps go in positional order, earliest first.
        offered.Select(i => i.Id).Should().Equal("g0", "g1");
        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, pKeep).Should().Be(0);
        offered.RemoveAt(0);
        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, pKeep).Should().Be(0);
    }

    [Fact]
    public void Drop_order_helper_does_not_treat_a_scored_item_as_lower_than_an_unscored_one()
    {
        // The exact artefact R1 corrects: a HIGH-pKeep scored item must still be preferred over an
        // unscored gap, because "no evidence" is not "evidence of low value". The old rule sorted an
        // unscored id at double.NegativeInfinity and so dropped it first, every time.
        List<VideoAnalyzeStepExecutor.OfferedItem> offered = OfferedOrder(1, 3, 1);
        var pKeep = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["s0"] = 0.99,
            ["t0"] = 0.98
        };

        offered.Select(i => i.Id).Should().Equal("s0", "g0", "g1", "g2", "t0");
        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, pKeep).Should().Be(4, "the lowest SCORED item is t0, not g0");
    }

    [Fact]
    public void Drop_order_helper_keeps_todays_trailing_rule_when_there_is_no_map_to_sort_on()
    {
        List<VideoAnalyzeStepExecutor.OfferedItem> offered = OfferedOrder(2, 1, 1);

        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, null)
            .Should().Be(offered.Count - 1, "no map => today's rule, byte-for-byte");
        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, new Dictionary<string, double>())
            .Should().Be(offered.Count - 1, "an EMPTY map is the same signal as null");

        // A non-empty map whose ids are all gone from the list still must not fall back to trailing.
        var staleMap = new Dictionary<string, double>(StringComparer.Ordinal) { ["s9"] = 0.1 };
        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, staleMap)
            .Should().Be(0, "with no scored id left in the list, unscored items go earliest-positional first");
    }

    [Fact]
    public void Drop_order_helper_prefers_the_earliest_of_a_tied_pair()
    {
        List<VideoAnalyzeStepExecutor.OfferedItem> offered = OfferedOrder(3, 1, 0);
        var pKeep = new Dictionary<string, double>(StringComparer.Ordinal) { ["s0"] = 0.5, ["s1"] = 0.5, ["s2"] = 0.9 };

        VideoAnalyzeStepExecutor.IndexOfNextDrop(offered, pKeep)
            .Should().Be(0, "a stable ascending sort's head is the victim: the earliest of the tied items");
    }

    // =======================================================================
    // Fixtures and infrastructure
    // =======================================================================

    /// <summary>
    /// The offered id mix of a fixture, in the view's own offered order (shots, then silence gaps,
    /// then transcript segments — see <c>BuildOfferedItems</c>).
    /// </summary>
    private sealed record FixtureShape(int Shots, int Silences, int Segments)
    {
        /// <summary>
        /// When set, the fixture is one long talking-head source of this many seconds: shots and
        /// word-level transcript segments are spread evenly over the WHOLE duration (no silences).
        /// Unset keeps the original 30 s fixture byte for byte.
        /// </summary>
        public double? TalkingHeadSec { get; init; }

        public double SourceSec => TalkingHeadSec ?? 30;

        public IReadOnlyList<string> ShotIds { get; } = Enumerable.Range(0, Shots).Select(i => $"s{i}").ToList();

        public IReadOnlyList<string> SilenceIds { get; } = Enumerable.Range(0, Silences).Select(i => $"g{i}").ToList();

        public IReadOnlyList<string> SegmentIds { get; } = Enumerable.Range(0, Segments).Select(i => $"t{i}").ToList();

        /// <summary>The ids the pre-pass actually asks about, in offered order.</summary>
        public IReadOnlyList<string> ScoredIds => ShotIds.Concat(SegmentIds).ToList();

        /// <summary>Every offered id, in offered order.</summary>
        public IReadOnlyList<string> AllIds => ShotIds.Concat(SilenceIds).Concat(SegmentIds).ToList();
    }

    // ---- Whole-timeline coverage (view-coverage-1) ----

    private static readonly FixtureShape TalkingHead = new(Shots: 89, Silences: 0, Segments: 683) { TalkingHeadSec = 170 };

    [Theory]
    [InlineData(24_000)]
    [InlineData(60_000)]
    public async Task A_long_talking_head_view_reaches_the_end_of_the_source_at_any_budget(int maxOutputChars)
    {
        RunResult run = await RunAsync(TalkingHead, new Dictionary<string, double>(), maxOutputChars, Guid.NewGuid(), null);

        JsonElement coverage = run.Meta.GetProperty("offeredCoverage");
        coverage.GetProperty("sourceDurationSec").GetDouble().Should().BeApproximately(170, 0.01);
        coverage.GetProperty("lastSec").GetDouble().Should().BeGreaterThanOrEqualTo(170 * 0.95);
        coverage.GetProperty("firstSec").GetDouble().Should().BeLessThan(1);
        run.ViewIds.Should().OnlyHaveUniqueItems();
        run.ViewIds.Should().OnlyContain(id => TalkingHead.AllIds.Contains(id), "every shown id must stay resolvable");
        // In outline mode every id of the analysis is a valid keep (meta.offeredIdCount); meta.viewItemCount is what the view shows.
        bool outlined = run.Meta.GetProperty("droppedItems").GetInt32() > 0;
        run.OfferedIdCount.Should().Be(outlined ? TalkingHead.AllIds.Count : run.ViewIds.Count);
        if (outlined)
            run.Meta.GetProperty("viewItemCount").GetInt32().Should().Be(run.ViewIds.Count);
        run.Output.Should().NotContain("view_coverage_partial");

        // A view that did not fit becomes the outline of the whole film plus the first section only.
        if (run.Meta.GetProperty("droppedItems").GetInt32() > 0)
        {
            coverage.GetProperty("mode").GetString().Should().Be("outline");
            run.Meta.GetProperty("speechOutline").GetProperty("sections").GetInt32().Should().BeGreaterThanOrEqualTo(3);
            run.Output.Should().Contain("\"gist\"").And.Contain("\"cuts\"");
            run.ViewIds.Should().Contain("s0").And.Contain("t0").And.NotContain("t682");
        }
    }

    [Fact]
    public void Coverage_drop_thins_the_interior_and_keeps_both_ends()
    {
        List<VideoAnalyzeStepExecutor.OfferedItem> offered = Enumerable.Range(0, 10)
            .Select(i => new VideoAnalyzeStepExecutor.OfferedItem(
                "segment", $"t{i}", null, null, new VideoAnalysisSegment($"t{i}", null, i, i + 0.5, "w")))
            .ToList();

        VideoViewCoverage.ThinToCount(offered, 4);

        offered.Select(o => o.Id).Should().Contain("t0").And.Contain("t9").And.HaveCount(4);
        offered.Select(o => o.Segment!.StartSec).Zip(offered.Skip(1).Select(o => o.Segment!.StartSec), (a, b) => b - a)
            .Should().OnlyContain(gap => gap <= 4);
    }

    private static List<VideoAnalyzeStepExecutor.OfferedItem> OfferedOrder(int shots, int silences, int segments)
    {
        var shape = new FixtureShape(shots, silences, segments);
        return shape.AllIds.Select(id => new VideoAnalyzeStepExecutor.OfferedItem(KindOf(shape, id), id, null, null, null)).ToList();

        static string KindOf(FixtureShape shape, string id) =>
            shape.ShotIds.Contains(id) ? "shot" : shape.SilenceIds.Contains(id) ? "silence" : "segment";
    }

    /// <summary>The off-path fixture's shape — see the provenance comment on <see cref="OffPathSha256"/>.</summary>
    private static readonly FixtureShape OffPathFixture = new(Shots: 3, Silences: 4, Segments: 2);

    /// <summary>
    /// The off-path fixture's budget. A LITERAL, not the fixture-calibrated figure the other tests
    /// use, because this fixture has to be reproducible on another revision (see
    /// <see cref="OffPathSha256"/>).
    /// </summary>
    private const int OffPathMaxOutputChars = 400;

    private static readonly Dictionary<string, double> OffPathPKeep = new(StringComparer.Ordinal)
    {
        ["s0"] = 0.9,
        ["s1"] = 0.8,
        ["s2"] = 0.7,
        ["t0"] = 0.6,
        ["t1"] = 0.5
    };

    private sealed record RunResult(
        string Output,
        JsonElement Meta,
        IReadOnlyList<string> ViewIds,
        int OfferedIdCount,
        IReadOnlyList<string> Dropped,
        IReadOnlyList<string> ScoreRows,
        int ClientCalls)
    {
        public IEnumerable<string> PresentIds() => ViewIds;

        public JsonElement DerushPrePass() => Meta.GetProperty("derushPrePass");
    }

    /// <summary>Runs the fixture with exactly one drop, by calibrating the budget against the fixture.</summary>
    private static async Task<RunResult> RunWithOneDropAsync(FixtureShape shape, IReadOnlyDictionary<string, double> pKeep)
    {
        int full = await FullViewBudgetAsync(shape);
        return await RunAsync(shape, pKeep, full - 1, Guid.NewGuid(), new DecisionQuestionMemoizer());
    }

    /// <summary>Runs the fixture with a budget cut to a fraction of its full-view length (deep cuts).</summary>
    private static async Task<RunResult> RunWithBudgetFractionAsync(
        FixtureShape shape, IReadOnlyDictionary<string, double> pKeep, double fractionOfFullBudget)
    {
        int full = await FullViewBudgetAsync(shape);
        return await RunAsync(shape, pKeep, Math.Max(256, (int)(full * fractionOfFullBudget)), Guid.NewGuid(), new DecisionQuestionMemoizer());
    }

    /// <summary>
    /// The calibrated one-drop budget for a shape: full-view length minus one, which makes exactly
    /// one drop necessary and no second one possible.
    /// </summary>
    private static async Task<int> BudgetForAsync(FixtureShape shape) => (await FullViewBudgetAsync(shape)) - 1;

    /// <summary>
    /// The serialized length of this fixture's view with no budget pressure at all. Read from
    /// <c>meta.outputChars</c> of an execution whose budget is far larger than the view; that run
    /// performs no drop and (because the budget never binds) no pre-pass resolution either, so it
    /// cannot contaminate the execution under test.
    /// </summary>
    private static async Task<int> FullViewBudgetAsync(FixtureShape shape)
    {
        RunResult baseline = await RunAsync(shape, new Dictionary<string, double>(), 200_000, Guid.NewGuid(), null);
        baseline.Meta.GetProperty("truncated").GetBoolean().Should().BeFalse(
            "the calibration run must not drop anything, or meta.outputChars is not the full-view length");
        return baseline.Meta.GetProperty("outputChars").GetInt32();
    }

    private static async Task<RunResult> RunAsync(
        FixtureShape shape,
        IReadOnlyDictionary<string, double> pKeep,
        int maxOutputChars,
        Guid executionId,
        IDecisionQuestionMemoizer? memoizer)
    {
        return await RunAsync(shape, pKeep, maxOutputChars, executionId, memoizer, derushPrePass: memoizer is not null);
    }

    private static async Task<RunResult> RunAsync(
        FixtureShape shape,
        IReadOnlyDictionary<string, double> pKeep,
        int maxOutputChars,
        Guid executionId,
        IDecisionQuestionMemoizer? memoizer,
        bool derushPrePass)
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

        // 30s source; shots every 2s so each is comfortably inside the media and each silence gap
        // sits between two of them (FindAfterShot then resolves deterministically).
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(shape.SourceSec, 30, 1, 1920, 1080, "h264", "aac", 48000));

        silence.Setup(s => s.DetectAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, shape.Silences)
                .Select(i => (10.0 + (i * 2), 10.5 + (i * 2)))
                .ToArray());

        shotDetector
            .Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, shape.Shots)
                .Select(i => shape.TalkingHeadSec is { } span
                    ? (i * span / shape.Shots, ((i + 1) * span / shape.Shots) - 0.01)
                    : ((double)(i * 2), (double)((i * 2) + 1)))
                .ToArray());

        SetupTranscription(transcriptionFactory, providerResolver, shape);

        IDecisionClient? client = null;
        IDecisionClientFactory? clientFactory = null;
        if (memoizer is not null)
        {
            var stub = new StubDecisionClient(pKeep);
            client = stub;
            clientFactory = new StubDecisionClientFactory(stub);
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

        string scratchRoot = Path.Combine(Path.GetTempPath(), "derush-drop-order-tests", Guid.NewGuid().ToString("N"));
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
            decisionMemoizer: memoizer,
            decisionClientFactory: clientFactory);

        StepExecutionResult executed = await executor.ExecuteAsync(CreateContext(shape, maxOutputChars, executionId, derushPrePass));

        executed.Status.Should().Be(StepStatus.Completed, because: executed.ErrorDetails ?? executed.Output);

        using JsonDocument doc = JsonDocument.Parse(executed.Output);
        JsonElement root = doc.RootElement;
        JsonElement meta = root.GetProperty("meta");

        List<string> viewIds = ViewIds(root.GetProperty("view"));
        int offeredIdCount = meta.GetProperty("offeredIdCount").GetInt32();
        List<string> dropped = shape.AllIds.Where(id => !viewIds.Contains(id)).ToList();

        // The view's rows, for the "no pKeep anywhere off-path" assertion.
        List<string> scoreRows = [];
        foreach (string arrayName in new[] { "shots", "silences", "segments" })
        {
            foreach (JsonElement row in root.GetProperty("view").GetProperty(arrayName).EnumerateArray())
            {
                if (row.TryGetProperty("pKeep", out _))
                    scoreRows.Add(row.GetProperty("id").GetString()!);
            }
        }

        return new RunResult(
            executed.Output,
            meta.Clone(),
            viewIds,
            offeredIdCount,
            dropped,
            scoreRows,
            (client as StubDecisionClient)?.Calls ?? 0);
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
        FixtureShape shape)
    {
        providerResolver
            .Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedTranscriptionProvider(
                ProviderId: Guid.NewGuid(), Name: "whisper-local", Kind: InferenceProviderKind.OpenAICompatible,
                Endpoint: "http://localhost:9999", ModelName: "whisper-1", ApiKey: "", TimeoutSeconds: 30));

        var client = new Mock<ITranscriptionClient>();
        client
            .Setup(c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptResult(
                string.Join(" ", Enumerable.Range(0, shape.Segments).Select(i => $"Sentence number {i}.")),
                Enumerable.Range(0, shape.Segments)
                    .Select(i => shape.TalkingHeadSec is { } span
                        ? new TranscriptSegment($"word{i}", i * span / shape.Segments, (i * span / shape.Segments) + (0.8 * span / shape.Segments))
                        : new TranscriptSegment($"Sentence number {i}.", 12.0 + (i * 2), 13.0 + (i * 2)))
                    .ToArray(),
                Array.Empty<TranscriptWord>(),
                "en"));

        factory.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(client.Object);
    }

    private static StepExecutionContext CreateContext(
        FixtureShape shape, int maxOutputChars, Guid executionId, bool derushPrePass)
    {
        VideoAnalyzeStepConfig config = new VideoAnalyzeStepConfig(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput))
        with
        {
            DetectSilence = shape.Silences > 0,
            DetectShots = shape.Shots > 0,
            Transcription = VideoTranscriptionMode.Required,
            // Visual/audio analysis off: this test is about the DROP ORDER, and detail-degrades-
            // before-drop means a richer view would only add noise (and more drops) between the
            // fixture and the rule under test.
            AnalyzeVisuals = false,
            AnalyzeAudioLevels = false,
            MaxViewSegments = 1000,
            Outline = shape.TalkingHeadSec is not null,
            MaxOutputChars = maxOutputChars,
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

    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Answers every <c>keep &lt;id&gt;</c> question from the test's map, counting outbound calls so
    /// R2's "was a batch actually issued?" distinction can be asserted directly.
    /// </summary>
    private sealed class StubDecisionClient(IReadOnlyDictionary<string, double> pKeepById) : IDecisionClient
    {
        private const string QuestionPrefix = "keep ";

        public int Calls { get; private set; }

        public Task<DecisionResult> DecideAsync(
            DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct)
        {
            Calls++;

            var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            foreach (DecisionQuestion question in questions)
            {
                string id = question.Name.StartsWith(QuestionPrefix, StringComparison.Ordinal)
                    ? question.Name[QuestionPrefix.Length..]
                    : question.Name;
                double pKeep = pKeepById.TryGetValue(id, out double configured) ? configured : DefaultPKeep;

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

            return Task.FromResult(new DecisionResult("stub", answers, InputTokens: 1, OutputTokens: 1));
        }
    }

    private sealed class StubDecisionClientFactory(IDecisionClient client) : IDecisionClientFactory
    {
        public IDecisionClient Get(ResolvedDecisionProvider provider) => client;
    }
}