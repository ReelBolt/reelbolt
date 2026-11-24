using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

public class NarrationGlossaryStalenessTests
{
    private static NarrationGlossaryStaleness.CurrentGlossary Glossary(params GlossaryEntry[] entries) =>
        NarrationGlossaryStaleness.CurrentGlossary.From(entries, null);

    private const string Output =
        """{"view":{"lines":[{"id":"vo0","status":"ok","text":"Visit Novara."},{"id":"vo1","status":"ok","text":"Nothing here."},{"id":"vo2","status":"failed"}]}}""";

    [Fact]
    public void A_line_holding_a_term_the_run_did_not_respell_is_stale()
    {
        NarrationGlossaryStaleness.StaleLineIds(Output, Glossary(new GlossaryEntry("Novara", "Novàra", false)))
            .Should().BeEquivalentTo("vo0");
    }

    [Fact]
    public void An_output_made_with_the_same_glossary_has_no_stale_line()
    {
        var entry = new GlossaryEntry("Novara", "Novàra", false);
        string digest = PronunciationRespeller.Digest([entry])!;
        string output = Output.Replace("\"view\"", "\"meta\":{\"pronunciation\":{\"glossaryDigest\":\"" + digest + "\"}},\"view\"");

        NarrationGlossaryStaleness.StaleLineIds(output, Glossary(entry)).Should().BeEmpty();
    }

    [Fact]
    public void A_recorded_spoken_form_that_matches_the_current_glossary_is_not_stale()
    {
        string output = Output.Replace("\"text\":\"Visit Novara.\"", "\"text\":\"Visit Novara.\",\"spoken\":\"Visit Novàra.\"");

        NarrationGlossaryStaleness.StaleLineIds(output, Glossary(new GlossaryEntry("Novara", "Novàra", false))).Should().BeEmpty();
        NarrationGlossaryStaleness.StaleLineIds(output, Glossary(new GlossaryEntry("Novara", "Nòvara", false))).Should().BeEquivalentTo("vo0");
        NarrationGlossaryStaleness.StaleLineIds(output, Glossary()).Should().BeEquivalentTo(["vo0"], "a removed entry changes the spoken form");
    }

    [Fact]
    public void Terms_the_step_names_itself_are_left_to_the_step()
    {
        var glossary = NarrationGlossaryStaleness.CurrentGlossary.From([new GlossaryEntry("Novara", "Novàra", false)], ["Novara"]);

        NarrationGlossaryStaleness.StaleLineIds(Output, glossary).Should().BeEmpty();
    }

    [Fact]
    public void An_unreadable_output_has_no_stale_line()
    {
        NarrationGlossaryStaleness.StaleLineIds("not json", Glossary(new GlossaryEntry("a", "b", false))).Should().BeEmpty();
        NarrationGlossaryStaleness.StaleLineIds(null, Glossary(new GlossaryEntry("a", "b", false))).Should().BeEmpty();
    }
}
