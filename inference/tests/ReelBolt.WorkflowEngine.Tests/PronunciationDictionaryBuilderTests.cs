using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Tests.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The pronunciation dictionary (docs/voiceover.md "Pronunciation dictionary"): terms come from
/// the code analysis, phonemes from the first-party table, entries only for lines that contain the
/// term, the cap degrades instead of dropping, and the Fish wire shape is the verified inline form.
/// </summary>
public class PronunciationDictionaryBuilderTests
{
    private static readonly string DependencyAnalysis = JsonSerializer.Serialize(new
    {
        dependencies = new object[]
        {
            new { name = "@mantine/core", version = "8.0.0", purpose = "ui", isCore = true },
            new { name = "nginx-config", version = "1", purpose = "proxy", isCore = false },
            new { name = "pg", version = "8", purpose = "db", isCore = true }
        },
        devDependencies = new[] { "vitest", "eslint" },
        packageManager = "npm",
        recommendations = new object[0]
    });

    private static readonly string CodeStructure = JsonSerializer.Serialize(new
    {
        projectType = "Next.js app",
        framework = "Remotion + Mantine",
        directories = new object[0],
        entryPoints = new[] { "src/index.ts" },
        overallArchitecture = "monolith"
    });

    [Fact]
    public void Terms_come_from_the_analysis_outputs_and_unknown_shapes_contribute_nothing()
    {
        IReadOnlySet<string> terms = PronunciationDictionaryBuilder.ExtractAnalysisTerms(
            [DependencyAnalysis, CodeStructure, "not json", null, "[1,2,3]", "{\"keep\":[]}"]);

        terms.Should().Contain(["mantine", "core", "nginx", "config", "vitest", "eslint", "npm", "next", "app", "remotion"]);
        terms.Should().NotContain("pg", "two-letter tokens are never terms");
    }

    [Fact]
    public void An_entry_is_built_only_for_an_analysis_term_the_table_knows_that_appears_in_the_line_as_a_whole_word()
    {
        IReadOnlySet<string> terms = PronunciationDictionaryBuilder.ExtractAnalysisTerms([DependencyAnalysis, CodeStructure]);

        IReadOnlyList<PronunciationEntry> entries = PronunciationDictionaryBuilder.BuildForLine(
            "Built on Remotion and Mantine, served by nginx — the config stays simple.", terms, maxEntries: 16);

        entries.Select(e => e.Key).Should().Equal("Remotion", "Mantine", "nginx");
        entries.Should().OnlyContain(e => PronunciationDictionaryBuilder.Table[e.Key] == e.Value);
        entries.Should().OnlyContain(e => !e.CaseSensitive);
        // "config" is an analysis term the table does not know: fails closed, never guessed.
        entries.Select(e => e.Key).Should().NotContain("config");
    }

    [Fact]
    public void A_term_the_analysis_never_named_is_never_pronounced_specially()
    {
        IReadOnlySet<string> terms = PronunciationDictionaryBuilder.ExtractAnalysisTerms([CodeStructure]);

        PronunciationDictionaryBuilder.BuildForLine("Deploys through nginx take four minutes.", terms, 16)
            .Should().BeEmpty("nginx is in the table but not in this run's analysis");
    }

    [Fact]
    public void Short_all_caps_acronyms_are_sent_case_sensitively_and_substrings_never_match()
    {
        var terms = new HashSet<string>(["api", "sql"], System.StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<PronunciationEntry> entries = PronunciationDictionaryBuilder.BuildForLine(
            "A rapid API over SQL — mysql stays out.", terms, 16);

        entries.Select(e => e.Key).Should().Equal("API", "SQL");
        entries.Should().OnlyContain(e => e.CaseSensitive, "a substring-matching vendor must not rewrite the inside of 'rapid'");
    }

    [Fact]
    public void The_cap_keeps_the_earliest_entries_and_drops_the_rest_rather_than_the_whole_dictionary()
    {
        var terms = new HashSet<string>(["remotion", "mantine", "nginx", "redis"], System.StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<PronunciationEntry> entries = PronunciationDictionaryBuilder.BuildForLine(
            "Remotion, Mantine, nginx and Redis.", terms, maxEntries: 2);

        entries.Select(e => e.Key).Should().Equal("Remotion", "Mantine");
    }

    [Fact]
    public async Task Fish_client_sends_the_inline_pronunciation_dictionary_and_omits_it_when_there_are_no_entries()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(null, FishAudioSpeechSynthesisClientTests.GetSampleWavBytes(), HttpStatusCode.OK);
        var client = new FishAudioSpeechSynthesisClient(
            new HttpClient(handler) { BaseAddress = new System.Uri("https://api.fish.audio") }, null);

        await client.SynthesizeAsync(
            new SpeechRequest("Built on Remotion and an API", null, null,
                [new PronunciationEntry("Remotion", "riːˈmoʊʃən"), new PronunciationEntry("API", "eɪ piː aɪ", CaseSensitive: true)]),
            CancellationToken.None);

        using JsonDocument withDict = JsonDocument.Parse(handler.LastRequestBody!);
        JsonElement dictionaries = withDict.RootElement.GetProperty("pronunciation_dictionary");
        dictionaries.GetArrayLength().Should().Be(1, "one inline dictionary carries every entry");
        JsonElement items = dictionaries[0].GetProperty("items");
        items.GetArrayLength().Should().Be(2);
        items[0].GetProperty("key").GetString().Should().Be("Remotion");
        items[0].GetProperty("value").GetString().Should().Be("riːˈmoʊʃən");
        items[0].GetProperty("case_sensitive").GetBoolean().Should().BeFalse();
        items[1].GetProperty("case_sensitive").GetBoolean().Should().BeTrue();

        await client.SynthesizeAsync(new SpeechRequest("plain", null, null), CancellationToken.None);
        using JsonDocument without = JsonDocument.Parse(handler.LastRequestBody!);
        without.RootElement.TryGetProperty("pronunciation_dictionary", out _)
            .Should().BeFalse("no entries means the phase-1 request body, byte for byte");
    }

    [Theory]
    [InlineData("it", "it")]
    [InlineData("it-it", "it-IT")]
    [InlineData("IT-IT", "it-IT")]
    [InlineData("zh-cn", "zh-CN")]
    [InlineData("zh-hant-tw", "zh-Hant-TW")]
    [InlineData(" en ", "en")]
    [InlineData("xx", null)]
    [InlineData("nl-NL", null)]
    [InlineData("it_IT", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("it-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)]
    public void Fish_language_tag_mapper_keeps_only_documented_languages(string? input, string? expected) =>
        FishLanguageTag.From(input).Should().Be(expected);

    [Fact]
    public async Task Fish_client_sends_language_and_omits_it_when_null_or_unlisted()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(null, FishAudioSpeechSynthesisClientTests.GetSampleWavBytes(), HttpStatusCode.OK);
        var client = new FishAudioSpeechSynthesisClient(
            new HttpClient(handler) { BaseAddress = new System.Uri("https://api.fish.audio") }, null);

        await client.SynthesizeAsync(new SpeechRequest("ciao", null, null, Language: "it-it"), CancellationToken.None);
        using (JsonDocument with = JsonDocument.Parse(handler.LastRequestBody!))
            with.RootElement.GetProperty("language").GetString().Should().Be("it-IT");

        await client.SynthesizeAsync(new SpeechRequest("ciao", null, null, Language: "nl"), CancellationToken.None);
        using (JsonDocument unlisted = JsonDocument.Parse(handler.LastRequestBody!))
            unlisted.RootElement.TryGetProperty("language", out _).Should().BeFalse();

        await client.SynthesizeAsync(new SpeechRequest("ciao", null, null), CancellationToken.None);
        using JsonDocument none = JsonDocument.Parse(handler.LastRequestBody!);
        none.RootElement.TryGetProperty("language", out _).Should().BeFalse("no language is the previous body, byte for byte");
    }

    [Fact]
    public void Line_content_key_is_stable_without_a_language_and_changes_with_the_tag()
    {
        string plain = VoiceoverStepExecutor.ComputeLineContentKey("a", "v", "m", null, "e");
        VoiceoverStepExecutor.ComputeLineContentKey("a", "v", "m", null, "e", null, null).Should().Be(plain);
        string it = VoiceoverStepExecutor.ComputeLineContentKey("a", "v", "m", null, "e", null, "it-IT");
        string en = VoiceoverStepExecutor.ComputeLineContentKey("a", "v", "m", null, "e", null, "en");
        it.Should().NotBe(plain);
        it.Should().NotBe(en);
    }
}
