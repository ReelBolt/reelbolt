using AwesomeAssertions;
using System.Linq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// With the avatar kill switch off the derived app view of a plain inline generate step gains no Avatar input. Alone in
/// its collection because the switch is process-wide.
/// </summary>
[Collection(AvatarSwitchCollection.Name)]
public class AvatarSwitchFactsTests
{
    private const string Inline = """{"planSource":"Inline","inlinePrompt":"x"}""";

    [Fact]
    public void With_avatars_off_no_step_takes_the_brief_avatar_and_no_avatar_input_is_derived()
    {
        try
        {
            AppStepFacts.UseBriefAvatarSwitch(() => false);
            AppStepFacts off = AppStepFacts.From(StepType.VideoGenerate, null, null, Inline);
            off.TakesBriefAvatar.Should().BeFalse();
            AppManifestDeriver.Derive([off], requiresUserInput: false).Inputs
                .Should().NotContain(i => i.Field == BriefFieldConsumers.Avatar, "behaviour with the kill switch off is unchanged");

            AppStepFacts.UseBriefAvatarSwitch(() => true);
            AppStepFacts on = AppStepFacts.From(StepType.VideoGenerate, null, null, Inline);
            on.TakesBriefAvatar.Should().BeTrue();
            AppManifestDeriver.Derive([on], requiresUserInput: false).Inputs
                .Should().Contain(i => i.Field == BriefFieldConsumers.Avatar);
        }
        finally
        {
            AppStepFacts.UseBriefAvatarSwitch(() => true);
        }
    }

    [Fact]
    public void With_avatars_off_the_catalog_listing_docs_facts_and_create_manifests_leave_the_avatar_out()
    {
        string[] avatarKeys = [WorkflowTemplateCatalog.SpokespersonClipTemplateKey, WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey, WorkflowTemplateCatalog.AvatarClipTemplateKey];
        try
        {
            // The cached edition is built first, while the switch is on; reads must still follow the live switch.
            AppStepFacts.UseBriefAvatarSwitch(() => true);
            WorkflowTemplateCatalog.GetAll();
            WorkflowTemplateCatalog.GetListed().Select(t => t.Key).Should().Contain(WorkflowTemplateCatalog.AvatarClipTemplateKey);
            StepCatalog.GetTemplateFacts().Select(t => t.Key).Should().Contain(WorkflowTemplateCatalog.SpokespersonClipTemplateKey);

            AppStepFacts.UseBriefAvatarSwitch(() => false);
            WorkflowTemplateCatalog.GetListed().Select(t => t.Key).Should().NotContain(avatarKeys);
            WorkflowTemplateCatalog.GetListed().Select(t => t.Key).Should().NotContain(avatarKeys);
            StepCatalog.GetTemplateFacts().Select(t => t.Key).Should().NotContain(avatarKeys);
            foreach (WorkflowTemplateDefinition t in WorkflowTemplateCatalog.GetAll().Where(t => t.AppManifest is not null))
                WorkflowTemplateAppInputs.ManifestForCreate(t, t.Key, _ => null)!.Inputs
                    .Should().NotContain(i => i.Field == BriefFieldConsumers.Avatar, t.Key);
        }
        finally
        {
            AppStepFacts.UseBriefAvatarSwitch(() => true);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvatarSwitchCollection
{
    public const string Name = "AvatarSwitch";
}
