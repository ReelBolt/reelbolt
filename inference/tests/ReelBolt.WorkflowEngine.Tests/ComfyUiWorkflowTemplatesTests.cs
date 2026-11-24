using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The ComfyUI integration (issue #93 phase 5) submits ONLY vetted, first-party workflow graphs:
/// the "model" on a ComfyUI provider row is an exact template NAME, and the graph is built here from
/// literals plus a handful of typed slots. These tests pin the refusal of anything else — a free-form
/// graph, a path, a near-miss name — and the shape of what IS built, so the prompt can only ever land
/// in a <c>CLIPTextEncode.text</c> string input.
/// </summary>
public class ComfyUiWorkflowTemplatesTests
{
    [Theory]
    [InlineData("wan2.2-ti2v-5b-t2v", ComfyUiWorkflowTemplate.Wan22Ti2v5bTextToVideo)]
    [InlineData("wan2.2-ti2v-5b-i2v", ComfyUiWorkflowTemplate.Wan22Ti2v5bImageToVideo)]
    public void TryParse_accepts_exactly_the_two_vetted_names(string name, ComfyUiWorkflowTemplate expected)
    {
        ComfyUiWorkflowTemplates.TryParse(name, out ComfyUiWorkflowTemplate template).Should().BeTrue();
        template.Should().Be(expected);
        ComfyUiWorkflowTemplates.NameOf(template).Should().Be(name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("WAN2.2-TI2V-5B-T2V")]
    [InlineData("/wan2.2-ti2v-5b-t2v")]
    [InlineData("wan2.2-ti2v-5b")]
    [InlineData("ltx-2")]
    [InlineData("{\"1\":{\"class_type\":\"UNETLoader\"}}")]
    [InlineData("http://evil.example/graph.json")]
    [InlineData("../workflows/custom.json")]
    public void TryParse_rejects_anything_that_is_not_an_exact_vetted_name(string? name)
    {
        // Case, a path, a URL and a serialized graph are all refused: the only way a
        // graph is chosen is by exact ordinal match against the template enum's names.
        ComfyUiWorkflowTemplates.TryParse(name, out _).Should().BeFalse();
    }

    [Fact]
    public void Names_lists_exactly_the_enum_members()
    {
        ComfyUiWorkflowTemplates.Names.Should().BeEquivalentTo(
            Enum.GetValues<ComfyUiWorkflowTemplate>().Select(ComfyUiWorkflowTemplates.NameOf));
    }

    [Fact]
    public void Build_text_to_video_emits_a_fixed_node_set_and_puts_the_prompt_only_in_a_string_input()
    {
        const string prompt = "A lighthouse at dusk, \"quoted\", with a {brace} and a [bracket]";
        JsonObject graph = ComfyUiWorkflowTemplates.Build(
            ComfyUiWorkflowTemplate.Wan22Ti2v5bTextToVideo,
            new ComfyUiWorkflowSlots(prompt, Seed: 42, Width: 832, Height: 480, LengthFrames: 97, Fps: 24, FilenamePrefix: "abc"));

        // Every node is one of the template's own class names — nothing caller-chosen.
        IReadOnlyList<string> required = ComfyUiWorkflowTemplates.RequiredNodeClasses(ComfyUiWorkflowTemplate.Wan22Ti2v5bTextToVideo);
        foreach (KeyValuePair<string, JsonNode?> node in graph)
        {
            string classType = node.Value!["class_type"]!.GetValue<string>();
            required.Should().Contain(classType, $"node {node.Key} instantiates a class the template did not declare");
        }

        graph.Should().NotContainKey("10", "the text-to-video graph has no LoadImage node");

        // The prompt is the value of CLIPTextEncode.text and appears NOWHERE else — not as a key,
        // not as a class name, not as a filename.
        graph["4"]!["inputs"]!["text"]!.GetValue<string>().Should().Be(prompt);
        string serialized = graph.ToJsonString();
        int occurrences = CountOccurrences(serialized, System.Text.Json.JsonSerializer.Serialize(prompt).Trim('"'));
        occurrences.Should().Be(1, "the caller-authored prompt must appear exactly once, as a string value");

        graph["6"]!["inputs"]!["width"]!.GetValue<int>().Should().Be(832);
        graph["6"]!["inputs"]!["height"]!.GetValue<int>().Should().Be(480);
        graph["6"]!["inputs"]!["length"]!.GetValue<int>().Should().Be(97);
        graph["8"]!["inputs"]!["seed"]!.GetValue<long>().Should().Be(42);
    }

    [Fact]
    public void Build_image_to_video_requires_an_uploaded_start_image_and_wires_it_into_the_latent()
    {
        Action noImage = () => ComfyUiWorkflowTemplates.Build(
            ComfyUiWorkflowTemplate.Wan22Ti2v5bImageToVideo,
            new ComfyUiWorkflowSlots("p", 1, 832, 480, 25, 24, "abc"));
        noImage.Should().Throw<ArgumentException>();

        JsonObject graph = ComfyUiWorkflowTemplates.Build(
            ComfyUiWorkflowTemplate.Wan22Ti2v5bImageToVideo,
            new ComfyUiWorkflowSlots("p", 1, 832, 480, 25, 24, "abc", StartImageName: "reelbolt/start.png"));

        graph["10"]!["class_type"]!.GetValue<string>().Should().Be("LoadImage");
        graph["10"]!["inputs"]!["image"]!.GetValue<string>().Should().Be("reelbolt/start.png");
        graph["6"]!["inputs"]!["start_image"]!.AsArray()[0]!.GetValue<string>().Should().Be("10");
    }

    [Theory]
    [InlineData("480P", "16:9", 832, 480)]
    [InlineData("480P", "9:16", 480, 832)]
    [InlineData("480P", "1:1", 480, 480)]
    [InlineData("720P", "16:9", 1280, 704)]
    public void Dimensions_are_template_constants(string resolution, string ratio, int width, int height)
    {
        ComfyUiWorkflowTemplates.Dimensions(resolution, ratio).Should().Be((width, height));
    }

    [Theory]
    [InlineData("1080P", "16:9")]
    [InlineData("480P", "4:3")]
    public void Dimensions_reject_an_unoffered_resolution_or_ratio(string resolution, string ratio)
    {
        Action act = () => ComfyUiWorkflowTemplates.Dimensions(resolution, ratio);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LengthFrames_is_seconds_times_fps_plus_one()
    {
        ComfyUiWorkflowTemplates.LengthFrames(1).Should().Be(25);
        ComfyUiWorkflowTemplates.LengthFrames(5).Should().Be(121);
    }

    [Fact]
    public void Describe_prices_in_GPU_seconds_per_output_second_never_zero()
    {
        VideoGenerationCapabilities caps = ComfyUiWorkflowTemplates.Describe(ComfyUiWorkflowTemplate.Wan22Ti2v5bTextToVideo);
        caps.PricePerSecondByResolution.Values.Should().OnlyContain(v => v > 0m,
            "a zero estimate would pass every cap and credit the ledger with GPU time never spent");
        caps.MinDurationSeconds.Should().Be(1);
        caps.MaxDurationSeconds.Should().Be(5);
        caps.AllowedRatios.Should().BeEquivalentTo(new[] { "16:9", "9:16", "1:1" });
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
