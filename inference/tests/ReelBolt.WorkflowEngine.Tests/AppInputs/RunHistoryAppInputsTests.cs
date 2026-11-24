using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Lane L10 (docs/design/app-inputs.md 10.4, 26): the run history and the run detail show each run's app fields in words,
/// labelled by the run's own page (labels of the time), a model by name and never by id.
/// </summary>
public class RunHistoryAppInputsTests
{
    private static readonly Guid VideoModel = Guid.NewGuid();

    private static readonly AppManifest Page = Manifest(
        Input("whatTheyDo", "longText", "What they do"),
        Input("liteMode", "toggle", "Lite mode") with { OnLabel = "Cheaper", LabelKey = "recipes.inputs.liteMode.label" },
        Input("look", "choice", "Caption look", options: [new AppInputOption(J("\"Karaoke\""), "Word by word", LabelKey: "recipes.inputs.options.karaoke")]),
        Input("length", "duration", "Length") with { Unit = "s" },
        Input("model", "model", "Video model", capability: "VideoGeneration"),
        Input("unused", "shortText", "Never set"));

    [Fact]
    public void Compose_lists_set_fields_in_page_order_in_words_and_never_a_model_id()
    {
        Dictionary<string, JsonElement> values = Values(
            ("model", $"\"{VideoModel}\""), ("whatTheyDo", "\"Pours latte art\""), ("liteMode", "true"),
            ("look", "\"Karaoke\""), ("length", "10"), ("unused", "\"  \""));

        IReadOnlyList<RunAppInputResponse> rows = RunAppInputView.Compose(Page, values, new Dictionary<Guid, string> { [VideoModel] = "Kling 3" })!;

        rows.Select(r => r.Id).Should().Equal("whatTheyDo", "liteMode", "look", "length", "model");
        rows[0].ValueText.Should().Be("Pours latte art");
        rows[1].Should().BeEquivalentTo(new { Label = "Lite mode", LabelKey = "recipes.inputs.liteMode.label", ValueText = "Cheaper", OnLabel = "Cheaper" });
        rows[2].Should().BeEquivalentTo(new { ValueText = "Word by word", ValueLabelKey = "recipes.inputs.options.karaoke" });
        rows[3].Should().BeEquivalentTo(new { ValueText = "10", Unit = "s" });
        rows[4].Should().BeEquivalentTo(new { Type = "model", ValueText = "Kling 3", ModelName = "Kling 3" });
        rows[4].Value.Should().BeNull("a model's value is an id");

        RunAppInputView.Compose(Page, values, new Dictionary<Guid, string>())![4].ValueText
            .Should().Be(RunAppInputView.UnknownModelWords).And.NotContain(VideoModel.ToString());
    }

    [Fact]
    public void Compose_is_null_for_a_run_without_app_fields()
    {
        RunAppInputView.Compose(Page, null, new Dictionary<Guid, string>()).Should().BeNull();
        RunAppInputView.Compose(null, Values(("whatTheyDo", "\"x\"")), new Dictionary<Guid, string>()).Should().BeNull();
        RunAppInputView.Compose(Page, Values(("gone", "\"x\"")), new Dictionary<Guid, string>()).Should().BeNull();
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Owner;
    }

    [Fact]
    public async Task List_and_detail_label_the_fields_with_the_run_s_own_page_and_leave_other_runs_untouched()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase("run-history-inputs-" + Guid.NewGuid().ToString("N")).Options);
        Guid owner = Guid.NewGuid();
        FakeCurrentUser user = new(owner);
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        // Today's page renamed the field; the run's snapshot keeps the label of the time.
        AppManifest today = Manifest(Input("whatTheyDo", "longText", "The action"));
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", AppManifestJson = AppManifestJson.Serialize(today), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "wf", Kind = WorkflowDefinitionKind.ExecutionSnapshot, AppManifestJson = AppManifestJson.Serialize(Page), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        WorkflowExecution withInputs = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            SnapshotDefinitionId = snapshot.Id,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            RunBriefJson = RunBriefJson.Serialize(new StoredRunBrief(Inputs: Values(("whatTheyDo", "\"Pours latte art\""), ("liteMode", "false")))),
        };
        WorkflowExecution plain = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow.AddMinutes(-9),
        };
        db.AddRange(project, workflow, snapshot, withInputs, plain);
        await db.SaveChangesAsync();

        WorkflowsController controller = new(
            db, user, TestTenancy.Access(db, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance);

        ActionResult<List<WorkflowExecutionResponse>> list = await controller.ListExecutions(project.Id, workflow.Id, CancellationToken.None);
        List<WorkflowExecutionResponse> items = (List<WorkflowExecutionResponse>)((OkObjectResult)list.Result!).Value!;
        items.Single(i => i.Id == plain.Id).AppInputs.Should().BeNull();
        IReadOnlyList<RunAppInputResponse> fields = items.Single(i => i.Id == withInputs.Id).AppInputs!;
        fields.Select(f => (f.Label, f.ValueText)).Should().Equal(("What they do", "Pours latte art"), ("Lite mode", "no"));

        ActionResult<WorkflowExecutionResponse> detail = await controller.GetExecution(project.Id, withInputs.Id, CancellationToken.None);
        ((WorkflowExecutionResponse)((OkObjectResult)detail.Result!).Value!).AppInputs!.Select(f => f.Label)
            .Should().Equal("What they do", "Lite mode");
        string json = JsonSerializer.Serialize(items.Single(i => i.Id == plain.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain("appInputs");
    }
}
