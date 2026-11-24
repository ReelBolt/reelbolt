using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>The talking-avatar templates' lip-sync model input is a LipSync model input, and the manifest validator holds that line.</summary>
public class LipSyncTemplateInputsTests
{
    private static readonly Dictionary<AgentType, Guid> AgentIds = Enum.GetValues<AgentType>().ToDictionary(t => t, _ => Guid.NewGuid());

    public static IEnumerable<object[]> Keys() =>
        WorkflowTemplateCatalog.GetAll()
            .Where(t => t.AppManifest?.Inputs?.Any(i => i.Id == "liteModel") == true)
            .Select(t => new object[] { t.Key });

    private static WorkflowTemplateDefinition Template(string key) => WorkflowTemplateCatalog.GetAll().Single(t => t.Key == key);

    private static IReadOnlyList<AppBindingStep> Steps(WorkflowTemplateDefinition t) =>
        [.. AppBindingStep.FromTemplate(t).Select((s, i) => s with { AgentDefinitionId = AgentIds[t.Steps[i].AgentType] })];

    private static AppManifest ManifestOf(WorkflowTemplateDefinition t) =>
        WorkflowTemplateAppInputs.ManifestForCreate(t, t.Key, type => AgentIds[type])!;

    [Theory]
    [MemberData(nameof(Keys))]
    public void The_lip_sync_model_input_picks_from_LipSync_and_the_manifest_validates(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        AppManifest manifest = ManifestOf(t);
        manifest.Inputs!.Single(i => i.Id == "liteModel").Capability.Should().Be("LipSync");
        AppManifestValidator.Validate(manifest, AppStepFacts.FromTemplate(t), Steps(t)).Should().BeEmpty();
        // The step's model string stays as a hint: a talking head with no bound provider runs the capability's own model.
        t.Steps.Any(s => s.StepType == StepType.VideoGenerate && s.VideoGenerateConfigJson!.Contains("lip-sync/image-to-video", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_talking_head_step_refuses_a_VideoGeneration_model_input_at_save(string key)
    {
        WorkflowTemplateDefinition t = Template(key);
        AppManifest manifest = ManifestOf(t);
        AppManifest wrong = manifest with
        {
            Inputs = [.. manifest.Inputs!.Select(i => i.Id == "liteModel" ? i with { Capability = "VideoGeneration" } : i)]
        };

        IReadOnlyList<string> errors = AppManifestValidator.Validate(wrong, AppStepFacts.FromTemplate(t), Steps(t));
        errors.Should().Contain(e => e.Contains("needs a LipSync model", StringComparison.Ordinal));
    }
}
