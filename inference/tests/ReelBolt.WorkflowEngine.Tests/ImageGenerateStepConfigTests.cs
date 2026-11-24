using System;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public sealed class ImageGenerateStepConfigTests
{
    private static readonly Guid FileId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ImageGenerateStepConfig Generate(Func<ImageGenerateStepConfig, ImageGenerateStepConfig>? edit = null) =>
        (edit ?? (c => c))(new ImageGenerateStepConfig(ImageGenerateMode.Generate, Prompt: "A red fox in snow"));

    private static ImageGenerateStepConfig Edit(Func<ImageGenerateStepConfig, ImageGenerateStepConfig>? edit = null) =>
        (edit ?? (c => c))(new ImageGenerateStepConfig(
            ImageGenerateMode.Edit, Instruction: "Make it night",
            Source: new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: FileId), AllowSourceMediaEgress: true));

    [Fact]
    public void A_plain_generate_and_a_plain_edit_are_valid()
    {
        Generate().Validate().Should().BeEmpty();
        Edit().Validate().Should().BeEmpty();
    }

    [Fact]
    public void Generate_needs_a_prompt_and_refuses_the_edit_only_fields()
    {
        Generate(c => c with { Prompt = " " }).Validate().Should().ContainSingle().Which.Should().Contain("Prompt is required");
        Generate(c => c with { Instruction = "x" }).Validate().Should().Contain(e => e.Contains("Instruction is for Edit only"));
        Generate(c => c with { Source = new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: FileId) }).Validate()
            .Should().Contain(e => e.Contains("Source is for Edit only"));
        Generate(c => c with { AllowSourceMediaEgress = true }).Validate().Should().Contain(e => e.Contains("AllowSourceMediaEgress"));
    }

    [Fact]
    public void Edit_needs_an_instruction_and_one_image_source_and_takes_one_to_four_variations()
    {
        Edit(c => c with { Instruction = null }).Validate().Should().Contain(e => e.Contains("Instruction is required"));
        Edit(c => c with { Prompt = "x" }).Validate().Should().Contain(e => e.Contains("Prompt is for Generate only"));
        Edit(c => c with { Source = null }).Validate().Should().Contain(e => e.Contains("Source is required"));
        Edit(c => c with { Source = new VideoSourceRef(VideoSourceKind.GeneratedClips) }).Validate().Should().Contain(e => e.Contains("not supported"));
        Edit(c => c with { Source = new VideoSourceRef(VideoSourceKind.ProjectFile) }).Validate().Should().Contain(e => e.Contains("requires ProjectFileId"));
        Edit(c => c with { Variations = 3 }).Validate().Should().BeEmpty();
        Edit(c => c with { Variations = 3 }).ImageCount.Should().Be(3);
        Edit(c => c with { Variations = 5 }).Validate().Should().Contain(e => e.Contains("Variations must be from 1 to 4"));
        Edit().ImageCount.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Variations_are_one_to_four(int variations) =>
        Generate(c => c with { Variations = variations }).Validate().Should().ContainSingle().Which.Should().Contain("Variations must be from 1 to 4");

    [Fact]
    public void Text_length_control_characters_prefix_spend_provider_and_sizes_are_checked()
    {
        Generate(c => c with { Prompt = new string('a', 4001) }).Validate().Should().Contain(e => e.Contains("at most 4000"));
        Generate(c => c with { Prompt = "a\u0001b" }).Validate().Should().Contain(e => e.Contains("control characters"));
        Generate(c => c with { MaxSpendCredits = 0 }).Validate().Should().Contain(e => e.Contains("MaxSpendCredits"));
        Generate(c => c with { MaxSpendCredits = double.NaN }).Validate().Should().Contain(e => e.Contains("MaxSpendCredits"));
        Generate(c => c with { ProviderId = Guid.Empty }).Validate().Should().Contain(e => e.Contains("ProviderId"));
        Generate(c => c with { FileNamePrefix = "../x" }).Validate().Should().Contain(e => e.Contains("FileNamePrefix"));
        Generate(c => c with { AspectRatio = "wide" }).Validate().Should().Contain(e => e.Contains("AspectRatio"));
        Generate(c => c with { Width = 512 }).Validate().Should().Contain(e => e.Contains("both be set"));
        Generate(c => c with { Width = 512, Height = 512, AspectRatio = "1:1" }).Validate().Should().Contain(e => e.Contains("not both"));
        Generate(c => c with { Width = 9000, Height = 512 }).Validate().Should().Contain(e => e.Contains("at most 8192"));
        Generate(c => c with { Width = 1024, Height = 576, MaxSpendCredits = 5, FileNamePrefix = "hero_1" }).Validate().Should().BeEmpty();
        Generate(c => c with { AspectRatio = "16:9" }).Validate().Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_mode_is_refused_and_the_json_round_trips_by_name()
    {
        new ImageGenerateStepConfig((ImageGenerateMode)9).Validate().Should().ContainSingle().Which.Should().Contain("Mode must be one of");

        string json = JsonSerializer.Serialize(Edit(), ImageGenerateStepConfig.JsonOptions);
        json.Should().Contain("\"mode\":\"Edit\"");
        JsonSerializer.Deserialize<ImageGenerateStepConfig>(json, ImageGenerateStepConfig.JsonOptions).Should().BeEquivalentTo(Edit());
        JsonSerializer.Deserialize<ImageGenerateStepConfig>("""{"mode":"Generate","prompt":"x","variations":3}""", ImageGenerateStepConfig.JsonOptions)!
            .ImageCount.Should().Be(3);
    }

    // ================================================================ batch

    [Fact]
    public void ParseItems_splits_on_lines_commas_and_semicolons_trims_and_drops_empties_and_duplicates()
    {
        (System.Collections.Generic.IReadOnlyList<string> items, System.Collections.Generic.List<string> errors) =
            ImageGenerateStepConfig.ParseItems(" Red, blue\r\n\nRED;  Green ;;");

        items.Should().Equal("Red", "blue", "Green");
        errors.Should().BeEmpty();
        ImageGenerateStepConfig.ParseItems(null).Items.Should().BeEmpty();
        ImageGenerateStepConfig.ParseItems("  \n ").Items.Should().BeEmpty();
    }

    [Fact]
    public void ParseItems_reports_too_many_items_a_long_item_and_a_control_character()
    {
        ImageGenerateStepConfig.ParseItems(string.Join(",", Enumerable.Range(1, 25))).Errors.Should().ContainSingle().Which.Should().Contain("at most 24");
        ImageGenerateStepConfig.ParseItems(new string('x', 121)).Errors.Should().ContainSingle().Which.Should().Contain("120");
        ImageGenerateStepConfig.ParseItems("a\u0001b").Errors.Should().ContainSingle().Which.Should().Contain("control");
    }

    [Fact]
    public void An_item_list_needs_the_item_token_and_multiplies_the_picture_count()
    {
        Generate(c => c with { ItemList = "red,blue" }).Validate().Should().ContainSingle().Which.Should().Contain("{item}");

        ImageGenerateStepConfig batch = Generate(c => c with { Prompt = "A {item} fox", ItemList = "red, blue, green", Variations = 2 });
        batch.Validate().Should().BeEmpty();
        batch.ImageCount.Should().Be(6);
        batch.PromptFor("red").Should().Be("A red fox");
        Generate(c => c with { Variations = 3 }).ImageCount.Should().Be(3);
    }

    [Fact]
    public void Without_an_item_list_the_token_is_literal_and_valid()
    {
        ImageGenerateStepConfig plain = Generate(c => c with { Prompt = "Keep {item}" });
        plain.Validate().Should().BeEmpty();
        plain.PromptFor(null).Should().Be("Keep {item}");
        plain.ImageCount.Should().Be(1);
    }

    [Fact]
    public void A_step_may_make_at_most_24_pictures_and_an_empty_list_is_refused()
    {
        Generate(c => c with { Prompt = "{item}", ItemList = string.Join(",", Enumerable.Range(1, 7)), Variations = 4 }).Validate()
            .Should().Contain(e => e.Contains("at most 24 pictures"));
        Generate(c => c with { Prompt = "{item}", ItemList = string.Join(",", Enumerable.Range(1, 6)), Variations = 4 }).Validate().Should().BeEmpty();
        Generate(c => c with { Prompt = "{item}", ItemList = " , " }).Validate().Should().Contain(e => e.Contains("no items"));
    }

    [Fact]
    public void Edit_takes_the_run_picture_files_as_items_priced_at_the_worst_case_and_refuses_an_item_list_with_them()
    {
        var source = new VideoSourceRef(VideoSourceKind.BriefFiles, Role: ReelBolt.Shared.Briefs.RunBriefFileRole.Image);
        ImageGenerateStepConfig config = Edit(c => c with { Source = source, Variations = 2 });

        config.Validate().Should().BeEmpty();
        config.ImageCount.Should().Be(24, "12 files x 2 is the most the step may make");
        Edit(c => c with { Source = source, Variations = 4 }).ImageCount.Should().Be(24);
        Edit(c => c with { Source = source, Instruction = "{item} night", ItemList = "a" }).Validate()
            .Should().Contain(e => e.Contains("not allowed with Source=BriefFiles"));
        Edit(c => c with { Source = source with { Role = ReelBolt.Shared.Briefs.RunBriefFileRole.Video } }).Validate()
            .Should().Contain(e => e.Contains("Role=Image"));
        Generate(c => c with { Source = source }).Validate().Should().Contain(e => e.Contains("Source is for Edit only"));
    }

    [Fact]
    public void The_item_list_round_trips_through_json()
    {
        string json = JsonSerializer.Serialize(Generate(c => c with { Prompt = "{item}", ItemList = "a,b" }), ImageGenerateStepConfig.JsonOptions);
        json.Should().Contain("\"itemList\":\"a,b\"");
        JsonSerializer.Deserialize<ImageGenerateStepConfig>(json, ImageGenerateStepConfig.JsonOptions)!.Items.Should().Equal("a", "b");
        JsonSerializer.Serialize(Generate(), ImageGenerateStepConfig.JsonOptions).Should().Contain("\"itemList\":null");
    }

    [Fact]
    public void An_app_may_offer_image_files_to_an_ImageGenerate_that_edits_the_brief_files()
    {
        const string briefEdit = """{"mode":"Edit","instruction":"x","allowSourceMediaEgress":true,"source":{"kind":"BriefFiles","role":"Image"}}""";
        const string plain = """{"mode":"Generate","prompt":"x"}""";

        ReelBolt.Shared.Workflows.Apps.BriefFieldConsumers.AcceptsFileRole("image",
            [ReelBolt.Shared.Workflows.Apps.AppStepFacts.From(ReelBolt.Shared.Data.Models.StepType.ImageGenerate, null, null, null, null, briefEdit)]).Should().BeTrue();
        ReelBolt.Shared.Workflows.Apps.BriefFieldConsumers.AcceptsFileRole("image",
            [ReelBolt.Shared.Workflows.Apps.AppStepFacts.From(ReelBolt.Shared.Data.Models.StepType.ImageGenerate, null, null, null, null, plain)]).Should().BeFalse();
    }
}
