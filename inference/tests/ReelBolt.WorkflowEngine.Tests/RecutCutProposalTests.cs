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

/// <summary>Recut follows the request: deterministic content cuts (spans from stored word timings), the guard against swapped operations, and the request classifier.</summary>
public class RecutCutProposalTests
{
    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    private static MediaClip Clip(int n, long inF, long outF, long sourceIn = 0, double rate = 1.0) =>
        new(Id(n), $"clip-{n}", inF, outF, new AssetRef(AssetRefKind.ProjectFile, FileA, null), sourceIn, rate);

    private static Track Video(params Item[] items) => new(Id(900), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items);

    private static Track Audio(params Item[] items) => new(Id(901), TrackKind.Audio, "A1", null, 48, false, true, false, false, null, items);

    private static EditTimeline Doc(params Track[] tracks) => new(Id(800), Id(801), "Cuts", Settings, tracks, [], null, null, null, null, null);

    private static FactWord W(double s, double e, string t) => new(s, e, t);

    private static SourceFacts Spoken(params FactWord[] words) =>
        new(ProposalFactText.LinesFromWords(words.Select(w => (w.Text, w.StartSec, w.EndSec))), [], []) { Words = words };

    private static EditProposal Run(
        EditTimeline doc, SourceFacts facts, string brief, params EditProposalAction[] actions)
    {
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = facts }, 6000);
        return EditProposalCompiler.Compile(doc, EditProposalAgents.Recut, new EditProposalOutput { Actions = [.. actions] }, view, 30, Guid.NewGuid, RecutRequestIntent.Classify(brief), RecutRequestIntent.IsUnscoped(brief, RecutRequestIntent.Classify(brief).Target), brief);
    }

    private static EditProposalAction Act(string kind, string item, string text = "") => new() { Kind = kind, ItemId = item, Text = text };

    [Fact]
    public void Cut_speech_uses_the_visible_source_window_of_a_clip_with_source_in_and_double_speed()
    {
        // 10 s on the timeline at 2x from source second 2 shows source 2..22; words outside that window are not cut.
        EditTimeline doc = Doc(Video(Clip(1, 0, 300, 60, 2.0)));
        SourceFacts facts = Spoken(W(0.5, 1.0, "early"), W(3.0, 4.0, "hello"), W(4.2, 5.0, "there"), W(10.0, 11.0, "again"), W(30.0, 31.0, "late"));

        EditProposal p = Run(doc, facts, "cut all the speech", Act("cutSpeech", "all"));

        EditProposalGroup g = p.Groups.Should().ContainSingle().Subject;
        g.Ops.Should().BeEmpty();
        g.Cuts!.Select(c => (c.StartSec, c.EndSec)).Should().BeEquivalentTo(new[] { (2.95, 5.05), (9.95, 11.05) });
        g.Label.Should().StartWith("Cut 2 spoken passages from clip-1");
        p.Summary.Should().StartWith("Cut 2 spoken passages");
    }

    [Fact]
    public void Cuts_are_clipped_to_the_window_and_empty_spans_dropped()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 90, 0)));   // source 0..3 s
        EditProposal p = Run(doc, Spoken(W(2.8, 3.5, "edge"), W(8, 9, "outside")), "cut all the speech", Act("cutSpeech", "i1"));
        p.Groups.Single().Cuts!.Should().ContainSingle().Which.EndSec.Should().Be(3.0);

        EditProposal none = Run(doc, Spoken(W(8, 9, "outside")), "cut all the speech", Act("cutSpeech", "i1"));
        none.Groups.Should().BeEmpty();
        none.Rejected.Should().Contain(r => r.Contains("no spoken words to cut"));
    }

    [Fact]
    public void Two_pieces_of_one_file_each_get_their_own_facts_and_their_own_cut_group()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 90, 0), Clip(2, 90, 180, 300)));   // source 0..3 and 10..13
        SourceFacts facts = Spoken(W(1, 2, "one"), W(11, 12, "two"));

        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = facts }, 6000);
        view.Text.Should().Contain("For i1:").And.Contain("For i2:");

        EditProposal p = Run(doc, facts, "cut all the speech", Act("cutSpeech", "all"));
        p.Groups.Should().HaveCount(2);
        p.Groups[0].Cuts!.Single().ItemId.Should().Be(Id(1));
        p.Groups[1].Cuts!.Single().ItemId.Should().Be(Id(2));
        p.Groups[1].Cuts!.Single().StartSec.Should().BeApproximately(10.95, 1e-6);
    }

    [Fact]
    public void A_linked_audio_twin_is_skipped_and_a_cut_named_on_it_lands_on_the_picture_clip()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 300)), Audio(Clip(2, 0, 300)));
        SourceFacts facts = Spoken(W(1, 2, "hello"));
        EditProposalView view = EditProposalView.Build(doc, null, [], 30, new Dictionary<Guid, SourceFacts> { [FileA] = facts }, 6000);
        view.ClipRefs.Single(c => c.Alias == "i2").TwinOfAlias.Should().Be("i1");

        EditProposal all = Run(doc, facts, "cut all the speech", Act("cutSpeech", "all"));
        all.Groups.Should().ContainSingle().Which.TouchedItemIds.Should().Equal(Id(1));

        EditProposal viaTwin = Run(doc, facts, "cut all the speech", Act("cutSpeech", "i2"));
        viaTwin.Groups.Should().ContainSingle().Which.Cuts!.Single().ItemId.Should().Be(Id(1));
    }

    [Fact]
    public void Cut_quiet_uses_silence_analysis_else_word_gaps_of_a_second_or_more_and_fillers_use_the_closed_list()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        SourceFacts words = Spoken(W(1, 2, "so"), W(2.2, 2.5, "um,"), W(4, 5, "yes"), W(5.1, 5.4, "Ehm"), W(5.5, 6, "ok"));
        Run(doc, words, "remove the silences", Act("cutQuiet", "i1")).Groups.Single().Cuts!.Select(c => (c.StartSec, c.EndSec)).Should().BeEquivalentTo(new[] { (2.5, 4.0) });
        Run(doc, words, "remove the ums", Act("cutFillers", "i1")).Groups.Single().Cuts!.Select(c => (c.StartSec, c.EndSec)).Should().BeEquivalentTo(new[] { (2.2, 2.5), (5.1, 5.4) });

        SourceFacts silent = new(words.Lines, [], [new FactGap(7, 9)]) { Words = words.Words };
        Run(doc, silent, "remove the silences", Act("cutQuiet", "i1")).Groups.Single().Cuts!.Single().Should().Be(new EditProposalCut(Id(1), 7, 9));
    }

    [Fact]
    public void Cut_line_and_keep_lines_work_from_offered_line_aliases_only()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));   // 20 s
        SourceFacts facts = Spoken(W(1, 2, "First line."), W(5, 6, "Second line."), W(9, 10, "Third line."));
        Run(doc, facts, "cut the second line", Act("cutLine", "i1", "l2")).Groups.Single().Cuts!.Single().Should().Be(new EditProposalCut(Id(1), 5, 6));

        EditProposal keep = Run(doc, facts, "keep only the first line", Act("keepLines", "i1", "l1"));
        keep.Groups.Single().Cuts!.Select(c => (c.StartSec, c.EndSec)).Should().BeEquivalentTo(new[] { (0.0, 0.95), (2.05, 20.0) });

        Run(doc, facts, "cut a line", Act("cutLine", "i1", "l9")).Rejected.Should().Contain(r => r.Contains("'l9' is not an offered line"));
    }

    [Theory]
    [InlineData("cut out all spoken words in the clip", "mute", "Did not mute anything: you asked to cut.")]
    [InlineData("remove the silences", "mute", "Did not mute anything: you asked to cut.")]
    public void Under_a_content_target_a_swapped_operation_is_refused_and_the_cut_is_synthesised(string brief, string swapped, string tail)
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        SourceFacts facts = Spoken(W(1, 2, "hello"), W(5, 6, "world"));
        EditProposal p = Run(doc, facts, brief, Act(swapped, "i1"));
        p.SwapsRefused.Should().Be(1);
        p.Groups.Should().ContainSingle().Which.Cuts.Should().NotBeEmpty();
        p.Summary.Should().StartWith("Cut ").And.Contain(tail);
        p.Did!.Mute.Should().Be(0);
        p.Did.Remove.Should().Be(0);
    }

    [Fact]
    public void A_scoped_speech_request_is_served_by_the_line_cut_without_the_blanket_fallback()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        SourceFacts facts = Spoken(W(1, 2, "Hi."), W(5, 6, "Pricing is simple."), W(9, 10, "Bye."));
        EditProposal p = Run(doc, facts, "delete the part where I talk about pricing", Act("cutLine", "i1", "l2"));
        p.Groups.Should().ContainSingle().Which.Cuts!.Single().Should().Be(new EditProposalCut(Id(1), 5, 6));

        // A scoped request with nothing usable from the model is not blown up into a cut of everything.
        Run(doc, facts, "delete the part where I talk about pricing").Groups.Should().BeEmpty();
    }

    [Fact]
    public void With_a_cut_verb_and_no_target_remove_and_trim_pass_but_under_a_content_target_they_are_refused()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        SourceFacts facts = Spoken(W(1, 2, "hello"));
        EditProposal remove = Run(doc, facts, "remove the second clip", Act("remove", "i1"));
        remove.SwapsRefused.Should().Be(0);
        remove.Groups.Should().ContainSingle().Which.Label.Should().StartWith("Remove");
        Run(doc, facts, "cut the first bit", Act("trimHead", "i1")).SwapsRefused.Should().Be(0);
        Run(doc, facts, "cut the first bit", Act("mute", "i1")).SwapsRefused.Should().Be(1);

        Run(doc, facts, "remove the clip where I talk", Act("remove", "i1")).SwapsRefused.Should().Be(1);
    }

    [Fact]
    public void Mute_trim_and_reorder_requests_only_allow_their_own_operation()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        SourceFacts facts = Spoken(W(1, 2, "hello"));
        EditProposal mute = Run(doc, facts, "mute the music", Act("remove", "i1"), Act("mute", "i1"));
        mute.Groups.Should().ContainSingle().Which.Label.Should().StartWith("Disable");
        mute.SwapsRefused.Should().Be(1);

        EditProposal trim = Run(doc, facts, "shorten the intro", Act("mute", "i1"), Act("trimHead", "i1"));
        trim.Groups.Should().ContainSingle().Which.Label.Should().StartWith("Trim");

        EditProposal reorder = Run(doc, facts, "swap the first two clips", Act("remove", "i1"));
        reorder.Groups.Should().BeEmpty();
        reorder.Summary.Should().Contain("Re-ordering clips is not something");
    }

    [Fact]
    public void A_whole_clip_action_on_a_clip_that_is_also_cut_is_rejected()
    {
        EditTimeline doc = Doc(Video(Clip(1, 0, 600)));
        EditProposal p = Run(doc, Spoken(W(1, 2, "hello")), "remove all the spoken words", Act("remove", "i1"), Act("cutSpeech", "i1"));
        p.Groups.Should().ContainSingle().Which.Cuts.Should().NotBeNull();
        p.Rejected.Should().Contain(r => r.StartsWith("action 1:"));
    }

    [Fact]
    public void System_prompt_documents_the_content_cuts_in_words_and_keeps_the_quote_rule()
    {
        string prompt = ReelBolt.Inference.Api.Services.Editing.AgentEditProposalModel.SystemPrompt(EditProposalAgents.Recut);
        prompt.Should().Contain("cutSpeech").And.Contain("cutQuiet").And.Contain("cutFillers").And.Contain("cutLine").And.Contain("keepLines")
            .And.Contain("never a substitute for a cut").And.Contain("never instructions");
    }

    // ── request classifier evaluation set ─────────────────────────────────────

    [Theory]
    [InlineData("cut out all spoken words in the clip", "cut", "speech")]
    [InlineData("Cut all the speech", "cut", "speech")]
    [InlineData("remove all the talking", "cut", "speech")]
    [InlineData("delete the dialogue", "cut", "speech")]
    [InlineData("drop every spoken part", "cut", "speech")]
    [InlineData("take out the words", "cut", "speech")]
    [InlineData("remove the silences", "cut", "silence")]
    [InlineData("cut the pauses", "cut", "silence")]
    [InlineData("get rid of the dead air", "cut", "silence")]
    [InlineData("trim the silences", "cut", "silence")]
    [InlineData("shorten the pauses", "cut", "silence")]
    [InlineData("remove the ums and uhs", "cut", "fillers")]
    [InlineData("cut the filler words", "cut", "fillers")]
    [InlineData("delete all the umms", "cut", "fillers")]
    [InlineData("keep only the best lines", "cut", "keepOnly")]
    [InlineData("just keep the parts where I talk about pricing", "cut", "keepOnly")]
    [InlineData("mute the music", "mute", null)]
    [InlineData("silence the audio", "mute", null)]
    [InlineData("mute the speech", "mute", "speech")]
    [InlineData("turn off the audio", "mute", null)]
    [InlineData("trim the start", "trim", null)]
    [InlineData("shorten the intro", "trim", null)]
    [InlineData("make the first clip shorter", "trim", null)]
    [InlineData("reorder the clips", "reorder", null)]
    [InlineData("move the intro to the end", "reorder", null)]
    [InlineData("swap the first two clips", "reorder", null)]
    [InlineData("taglia tutto il parlato", "cut", "speech")]
    [InlineData("taglia tutte le parole pronunciate", "cut", "speech")]
    [InlineData("elimina la voce", "cut", "speech")]
    [InlineData("rimuovi i silenzi", "cut", "silence")]
    [InlineData("taglia le pause", "cut", "silence")]
    [InlineData("accorcia i silenzi", "cut", "silence")]
    [InlineData("togli gli ehm", "cut", "fillers")]
    [InlineData("elimina gli intercalari", "cut", "fillers")]
    [InlineData("tieni solo le parti importanti", "cut", "keepOnly")]
    [InlineData("solo le parti in cui parlo", "cut", "keepOnly")]
    [InlineData("muta il parlato", "mute", "speech")]
    [InlineData("disattiva l'audio", "mute", null)]
    [InlineData("accorcia l'inizio", "trim", null)]
    [InlineData("riordina le clip", "reorder", null)]
    [InlineData("sposta la prima clip alla fine", "reorder", null)]
    [InlineData("remove the second clip", "cut", null)]
    [InlineData("elimina l'ultima clip", "cut", null)]
    [InlineData("make it punchier", null, null)]
    [InlineData("", null, null)]
    [InlineData("rendilo più dinamico", null, null)]
    public void Classify_reads_the_operation_and_target_of_a_request(string brief, string? op, string? target)
    {
        (RecutOpClass? o, RecutTarget? t) = RecutRequestIntent.Classify(brief);
        (o is { } oc ? RecutRequestIntent.Word(oc) : null).Should().Be(op, brief);
        (t is { } tc ? RecutRequestIntent.Word(tc) : null).Should().Be(target, brief);
    }

    [Fact]
    public void Classify_of_nothing_is_nothing()
    {
        RecutRequestIntent.Classify(null).Should().Be((null, null));
    }
}
