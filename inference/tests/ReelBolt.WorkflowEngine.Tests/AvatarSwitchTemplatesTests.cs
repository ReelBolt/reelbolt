using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// While the avatar switch is off no template handed out by the catalog carries the brief Avatar input, whichever accessor
/// is used, and a recipe the assistant creates is saved without it. Alone in its collection: the switch is process-wide.
/// </summary>
[Collection(AvatarSwitchCollection.Name)]
public class AvatarSwitchTemplatesTests
{
    private static bool HasAvatarInput(WorkflowTemplateDefinition t) =>
        t.AppManifest?.Inputs?.Any(i => i.Field == BriefFieldConsumers.Avatar) == true;

    [Fact]
    public void With_avatars_off_no_accessor_hands_out_a_template_with_the_avatar_input()
    {
        try
        {
            AppStepFacts.UseBriefAvatarSwitch(() => true);
            WorkflowTemplateCatalog.GetAll()
                .Any(HasAvatarInput).Should().BeTrue("with avatars on the Avatar input exists, so the check below is not vacuous");

            AppStepFacts.UseBriefAvatarSwitch(() => false);
            List<WorkflowTemplateDefinition> seen = [];
            seen.AddRange(WorkflowTemplateCatalog.GetListed());
            seen.AddRange(WorkflowTemplateCatalog.GetAll());
            seen.AddRange(WorkflowTemplateCatalog.GetAutoCreateTemplates());
            seen.AddRange(WorkflowTemplateCatalog.GetAll().Select(t => WorkflowTemplateCatalog.GetByKey(t.Key)!));
            seen.AddRange(WorkflowTemplateCatalog.GetAll().Select(t => WorkflowTemplateCatalog.GetByKey(t.Key)!));
            seen.Should().NotBeEmpty();
            seen.Where(HasAvatarInput).Select(t => t.Key).Should().BeEmpty();
        }
        finally
        {
            AppStepFacts.UseBriefAvatarSwitch(() => true);
        }
    }

    [Fact]
    public async Task With_avatars_off_a_recipe_created_by_the_assistant_is_saved_without_the_avatar_input()
    {
        string key = WorkflowTemplateCatalog.GetAll()
            .FirstOrDefault(t => HasAvatarInput(t) && !WorkflowTemplateCatalog.IsAvatarTemplate(t.Key) && !t.Hidden)?.Key
            ?? WorkflowTemplateCatalog.StarterTemplateKey;
        (Microsoft.Data.Sqlite.SqliteConnection connection, InferenceApiDbContext db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        try
        {
            AppStepFacts.UseBriefAvatarSwitch(() => false);
            using (connection)
            using (db)
            {
                Guid owner = Guid.NewGuid();
                Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
                db.Projects.Add(project);
                foreach (AgentType type in Enum.GetValues<AgentType>())
                    db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), Description = "d", SystemPrompt = "p", AgentType = type, IsBuiltIn = true, CreatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
                FakeCurrentUser user = new(owner);
                IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Avatars:Enabled"] = "false"
                }).Build();
                IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
                AssistantRecipeTools recipes = new(db, TestTenancy.Access(db, user), new ProjectReadinessFacts(db),
                    new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, config),
                    new WorkflowEditDiffService(db, tokens), tokens, user, appInputs: new AppInputValueValidator(db));
                AssistantToolContext context = new(owner, project.Id);

                async Task<JsonElement> Call(string tool, params (string, object?)[] args)
                {
                    AIFunction function = recipes.GetTools(context).Single(t => t.Name == tool);
                    AIFunctionArguments arguments = new();
                    foreach ((string name, object? value) in args)
                        arguments[name] = value;
                    object? raw = await function.InvokeAsync(arguments);
                    return JsonDocument.Parse(raw is JsonElement { ValueKind: JsonValueKind.String } t ? t.GetString()! : raw!.ToString()!).RootElement.Clone();
                }

                JsonElement proposal = await Call("ProposeCreateFromTemplate", ("templateKey", key));
                proposal.TryGetProperty("error", out JsonElement error).Should().BeFalse(error.ToString());
                JsonElement created = await Call("ApplyCreateFromTemplate", ("templateKey", key),
                    ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));
                created.TryGetProperty("error", out JsonElement createError).Should().BeFalse(createError.ToString());

                WorkflowDefinition row = await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == created.GetProperty("workflowId").GetGuid());
                AppManifestJson.TryParse(row.AppManifestJson)?.Inputs.Should().NotContain(i => i.Field == BriefFieldConsumers.Avatar);
            }
        }
        finally
        {
            AppStepFacts.UseBriefAvatarSwitch(() => true);
        }
    }
}
