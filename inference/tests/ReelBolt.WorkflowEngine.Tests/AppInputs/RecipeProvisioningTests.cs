using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Provisioning from the app-input edition (docs/design/app-inputs.md 22): the switch picks the edition, the gallery never
/// lists the hidden avatar templates, an old avatar key creates "Avatar clip" with its own defaults, and agent-model inputs
/// get the built-in agent's id.
/// </summary>
public class RecipeProvisioningTests : IDisposable
{
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private readonly Dictionary<AgentType, AgentDefinition> _agents = [];
    private readonly Guid _projectId = Guid.NewGuid();

    public RecipeProvisioningTests()
    {
        foreach (AgentType type in Enum.GetValues<AgentType>())
        {
            AgentDefinition agent = new()
            {
                Id = Guid.NewGuid(),
                Name = type.ToString(),
                Description = "d",
                SystemPrompt = "p",
                AgentType = type,
                IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow
            };
            _agents[type] = agent;
            _db.AgentDefinitions.Add(agent);
        }

        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private WorkflowTemplateProvisioningService Service(bool appInputs, bool avatars = true) => new(
        _db, NullLogger<WorkflowTemplateProvisioningService>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Avatars:Enabled"] = avatars ? "true" : "false"
        }).Build());

    [Fact]
    public async Task With_avatars_off_no_avatar_recipe_is_listed_or_created()
    {
        bool appInputs = true;
        WorkflowTemplateProvisioningService off = Service(appInputs, avatars: false);
        string[] avatarKeys = [WorkflowTemplateCatalog.SpokespersonClipTemplateKey, WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey, WorkflowTemplateCatalog.AvatarClipTemplateKey];

        (await off.ListRecipesAsync(CancellationToken.None)).Select(t => t.Key).Should().NotContain(avatarKeys);
        (await off.ListAvailableTemplatesAsync(CancellationToken.None)).Select(t => t.Key).Should().NotContain(avatarKeys);
        foreach (string key in avatarKeys)
        {
            Func<Task> create = () => off.CreateFromTemplateAsync(_projectId, key, false, CancellationToken.None);
            await create.Should().ThrowAsync<InvalidOperationException>(key);
            (await off.BuildDryStepsAsync(key, CancellationToken.None)).Should().BeNull(key);
        }

        // Turned on, the same recipes are offered again.
        (await Service(appInputs, avatars: true).ListRecipesAsync(CancellationToken.None)).Select(t => t.Key)
            .Should().Contain(WorkflowTemplateCatalog.AvatarClipTemplateKey);
    }

    [Fact]
    public async Task The_gallery_lists_avatar_clip_and_not_the_two_it_replaces()
    {
        IReadOnlyList<string> keys = [.. (await Service(appInputs: true).ListRecipesAsync(CancellationToken.None)).Select(t => t.Key)];
        keys.Should().Contain(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        keys.Should().NotContain([WorkflowTemplateCatalog.SpokespersonClipTemplateKey, WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey]);
        (await Service(appInputs: true).ListAvailableTemplatesAsync(CancellationToken.None)).Should().BeSameAs(WorkflowTemplateCatalog.GetAll());
    }

    [Fact]
    public async Task The_budget_key_creates_avatar_clip_with_lite_mode_on()
    {
        WorkflowDefinition? workflow = await Service(appInputs: true).CreateFromTemplateAsync(
            _projectId, WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey, false, CancellationToken.None);

        workflow!.TemplateKey.Should().Be(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        workflow.Name.Should().Be("Avatar clip v2");
        workflow.Steps.Should().HaveCount(5);
        AppManifest manifest = AppManifestJson.TryParse(workflow.AppManifestJson)!;
        manifest.Inputs!.Single(i => i.Id == "liteMode").Default!.Value.GetBoolean().Should().BeTrue();
        manifest.RequestTemplate.Should().BeNull("Lite mode speaks the person's words itself (F1)");
    }

    [Fact]
    public async Task Agent_model_inputs_name_the_built_in_agents_of_the_project()
    {
        WorkflowDefinition? promo = await Service(appInputs: true).CreateFromTemplateAsync(
            _projectId, WorkflowTemplateCatalog.StarterTemplateKey, false, CancellationToken.None);

        AppManifest manifest = AppManifestJson.TryParse(promo!.AppManifestJson)!;
        manifest.Version.Should().Be(AppManifestVocabulary.Version2);
        manifest.Inputs!.Single(i => i.Id == "scriptModel").Agent.Should().Be(_agents[AgentType.ScriptwriterAgent].Id);
        manifest.Inputs!.Single(i => i.Id == "builderModel").Agent.Should().Be(_agents[AgentType.AuthorAgent].Id);
        promo.Steps.Select(s => s.AgentDefinitionId).Should().Contain([_agents[AgentType.ScriptwriterAgent].Id, _agents[AgentType.AuthorAgent].Id]);
    }
}
