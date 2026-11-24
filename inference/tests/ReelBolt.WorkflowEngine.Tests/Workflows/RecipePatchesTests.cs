using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// The closed patch catalog (docs/design/workflow-apps.md 6.1, decision Q4): each patch is a deterministic graph
/// transform. The footage-family goldens pin a patched recipe against the built-in recipe that already has the
/// feature, so a patch and its template cannot drift apart; every other path is a refusal, never an improvised graph.
/// </summary>
public class RecipePatchesTests
{
    private static readonly IReadOnlyDictionary<AgentType, Guid> Agents =
        Enum.GetValues<AgentType>().ToDictionary(t => t, _ => Guid.NewGuid());

    private static List<CreateWorkflowStepRequest> Steps(string key) =>
        [.. WorkflowTemplateCatalog.BaseByKey(key)!.Steps.Select((s, i) => AssistantStepShapes.FromTemplateStep(s, i + 1, Agents))];

    private static IReadOnlyList<CreateWorkflowStepRequest> Patch(IReadOnlyList<CreateWorkflowStepRequest> steps, params string[] patches)
    {
        IReadOnlyList<string> ids = RecipePatches.Normalize(patches, out _);
        RecipePatchOutcome outcome = RecipePatches.ApplyAll(steps, ids, Agents);
        outcome.Refusal.Should().BeNull();
        return outcome.Steps!;
    }

    private static IReadOnlyList<CreateWorkflowStepRequest> Patch(string key, params string[] patches) =>
        Patch(Steps(key), patches);

    [Fact]
    public void AddMediaOverlay_resolves_the_planner_through_the_restricted_patch_agent_set()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.BaseByKey("video-derush-edit-music")!;
        // The real create path loads the template's own agents plus the patch catalog's, and nothing else.
        HashSet<AgentType> needed = [.. RecipeInstance.AgentTypesOf(template), .. RecipeInstance.PatchAgentTypes];
        IReadOnlyDictionary<AgentType, Guid> restricted = needed.ToDictionary(t => t, _ => Guid.NewGuid());
        IReadOnlyList<CreateWorkflowStepRequest> steps =
        [
            .. template.Steps.Select((s, i) => AssistantStepShapes.FromTemplateStep(s, i + 1, restricted))
        ];
        IReadOnlyList<string> ids = RecipePatches.Normalize([RecipePatches.AddMediaOverlay], out _);

        RecipePatchOutcome outcome = RecipePatches.ApplyAll(steps, ids, restricted);

        outcome.Refusal.Should().BeNull();
        outcome.Steps!.Should().Contain(s =>
            s.StepType == nameof(StepType.Agent) && s.AgentDefinitionId == restricted[AgentType.OverlayPlanner]);
    }

    /// <summary>
    /// The base edit with the overlay planner and its wiring removed: the pre-overlay shape the feature
    /// recipes (music, sfx, graphics, grade, room, narration) were built from. The overlay planner is
    /// orthogonal to those features, so patching the stripped base is what the goldens pin.
    /// </summary>
    private static IReadOnlyList<CreateWorkflowStepRequest> WithoutOverlay(string key)
    {
        List<CreateWorkflowStepRequest> steps = Steps(key);
        int planner = steps.FindIndex(s => s.StepType == nameof(StepType.Agent) && s.Label == "Plan picture overlays");
        if (planner < 0)
            return steps;

        int at = steps[planner].StepOrder;
        steps.RemoveAt(planner);
        for (int i = 0; i < steps.Count; i++)
        {
            CreateWorkflowStepRequest step = steps[i];
            steps[i] = step with
            {
                StepOrder = step.StepOrder > at ? step.StepOrder - 1 : step.StepOrder,
                LoopTargetStepOrder = step.LoopTargetStepOrder is { } loop && loop > at ? loop - 1 : step.LoopTargetStepOrder,
                VideoAnalyzeConfigJson = RemoveMember(step.VideoAnalyzeConfigJson, "emitOverlayOffers"),
                VideoCompileConfigJson = ShiftMotionPlan(RemoveMember(step.VideoCompileConfigJson, "mediaOverlayPlan"), at),
            };
        }

        return steps;
    }

    private static string? ShiftMotionPlan(string? json, int removedAt)
    {
        if (json is null)
            return null;
        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        if (obj["motionPlan"] is JsonObject plan && plan["stepOrder"] is JsonValue order && order.TryGetValue(out int value) && value > removedAt)
        {
            plan["stepOrder"] = value - 1;
            return obj.ToJsonString();
        }

        return json;
    }

    private static string? RemoveMember(string? json, string member)
    {
        if (json is null)
            return null;
        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        return obj.Remove(member) ? obj.ToJsonString() : json;
    }

    private static string? Refusal(string key, string patch) =>
        RecipePatches.ApplyAll(Steps(key), RecipePatches.Normalize([patch], out _), Agents).Refusal;

    private static void Json(string? actual, string? expected, string where)
    {
        if (expected is null)
        {
            actual.Should().BeNull(where);
            return;
        }

        actual.Should().NotBeNull(where);
        JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(expected)).Should().BeTrue($"{where}: {actual} vs {expected}");
    }

    /// <summary>The patched steps equal the recipe's own steps: agent, type, wiring and every config (the compile step keeps its own label).</summary>
    private static void ShouldEqualRecipe(IReadOnlyList<CreateWorkflowStepRequest> patched, string recipeKey)
    {
        List<CreateWorkflowStepRequest> recipe = Steps(recipeKey);
        patched.Should().HaveCount(recipe.Count);
        for (int i = 0; i < recipe.Count; i++)
        {
            CreateWorkflowStepRequest got = patched[i];
            CreateWorkflowStepRequest want = recipe[i];
            string at = $"step {i + 1} ({want.Label})";
            got.StepOrder.Should().Be(want.StepOrder, at);
            got.AgentDefinitionId.Should().Be(want.AgentDefinitionId, at);
            got.StepType.Should().Be(want.StepType, at);
            got.LoopTargetStepOrder.Should().Be(want.LoopTargetStepOrder, at);
            got.AgentInputContextMode.Should().Be(want.AgentInputContextMode, at);
            got.CacheMode.Should().Be(want.CacheMode, at);
            if (want.StepType != nameof(StepType.VideoCompile))
                got.Label.Should().Be(want.Label, at);
            Json(got.VideoAnalyzeConfigJson, want.VideoAnalyzeConfigJson, at + " analyze");
            Json(got.VideoCompileConfigJson, want.VideoCompileConfigJson, at + " compile");
            Json(got.VoiceoverConfigJson, want.VoiceoverConfigJson, at + " voiceover");
            Json(got.EditRoomConfigJson, want.EditRoomConfigJson, at + " room");
            Json(got.ColorGradeRoomConfigJson, want.ColorGradeRoomConfigJson, at + " grade");
        }
    }

    [Fact]
    public void No_recipe_with_any_patch_puts_a_review_before_the_voice_or_final_cut()
    {
        static bool IsEncodeOnceFinal(CreateWorkflowStepRequest s) =>
            s.StepType == nameof(StepType.VideoCompile)
            && s.VideoCompileConfigJson?.Contains("\"encodeStage\":\"Final\"", StringComparison.Ordinal) == true;

        string[] late = [nameof(StepType.Voiceover), nameof(StepType.VideoCompile), nameof(StepType.AudioGenerate)];
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.BaseTemplates)
        {
            foreach (string[] patches in RecipePatches.Ids.Select(id => new[] { id }).Append([.. RecipePatches.Ids]))
            {
                RecipePatchOutcome outcome = RecipePatches.ApplyAll(Steps(template.Key), RecipePatches.Normalize(patches, out _), Agents);
                IReadOnlyList<CreateWorkflowStepRequest> result = outcome.Refusal is null ? outcome.Steps! : Steps(template.Key);
                foreach (CreateWorkflowStepRequest review in result.Where(s => s.StepType == nameof(StepType.ReviewLoop)))
                {
                    // An encode-once Final-stage compile is deliberately the one cut after the review (it encodes
                    // the cut review ended on); every OTHER voice/cut step stays before the review.
                    result.Where(s => s.StepOrder > review.StepOrder && late.Contains(s.StepType) && !IsEncodeOnceFinal(s))
                        .Should().BeEmpty(
                            $"{template.Key} + [{string.Join(",", patches)}]: the review at step {review.StepOrder} must come after the voice and final cut");
                    review.LoopTargetStepOrder.Should().BeLessThan(review.StepOrder);
                }
            }
        }
    }

    [Fact]
    public void Catalog_is_exactly_the_eight_closed_ids_in_canonical_order()
    {
        RecipePatches.Ids.Should().Equal("swapToRoom", "addVoiceover", "addCaptions", "addMusic", "addSfx", "addColorGrade", "addGraphics", "addMediaOverlay");
    }

    [Fact]
    public void Normalize_dedupes_orders_canonically_and_reports_unknown_ids()
    {
        IReadOnlyList<string> ids = RecipePatches.Normalize([" ADDMUSIC", "addVoiceover", "addmusic", "makeItPop"], out IReadOnlyList<string> unknown);
        ids.Should().Equal("addVoiceover", "addMusic");
        unknown.Should().Equal("makeItPop");
    }

    [Fact]
    public void AddMusic_on_the_highlight_edit_is_the_music_recipe() =>
        ShouldEqualRecipe(Patch(WithoutOverlay("video-derush-edit"), "addMusic"), "video-derush-edit-music");

    [Fact]
    public void AddSfx_on_the_highlight_edit_is_the_sound_effects_recipe() =>
        ShouldEqualRecipe(Patch(WithoutOverlay("video-derush-edit"), "addSfx"), "video-derush-edit-sfx");

    [Fact]
    public void AddGraphics_on_the_highlight_edit_is_the_graphics_recipe() =>
        ShouldEqualRecipe(Patch(WithoutOverlay("video-derush-edit"), "addGraphics"), "video-derush-edit-graphics");

    [Fact]
    public void AddColorGrade_on_the_highlight_edit_is_the_colour_grade_recipe() =>
        ShouldEqualRecipe(Patch(WithoutOverlay("video-derush-edit"), "addColorGrade"), "video-derush-edit-grade-room");

    [Fact]
    public void SwapToRoom_on_the_highlight_edit_is_the_editing_team_recipe() =>
        ShouldEqualRecipe(Patch(WithoutOverlay("video-derush-edit"), "swapToRoom"), "video-derush-edit-room");

    [Fact]
    public void AddVoiceover_and_addCaptions_on_the_highlight_edit_are_the_narration_recipe() =>
        ShouldEqualRecipe(Patch(WithoutOverlay("video-derush-edit"), "addCaptions", "addVoiceover"), "video-derush-edit-voiceover");

    [Fact]
    public void AddMediaOverlay_inserts_the_planner_sets_the_analyze_flag_and_points_both_compiles_at_it()
    {
        IReadOnlyList<CreateWorkflowStepRequest> steps = Patch("video-derush-edit-music", "addMediaOverlay");

        steps.Select(s => s.StepOrder).Should().Equal(Enumerable.Range(1, steps.Count));
        CreateWorkflowStepRequest planner = steps.Single(s => s.StepType == nameof(StepType.Agent) && s.Label == "Plan picture overlays");
        planner.AgentInputContextMode.Should().Be("FullWorkflow");
        planner.CacheMode.Should().Be("Never");

        CreateWorkflowStepRequest analyze = steps.Single(s => s.StepType == nameof(StepType.VideoAnalyze));
        JsonNode.Parse(analyze.VideoAnalyzeConfigJson!)!["emitOverlayOffers"]!.GetValue<bool>().Should().BeTrue();

        CreateWorkflowStepRequest compile = steps.Single(s => s.StepType == nameof(StepType.VideoCompile));
        JsonNode config = JsonNode.Parse(compile.VideoCompileConfigJson!)!;
        config["mediaOverlayPlan"]!["from"]!.GetValue<string>().Should().Be("Step");
        config["mediaOverlayPlan"]!["stepOrder"]!.GetValue<int>().Should().Be(planner.StepOrder);
        // The decision still reads the story editor (step 2), not the inserted planner.
        config["decision"]!["stepOrder"]!.GetValue<int>().Should().Be(2);
        steps.Single(s => s.StepType == nameof(StepType.ReviewLoop)).LoopTargetStepOrder.Should().Be(2);
    }

    [Fact]
    public void AddMediaOverlay_is_refused_on_a_recipe_that_already_has_the_planner()
    {
        Refusal("video-derush-edit", "addMediaOverlay").Should().Contain("already plans");
    }

    [Fact]
    public void Patches_are_deterministic_and_input_order_does_not_matter()
    {
        string Shape(IReadOnlyList<CreateWorkflowStepRequest> steps) =>
            string.Join("|", steps.Select(s => $"{s.StepOrder}:{s.StepType}:{s.Label}:{s.LoopTargetStepOrder}:{s.VideoCompileConfigJson}"));
        Shape(Patch("video-derush-edit", "addMusic", "addGraphics", "addVoiceover"))
            .Should().Be(Shape(Patch("video-derush-edit", "addVoiceover", "addGraphics", "addMusic")));
    }

    [Fact]
    public void Several_patches_renumber_every_reference_and_keep_the_review_loop_on_the_editor()
    {
        IReadOnlyList<CreateWorkflowStepRequest> steps = Patch("video-derush-edit", "addVoiceover", "addMusic", "addGraphics");

        steps.Select(s => s.StepOrder).Should().Equal(Enumerable.Range(1, steps.Count));
        CreateWorkflowStepRequest compile = steps.Single(s => s.StepType == nameof(StepType.VideoCompile));
        JsonNode config = JsonNode.Parse(compile.VideoCompileConfigJson!)!;
        int Order(string member) => config[member]!["stepOrder"]!.GetValue<int>();
        int OrderOf(string label) => steps.Single(s => s.Label == label).StepOrder;

        config["voiceoverStepOrder"]!.GetValue<int>().Should().Be(OrderOf("Synthesize the narration"));
        Order("musicPlan").Should().Be(OrderOf("Pick background music"));
        Order("graphicsPlan").Should().Be(OrderOf("Plan motion graphics overlays"));
        Order("decision").Should().Be(2);
        steps.Single(s => s.StepType == nameof(StepType.ReviewLoop)).LoopTargetStepOrder.Should().Be(2);
        steps.Last().StepType.Should().Be(nameof(StepType.ReviewLoop));
    }

    [Fact]
    public void AddVoiceover_on_the_promo_recipe_adds_a_script_voiceover_before_the_director_and_repoints_the_review_loop()
    {
        IReadOnlyList<CreateWorkflowStepRequest> steps = Patch("quick-win-promo-ds", "addVoiceover");

        CreateWorkflowStepRequest voice = steps.Single(s => s.StepType == nameof(StepType.Voiceover));
        voice.StepOrder.Should().Be(5);
        steps[5].Label.Should().Be("Build shot direction");
        Json(voice.VoiceoverConfigJson, """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":4}}""", "voiceover");
        steps.Single(s => s.StepType == nameof(StepType.ReviewLoop)).LoopTargetStepOrder.Should().Be(7);
        steps.Select(s => s.StepOrder).Should().Equal(Enumerable.Range(1, steps.Count));
    }

    [Theory]
    [InlineData("quick-win-promo-ds", "addMusic")]
    [InlineData("quick-win-promo-ds", "addCaptions")]
    [InlineData("quick-win-promo-ds", "swapToRoom")]
    [InlineData("green-screen-insert", "addVoiceover")]
    [InlineData("video-region-effect", "addColorGrade")]
    [InlineData("product-profile-from-code", "addGraphics")]
    public void A_patch_that_does_not_fit_the_recipe_is_refused_not_improvised(string key, string patch)
    {
        Refusal(key, patch).Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("video-derush-edit-music", "addMusic", "already has")]
    [InlineData("video-derush-edit-sfx", "addSfx", "already has")]
    [InlineData("video-derush-edit-graphics", "addGraphics", "already has")]
    [InlineData("video-derush-edit-grade-room", "addColorGrade", "already has")]
    [InlineData("video-derush-edit-room", "swapToRoom", "already has")]
    [InlineData("video-derush-edit-voiceover", "addVoiceover", "already has")]
    [InlineData("video-derush-edit-voiceover", "addCaptions", "already has")]
    [InlineData("video-derush-edit", "addCaptions", "add the voiceover first")]
    public void A_patch_that_is_already_on_or_missing_its_prerequisite_is_refused_with_a_plain_reason(string key, string patch, string phrase)
    {
        Refusal(key, patch).Should().Contain(phrase);
    }

    [Fact]
    public void A_stream_copy_compile_refuses_every_compile_patch()
    {
        foreach (string patch in new[] { "addVoiceover", "addMusic", "addSfx", "addColorGrade", "addGraphics", "addMediaOverlay" })
        {
            List<CreateWorkflowStepRequest> steps = [.. WithoutOverlay("video-derush-edit")];
            int compile = steps.FindIndex(s => s.StepType == nameof(StepType.VideoCompile));
            steps[compile] = steps[compile] with { VideoCompileConfigJson = steps[compile].VideoCompileConfigJson!.Replace("\"version\":1,", "\"version\":1,\"mode\":\"StreamCopy\",") };

            RecipePatches.Apply(patch, steps, Agents).Refusal.Should().Contain("without re-encoding", patch);
        }
    }

    [Fact]
    public void ShiftJson_moves_only_step_references_at_or_after_the_insertion_and_leaves_other_configs_alone()
    {
        RecipePatches.ShiftJson("""{"decision":{"from":"Step","stepOrder":4},"analysisStepOrder":1,"minSegmentMs":5}""", 3, 1)
            .Should().Be("""{"decision":{"from":"Step","stepOrder":5},"analysisStepOrder":1,"minSegmentMs":5}""");
        RecipePatches.ShiftJson("""{"a":2}""", 1, 1).Should().Be("""{"a":2}""");
        RecipePatches.ShiftJson("not json", 1, 1).Should().Be("not json");
    }
}
