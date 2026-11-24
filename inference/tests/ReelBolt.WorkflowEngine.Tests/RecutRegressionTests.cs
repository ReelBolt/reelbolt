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

/// <summary>Regression tests from the QA review of recut-follows-request: each pins the spec-correct behaviour.</summary>
public class RecutRegressionTests
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

    private static EditProposal Run(EditTimeline doc, SourceFacts facts, string brief, EditRange? range, params EditProposalAction[] actions)
    {
        EditProposalView view = EditProposalView.Build(doc, range, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = facts }, 6000);
        return EditProposalCompiler.Compile(doc, EditProposalAgents.Recut, new EditProposalOutput { Actions = [.. actions] }, view, 30, Guid.NewGuid,
            RecutRequestIntent.Classify(brief), RecutRequestIntent.IsUnscoped(brief, RecutRequestIntent.Classify(brief).Target), brief);
    }

    private static EditProposalAction Act(string kind, string item, string text = "") => new() { Kind = kind, ItemId = item, Text = text };

    private static IEnumerable<EditProposalCut> AllCuts(EditProposal p) => p.Groups.SelectMany(g => g.Cuts ?? []);

    // ── source-second spans ──────────────────────────────────────────────────

    [Fact]
    public void Reversed_clip_gets_no_content_cut_and_an_explicit_action_says_why()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 300, reverse: true)));
        SourceFacts facts = Spoken(W(2.0, 2.5, "hidden"), W(7.0, 7.5, "visible"));
        AllCuts(Run(doc, facts, "cut all the speech", new EditRange(0, 150), Act("cutSpeech", "all"))).Should().BeEmpty();
        EditProposal explicitAction = Run(doc, facts, "cut all the speech", null, Act("cutSpeech", "i1"));
        explicitAction.Rejected.Should().Contain(r => r.Contains("reversed or a freeze frame"));
    }

    [Fact]
    public void Probe_source_in_and_rate_two_partly_outside_range()
    {
        // sourceIn 2 s, rate 2; range 150..300 shows source 2 + 150*2/30 = 12 .. 22.
        EditTimeline doc = Doc(Video(Clip(1, 0, 300, 60, 2.0)));
        SourceFacts facts = Spoken(W(5, 6, "before"), W(15, 16, "inside"), W(25, 26, "after"));
        EditProposal p = Run(doc, facts, "cut all the speech", new EditRange(150, 300), Act("cutSpeech", "all"));
        AllCuts(p).Select(c => (c.StartSec, c.EndSec)).Should().BeEquivalentTo(new[] { (14.95, 16.05) });
    }

    [Fact]
    public void Probe_hold_clip_has_no_audio_so_no_speech_cut()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 300, hold: true)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "hello")), "cut all the speech", null, Act("cutSpeech", "all"));
        AllCuts(p).Should().BeEmpty("a freeze frame plays no source audio");
    }

    [Fact]
    public void L_cut_audio_tail_is_a_documented_limitation_the_overlapping_audio_is_still_a_twin()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 150)), Audio(Clip(2, 0, 300)));
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = Spoken(W(1, 2, "both")) }, 6000);
        view.ClipRefs.Single(c => c.Alias == "i2").TwinOfAlias.Should().Be("i1");
    }

    [Fact]
    public void Probe_same_file_audio_placed_elsewhere_is_not_a_twin()
    {
        // Same file, same source window, but on the timeline 300..600 while the picture is 0..300: an independent copy, not a linked partner.
        EditTimeline doc = Doc(Video(Clip(1, 0, 300)), Audio(Clip(2, 300, 600)));
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = Spoken(W(1, 2, "x")) }, 6000);
        view.ClipRefs.Single(c => c.Alias == "i2").TwinOfAlias.Should().BeNull();
    }

    // ── guard ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cut out all the spoken words", "remove")]
    [InlineData("cut out all the spoken words", "trimHead")]
    [InlineData("cut out all spoken words in the clip", "remove")]
    [InlineData("cut out all spoken words in the clip", "trimTail")]
    public void Probe_remove_or_trim_answer_to_a_content_cut_is_refused_and_counted(string brief, string swapped)
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "hello"), W(5, 6, "world")), brief, null, Act(swapped, "i1"));
        p.Groups.Should().OnlyContain(g => g.Cuts != null && g.Cuts.Count > 0, "no whole-clip op may stand in for a content cut");
        p.SwapsRefused.Should().BeGreaterThan(0);
        AllCuts(p).Should().NotBeEmpty("the deterministic cut is synthesised");
    }

    [Theory]
    [InlineData("make it punchier")]
    [InlineData("")]
    public void Probe_unclassified_request_still_allows_mute(string brief)
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "hello")), brief, null, Act("mute", "i1"));
        p.SwapsRefused.Should().Be(0, "nothing was asked, so a mute swaps nothing");
        p.Groups.Should().ContainSingle();
    }

    [Fact]
    public void Silence_at_the_start_is_scoped_so_the_fallback_does_not_cut_every_gap()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 900)));
        SourceFacts facts = Spoken(W(3, 4, "hi"), W(10, 11, "mid"), W(20, 21, "end"));
        AllCuts(Run(doc, facts, "remove the silence at the start", null)).Should().BeEmpty();
        AllCuts(Run(doc, facts, "remove the silences", null)).Should().NotBeEmpty();
    }

    [Fact]
    public void Muting_under_a_target_without_a_verb_is_refused_without_saying_the_editor_asked_to_cut()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "hello")), "the ums are annoying", null, Act("mute", "i1"));
        p.SwapsRefused.Should().Be(1);
        p.Summary.Should().Contain("muting does not remove").And.NotContain("you asked to cut");
    }

    [Fact]
    public void A_refused_or_synthesised_answer_drops_the_model_prose_and_a_clean_one_keeps_it()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        SourceFacts facts = Spoken(W(1, 2, "hello"));
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = facts }, 6000);
        EditProposal refused = EditProposalCompiler.Compile(doc, EditProposalAgents.Recut,
            new EditProposalOutput { Summary = "Muted the camera audio.", Actions = [Act("mute", "i1")] }, view, 30, Guid.NewGuid,
            RecutRequestIntent.Classify("cut all the speech"), true);
        refused.Summary.Should().NotContain("Muted the camera audio");
        EditProposal clean = EditProposalCompiler.Compile(doc, EditProposalAgents.Recut,
            new EditProposalOutput { Summary = "Tight.", Actions = [Act("cutSpeech", "i1")] }, view, 30, Guid.NewGuid,
            RecutRequestIntent.Classify("cut all the speech"), true);
        clean.Summary.Should().EndWith("Tight.");
        clean.Did!.Cuts.Should().ContainSingle().Which.Kind.Should().Be("speech");
    }

    [Theory]
    [InlineData("cut all the spoken words", "speech", true)]
    [InlineData("cut out all spoken words in the clip", "speech", true)]
    [InlineData("remove every word", "speech", true)]
    [InlineData("delete all of the talking", "speech", true)]
    [InlineData("taglia tutto il parlato", "speech", true)]
    [InlineData("elimina ogni parola", "speech", true)]
    [InlineData("taglia tutte le parole", "speech", true)]
    [InlineData("cut the spoken words", "speech", false)]
    [InlineData("remove all the bits where I talk about pricing", "speech", false)]
    [InlineData("cut all the speech except the intro", "speech", false)]
    [InlineData("cut all the speech at the end", "speech", false)]
    [InlineData("elimina la parte in cui parlo dei prezzi in tutto il video", "speech", false)]
    [InlineData("taglia tutto il parlato tranne l'intro", "speech", false)]
    [InlineData("remove the silences", "silence", true)]
    [InlineData("rimuovi i silenzi", "silence", true)]
    [InlineData("remove the silence at the start", "silence", false)]
    [InlineData("taglia i silenzi alla fine", "silence", false)]
    [InlineData("remove the ums", "fillers", true)]
    [InlineData("togli gli ehm", "fillers", true)]
    [InlineData("remove the ums before the intro", "fillers", false)]
    public void Is_unscoped_needs_no_qualifier_and_for_speech_a_quantifier(string brief, string target, bool expected)
    {
        RecutTarget t = Enum.GetValues<RecutTarget>().Single(x => RecutRequestIntent.Word(x) == target);
        RecutRequestIntent.IsUnscoped(brief, t).Should().Be(expected, brief);
    }

    // ── topical requests must not become cutSpeech over all ─────────────────

    [Theory]
    [InlineData("delete the part where I talk about pricing")]
    [InlineData("delete every part where I talk about pricing")]
    [InlineData("remove all the bits where I talk about pricing")]
    [InlineData("elimina la parte in cui parlo dei prezzi in tutto il video")]
    [InlineData("make all the dialogue louder")]
    public void Probe_topical_or_non_cut_request_with_no_model_action_cuts_nothing(string brief)
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        SourceFacts facts = Spoken(W(1, 2, "Hi."), W(5, 6, "Pricing is simple."), W(9, 10, "Bye."));
        EditProposal p = Run(doc, facts, brief, null);
        AllCuts(p).Should().BeEmpty(brief);
    }

    // ── classifier: plausible phrasings ─────────────────────────────────────

    [Theory]
    [InlineData("remove the silent parts", "silence")]
    [InlineData("taglia le parti silenziose", "silence")]
    [InlineData("cut the gaps", "silence")]
    [InlineData("elimina tutte le parti parlate", "speech")]
    [InlineData("remove all the on-screen words", null)]
    [InlineData("remove all the parts where nobody is talking", "silence")]
    [InlineData("cut the parts with no speech", "silence")]
    [InlineData("taglia dove nessuno parla", "silence")]
    [InlineData("taglia le parti senza parlato", "silence")]
    [InlineData("delete the silent bits", "silence")]
    [InlineData("togli i sottotitoli", null)]
    public void Probe_classifier_target(string brief, string? target)
    {
        RecutTarget? t = RecutRequestIntent.Classify(brief).Target;
        (t is { } tc ? RecutRequestIntent.Word(tc) : null).Should().Be(target, brief);
    }

    [Theory]
    [InlineData("remove the camera audio")]
    [InlineData("togli l'audio della camera")]
    [InlineData("cut the sound")]
    [InlineData("elimina il suono")]
    [InlineData("togli l'audio")]
    public void Probe_removing_audio_is_not_a_cut_that_refuses_mute(string brief)
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)), Audio(Clip(2, 0, 600)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "hello")), brief, null, Act("mute", "i2"));
        p.SwapsRefused.Should().Be(0, brief);
    }

    [Fact]
    public void Spans_closer_than_a_quarter_second_are_one_cut()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 900)));
        // Fillers at 1.0-1.2 and 1.4-1.6 leave a 0.2 s keep (coalesced); 3.0-3.2 is 1.4 s later (separate).
        SourceFacts facts = Spoken(W(1.0, 1.2, "um"), W(1.4, 1.6, "uh"), W(3.0, 3.2, "um"));
        AllCuts(Run(doc, facts, "remove the ums", null, Act("cutFillers", "i1"))).Select(c => (c.StartSec, c.EndSec))
            .Should().BeEquivalentTo(new[] { (1.0, 1.6), (3.0, 3.2) });
    }

    private static SourceFacts ManyFillers(int count, double start = 0) =>
        Spoken(Enumerable.Range(0, count).Select(i => W(start + i * 1.0, start + i * 1.0 + 0.2, "um")).ToArray());

    [Fact]
    public void A_group_over_two_hundred_spans_keeps_the_first_two_hundred_and_says_so()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 30 * 400)));
        EditProposal p = Run(doc, ManyFillers(250), "remove the ums", null, Act("cutFillers", "i1"));
        EditProposalGroup g = p.Groups.Should().ContainSingle().Subject;
        g.Cuts.Should().HaveCount(ProposalCuts.MaxSpansPerGroup);
        g.Cuts![0].StartSec.Should().Be(0);
        g.Label.Should().StartWith("Cut 200 filler words");
        p.Did!.Cut.Should().Be(200);
        p.Rejected.Should().Contain("Only the first 200 of 250 cuts in clip-1 are proposed; mark a later range and ask again for the rest.");
    }

    [Fact]
    public void A_proposal_over_three_hundred_spans_drops_the_later_groups_with_their_own_line()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 30 * 400), Clip(2, 30 * 400, 30 * 800), Clip(3, 30 * 800, 30 * 1200)));
        EditProposal p = Run(doc, ManyFillers(200), "remove the ums", null, Act("cutFillers", "all"));
        p.Groups.Should().ContainSingle("200 + 200 is over 300, so the second and third groups are dropped");
        p.Did!.Cut.Should().Be(200);
        p.Rejected.Should().Contain(r => r.Contains("at most 300 cuts") && r.Contains("clip-2"));
        p.Rejected.Should().Contain(r => r.Contains("at most 300 cuts") && r.Contains("clip-3"));
    }

    [Fact]
    public void A_repeated_line_alias_counts_once()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "One."), W(5, 6, "Two.")), "cut that line", null, Act("cutLine", "i1", "l1 l1, l1"));
        p.Groups.Should().ContainSingle().Which.Label.Should().StartWith("Cut 1 spoken line ");
        p.Summary.Should().Contain("Cut 1 spoken line");
    }
    // ── 0.25 s coalescing must not delete content that was meant to stay ─────

    [Fact]
    public void quarter_second_coalescing_does_not_cut_a_short_word_between_two_quiet_gaps()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        SourceFacts facts = new(Spoken(W(0.5, 1, "So"), W(3.0, 3.2, "no"), W(5, 6, "ok")).Lines, [], [new FactGap(1, 3), new FactGap(3.2, 5)])
        { Words = [W(0.5, 1, "So"), W(3.0, 3.2, "no"), W(5, 6, "ok")] };
        EditProposal p = Run(doc, facts, "remove the silences", null, Act("cutQuiet", "i1"));
        Covers(p, 3.05, 3.15).Should().BeFalse("the spoken 'no' between two silences is not quiet");
    }

    [Fact]
    public void quarter_second_coalescing_does_not_cut_a_real_word_between_two_fillers()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        EditProposal p = Run(doc, Spoken(W(1.0, 1.2, "um"), W(1.2, 1.4, "I"), W(1.4, 1.6, "uh"), W(3, 4, "think")), "remove the ums", null, Act("cutFillers", "i1"));
        Covers(p, 1.25, 1.35).Should().BeFalse("'I' is not a filler");
    }

    [Fact]
    public void keep_lines_keeps_a_short_kept_line()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        SourceFacts facts = Spoken(W(1, 2, "First."), W(5.0, 5.1, "Yes."), W(9, 10, "Third."));
        EditProposal p = Run(doc, facts, "keep only the yes", null, Act("keepLines", "i1", "l2"));
        Covers(p, 5.0, 5.1).Should().BeFalse("the kept line must survive");
    }

    // ── IsUnscoped: speech quantifier on a phrase that KEEPS the speech ──────

    [Theory]
    [InlineData("keep all the dialogue and cut the music")]
    [InlineData("cut the music, leave all the talking")]
    [InlineData("tieni tutto il parlato e taglia la musica")]
    [InlineData("don't remove all the spoken words")]
    public void request_that_keeps_all_speech_does_not_synthesise_a_speech_cut(string brief)
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "Hi."), W(5, 6, "Bye.")), brief, null);
        AllCuts(p).Should().BeEmpty(brief);
    }

    // ── audio-object -> mute must not swallow non-audio objects ─────────────

    [Theory]
    [InlineData("remove the sound effects")]
    [InlineData("delete the audio clip")]
    public void removing_a_sound_clip_is_still_a_remove(string brief)
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)), Audio(Clip(2, 0, 600)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "hello")), brief, null, Act("remove", "i2"));
        p.SwapsRefused.Should().Be(0, brief);
    }

    // ── proposal cap vs "already being cut" ──────────────────────────────────

    [Fact]
    public void a_remove_dropped_for_a_cut_that_the_cap_then_drops_leaves_the_clip_untouched_silently()
    {
        // Three 400 s clips, 200 fillers each; cut all (cap keeps only the first group) and remove i3.
        EditTimeline doc = Doc(Video(Clip(1, 0, 30 * 400), Clip(2, 30 * 400, 30 * 800), Clip(3, 30 * 800, 30 * 1200)));
        SourceFacts facts = Spoken(Enumerable.Range(0, 1200).Select(i => W(i * 1.0, i * 1.0 + 0.2, "um")).ToArray());
        EditProposal p = Run(doc, facts, "make it punchier", null, Act("cutFillers", "all"), Act("remove", "i3"));
        bool i3Touched = p.Groups.Any(g => g.TouchedItemIds.Contains(Id(3)));
        i3Touched.Should().BeTrue("a cut dropped by the cap must not also block the remove");
        p.Groups.Should().Contain(g => g.Label.StartsWith("Remove"));
    }

    private static bool Covers(EditProposal p, double s, double e) => AllCuts(p).Any(c => c.StartSec < e && c.EndSec > s);

    [Fact]
    public void Without_all_a_model_remove_under_a_speech_target_is_still_refused_and_nothing_is_synthesised()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "hello")), "cut out the spoken words", null, Act("remove", "i1"));
        p.SwapsRefused.Should().Be(1);
        p.Groups.Should().BeEmpty();
    }

    [Fact]
    public void The_model_trim_head_stands_for_remove_the_silence_at_the_start()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 900)));
        EditProposal p = Run(doc, Spoken(W(3, 4, "hi"), W(10, 11, "mid")), "remove the silence at the start", null, Act("trimHead", "i1"));
        p.SwapsRefused.Should().Be(0);
        p.Groups.Should().ContainSingle().Which.Label.Should().StartWith("Trim");
        Run(doc, Spoken(W(3, 4, "hi")), "remove all the spoken words", null, Act("trimHead", "i1")).SwapsRefused.Should().Be(1);
    }

    [Fact]
    public void An_audio_tail_that_runs_past_its_picture_is_reported_not_cut()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 150)), Audio(Clip(2, 0, 300)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "hello")), "cut all the speech", null, Act("cutSpeech", "all"));
        p.Groups.Should().ContainSingle();
        p.Rejected.Should().Contain("The sound of clip-2 that runs past its picture was not cut.");
    }

    [Theory]
    [InlineData("remove the sound effects", "cut")]
    [InlineData("delete the audio clip", "cut")]
    [InlineData("elimina gli effetti sonori", "cut")]
    [InlineData("remove the camera audio", "mute")]
    [InlineData("cut the sound", "mute")]
    [InlineData("togli l'audio", "mute")]
    [InlineData("remove the audio track", "mute")]
    public void Audio_is_a_mute_only_when_it_is_the_whole_object(string brief, string op) =>
        RecutRequestIntent.Word(RecutRequestIntent.Classify(brief).OpClass!.Value).Should().Be(op, brief);

    [Theory]
    [InlineData("keep all the dialogue and cut the music")]
    [InlineData("tieni tutto il parlato e taglia la musica")]
    public void A_keep_verb_on_the_speech_noun_means_no_speech_target(string brief)
    {
        RecutRequestIntent.Classify(brief).Target.Should().BeNull(brief);
        RecutRequestIntent.IsUnscoped(brief, RecutTarget.Speech).Should().BeFalse(brief);
    }
}
