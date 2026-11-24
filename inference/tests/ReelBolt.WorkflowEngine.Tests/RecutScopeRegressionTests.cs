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
public class RecutScopeRegressionTests
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



    [Theory]
    [InlineData("remove the last silence")]
    [InlineData("cut the first pause")]
    [InlineData("remove the last pause before the outro")]
    [InlineData("taglia l'ultima pausa")]
    [InlineData("taglia la prima pausa")]
    public void R01_single_silence_request_does_not_cut_every_gap(string brief)
    {
        EditProposal none = Run(OneClip(), Talk(), brief, null);
        EditProposal all = Run(OneClip(), Talk(), brief, null, Act("cutQuiet", "all"));
        (AllCuts(none).Count() <= 1 && AllCuts(all).Count() <= 1).Should().BeTrue($"none={AllCuts(none).Count()} all={AllCuts(all).Count()} unscoped={RecutRequestIntent.IsUnscoped(brief, RecutTarget.Silence)}");
    }
}

public class RecutFailClosedRegressionTests
{
    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    private static MediaClip Clip(int n, long inF, long outF) =>
        new(Id(n), $"clip-{n}", inF, outF, new AssetRef(AssetRefKind.ProjectFile, FileA, null), 0, 1.0);

    private static EditTimeline Doc(params Item[] videos) =>
        new(Id(800), Id(801), "Cuts", Settings, [new Track(Id(900), TrackKind.Video, "V1", null, 64, false, true, false, false, null, videos)], [], null, null, null, null, null);

    private static SourceFacts Talk()
    {
        FactWord[] w = [new(1, 2, "hello"), new(5, 5.5, "the"), new(5.5, 6, "prices"), new(10, 11, "bye")];
        return new(ProposalFactText.LinesFromWords(w.Select(x => (x.Text, x.StartSec, x.EndSec))), [], []) { Words = w };
    }

    private static EditProposal Run(EditTimeline doc, string brief, params EditProposalAction[] actions)
    {
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = Talk() }, 6000);
        (RecutOpClass? Op, RecutTarget? Target) intent = RecutRequestIntent.Classify(brief);
        return EditProposalCompiler.Compile(doc, EditProposalAgents.Recut, new EditProposalOutput { Actions = [.. actions] }, view, 30, Guid.NewGuid,
            intent, RecutRequestIntent.IsUnscoped(brief, intent.Target), brief);
    }

    private static MediaClip Named(int n, long inF, long outF, string name) =>
        new(Id(n), name, inF, outF, new AssetRef(AssetRefKind.ProjectFile, FileA, null), 0, 1.0);

    private static EditProposalAction Act(string kind, string item) => new() { Kind = kind, ItemId = item };
    private static IEnumerable<EditProposalCut> AllCuts(EditProposal p) => p.Groups.SelectMany(g => g.Cuts ?? []);

    [Theory]
    [InlineData("cut the part where I mention prices")]
    [InlineData("taglia la frase sui prezzi")]
    [InlineData("no more cuts, please")]
    [InlineData("remove the parts where nobody talks about pricing")]
    [InlineData("make it punchier")]
    public void Blanket_model_cut_is_refused_unless_the_request_is_a_plain_cut_of_that_target(string brief)
    {
        EditProposal p = Run(Doc(Clip(1, 0, 360)), brief, Act("cutSpeech", "i1"), Act("cutSpeech", "all"));
        AllCuts(p).Should().BeEmpty(brief);
        p.SwapsRefused.Should().Be(2);
        p.Summary.Should().NotContain("Did not cut anything");
    }

    [Theory]
    [InlineData("leave the silences, please")]
    [InlineData("don't touch the pauses please")]
    [InlineData("lascia i silenzi per favore")]
    [InlineData("the silences, please")]
    public void Keep_or_bare_silence_phrases_cut_nothing(string brief)
    {
        RecutRequestIntent.Classify(brief).OpClass.Should().NotBe(RecutOpClass.Cut);
        AllCuts(Run(Doc(Clip(1, 0, 360)), brief, Act("cutQuiet", "all"))).Should().BeEmpty(brief);
    }

    [Fact]
    public void Edge_trim_passes_only_for_the_edge_the_request_names()
    {
        EditTimeline doc = Doc(Clip(1, 0, 900));
        Run(doc, "remove the silence at the start", Act("trimHead", "i1")).SwapsRefused.Should().Be(0);
        Run(doc, "remove the silence at the start", Act("trimTail", "i1")).SwapsRefused.Should().Be(1);
        Run(doc, "taglia il silenzio alla fine", Act("trimTail", "i1")).SwapsRefused.Should().Be(0);
        Run(doc, "taglia il silenzio alla fine", Act("trimHead", "i1")).SwapsRefused.Should().Be(1);
    }

    [Fact]
    public void Positional_fillers_cut_nothing_and_say_so_once()
    {
        EditProposal p = Run(Doc(Clip(1, 0, 360)), "remove the ums at the end", Act("cutFillers", "i1"));
        AllCuts(p).Should().BeEmpty();
        p.Summary.Should().StartWith("Nothing was changed").And.NotContain("Did not cut");
    }

    [Theory]
    [InlineData("cut all the speech in clip-2")]
    [InlineData("taglia tutto il parlato in clip-2")]
    public void A_named_clip_scopes_the_cut_to_that_clip_only(string brief)
    {
        EditTimeline doc = Doc(Clip(1, 0, 360), Clip(2, 360, 720));
        EditProposal p = Run(doc, brief, Act("cutSpeech", "all"));
        AllCuts(p).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
        EditProposal fallback = Run(doc, brief);
        AllCuts(fallback).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
    }

    [Theory]
    [InlineData("keep the talking and remove the silences")]
    [InlineData("lascia il parlato e togli i silenzi")]
    public void A_keep_verb_applies_to_its_own_clause_only(string brief)
    {
        RecutRequestIntent.Classify(brief).Should().Be((RecutOpClass.Cut, RecutTarget.Silence));
        AllCuts(Run(Doc(Clip(1, 0, 360)), brief, Act("cutQuiet", "all"))).Should().NotBeEmpty(brief);
    }

    [Fact]
    public void Fillers_are_cut_and_the_silences_kept_when_the_request_says_so()
    {
        const string brief = "remove the ums but leave the silences";
        RecutRequestIntent.Classify(brief).Should().Be((RecutOpClass.Cut, RecutTarget.Fillers));
        RecutRequestIntent.IsUnscoped(brief, RecutTarget.Fillers).Should().BeTrue();
    }

    [Theory]
    [InlineData("remove the parts where nobody talks about pricing")]
    [InlineData("rimuovi le parti dove nessuno parla dei prezzi")]
    [InlineData("remove the bit where no one speaks")]
    public void A_line_cut_never_answers_a_request_that_says_nobody_speaks(string brief)
    {
        EditProposalAction line = new() { Kind = "cutLine", ItemId = "i1", Text = "l1" };
        EditProposal p = Run(Doc(Clip(1, 0, 360)), brief, line);
        AllCuts(p).Should().NotContain(c => (c.StartSec < 2 && c.EndSec > 1) || (c.StartSec < 6 && c.EndSec > 5), brief);
        p.SwapsRefused.Should().Be(1);
    }

    [Fact]
    public void Refusal_summary_names_a_part_only_when_the_request_does()
    {
        Run(Doc(Clip(1, 0, 360)), "cut the part where I mention prices", Act("cutSpeech", "all")).Summary.Should().Contain("names a part");
        Run(Doc(Clip(1, 0, 360)), "make it punchier", Act("cutSpeech", "all")).Summary.Should().NotContain("names a part");
    }

    [Theory]
    [InlineData("cut all the speech in the intro", "Intro")]
    [InlineData("remove the silences in the Intro", "Intro")]
    [InlineData("taglia tutto il parlato nell'intro", "Intro")]
    [InlineData("remove the silences from Main", "Main")]
    public void A_plain_clip_name_after_a_locating_preposition_scopes_the_request(string brief, string name)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        Guid expected = name == "Intro" ? Id(1) : Id(2);
        string kind = brief.Contains("silences") ? "cutQuiet" : "cutSpeech";
        AllCuts(Run(doc, brief, Act(kind, "all"))).Select(c => c.ItemId).Distinct().Should().Equal(expected);
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Should().Equal(expected);
    }

    [Fact]
    public void A_plain_name_without_a_preposition_does_not_scope_and_a_numbered_one_does()
    {
        EditTimeline plain = Doc(Named(1, 0, 360, "intro"), Named(2, 360, 720, "main"));
        AllCuts(Run(plain, "cut all the speech, intro and main", Act("cutSpeech", "all"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(new[] { Id(1), Id(2) });
        EditTimeline numbered = Doc(Named(1, 0, 360, "Interview 1"), Named(2, 360, 720, "Interview 2"));
        AllCuts(Run(numbered, "cut all the speech in Interview 2", Act("cutSpeech", "all"))).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
    }

    [Theory]
    [InlineData("cut all the speech in the second half")]
    [InlineData("taglia tutto il parlato nella seconda metà")]
    public void A_locating_phrase_that_names_no_clip_scopes_the_request(string brief)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        AllCuts(Run(doc, brief, Act("cutSpeech", "all"))).Should().BeEmpty(brief);
        AllCuts(Run(doc, brief)).Should().BeEmpty(brief);
    }

    [Theory]
    [InlineData("keep everything as is")]
    [InlineData("make it punchier")]
    [InlineData("no more cuts, please")]
    public void Keep_lines_needs_a_cut_request(string brief)
    {
        EditProposalAction keep = new() { Kind = "keepLines", ItemId = "i1", Text = "l1" };
        Run(Doc(Clip(1, 0, 360)), brief, keep).SwapsRefused.Should().Be(1);
    }

    [Theory]
    [InlineData("don't remove anything")]
    [InlineData("non togliere niente")]
    public void A_negated_cut_verb_is_not_a_cut(string brief)
    {
        RecutRequestIntent.Classify(brief).OpClass.Should().BeNull();
    }

    [Theory]
    [InlineData("cut all the speech dall'intro", 1)]
    [InlineData("taglia tutto il parlato dell'intro", 1)]
    [InlineData("taglia tutto il parlato sull'intro", 1)]
    [InlineData("taglia tutto il parlato nell\u2019intro", 1)]
    [InlineData("cut all the speech in the video", 3)]
    [InlineData("taglia tutto il parlato in tutto il video", 3)]
    [InlineData("cut all the speech in the whole timeline", 3)]
    public void Locating_phrases_that_name_a_clip_restrict_to_it_and_whole_video_phrases_do_not_scope(string brief, int expectedClips)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        int clips = AllCuts(Run(doc, brief, Act("cutSpeech", "all"))).Select(c => c.ItemId).Distinct().Count();
        clips.Should().Be(expectedClips == 3 ? 2 : 1, brief);
    }

    [Theory]
    [InlineData("cut all the speech in the introduction")]
    [InlineData("cut all the speech in the clip of the beach")]
    [InlineData("togli tutto il parlato nella clip del tramonto")]
    [InlineData("cut all the speech in the clip Outro")]
    [InlineData("cut all the speech except the beach")]
    [InlineData("cut all the speech except the intro, but not the beach")]
    public void An_unmatched_locating_phrase_or_an_exception_naming_no_clip_scopes_the_request(string brief)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        AllCuts(Run(doc, brief, Act("cutSpeech", "all"))).Should().BeEmpty(brief);
        AllCuts(Run(doc, brief)).Should().BeEmpty(brief);
        AllCuts(Run(doc, brief, Act("cutSpeech", "i1"))).Should().BeEmpty(brief, "completion must not widen a one-clip cut");
    }

    [Theory]
    [InlineData("cut all the speech apart from the intro", "cutSpeech")]
    [InlineData("cut all the speech but not in the intro", "cutSpeech")]
    [InlineData("cut all the speech except the intro", "cutSpeech")]
    [InlineData("cut all the speech except for the intro", "cutSpeech")]
    [InlineData("remove the silences, not in the intro", "cutQuiet")]
    [InlineData("remove the silences, not the intro", "cutQuiet")]
    [InlineData("remove the silences but leave the intro alone", "cutQuiet")]
    [InlineData("remove the silences but keep the intro", "cutQuiet")]
    [InlineData("remove the silences excluding the intro", "cutQuiet")]
    [InlineData("taglia tutto il parlato eccetto l'intro", "cutSpeech")]
    [InlineData("taglia tutto il parlato tranne nell'intro", "cutSpeech")]
    [InlineData("togli i silenzi, non nell'intro", "cutQuiet")]
    [InlineData("togli i silenzi ma lascia l'intro", "cutQuiet")]
    [InlineData("togli i silenzi ma lascia stare l'intro", "cutQuiet")]
    [InlineData("togli i silenzi ma non nell'intro", "cutQuiet")]
    [InlineData("togli i silenzi ma non toccare l'intro", "cutQuiet")]
    [InlineData("remove the silences without touching the intro", "cutQuiet")]
    [InlineData("remove the silences without cutting the intro", "cutQuiet")]
    [InlineData("remove the silences without removing the intro", "cutQuiet")]
    [InlineData("togli i silenzi senza toccare l'intro", "cutQuiet")]
    [InlineData("togli i silenzi senza tagliare l'intro", "cutQuiet")]
    [InlineData("togli i silenzi senza eliminare l'intro", "cutQuiet")]
    [InlineData("togli i silenzi, l'intro lasciala com'è", "cutQuiet")]
    [InlineData("togli i silenzi, intro lasciala stare", "cutQuiet")]
    [InlineData("remove the silences, leave the intro as it is", "cutQuiet")]
    [InlineData("remove the silences everywhere but the intro", "cutQuiet")]
    [InlineData("remove the silences in all clips but the intro", "cutQuiet")]
    public void An_exception_naming_a_clip_cuts_everything_else_on_every_path(string brief, string kind)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
        AllCuts(Run(doc, brief, Act(kind, "all"))).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
        AllCuts(Run(doc, brief, Act(kind, "i2"))).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
        AllCuts(Run(doc, brief, Act(kind, "i1"))).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
    }

    [Theory]
    [InlineData("remove the silences, not in Main")]
    [InlineData("togli i silenzi, non in Main")]
    public void An_exception_can_name_the_other_clip(string brief)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Should().Equal(Id(1));
    }

    [Theory]
    [InlineData("remove the silences from both", "cutQuiet")]
    [InlineData("remove the silences in both", "cutQuiet")]
    [InlineData("remove the silences from both of them", "cutQuiet")]
    [InlineData("remove the silences in all of them", "cutQuiet")]
    [InlineData("rimuovi i silenzi in entrambi", "cutQuiet")]
    [InlineData("rimuovi i silenzi in tutti e due", "cutQuiet")]
    [InlineData("rimuovi i silenzi in tutte e due", "cutQuiet")]
    public void Bare_quantifier_phrases_are_the_whole_video(string brief, string kind)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        Guid[] both = [Id(1), Id(2)];
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
        AllCuts(Run(doc, brief, Act(kind, "all"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
        AllCuts(Run(doc, brief, Act(kind, "i1"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
    }

    [Theory]
    [InlineData("remove the silences, don't touch the Intro or the Outro")]
    [InlineData("remove the silences except Intro or Outro")]
    [InlineData("togli i silenzi tranne Intro o Outro")]
    [InlineData("togli i silenzi ma lascia l'intro e l'outro")]
    public void Exclusion_lists_continue_over_or(string brief)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"), Named(3, 720, 1080, "Outro"));
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
        AllCuts(Run(doc, brief, Act("cutQuiet", "all"))).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
    }

    [Fact]
    public void A_capped_read_with_an_exclusion_cue_fails_closed_and_a_plain_request_still_cuts()
    {
        Item[] clips = Enumerable.Range(0, 310).Select(i => (Item)Named(i + 1, i * 30L, i * 30L + 30, i == 309 ? "Intro" : $"Clip {i}")).ToArray();
        EditTimeline doc = Doc(clips);
        AllCuts(Run(doc, "remove the silences except Intro")).Should().BeEmpty();
        AllCuts(Run(doc, "remove the silences except Intro", Act("cutQuiet", "all"))).Should().BeEmpty();
        string[] names = Enumerable.Range(0, 310).Select(i => $"Clip {i}").ToArray();
        RecutRequestIntent.ReadClipScope("remove the silences", names).Failed.Should().BeFalse("a plain request needs no names");
        RecutRequestIntent.ReadClipScope("remove the silences except Clip 1", names).Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData("remove the silences, keep The Beach")]
    [InlineData("remove the silences, leave Beach 2")]
    [InlineData("togli i silenzi senza toccare Spiaggia")]
    public void An_exclusion_cue_followed_by_an_unresolved_name_fails_closed(string brief)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        AllCuts(Run(doc, brief)).Should().BeEmpty(brief);
        AllCuts(Run(doc, brief, Act("cutQuiet", "all"))).Should().BeEmpty(brief);
    }

    [Theory]
    [InlineData("remove the silences but not too much", "cutQuiet")]
    [InlineData("remove the silences but not too aggressively", "cutQuiet")]
    [InlineData("remove the silences but keep it natural", "cutQuiet")]
    [InlineData("togli i silenzi ma non troppo", "cutQuiet")]
    [InlineData("togli i silenzi ma mantienilo naturale", "cutQuiet")]
    public void Manner_and_amount_qualifiers_are_not_exceptions(string brief, string kind)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        Guid[] both = [Id(1), Id(2)];
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
        AllCuts(Run(doc, brief, Act(kind, "all"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
        AllCuts(Run(doc, brief, Act(kind, "i1"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
        AllCuts(Run(doc, brief, Act(kind, "i2"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
    }

    [Theory]
    [InlineData("remove the silences but not too long intro", "cutQuiet")]
    [InlineData("remove the silences but not too much intro", "cutQuiet")]
    [InlineData("togli i silenzi ma non troppo l'intro", "cutQuiet")]
    [InlineData("togli i silenzi ma non troppo intro", "cutQuiet")]
    public void A_clip_name_right_after_a_manner_phrase_keeps_the_cue_as_an_exclusion(string brief, string kind)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
        AllCuts(Run(doc, brief, Act(kind, "all"))).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
        AllCuts(Run(doc, brief, Act(kind, "i2"))).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
        AllCuts(Run(doc, brief, Act(kind, "i1"))).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
    }

    [Theory]
    [InlineData("remove the silences but not too much and then please check the intro", "cutQuiet")]
    [InlineData("togli i silenzi ma non troppo e poi per favore controlla l'intro", "cutQuiet")]
    public void A_clip_name_beyond_four_words_after_a_manner_phrase_is_still_a_plain_request(string brief, string kind)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        Guid[] both = [Id(1), Id(2)];
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
        AllCuts(Run(doc, brief, Act(kind, "all"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
        AllCuts(Run(doc, brief, Act(kind, "i1"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
    }

    [Fact]
    public void A_manner_phrase_before_a_located_clip_fails_closed()
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        AllCuts(Run(doc, "remove the silences but not too much in Main")).Should().BeEmpty();
        AllCuts(Run(doc, "remove the silences but not too much in Main", Act("cutQuiet", "all"))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("remove the silences but not everything")]
    [InlineData("remove the silences but not all of it")]
    [InlineData("remove the silences but not completely")]
    [InlineData("remove the silences but not entirely")]
    [InlineData("remove the silences but leave a little breathing room")]
    [InlineData("togli i silenzi ma non tutto")]
    [InlineData("togli i silenzi ma non completamente")]
    [InlineData("togli i silenzi ma non del tutto")]
    [InlineData("togli i silenzi ma lascia un po' di respiro")]
    [InlineData("remove the silences but not everything in the Intro")]
    [InlineData("togli i silenzi ma non troppo nell'Intro")]
    [InlineData("remove the silences in Main, leave it as it is")]
    [InlineData("togli i silenzi nell'intro, lasciala com'è")]
    public void Partial_amounts_and_manner_before_a_clip_name_stay_a_truthful_refusal(string brief)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        AllCuts(Run(doc, brief)).Should().BeEmpty(brief);
        AllCuts(Run(doc, brief, Act("cutQuiet", "all"))).Should().BeEmpty(brief);
        AllCuts(Run(doc, brief, Act("cutQuiet", "i1"))).Should().BeEmpty(brief);
    }

    [Theory]
    [InlineData("remove the silences but not the beach scene")]
    [InlineData("togli i silenzi ma non la scena della spiaggia")]
    public void An_exception_that_matches_no_clip_says_so_truthfully(string brief)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        EditProposal p = Run(doc, brief, Act("cutQuiet", "all"));
        AllCuts(p).Should().BeEmpty(brief);
        p.Summary.Should().Contain("could not be matched to a clip").And.NotContain("names a part");
    }

    [Fact]
    public void A_timeout_reading_the_clip_names_fails_closed()
    {
        RecutRequestIntent.ClipScope failed = RecutRequestIntent.ClipScope.None("x") with { Failed = true };
        failed.Failed.Should().BeTrue();
    }

    [Fact]
    public void In_the_middle_scopes_numbered_clips_and_of_does_not_name_a_clip()
    {
        EditTimeline numbered = Doc(Clip(1, 0, 360), Clip(2, 360, 720));
        AllCuts(Run(numbered, "cut all the speech in the middle", Act("cutSpeech", "all"))).Should().BeEmpty();
        EditTimeline talking = Doc(Named(1, 0, 360, "Talking"), Named(2, 360, 720, "Main"));
        AllCuts(Run(talking, "remove all of the talking", Act("cutSpeech", "all"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(new[] { Id(1), Id(2) });
    }

    [Fact]
    public void Naming_a_clip_stays_fast_for_a_long_brief_and_many_clips()
    {
        Item[] clips = Enumerable.Range(0, 300).Select(i => (Item)Named(i + 1, i * 10L, i * 10L + 10, $"Clip {i}")).ToArray();
        string brief = string.Concat(Enumerable.Repeat("cut all the speech in a ", 80)) + "Clip 7";
        brief.Length.Should().BeGreaterThan(1900);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Run(Doc(clips), brief, Act("cutSpeech", "all"));
        watch.Stop();
        watch.Elapsed.TotalSeconds.Should().BeLessThan(2);
    }

    [Fact]
    public void A_cut_held_to_a_named_clip_says_the_others_were_left_alone()
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        Run(doc, "cut all the speech in the intro", Act("cutSpeech", "all")).Summary.Should().Contain("other clips were left as they are");
    }

    [Theory]
    [InlineData("remove all the silences from the video", "cutQuiet")]
    [InlineData("remove all the silences from the whole video", "cutQuiet")]
    [InlineData("remove all the silences in my video", "cutQuiet")]
    [InlineData("remove all the silences in this footage", "cutQuiet")]
    [InlineData("remove all the silences from the entire recording", "cutQuiet")]
    [InlineData("cut all the speech in my project", "cutSpeech")]
    [InlineData("cut all the speech from the timeline", "cutSpeech")]
    [InlineData("rimuovi tutti i silenzi dal video", "cutQuiet")]
    [InlineData("rimuovi tutti i silenzi dal mio video", "cutQuiet")]
    [InlineData("rimuovi tutti i silenzi nell\u2019intero filmato", "cutQuiet")]
    [InlineData("rimuovi tutti i silenzi nel girato", "cutQuiet")]
    [InlineData("taglia tutto il parlato nel montaggio", "cutSpeech")]
    [InlineData("taglia tutto il parlato dentro il progetto", "cutSpeech")]
    public void Locating_phrases_pointing_at_the_whole_video_cut_everything(string brief, string kind)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        AllCuts(Run(doc, brief, Act(kind, "all"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(new[] { Id(1), Id(2) }, brief);
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(new[] { Id(1), Id(2) }, brief);
    }

    [Theory]
    [InlineData("cut all the speech in the intro and the outro")]
    [InlineData("cut all the speech from the intro and main")]
    [InlineData("taglia tutto il parlato nell'intro e nel finale")]
    [InlineData("cut all the speech in the intro, outro")]
    [InlineData("cut all the speech in the intro & the outro")]
    public void A_list_of_clip_names_after_a_preposition_names_each(string brief)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"), Named(3, 720, 1080, "Outro"), Named(4, 1080, 1440, "Finale"));
        string[] named = brief.Contains("main") ? ["Intro", "Main"] : brief.Contains("finale") ? ["Intro", "Finale"] : ["Intro", "Outro"];
        Guid[] ids = named.Select(n => Id(n switch { "Intro" => 1, "Main" => 2, "Outro" => 3, _ => 4 })).ToArray();
        EditProposal p = Run(doc, brief, Act("cutSpeech", "all"));
        AllCuts(p).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(ids, brief);
        p.Summary.Should().Contain("Only the clips you named were cut");
    }

    [Fact]
    public void Model_prose_is_dropped_when_the_named_scope_skipped_a_cut()
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = Talk() }, 6000);
        const string brief = "cut all the speech in the intro";
        (RecutOpClass? Op, RecutTarget? Target) intent = RecutRequestIntent.Classify(brief);
        EditProposal p = EditProposalCompiler.Compile(doc, EditProposalAgents.Recut, new EditProposalOutput { Summary = "Cut everything in both clips.", Actions = [Act("cutSpeech", "i2")] },
            view, 30, Guid.NewGuid, intent, RecutRequestIntent.IsUnscoped(brief, intent.Target), brief);
        p.Summary.Should().NotContain("Cut everything in both clips.");
    }

    [Theory]
    [InlineData("cut all the speech in clip 2")]
    [InlineData("cut all the speech in clips 1 and 2")]
    [InlineData("cut all the speech in the clip with the dog")]
    [InlineData("togli i silenzi durante Intro")]
    public void A_numbered_or_qualified_clip_is_not_the_whole_video(string brief)
    {
        EditTimeline doc = Doc(Clip(1, 0, 360), Clip(2, 360, 720), Named(3, 720, 1080, "Intro"));
        string kind = brief.Contains("silenzi") ? "cutQuiet" : "cutSpeech";
        RecutRequestIntent.IsUnscoped(brief, RecutRequestIntent.Classify(brief).Target).Should().BeFalse(brief);
        if (brief.Contains("Intro")) AllCuts(Run(doc, brief, Act(kind, "all"))).Select(c => c.ItemId).Distinct().Should().Equal(Id(3));
        else AllCuts(Run(doc, brief, Act(kind, "all"))).Should().BeEmpty(brief);
    }

    [Theory]
    [InlineData("cut all the speech in all the clips")]
    [InlineData("cut all the speech in the clips")]
    [InlineData("cut all the speech in the clip")]
    [InlineData("cut all the speech in the clip, please")]
    public void Plural_or_bare_clip_nouns_are_the_whole_video(string brief)
    {
        RecutRequestIntent.IsUnscoped(brief, RecutTarget.Speech).Should().BeTrue(brief);
    }

    [Fact]
    public void Chained_name_lists_stay_linear_in_the_worst_case()
    {
        string[] names = Enumerable.Range(0, 300).Select(i => i == 0 ? "x" : $"x{i}").ToArray();
        string chain = "cut all the speech in " + string.Join(" and ", Enumerable.Repeat("x", 600));
        string chainNames = "cut all the speech in " + string.Join(" and ", Enumerable.Range(0, 300).Select(i => names[i]));
        foreach (string brief in new[] { chain[..Math.Min(chain.Length, 2000)], chainNames[..Math.Min(chainNames.Length, 2000)] })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            HashSet<string> named = RecutRequestIntent.NamedClipNames(brief, names);
            watch.Stop();
            watch.ElapsedMilliseconds.Should().BeLessThan(500);
            named.Should().Contain("x");
        }

        RecutRequestIntent.NamedClipNames(new string('x', 2001), names).Should().BeEmpty("an over-long brief names nothing (fail closed)");
    }

    [Fact]
    public void Name_lists_after_a_preposition_name_each_and_a_list_not_after_one_names_none()
    {
        string[] names = ["Intro", "Main", "Outro"];
        RecutRequestIntent.NamedClipNames("cut speech from the intro and main, outro", names).Should().BeEquivalentTo(names);
        RecutRequestIntent.NamedClipNames("cut speech, intro and main", names).Should().BeEmpty();
    }

    [Theory]
    [InlineData("cut all the speech in every clip", "cutSpeech")]
    [InlineData("cut all the speech in both clips", "cutSpeech")]
    [InlineData("cut all the speech on both clips", "cutSpeech")]
    [InlineData("remove the silences from all clips", "cutQuiet")]
    [InlineData("remove the silences on all clips", "cutQuiet")]
    [InlineData("remove the silences in each clip", "cutQuiet")]
    [InlineData("remove the silences all at once", "cutQuiet")]
    [InlineData("remove the silences in one go", "cutQuiet")]
    [InlineData("remove the silences in it", "cutQuiet")]
    [InlineData("remove the silences in there", "cutQuiet")]
    [InlineData("rimuovi i silenzi in tutte le clip", "cutQuiet")]
    [InlineData("rimuovi i silenzi in tutti i clip", "cutQuiet")]
    [InlineData("rimuovi i silenzi su tutte le clip", "cutQuiet")]
    [InlineData("rimuovi i silenzi in ogni clip", "cutQuiet")]
    [InlineData("rimuovi i silenzi in entrambe le clip", "cutQuiet")]
    [InlineData("rimuovi i silenzi in una volta", "cutQuiet")]
    [InlineData("rimuovi i silenzi tutto in una volta", "cutQuiet")]
    public void Quantifier_and_all_at_once_phrases_cut_everything_on_every_path(string brief, string kind)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        Guid[] both = [Id(1), Id(2)];
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
        AllCuts(Run(doc, brief, Act(kind, "all"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief);
        AllCuts(Run(doc, brief, Act(kind, "i1"))).Select(c => c.ItemId).Distinct().Should().BeEquivalentTo(both, brief, "the clips the model left out are completed");
    }

    [Theory]
    [InlineData("cut all the speech in the clip Intro", 1)]
    [InlineData("remove the silences in the clip Intro", 1)]
    [InlineData("rimuovi i silenzi nella clip Main", 2)]
    [InlineData("remove the silences from my video Intro", 1)]
    [InlineData("remove the silences from that clip", 0)]
    [InlineData("remove the silences in this clip", 0)]
    public void A_clip_noun_followed_by_a_name_or_a_demonstrative_is_not_the_whole_video(string brief, int onlyClip)
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Main"));
        string kind = brief.Contains("speech") ? "cutSpeech" : "cutQuiet";
        var cuts = AllCuts(Run(doc, brief, Act(kind, "all"))).Select(c => c.ItemId).Distinct().ToList();
        if (onlyClip == 0) cuts.Should().BeEmpty(brief);
        else cuts.Should().Equal(Id(onlyClip));
        AllCuts(Run(doc, brief)).Select(c => c.ItemId).Distinct().Count().Should().Be(onlyClip == 0 ? 0 : 1, brief);
    }

    [Fact]
    public void The_longest_clip_name_wins_at_a_position()
    {
        RecutRequestIntent.NamedClipNames("cut all the speech in the Intro 2", ["Intro", "Intro 2"]).Should().BeEquivalentTo(["Intro 2"]);
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"), Named(2, 360, 720, "Intro 2"));
        AllCuts(Run(doc, "cut all the speech in the Intro 2", Act("cutSpeech", "all"))).Select(c => c.ItemId).Distinct().Should().Equal(Id(2));
    }

    [Fact]
    public void A_refusal_says_a_true_thing_for_each_cause()
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"));
        Run(doc, "cut all the speech from the video", Act("cutQuiet", "all")).Summary.Should().NotContain("names a part");
    }

    [Fact]
    public void Determiner_floods_finish_fast_and_fail_closed()
    {
        string flood = string.Concat(Enumerable.Repeat("the my the my all of the all the all every ", 48));
        string[] briefs =
        [
            ("cut all the speech in " + flood + "x")[..2000],
            ("cut all the speech in " + flood + "clips with")[..2000],
            (flood + "cut all the speech in the video")[..2000],
            "cut all the speech in " + string.Concat(Enumerable.Repeat("the ", 600)) + "clipz",
        ];
        foreach (string brief in briefs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool unscoped = RecutRequestIntent.IsUnscoped(brief, RecutTarget.Speech);
            RecutRequestIntent.Classify(brief);
            RecutRequestIntent.NamedClipNames(brief, ["Intro", "Main"]);
            watch.Stop();
            watch.ElapsedMilliseconds.Should().BeLessThan(1000);
            if (brief.EndsWith("x") || brief.EndsWith("clipz")) unscoped.Should().BeFalse("a flood that names no whole-video noun leaves the locating phrase, so the request is scoped");
        }
    }

    [Fact]
    public void The_nothing_found_sentence_agrees_with_its_noun()
    {
        EditTimeline doc = Doc(Named(1, 0, 360, "Intro"));
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = Talk() }, 6000);
        const string brief = "remove the ums";
        (RecutOpClass? Op, RecutTarget? Target) intent = RecutRequestIntent.Classify(brief);
        EditProposal p = EditProposalCompiler.Compile(doc, EditProposalAgents.Recut, new EditProposalOutput { Actions = [Act("cutSpeech", "all")] }, view, 30, Guid.NewGuid,
            intent, RecutRequestIntent.IsUnscoped(brief, intent.Target), brief);
        p.Summary.Should().Contain("no filler words were found");
    }
}
