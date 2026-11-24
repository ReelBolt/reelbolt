using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-106: save-time rules for a VideoCompile step's insert content references.</summary>
public sealed class InsertContentRefSaveValidationTests
{
    private static readonly Guid ResultA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static CreateWorkflowStepRequest Step(int order, StepType type, string? compileConfig = null) =>
        new(Guid.NewGuid(), order, null, $"s{order}", StepType: type.ToString(),
            VideoCompileConfigJson: compileConfig,
            VideoAnalyzeConfigJson: type == StepType.VideoAnalyze ? "{\"version\":1,\"source\":{\"kind\":\"PreviousStepOutput\"}}" : null,
            ExtractFrameConfigJson: type == StepType.ExtractFrame ? "{\"version\":1}" : null);

    private static CreateWorkflowStepRequest Compile(int order, string refJson) =>
        Step(order, StepType.VideoCompile, $"{{\"version\":1,\"decision\":{{\"source\":\"Previous\"}},\"insertContentRef\":{refJson}}}");

    /// <summary>A compile whose media overlay names its content by reference.</summary>
    private static CreateWorkflowStepRequest MediaOverlayCompile(int order, string contentJson) =>
        Step(order, StepType.VideoCompile,
            $"{{\"version\":1,\"decision\":{{\"source\":\"Previous\"}},\"mediaOverlays\":[{{\"content\":{contentJson}}}]}}");

    [Fact]
    public void A_media_overlay_step_reference_to_an_earlier_render_is_accepted()
    {
        Structural(Step(1, StepType.VideoCompile, "{\"version\":1}"),
            MediaOverlayCompile(2, "{\"from\":\"Step\",\"stepOrder\":1}"))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void A_media_overlay_step_reference_must_name_an_earlier_step(int target)
    {
        IReadOnlyList<string> errors = Structural(
            Step(1, StepType.VideoCompile, "{\"version\":1}"),
            MediaOverlayCompile(2, $"{{\"from\":\"Step\",\"stepOrder\":{target}}}"),
            Step(3, StepType.VideoCompile, "{\"version\":1}"));

        errors.Should().ContainSingle().Which.Should().Contain("Step 2 (VideoCompile)").And.Contain("earlier step");
    }

    [Fact]
    public void A_media_overlay_step_reference_must_name_a_step_with_a_playable_output()
    {
        IReadOnlyList<string> errors = Structural(
            Step(1, StepType.VideoAnalyze),
            MediaOverlayCompile(2, "{\"from\":\"Step\",\"stepOrder\":1}"));

        errors.Should().ContainSingle().Which.Should().Contain("Step 2 (VideoCompile)").And.Contain("video or image");
    }

    [Fact]
    public void A_media_overlay_project_file_reference_is_structurally_accepted()
    {
        Structural(MediaOverlayCompile(1, "{\"from\":\"ProjectFile\",\"projectFileId\":\"00000000-0000-0000-0000-000000000001\"}"))
            .Should().BeEmpty();
    }

    [Fact]
    public void A_malformed_media_overlay_content_is_reported()
    {
        Structural(MediaOverlayCompile(1, "{}"))
            .Should().ContainSingle().Which.Should().Contain("Step 1 (VideoCompile)");
    }

    private static IReadOnlyList<string> Structural(params CreateWorkflowStepRequest[] steps) =>
        InsertContentRefSaveValidator.Validate(steps);

    [Fact]
    public void A_Step_reference_to_an_earlier_step_that_renders_is_accepted()
    {
        Structural(Step(1, StepType.VideoCompile, "{\"version\":1}"), Compile(2, "{\"from\":\"Step\",\"stepOrder\":1}"))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(2)]  // itself
    [InlineData(3)]  // a later step
    [InlineData(7)]  // no such step
    public void A_Step_reference_must_name_an_earlier_step(int target)
    {
        IReadOnlyList<string> errors = Structural(
            Step(1, StepType.VideoCompile, "{\"version\":1}"),
            Compile(2, $"{{\"from\":\"Step\",\"stepOrder\":{target}}}"),
            Step(3, StepType.VideoCompile, "{\"version\":1}"));

        errors.Should().ContainSingle().Which.Should().Contain("Step 2 (VideoCompile)").And.Contain("earlier step");
    }

    [Fact]
    public void A_Step_reference_must_name_a_step_with_a_playable_output()
    {
        IReadOnlyList<string> errors = Structural(Step(1, StepType.VideoAnalyze), Compile(2, "{\"from\":\"Step\",\"stepOrder\":1}"));

        errors.Should().ContainSingle().Which.Should().Contain("Step 2 (VideoCompile)").And.Contain("video or image")
            .And.Contain("step 1 is VideoAnalyze, which produces a plan, decision or JSON document",
                "the refusal says which kind of step was named and why it has nothing to insert");
    }

    [Fact]
    public void An_image_producing_step_is_playable_output()
    {
        Structural(Step(1, StepType.ExtractFrame), Compile(2, "{\"from\":\"Step\",\"stepOrder\":1}")).Should().BeEmpty();
    }

    /// <summary>
    /// QA-P1-3: an Agent step that rendered is the brief's "pin the Author render" case, and a VideoGenerate
    /// step uploads a generated clip; the engine resolves a from=Step reference to whatever object the step's
    /// latest result holds, so both are insertable at run time and the save-time rule now says so.
    /// </summary>
    [Theory]
    [InlineData(StepType.Agent)]
    [InlineData(StepType.VideoGenerate)]
    [InlineData(StepType.TimelineRender)]
    [InlineData(StepType.Revise)]
    [InlineData(StepType.ThumbnailGenerate)]
    public void A_step_that_can_upload_a_render_or_a_still_is_playable_output(StepType target)
    {
        Structural(Step(1, target), Compile(2, "{\"from\":\"Step\",\"stepOrder\":1}")).Should().BeEmpty();
    }

    /// <summary>
    /// QA-P1-3: the other half of the rule — a type that can never produce a video or an image is still
    /// refused, and the sentence says what it produces instead, so the author knows what they named.
    /// </summary>
    [Theory]
    [InlineData(StepType.Voiceover, "produces audio")]
    [InlineData(StepType.AudioGenerate, "produces audio")]
    [InlineData(StepType.ReviewLoop, "produces review scores")]
    [InlineData(StepType.EditRoom, "produces an editorial decision document")]
    [InlineData(StepType.Parallel, "produces a plan, decision or JSON document")]
    [InlineData(StepType.DesignSystem, "produces a plan, decision or JSON document")]
    public void A_step_that_can_never_produce_one_is_refused_with_the_reason(StepType target, string because)
    {
        IReadOnlyList<string> errors = Structural(Step(1, target), Compile(2, "{\"from\":\"Step\",\"stepOrder\":1}"));

        errors.Should().ContainSingle().Which.Should().Contain("Step 2 (VideoCompile)")
            .And.Contain("video or image").And.Contain($"step 1 is {target}, which {because}");
    }

    [Theory]
    [InlineData("{\"from\":\"Bogus\"}")]
    [InlineData("{\"from\":\"StepResult\"}")]
    [InlineData("{\"from\":\"Step\",\"stepOrder\":1,\"projectFileId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"}")]
    public void A_malformed_reference_is_refused_at_save(string refJson)
    {
        Structural(Step(1, StepType.VideoCompile, "{\"version\":1}"), Compile(2, refJson))
            .Should().ContainSingle().Which.Should().Contain("Step 2 (VideoCompile)").And.Contain("insertContentRef");
    }

    [Fact]
    public void A_per_entry_reference_is_checked_like_the_config_level_one()
    {
        CreateWorkflowStepRequest compile = Step(2, StepType.VideoCompile,
            "{\"version\":1,\"insertContents\":[{\"regionId\":\"r0\",\"insertContentRef\":{\"from\":\"Step\",\"stepOrder\":9}}]}");

        Structural(compile).Should().ContainSingle().Which.Should().Contain("earlier step");
    }

    [Fact]
    public void A_config_without_references_adds_no_errors()
    {
        Structural(Step(1, StepType.VideoCompile, "{\"version\":1,\"insertContentProjectFileId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"}"))
            .Should().BeEmpty();
    }

    [Fact]
    public void The_shared_save_validator_runs_the_reference_rules()
    {
        StepConfigSaveValidator.Validate([Step(1, StepType.VideoCompile, "{\"version\":1}"), Compile(2, "{\"from\":\"Step\",\"stepOrder\":2}")])
            .Should().ContainSingle().Which.Should().Contain("earlier step");
    }

    [Fact]
    public async Task A_StepResult_reference_is_looked_up_in_the_project_and_refused_with_the_shared_wording()
    {
        List<Guid> asked = [];
        IReadOnlyList<string> errors = await StepConfigSaveValidator.ValidateStepResultSourcesAsync(
            [Compile(1, $"{{\"from\":\"StepResult\",\"stepResultId\":\"{ResultA}\"}}")],
            (id, _) => { asked.Add(id); return Task.FromResult(false); },
            CancellationToken.None);

        asked.Should().Equal(ResultA);
        errors.Should().ContainSingle().Which.Should().Contain("Step 1 (VideoCompile)").And.Contain("not found in this project");
    }

    [Fact]
    public async Task A_StepResult_reference_the_project_owns_is_accepted()
    {
        (await StepConfigSaveValidator.ValidateStepResultSourcesAsync(
            [Compile(1, $"{{\"from\":\"StepResult\",\"stepResultId\":\"{ResultA}\"}}")],
            (_, _) => Task.FromResult(true), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Other_reference_kinds_never_trigger_a_lookup()
    {
        int lookups = 0;
        await StepConfigSaveValidator.ValidateStepResultSourcesAsync(
            [Compile(1, "{\"from\":\"Step\",\"stepOrder\":1}"), Compile(2, $"{{\"from\":\"ProjectFile\",\"projectFileId\":\"{ResultA}\"}}")],
            (_, _) => { lookups++; return Task.FromResult(false); }, CancellationToken.None);

        lookups.Should().Be(0);
    }

    // ================================================================ S-62: member names in any case (the engine binds any case)

    [Theory]
    [InlineData("{{\"version\":1,\"InsertContentRef\":{0}}}")]
    [InlineData("{{\"version\":1,\"INSERTCONTENTREF\":{0}}}")]
    [InlineData("{{\"version\":1,\"insertcontentref\":{0}}}")]
    [InlineData("{{\"version\":1,\"InsertContents\":[{{\"regionId\":\"r0\",\"insertContentRef\":{0}}}]}}")]
    [InlineData("{{\"version\":1,\"insertContents\":[{{\"regionId\":\"r0\",\"InsertContentRef\":{0}}}]}}")]
    [InlineData("{{\"version\":1,\"INSERTCONTENTS\":[{{\"regionId\":\"r0\",\"INSERTCONTENTREF\":{0}}}]}}")]
    [InlineData("{{\"version\":1,\"insertContentRef\":{{\"from\":\"ProjectFile\",\"projectFileId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaab\"}},\"InsertContentRef\":{0}}}")]
    public async Task S62_a_foreign_StepResult_reference_in_any_member_case_is_looked_up_and_refused(string template)
    {
        string config = string.Format(System.Globalization.CultureInfo.InvariantCulture, template,
            $"{{\"from\":\"StepResult\",\"stepResultId\":\"{ResultA}\"}}");
        List<Guid> asked = [];

        IReadOnlyList<string> errors = await StepConfigSaveValidator.ValidateStepResultSourcesAsync(
            [Step(1, StepType.VideoCompile, config)],
            (id, _) => { asked.Add(id); return Task.FromResult(false); },
            CancellationToken.None);

        asked.Should().Contain(ResultA);
        errors.Should().ContainSingle().Which.Should().Contain("not found in this project");
    }

    [Theory]
    [InlineData("{\"version\":1,\"InsertContentRef\":{\"from\":\"Step\",\"stepOrder\":9}}")]
    [InlineData("{\"version\":1,\"InsertContents\":[{\"regionId\":\"r0\",\"INSERTCONTENTREF\":{\"from\":\"Step\",\"stepOrder\":9}}]}")]
    public void S62_a_Step_reference_in_any_member_case_gets_the_order_rule(string config)
    {
        Structural(Step(1, StepType.VideoCompile, "{\"version\":1}"), Step(2, StepType.VideoCompile, config))
            .Should().ContainSingle().Which.Should().Contain("earlier step");
    }

    // ================================================================ S-66: step type spelled any way the save path stores

    private static CreateWorkflowStepRequest Spelled(CreateWorkflowStepRequest step, string stepType) => step with { StepType = stepType };

    public static TheoryData<string> CompileSpellings => new()
    {
        " VideoCompile ", "videocompile", "VIDEOCOMPILE", ((int)StepType.VideoCompile).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    [Theory]
    [MemberData(nameof(CompileSpellings))]
    public void S66_the_shape_and_order_rules_run_for_every_VideoCompile_spelling(string spelling)
    {
        Structural(Step(1, StepType.VideoCompile, "{\"version\":1}"), Spelled(Compile(2, "{\"from\":\"Step\",\"stepOrder\":9}"), spelling))
            .Should().ContainSingle().Which.Should().Contain("earlier step");
        Structural(Spelled(Compile(2, "{\"from\":\"Bogus\"}"), spelling))
            .Should().ContainSingle().Which.Should().Contain("insertContentRef");
    }

    [Theory]
    [MemberData(nameof(CompileSpellings))]
    public void S66_a_target_step_type_spelled_any_way_is_judged_by_its_parsed_type(string spelling)
    {
        Structural(Spelled(Step(1, StepType.VideoCompile, "{\"version\":1}"), spelling), Compile(2, "{\"from\":\"Step\",\"stepOrder\":1}"))
            .Should().BeEmpty();
    }
}
