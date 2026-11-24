using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Save-time validator coverage for <see cref="StepType.Voiceover"/>, the arm T-067 added to
/// <see cref="StepConfigSaveValidator.ConfigJsonFor"/>. A Voiceover step with no config is not
/// "unconfigured but savable": <c>VoiceoverStepExecutor</c> hard-fails it with
/// <c>CONFIG_INVALID</c>, so saving one only defers the failure to a real execution run.
///
/// <para>
/// Every step here is built by deserializing raw JSON, never with a
/// <c>new CreateWorkflowStepRequest(… VoiceoverConfigJson: …)</c> initializer, because the file has
/// to COMPILE against the pre-T-067 revision so that reverting the six T-067 source paths makes it
/// fail at run time. With an initializer it would not build at all before the fix and the red
/// proof would be meaningless. Deserializing drops <c>voiceoverConfigJson</c> as an unknown member
/// on the pre-fix DTO — the same way the real HTTP binder dropped it — so the pre-fix validator
/// sees exactly the null config it saw in production.
/// </para>
/// </summary>
public sealed class StepConfigSaveValidatorVoiceoverTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static CreateWorkflowStepRequest Step(JsonObject stepJson) =>
        JsonSerializer.Deserialize<CreateWorkflowStepRequest>(stepJson.ToJsonString(), WebJson)!;

    private static JsonObject StepJson(string stepType, int stepOrder, string? voiceoverConfigJson = null)
    {
        var step = new JsonObject
        {
            ["agentDefinitionId"] = Guid.NewGuid().ToString(),
            ["stepOrder"] = stepOrder,
            ["label"] = "Narrate",
            ["stepType"] = stepType
        };

        // A null value leaves the member out of the body entirely, which is the shape a client
        // sends when it has no config to give.
        if (voiceoverConfigJson is not null)
        {
            step["voiceoverConfigJson"] = voiceoverConfigJson;
        }

        return step;
    }

    [Fact]
    public void A_Voiceover_step_whose_body_omits_voiceoverConfigJson_is_rejected()
    {
        CreateWorkflowStepRequest step = Step(StepJson(nameof(StepType.Voiceover), 2));

        IReadOnlyList<string> errors = StepConfigSaveValidator.Validate([step]);

        errors.Should().ContainSingle();
        errors[0].Should().Be("Step 2 (Voiceover): VoiceoverConfigJson is required.");
        errors[0].Should().Contain("VoiceoverConfigJson");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Voiceover_step_with_a_blank_voiceoverConfigJson_is_rejected(string blank)
    {
        CreateWorkflowStepRequest step = Step(StepJson(nameof(StepType.Voiceover), 3, blank));

        IReadOnlyList<string> errors = StepConfigSaveValidator.Validate([step]);

        errors.Should().ContainSingle();
        errors[0].Should().Be("Step 3 (Voiceover): VoiceoverConfigJson is required.");
        errors[0].Should().Contain("VoiceoverConfigJson");
    }

    [Fact]
    public void A_Voiceover_step_with_a_voiceoverConfigJson_that_is_not_a_JSON_object_is_rejected()
    {
        CreateWorkflowStepRequest step = Step(StepJson(nameof(StepType.Voiceover), 4, "\"just a string\""));

        IReadOnlyList<string> errors = StepConfigSaveValidator.Validate([step]);

        errors.Should().ContainSingle();
        errors[0].Should().Be("Step 4 (Voiceover): VoiceoverConfigJson must be a JSON object.");
    }

    [Fact]
    public void A_Voiceover_step_with_a_JSON_object_config_is_accepted()
    {
        CreateWorkflowStepRequest step = Step(StepJson(nameof(StepType.Voiceover), 1,
            """{"source":{"kind":"Inline","lines":[{"text":"Hello there.","startSec":0}]}}"""));

        StepConfigSaveValidator.Validate([step]).Should().BeEmpty();
    }

    [Fact]
    public void A_step_type_carrying_no_config_blob_is_still_accepted_with_a_null_config()
    {
        // Control: adding the Voiceover arm must not turn into "every step needs a config". Agent
        // is a StepType the switch maps to no property at all, so a null config stays savable —
        // and a stray VoiceoverConfigJson on a non-Voiceover step is not its business either.
        CreateWorkflowStepRequest agentStep = Step(StepJson(nameof(StepType.Agent), 1));
        CreateWorkflowStepRequest agentStepWithStrayConfig =
            Step(StepJson(nameof(StepType.Agent), 2, "{bogus"));

        StepConfigSaveValidator.Validate([agentStep, agentStepWithStrayConfig]).Should().BeEmpty();
    }

    [Fact]
    public void A_Voiceover_step_is_checked_alongside_every_other_config_bearing_step()
    {
        var steps = new List<CreateWorkflowStepRequest>
        {
            Step(StepJson(nameof(StepType.Voiceover), 1)),
            Step(StepJson(nameof(StepType.Agent), 2)),
            Step(StepJson(nameof(StepType.Voiceover), 3,
                """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":2}}""")),
            Step(StepJson(nameof(StepType.Extract), 4)),
        };

        IReadOnlyList<string> errors = StepConfigSaveValidator.Validate(steps);

        errors.Should().HaveCount(2);
        errors[0].Should().Be("Step 1 (Voiceover): VoiceoverConfigJson is required.");
        errors[1].Should().Be("Step 4 (Extract): ExtractConfigJson is required.");
    }

    // =====================================================================
    // T-204 — the domain-aware NarrationPlan arm, and the ADDITIVE rule that
    // nothing else about a Voiceover config changed
    // =====================================================================

    [Fact]
    public void A_NarrationPlan_voiceover_config_naming_a_narration_writer_step_is_accepted()
    {
        CreateWorkflowStepRequest step = Step(StepJson(nameof(StepType.Voiceover), 4,
            """{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":3}}"""));

        StepConfigSaveValidator.Validate([step]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"source":{"kind":"NarrationPlan"}}""")]
    [InlineData("""{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":null}}""")]
    [InlineData("""{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":0}}""")]
    [InlineData("""{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":-1}}""")]
    public void A_NarrationPlan_voiceover_config_without_a_usable_step_order_is_rejected(string configJson)
    {
        CreateWorkflowStepRequest step = Step(StepJson(nameof(StepType.Voiceover), 4, configJson));

        IReadOnlyList<string> errors = StepConfigSaveValidator.Validate([step]);

        // The executor hard-fails exactly this config with NARRATION_SOURCE_INVALID, so saving it
        // only defers the failure to a real execution run — the same argument the missing-config
        // check above makes.
        errors.Should().ContainSingle();
        errors[0].Should().Be(
            "Step 4 (Voiceover): VoiceoverConfigJson source narrationWriterStepOrder is required "
            + "and must be at least 1 when the source kind is NarrationPlan.");
    }

    [Fact]
    public void The_NarrationPlan_arm_is_additive_and_leaves_the_other_source_kinds_alone()
    {
        // The rule the ticket states explicitly: this arm must not become a general tightening of
        // Voiceover validation. An Inline config carries no step order at all, and a ScriptScenes
        // config is checked for nothing beyond the three generic JSON checks — including one whose
        // ScriptwriterStepOrder is absent, which the EXECUTOR rejects (SCRIPT_SOURCE_INVALID) and
        // which must stay savable precisely because it used to be.
        var steps = new List<CreateWorkflowStepRequest>
        {
            Step(StepJson(nameof(StepType.Voiceover), 1,
                """{"source":{"kind":"Inline","lines":[{"text":"Hello there.","startSec":0}]}}""")),
            Step(StepJson(nameof(StepType.Voiceover), 2,
                """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":1}}""")),
            Step(StepJson(nameof(StepType.Voiceover), 3,
                """{"source":{"kind":"ScriptScenes"}}""")),
            // An empty object and a null source are both shapes the EXECUTOR owns (CONFIG_INVALID /
            // SOURCE_INVALID); neither is a new save-time rejection.
            Step(StepJson(nameof(StepType.Voiceover), 4, "{}")),
            Step(StepJson(nameof(StepType.Voiceover), 5, """{"source":null}""")),
        };

        StepConfigSaveValidator.Validate(steps).Should().BeEmpty();
    }

    [Fact]
    public void A_voiceover_config_whose_kind_is_not_a_known_member_is_left_to_the_executor()
    {
        // Not a NarrationPlan, so this arm reports nothing: an unparseable enum member is
        // SOURCE_INVALID's job at run time, not a new save-time rejection.
        CreateWorkflowStepRequest step = Step(StepJson(nameof(StepType.Voiceover), 6,
            """{"source":{"kind":"SomethingElse","narrationWriterStepOrder":3}}"""));

        StepConfigSaveValidator.Validate([step]).Should().BeEmpty();
    }

    [Fact]
    public void The_NarrationPlan_rule_still_reports_through_the_shared_step_error_prefix()
    {
        // The prefix is what every save path renders into one 400 body, and what the assistant's
        // propose/apply diff shows a user — a raw message here would lose the step it belongs to.
        var steps = new List<CreateWorkflowStepRequest>
        {
            Step(StepJson(nameof(StepType.Voiceover), 7, """{"source":{"kind":"NarrationPlan"}}""")),
        };

        StepConfigSaveValidator.FormatMessage(StepConfigSaveValidator.Validate(steps))
            .Should().StartWith("Step 7 (Voiceover): ");
    }
}
