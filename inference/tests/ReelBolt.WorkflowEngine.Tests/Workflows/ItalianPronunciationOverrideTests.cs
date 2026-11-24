using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

public class ItalianPronunciationOverrideTests
{
    private static readonly IReadOnlySet<string> NoTerms = new HashSet<string>();

    [Fact]
    public void Sanitize_normalises_decomposed_accents_to_nfc()
    {
        VoiceoverTextSanitizer.Sanitize("perché è", 100).Should().Be("perché è");
    }

    [Fact]
    public void Sanitize_leaves_nfc_input_unchanged()
    {
        VoiceoverTextSanitizer.Sanitize("Perché la città è bella", 100).Should().Be("Perché la città è bella");
    }

    [Fact]
    public void Overrides_apply_as_whole_words_even_with_no_analysis_terms()
    {
        var overrides = new[] { new PronunciationOverride("perché", "perˈke"), new PronunciationOverride("città", "tʃitˈta") };

        var entries = PronunciationDictionaryBuilder.BuildForLineWithOverrides("Perché la città? Perchéxx no.", NoTerms, 16, overrides);

        entries.Select(e => (e.Key, e.Value)).Should().Equal(("Perché", "perˈke"), ("città", "tʃitˈta"));
    }

    [Fact]
    public void Overrides_win_over_the_table_and_survive_the_cap()
    {
        var terms = new HashSet<string> { "graphql", "kubernetes" };
        var overrides = new[] { new PronunciationOverride("GraphQL", "ˈdɒkɚ") };

        var entries = PronunciationDictionaryBuilder.BuildForLineWithOverrides("GraphQL e Kubernetes", terms, 1, overrides);

        entries.Should().ContainSingle().Which.Value.Should().Be("ˈdɒkɚ");
    }

    [Fact]
    public void No_overrides_equals_the_plain_builder()
    {
        var terms = new HashSet<string> { "graphql" };
        PronunciationDictionaryBuilder.BuildForLineWithOverrides("GraphQL", terms, 16, null)
            .Should().BeEquivalentTo(PronunciationDictionaryBuilder.BuildForLine("GraphQL", terms, 16));
    }

    [Fact]
    public void Validation_accepts_italian_ipa_and_refuses_bad_entries()
    {
        PronunciationOverride.ValidateAll([new("perché", "perˈke"), new("città", "tʃitˈta")]).Should().BeEmpty();
        PronunciationOverride.ValidateAll([new("a", "x")]).Should().ContainSingle(e => e.Contains("term"));
        PronunciationOverride.ValidateAll([new("perché", "per<ke>")]).Should().ContainSingle(e => e.Contains("ipa"));
        PronunciationOverride.ValidateAll([new("perché", "perˈke"), new("PERCHÉ", "x")]).Should().ContainSingle(e => e.Contains("duplicate"));
        PronunciationOverride.ValidateAll(Enumerable.Range(0, 65).Select(i => new PronunciationOverride($"term{i}", "a")).ToList())
            .Should().Contain(e => e.Contains("at most 64"));
    }

    [Fact]
    public void Validation_refuses_blank_and_format_char_terms_and_bounds_the_duplicate_echo()
    {
        PronunciationOverride.ValidateAll([new("   ", "a")]).Should().ContainSingle(e => e.Contains("term"));
        PronunciationOverride.ValidateAll([new("ab\u200Bc", "a")]).Should().ContainSingle(e => e.Contains("term"));
        string longTerm = new string('x', 60);
        var dup = PronunciationOverride.ValidateAll([new(longTerm, "a"), new(longTerm.ToUpperInvariant(), "a")]);
        dup.Should().ContainSingle().Which.Should().NotContain(longTerm);
    }

    [Fact]
    public void Every_built_in_table_value_passes_the_override_validator()
    {
        foreach (var kv in PronunciationDictionaryBuilder.Table)
            PronunciationOverride.ValidateAll([new("term", kv.Value)]).Should().BeEmpty($"table entry {kv.Key} -> {kv.Value}");
    }

    [Fact]
    public void A_non_matching_case_sensitive_override_does_not_suppress_the_table_entry()
    {
        var terms = new HashSet<string> { "graphql" };
        var overrides = new[] { new PronunciationOverride("graphql", "x", CaseSensitive: true) };

        var entries = PronunciationDictionaryBuilder.BuildForLineWithOverrides("GraphQL", terms, 16, overrides);

        entries.Should().ContainSingle().Which.Value.Should().Be(PronunciationDictionaryBuilder.Table["graphql"]);
    }

    [Fact]
    public void Save_validator_names_the_bad_override_field()
    {
        string json = "{\"source\":{\"kind\":\"Inline\"},\"pronunciationOverrides\":[{\"term\":\"perché\",\"ipa\":\"<x>\"}]}";
        ReelBolt.Inference.Api.Services.Workflows.StepConfigSaveValidator.ValidateVoiceoverConfig(json)
            .Should().ContainSingle(e => e.Contains("pronunciationOverrides[0].ipa"));
    }
}
