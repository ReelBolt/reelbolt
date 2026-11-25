#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Proposals;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Regression tests for the Re-cut guard: the compiler never does something other than what the request asked, whatever the model answers.</summary>
public class RecutGuardRegressionTests
{
    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    private static MediaClip Clip(int n, long inF, long outF, long sourceIn = 0, double rate = 1.0, bool reverse = false, bool hold = false) =>
        new(Id(n), $"clip-{n}", inF, outF, new AssetRef(AssetRefKind.ProjectFile, FileA, null), sourceIn, rate, Reverse: reverse, Hold: hold);

    private static Track Video(params Item[] items) => new(Id(900), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items);
    private static Track Audio(params Item[] items) => new(Id(901), TrackKind.Audio, "A1", null, 48, false, true, false, false, null, items);
    private static EditTimeline Doc(params Track[] tracks) => new(Id(800), Id(801), "Cuts", Settings, tracks, [], null, null, null, null, null);
    private static FactWord W(double s, double e, string t) => new(s, e, t);

    private static SourceFacts Spoken(params FactWord[] words) =>
        new(ProposalFactText.LinesFromWords(words.Select(w => (w.Text, w.StartSec, w.EndSec))), [], []) { Words = words };

    // Speech at 1-2, 5-6 ("prices"), 10-11; quiet in between.
    private static SourceFacts Talk() => Spoken(W(1, 2, "hello"), W(5, 5.5, "the"), W(5.5, 6, "prices"), W(10, 11, "bye"));

    private static EditProposal Run(EditTimeline doc, SourceFacts facts, string? brief, string? modelSummary, params EditProposalAction[] actions)
    {
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = facts }, 6000);
        (RecutOpClass?, RecutTarget?) intent = RecutRequestIntent.Classify(brief);
        return EditProposalCompiler.Compile(doc, EditProposalAgents.Recut, new EditProposalOutput { Summary = modelSummary, Actions = [.. actions] }, view, 30, Guid.NewGuid,
            intent, RecutRequestIntent.IsUnscoped(brief, intent.Item2), brief);
    }

    private static EditProposalAction Act(string kind, string item, string text = "") => new() { Kind = kind, ItemId = item, Text = text };
    private static IEnumerable<EditProposalCut> AllCuts(EditProposal p) => p.Groups.SelectMany(g => g.Cuts ?? []);
    private static double CutSeconds(EditProposal p) => AllCuts(p).Sum(c => c.EndSec - c.StartSec);
    private static EditTimeline OneClip() => Doc(Video(Clip(1, 0, 360)));

    // ── goal 2: a topical / non-cut / negated / other-target request must not wipe every spoken passage ──

    [Theory]
    [InlineData("remove all the bits where I talk about pricing")]
    [InlineData("elimina le parti in cui parlo dei prezzi in tutto il video")]
    public void P01_topical_request_model_cut_speech_all_does_not_wipe_all_speech(string brief)
    {
        EditProposal p = Run(OneClip(), Talk(), brief, null, Act("cutSpeech", "all"));
        AllCuts(p).Should().NotContain(c => c.StartSec < 1.5 && c.EndSec > 1.5, "the 'hello' passage is not about pricing; a blanket speech cut is a different edit");
    }

    [Fact]
    public void P02_op_null_make_dialogue_louder_model_cut_speech_all_cuts_nothing()
    {
        EditProposal p = Run(OneClip(), Talk(), "make all the dialogue louder", null, Act("cutSpeech", "all"));
        AllCuts(p).Should().BeEmpty("louder is not a cut");
    }

    [Fact]
    public void P03_op_null_make_dialogue_louder_with_no_model_action_cuts_nothing_and_needs_no_transcript()
    {
        EditProposal p = Run(OneClip(), Talk(), "make all the dialogue louder", null);
        AllCuts(p).Should().BeEmpty();
        // The controller asks for a transcript only when the asked operation is a cut; this request is not one.
        RecutRequestIntent.Classify("make all the dialogue louder").OpClass.Should().NotBe(RecutOpClass.Cut);
    }

    [Fact]
    public void P04_negated_request_model_cut_speech_does_not_cut_the_speech()
    {
        EditProposal p = Run(OneClip(), Talk(), "remove all the parts where nobody is talking", null, Act("cutSpeech", "all"));
        AllCuts(p).Should().NotContain(c => c.StartSec < 1.5 && c.EndSec > 1.5, "the editor asked to cut where nobody talks, not the talking");
    }

    [Theory]
    [InlineData("remove the silences")]
    [InlineData("remove the ums")]
    [InlineData("rimuovi i silenzi")]
    public void P05_other_content_target_model_cut_speech_all_is_refused(string brief)
    {
        EditProposal p = Run(OneClip(), Talk(), brief, null, Act("cutSpeech", "all"));
        AllCuts(p).Should().NotContain(c => c.StartSec < 1.5 && c.EndSec > 1.5, "cutting speech is not cutting silence/fillers");
    }

    // ── mute intent ──

    [Theory]
    [InlineData("remove the camera audio")]
    [InlineData("togli l'audio")]
    public void P06_mute_intent_mute_passes_and_a_speech_cut_is_refused(string brief)
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 360)), Audio(Clip(2, 0, 360)));
        EditProposal mute = Run(doc, Talk(), brief, null, Act("mute", "i2"));
        mute.SwapsRefused.Should().Be(0);
        mute.Did!.Mute.Should().Be(1);
        EditProposal cut = Run(doc, Talk(), brief, null, Act("cutSpeech", "all"));
        AllCuts(cut).Should().BeEmpty();
        cut.SwapsRefused.Should().Be(1);
    }

    // ── trim / reorder intent ──

    [Fact]
    public void P07_trim_intent_allows_trim_refuses_remove_and_cut()
    {
        EditProposal p = Run(OneClip(), Talk(), "trim the start", null, Act("trimHead", "i1"), Act("remove", "i1"), Act("cutSpeech", "all"));
        p.Did!.Trim.Should().Be(1);
        p.Did.Remove.Should().Be(0);
        AllCuts(p).Should().BeEmpty();
        p.SwapsRefused.Should().Be(2);
    }

    [Fact]
    public void P08_reorder_intent_changes_nothing_and_says_so()
    {
        EditProposal p = Run(OneClip(), Talk(), "move the intro to the end", "Moved it.", Act("remove", "i1"), Act("cutSpeech", "all"));
        p.Groups.Should().BeEmpty();
        p.Summary.Should().Contain("Re-ordering clips is not something the re-cut can do").And.NotContain("Moved it.");
    }

    [Theory]
    [InlineData("make the video shorter by cutting out all the spoken words")]
    [InlineData("shorten it: remove all the talking")]
    public void P09_trim_verb_plus_speech_cut_request_lets_the_speech_cut_through(string brief)
    {
        EditProposal p = Run(OneClip(), Talk(), brief, null, Act("cutSpeech", "all"));
        AllCuts(p).Should().NotBeEmpty("the editor asked to shorten by cutting the speech; that is a cut");
    }

    // ── empty / unclassified brief ──

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("rendilo più dinamico")]
    public void P10_unclassified_brief_mute_passes_and_summary_does_not_claim_a_cut_was_asked(string? brief)
    {
        EditProposal p = Run(OneClip(), Talk(), brief, null, Act("mute", "i1"));
        p.SwapsRefused.Should().Be(0);
        p.Did!.Mute.Should().Be(1);
        p.Summary.Should().NotContain("you asked to cut");
    }

    // ── model answers remove / trim / mute to a content cut ──

    [Theory]
    [InlineData("taglia tutto il parlato", "remove")]
    [InlineData("taglia tutto il parlato", "trimTail")]
    [InlineData("taglia tutto il parlato", "mute")]
    [InlineData("remove the silences", "remove")]
    [InlineData("remove the silences", "mute")]
    [InlineData("remove the ums", "trimHead")]
    public void P11_whole_clip_answer_to_a_content_cut_never_stands(string brief, string kind)
    {
        EditProposal p = Run(OneClip(), Spoken(W(1, 2, "um"), W(4, 5, "hi"), W(9, 10, "uh")), brief, "Did it.", Act(kind, "i1"));
        p.Groups.Should().OnlyContain(g => g.Cuts != null && g.Cuts.Count > 0);
        p.SwapsRefused.Should().Be(1);
        p.Summary.Should().NotContain("Did it.");
    }

    // ── silence scoped ──

    [Fact]
    public void P12_scoped_silence_at_the_start_model_cut_quiet_all_does_not_cut_every_gap()
    {
        EditProposal p = Run(OneClip(), Talk(), "remove the silence at the start", null, Act("cutQuiet", "all"));
        AllCuts(p).Should().NotContain(c => c.StartSec > 2.5, "only the silence at the start was asked for");
    }

    // ── reversed and hold clips ──

    [Fact]
    public void P13_reversed_clip_fallback_cuts_nothing_and_summary_does_not_claim_a_cut()
    {
        EditProposal p = Run(Doc(Video(Clip(1, 0, 360, reverse: true))), Talk(), "cut all the spoken words", null);
        AllCuts(p).Should().BeEmpty();
        p.Summary.Should().NotMatchRegex(@"Cut \d+ spoken");
    }

    [Fact]
    public void P14_hold_picture_with_its_playing_audio_still_gets_the_audio_speech_cut()
    {
        // Freeze-frame picture over the clip's own audio that keeps playing: the sound has speech, the picture is a still.
        EditTimeline doc = Doc(Video(Clip(1, 0, 360, hold: true)), Audio(Clip(2, 0, 360)));
        EditProposal p = Run(doc, Talk(), "cut all the spoken words", null);
        AllCuts(p).Should().Contain(c => c.ItemId == Id(2), "the audio clip plays forward and holds the speech");
    }

    // ── linked / twin / L-cut / independent copy ──

    [Fact]
    public void P15_linked_twin_cut_once_on_picture()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 360)), Audio(Clip(2, 0, 360)));
        EditProposal p = Run(doc, Talk(), "cut all the spoken words", null, Act("cutSpeech", "i2"));
        AllCuts(p).Select(c => c.ItemId).Distinct().Should().Equal(Id(1));
    }

    [Fact]
    public void P16_L_cut_tail_speech_is_cut_or_at_least_reported()
    {
        // Picture 0..5 s, its audio 0..10 s; "bye" at 10-11 is outside, "prices" at 5-6 is in the tail.
        EditTimeline doc = Doc(Video(Clip(1, 0, 150)), Audio(Clip(2, 0, 360)));
        EditProposal p = Run(doc, Talk(), "cut all the spoken words", null);
        bool tailCut = AllCuts(p).Any(c => c.StartSec < 5.75 && c.EndSec > 5.75);
        bool reported = p.Rejected.Any(r => r.Contains("runs past its picture"));
        (tailCut || reported).Should().BeTrue("the sound past its picture is either cut or reported as not cut");
    }

    [Fact]
    public void P17_same_file_audio_overlapping_in_time_but_out_of_sync_is_not_a_twin()
    {
        // Picture plays source 0..12 s; an independent copy of the same file on A1 at the same time plays source 5..17 s (slipped 5 s).
        EditTimeline doc = Doc(Video(Clip(1, 0, 360)), Audio(Clip(2, 0, 360, sourceIn: 150)));
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = Talk() }, 6000);
        view.ClipRefs.Single(c => c.Alias == "i2").TwinOfAlias.Should().BeNull("not in sync, so it is not the picture's own sound");
    }

    [Fact]
    public void P18_independent_copy_elsewhere_gets_its_own_cut()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 360)), Audio(Clip(2, 400, 760)));
        EditProposal p = Run(doc, Talk(), "cut all the spoken words", null);
        AllCuts(p).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(new[] { Id(1), Id(2) });
    }

    // ── summary when actions were refused ──

    [Fact]
    public void P19_summary_drops_model_prose_when_its_remove_was_dropped_as_already_cut()
    {
        EditProposal p = Run(OneClip(), Talk(), "remove the silences", "Removed the intro clip.", Act("cutQuiet", "i1"), Act("remove", "i1"));
        p.Did!.Remove.Should().Be(0);
        p.Summary.Should().NotContain("Removed the intro clip.", "the remove was not done");
    }

    [Fact]
    public void P20_summary_after_mute_refusal_lists_what_was_done_and_not_the_mute()
    {
        EditProposal p = Run(OneClip(), Talk(), "cut out all spoken words in the clip", "Muted the camera audio.", Act("mute", "i1"));
        p.Summary.Should().StartWith("Cut ").And.Contain("Did not mute anything").And.NotContain("Muted the camera audio");
    }

    // ── classifier wording ──

    [Theory]
    [InlineData("remove the silent parts", "cut", "silence")]
    [InlineData("cut the gaps", "cut", "silence")]
    [InlineData("rimuovi le parti silenziose", "cut", "silence")]
    [InlineData("elimina le parti parlate", "cut", "speech")]
    [InlineData("remove the on-screen words", "cut", null)]
    [InlineData("delete the words on screen", "cut", null)]
    [InlineData("cut the silence that follows the intro", "cut", "silence")]
    [InlineData("taglia le pause tra le frasi", "cut", "silence")]
    [InlineData("remove the quiet bits", "cut", "silence")]
    [InlineData("make the video shorter by cutting out all the spoken words", "cut", "speech")]
    [InlineData("shorten it: remove all the talking", "cut", "speech")]
    [InlineData("silence the intro clip", "mute", null)]
    public void P21_classifier_wording(string brief, string? op, string? target)
    {
        (RecutOpClass? o, RecutTarget? t) = RecutRequestIntent.Classify(brief);
        (o is null ? null : RecutRequestIntent.Word(o.Value)).Should().Be(op);
        (t is null ? null : RecutRequestIntent.Word(t.Value)).Should().Be(target);
    }

    // ── unscoped detection: broad words do not scope a request unless they truly qualify ──

    [Theory]
    [InlineData("remove all the silences, but only the long ones", false)]
    [InlineData("remove the silences", true)]
    [InlineData("taglia le pause tra le frasi", true)]
    [InlineData("remove the pauses between sentences", true)]
    [InlineData("remove the silences in the first 10 seconds", false)]
    [InlineData("remove the silences between 0:10 and 0:20", false)]
    [InlineData("cut the silence that follows the intro", false)]
    [InlineData("remove silences first", true)]
    public void Unscoped_requests_are_told_from_truly_qualifying_words(string brief, bool unscoped)
    {
        RecutRequestIntent.IsUnscoped(brief, RecutRequestIntent.Classify(brief).Target).Should().Be(unscoped);
    }

    [Fact]
    public void Model_cut_of_the_wrong_kind_is_refused_and_counted()
    {
        EditProposal p = Run(OneClip(), Talk(), "remove the silences", null, Act("cutSpeech", "all"), Act("cutFillers", "i1"));
        AllCuts(p).Should().NotContain(c => c.StartSec < 1.5 && c.EndSec > 1.5, "the silences were asked for, not the speech");
        p.SwapsRefused.Should().Be(2);
    }

    [Fact]
    public void Merge_still_keeps_a_short_word_between_two_close_spans()
    {
        ProposalCuts.Merge([(1.0, 2.0), (2.05, 3.0)], [(2.0, 2.05 + 0.01)]).Should().HaveCount(2);
        ProposalCuts.Merge([(1.0, 2.0), (2.05, 3.0)], [(8.0, 9.0)]).Should().HaveCount(1);
    }
}
