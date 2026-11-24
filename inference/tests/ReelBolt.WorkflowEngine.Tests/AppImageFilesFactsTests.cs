using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Tests.Marketplace;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A picture edit of the run's files (ImageGenerate Edit, Source=BriefFiles) makes a `files` role=image input valid, however the
/// step's facts are read (stored step, template step, portable snapshot), and the other file-taking steps keep working.
/// </summary>
public sealed class AppImageFilesFactsTests
{
    private const string EditBriefFiles =
        """{"version":1,"mode":"Edit","instruction":"make it blue","source":{"kind":"BriefFiles","role":"Image"}}""";

    private static readonly AppManifest ImageFilesManifest = new(Version: 1,
        Inputs: [new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "image")]);

    private static WorkflowStep EditStep(string config = EditBriefFiles) =>
        new() { StepOrder = 1, StepType = StepType.ImageGenerate, ImageGenerateConfigJson = config };

    private static WorkflowTemplateDefinition TemplateOf(string config) =>
        new("k", "n", "d", 1, false, false,
            [new WorkflowTemplateStepDefinition(AgentType.VideoTransform, "Edit", StepType.ImageGenerate, ImageGenerateConfigJson: config)]);

    [Fact]
    public void FromStep_sets_TakesBriefImageFiles_for_an_Edit_of_the_brief_files()
    {
        AppStepFacts.FromStep(EditStep(), null).TakesBriefImageFiles.Should().BeTrue();
        AppStepFacts.FromStep(EditStep("""{"version":1,"mode":"Generate","prompt":"a cat"}"""), null).TakesBriefImageFiles.Should().BeFalse();
        AppStepFacts.FromStep(new WorkflowStep { StepType = StepType.ImageGenerate }, null).TakesBriefImageFiles.Should().BeFalse();
    }

    [Fact]
    public void FromTemplate_and_the_switch_free_reader_set_TakesBriefImageFiles_for_an_Edit_of_the_brief_files()
    {
        WorkflowTemplateDefinition template = TemplateOf(EditBriefFiles);
        AppStepFacts.FromTemplate(template).Single().TakesBriefImageFiles.Should().BeTrue();
        AppStepFacts.FromTemplateIgnoringSwitch(template).Single().TakesBriefImageFiles.Should().BeTrue();
    }

    [Fact]
    public void An_image_files_input_is_an_orphan_only_without_a_step_that_takes_pictures()
    {
        AppManifestValidator.FindOrphans(ImageFilesManifest, [AppStepFacts.FromStep(EditStep(), null)]).Should().BeEmpty();
        AppManifestValidator.FindOrphans(ImageFilesManifest, [new AppStepFacts(StepType.VideoAnalyze)])
            .Should().Contain(o => o.Message.Contains("takes image files"));
        // The old, config-blind read: what the fix removed.
        AppManifestValidator.FindOrphans(ImageFilesManifest, [AppStepFacts.From(StepType.ImageGenerate, null, null)]).Should().NotBeEmpty();
    }

    private static PortableRecipe Recipe(StepType type, JsonNode config, AppManifest manifest)
    {
        WorkflowStep step = SnapshotFixtures.Step(1, type);
        SnapshotFixtures.SetConfig(step, config);
        return PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), [step], SnapshotFixtures.BuiltIns(), null).Recipe! with { Manifest = manifest };
    }

    [Fact]
    public void A_recipe_with_an_image_edit_of_the_brief_files_and_an_image_files_input_validates()
    {
        PortableRecipe recipe = Recipe(StepType.ImageGenerate, JsonNode.Parse(EditBriefFiles)!, ImageFilesManifest);

        PortableRecipeValidator.FactsOf(recipe).Should().ContainSingle().Which.TakesBriefImageFiles.Should().BeTrue();
        PortableRecipeValidator.Validate(recipe).Should().BeEmpty();
    }

    [Fact]
    public void The_same_manifest_without_such_a_step_is_still_refused()
    {
        PortableRecipe recipe = Recipe(StepType.ImageGenerate,
            JsonNode.Parse("""{"version":1,"mode":"Generate","prompt":"a cat"}""")!, ImageFilesManifest);

        PortableRecipeValidator.FactsOf(recipe).Single().TakesBriefImageFiles.Should().BeFalse();
        PortableRecipeValidator.Validate(recipe).Should().Contain(e => e.Contains("takes image files"));
    }

    [Theory]
    [InlineData(StepType.ExtractFrame)]
    [InlineData(StepType.PinInsert)]
    public void Steps_that_take_pictures_by_type_accept_an_image_files_input(StepType type) =>
        AppManifestValidator.FindOrphans(ImageFilesManifest, [new AppStepFacts(type)]).Should().BeEmpty();

    [Fact]
    public void AudioGenerate_takes_no_picture_files_so_an_image_files_input_is_refused()
    {
        // Reported as a fact, not a defect: AudioGenerate has no picture source.
        AppManifestValidator.FindOrphans(ImageFilesManifest, [new AppStepFacts(StepType.AudioGenerate)]).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("""{"version":1,"mode":"Generate","prompt":"a {item} poster","itemList":"{{input.things}}"}""")]
    [InlineData("""{"version":1,"mode":"Generate","prompt":"{{input.idea}}"}""")]
    [InlineData("""{"version":1,"mode":"Edit","instruction":"{{input.idea}}","source":{"kind":"BriefFiles","role":"Image"}}""")]
    public void Text_inputs_bind_into_the_prompt_and_item_list_of_an_ImageGenerate_step(string config)
    {
        bool usesList = config.Contains("input.things");
        string id = usesList ? "things" : "idea";
        string type = usesList ? "longText" : "shortText";
        IReadOnlyList<AppStepFacts> facts = [AppStepFacts.FromStep(EditStep(config), null)];
        IReadOnlyList<AppBindingStep> steps = [Step(1, StepType.ImageGenerate, config)];

        AppManifestValidator.Validate(AppManifestValidator.Sanitize(Manifest(Input(id, type))), facts, steps).Should().BeEmpty();
    }

    [Fact]
    public void The_bindable_catalog_lists_the_ImageGenerate_text_settings()
    {
        AppBindableCatalog.For(StepType.ImageGenerate).Where(f => f.Mode == AppBindingMode.Placeholder).Select(f => f.Path)
            .Should().Contain(["prompt", "instruction", "itemList"]);
    }
}
