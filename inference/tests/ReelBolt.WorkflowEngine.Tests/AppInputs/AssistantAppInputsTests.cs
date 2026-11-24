using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Lane L8 (docs/design/app-inputs.md 13.2, 13.3): the assistant sets up an app's request page (the patch operations and
/// <c>appManifest</c> on the workflow save), fills its fields on a run (<c>brief.inputs</c>, priced on the bound, chosen
/// branch), reads them with their model options, and offers the recipe edition the switch selects. With the switch off
/// every tool is today's.
/// </summary>
public class AssistantAppInputsTests
{
    private const string Generate =
        """{"planSource":"Inline","inlinePrompt":"A latte, slowly poured.","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":2,"maxSpendCredits":5}""";

    private static IConfiguration Switch(bool on) => new ConfigurationBuilder()
        .Build();

    private sealed class PatchScenario : IDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        public InferenceApiDbContext Db { get; }
        public Guid Owner { get; } = Guid.NewGuid();
        public Project Project { get; }
        public WorkflowDefinition Workflow { get; }
        public AssistantWorkflowPatchTools Tools { get; }
        public AssistantToolContext Context => new(Owner, Project.Id);

        public PatchScenario(bool on, string config = Generate, string? manifest = null)
        {
            (_connection, InferenceApiDbContext db) = WorkflowAssistantControllerTests.CreateSqliteContext();
            Db = db;
            Project = new Project { Id = Guid.NewGuid(), Name = "Café", OwnerId = Owner, OrganizationId = TestTenancy.OrgOf(Owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);
            AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "VideoTransform", Description = "d", SystemPrompt = "p", AgentType = AgentType.VideoTransform, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
            Db.AgentDefinitions.Add(agent);
            Workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Clip app", ProjectId = Project.Id, AppManifestJson = manifest, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Workflow.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Workflow.Id,
                AgentDefinitionId = agent.Id,
                StepOrder = 1,
                Label = "Make the clip",
                StepType = StepType.VideoGenerate,
                VideoGenerateConfigJson = config
            });
            Db.WorkflowDefinitions.Add(Workflow);
            Db.SaveChanges();

            IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
            Tools = new AssistantWorkflowPatchTools(
                Db, tokens, TestTenancy.Access(Db, new FakeCurrentUser(Owner)),
                new WorkflowEditDiffService(Db, tokens), new StepProviderOwnershipValidator(Db));
        }

        public async Task<JsonElement> InvokeAsync(string tool, params (string, object?)[] args)
        {
            AIFunction function = Tools.GetTools(Context).Single(t => t.Name == tool);
            AIFunctionArguments arguments = new();
            foreach ((string key, object? value) in args)
                arguments[key] = value;
            object? raw = await function.InvokeAsync(arguments);
            string text = raw switch
            {
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
                JsonElement e => e.GetRawText(),
                _ => raw?.ToString() ?? string.Empty
            };
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public WorkflowDefinition Stored() =>
            Db.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps).Single(w => w.Id == Workflow.Id);

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private static JsonElement Ops(string json) => J(json);

    private const string AddPromptField =
        """
        [
          {"op":"setAppInput","input":{"id":"videoPrompt","type":"longText","label":"What happens in the video","required":true,"example":"A barista pours latte art"}},
          {"op":"bindAppInput","stepOrder":1,"field":"inlinePrompt","inputId":"videoPrompt","mode":"placeholder"},
          {"op":"setAppInput","input":{"id":"clipLength","type":"duration","label":"Clip length","default":6,"min":4,"max":10}},
          {"op":"bindAppInput","stepOrder":1,"field":"durationSeconds","inputId":"clipLength"}
        ]
        """;

    [Fact]
    public async Task The_patch_sets_up_a_request_page_and_its_bindings_and_the_card_says_it_in_words()
    {
        using PatchScenario s = new(on: true);

        JsonElement proposal = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(AddPromptField)));

        proposal.TryGetProperty("error", out JsonElement refused).Should().BeFalse(refused.ToString());
        proposal.GetProperty("changesNothing").GetBoolean().Should().BeFalse();
        string[] lines = [.. proposal.GetProperty("appChanges").EnumerateArray().Select(l => l.GetString()!)];
        lines.Should().Contain("Adds the field \"What happens in the video\" (long text, required) to the app page.");
        lines.Should().Contain(l => l.StartsWith("Uses \"What happens in the video\" as the video prompt of step 1 \"Make the clip\"", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("Uses \"Clip length\" as the clip length of step 1", StringComparison.Ordinal));

        // The card reads the tool's own sentences, never the model's words.
        var builder = new AssistantApprovalCardBuilder(s.Db, TestTenancy.Access(s.Db, new FakeCurrentUser(s.Owner)), new FakeCurrentUser(s.Owner));
        ApprovalCard card = (await builder.BuildAsync(
            "ApplyPatchWorkflow", Args(("workflowId", s.Workflow.Id), ("operations", Ops(AddPromptField)), ("confirmationToken", "t")),
            s.Context, new ProposalRecord("ProposePatchWorkflow", proposal), CancellationToken.None)).Card!;
        card.Changes!.Select(c => c.Text).Should().Contain("Adds the field \"What happens in the video\" (long text, required) to the app page.");

        JsonElement applied = await s.InvokeAsync("ApplyPatchWorkflow",
            ("workflowId", s.Workflow.Id), ("operations", Ops(AddPromptField)), ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));
        applied.TryGetProperty("error", out JsonElement applyError).Should().BeFalse(applyError.ToString());

        WorkflowDefinition stored = s.Stored();
        AppManifest manifest = AppManifestJson.TryParse(stored.AppManifestJson)!;
        manifest.Version.Should().Be(2);
        manifest.Inputs!.Where(i => i.Kind == AppManifestVocabulary.KindInput).Select(i => i.Id).Should().Equal("videoPrompt", "clipLength");
        string config = stored.Steps.Single().VideoGenerateConfigJson!;
        config.Should().Contain("{{input.videoPrompt}}").And.Contain("\"appInputs\":{\"durationSeconds\":\"clipLength\"}");

        // A replay writes nothing more and says so.
        JsonElement again = await s.InvokeAsync("ApplyPatchWorkflow",
            ("workflowId", s.Workflow.Id), ("operations", Ops(AddPromptField)), ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));
        again.GetProperty("alreadyApplied").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_setting_off_the_allowlist_cannot_read_a_field_and_nothing_is_written()
    {
        using PatchScenario s = new(on: true);
        string before = s.Stored().VideoGenerateConfigJsonOrEmpty();

        JsonElement refused = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(
            """
            [
              {"op":"setAppInput","input":{"id":"share","type":"toggle","label":"Send my footage"}},
              {"op":"bindAppInput","stepOrder":1,"field":"allowSourceMediaEgress","inputId":"share","mode":"value"}
            ]
            """)));

        refused.GetProperty("error").GetString().Should().Contain("'allowSourceMediaEgress' cannot read a field");
        s.Stored().VideoGenerateConfigJsonOrEmpty().Should().Be(before);
        s.Stored().AppManifestJson.Should().BeNull();
    }

    [Fact]
    public async Task Removing_a_field_removes_every_use_of_it()
    {
        string bound = """{"planSource":"Inline","inlinePrompt":"{{input.videoPrompt}} Medium close-up.","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":2,"maxSpendCredits":5,"appInputs":{"durationSeconds":"clipLength"}}""";
        string manifest = AppManifestJson.Serialize(Manifest(
            Input("videoPrompt", "longText", "What happens in the video", def: "\"A latte\""),
            Input("clipLength", "duration", "Clip length", def: "6", min: 4, max: 10)));
        using PatchScenario s = new(on: true, bound, manifest);
        string ops = """[{"op":"removeAppInput","id":"videoPrompt"},{"op":"removeAppInput","id":"clipLength"}]""";

        JsonElement proposal = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)));
        proposal.TryGetProperty("error", out JsonElement refused).Should().BeFalse(refused.ToString());
        proposal.GetProperty("appChanges").EnumerateArray().Select(l => l.GetString()).Should().Contain("Removes the field \"What happens in the video\" from the app page.");
        await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)),
            ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));

        string config = s.Stored().Steps.Single().VideoGenerateConfigJson!;
        config.Should().NotContain("{{input.").And.NotContain("appInputs").And.Contain("A latte Medium close-up.");
    }

    [Fact]
    public async Task Bind_app_input_can_list_what_each_value_of_a_switch_writes()
    {
        // F1 (docs/design/app-inputs.md 27): value lists the setting's value per position of the switch.
        using PatchScenario s = new(on: true);
        string ops = """
            [
              {"op":"setAppInput","input":{"id":"twoTakes","type":"toggle","label":"Two takes"}},
              {"op":"bindAppInput","stepOrder":1,"field":"takes","inputId":"twoTakes","value":{"true":2,"false":1}}
            ]
            """;

        JsonElement proposal = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)));
        proposal.TryGetProperty("error", out JsonElement refused).Should().BeFalse(refused.ToString());
        await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)),
            ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));

        using JsonDocument config = JsonDocument.Parse(s.Stored().Steps.Single().VideoGenerateConfigJson!);
        JsonElement entry = config.RootElement.GetProperty("appInputs").GetProperty("takes");
        entry.GetProperty("input").GetString().Should().Be("twoTakes");
        entry.GetProperty("values").GetProperty("true").GetInt32().Should().Be(2);

        JsonElement bad = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(
            """[{"op":"setAppInput","input":{"id":"many","type":"toggle","label":"Many"}},{"op":"bindAppInput","stepOrder":1,"field":"takes","inputId":"many","value":{"true":9}}]""")));
        bad.GetProperty("error").GetString().Should().Contain("lists a value the setting cannot take");
    }

    // ------------------------------------------------------------------ the save tools' appManifest

    [Fact]
    public async Task The_workflow_save_tools_take_an_app_page()
    {
        using InferenceApiDbContext db = InMemory();
        AssistantToolProvider on = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(Guid.NewGuid()), appInputs: new AppInputValueValidator(db));
        AssistantToolContext context = new(Guid.NewGuid(), null);

        foreach (string name in new[] { "ValidateWorkflow", "ProposeWorkflow", "ApplyWorkflow" })
        {
            Properties(on.GetTools(context).Single(t => t.Name == name)).Should().Contain("appManifest");
        }

        JsonElement brief(AssistantToolProvider p) => p.GetTools(context).Single(t => t.Name == "ExecuteWorkflow").JsonSchema.GetProperty("properties").GetProperty("brief");
        brief(on).GetProperty("properties").TryGetProperty("inputs", out _).Should().BeTrue();
        on.GetTools(context).Single(t => t.Name == "ExecuteWorkflow").Description.Should().Contain("brief.inputs");
    }

    private static IEnumerable<string> Properties(AIFunction tool) =>
        tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name);

    // ------------------------------------------------------------------ the run card and the run's price

    private static async Task<(InferenceApiDbContext Db, Project Project, WorkflowDefinition Workflow, Guid Owner)> BranchedAppAsync()
    {
        InferenceApiDbContext db = InMemory();
        Guid owner = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Café", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "VideoTransform", Description = "d", SystemPrompt = "p", AgentType = AgentType.VideoTransform, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "Clip app",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            AppManifestJson = AppManifestJson.Serialize(Manifest(
                Input("liteMode", "toggle", "Lite mode (cheaper)", def: "false"),
                Input("whatTheyDo", "longText", "What happens in the video", required: true))),
        };
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Conditional, AgentDefinitionId = agent.Id, Label = "Lite mode?", ConditionExpression = "input.liteMode", TrueBranchStepOrder = "3", FalseBranchStepOrder = "2" });
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoGenerate, AgentDefinitionId = agent.Id, Label = "Make the full clip", VideoGenerateConfigJson = Generate.Replace("A latte, slowly poured.", "{{input.whatTheyDo}}") });
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoGenerate, AgentDefinitionId = agent.Id, Label = "Make the cheap clip", VideoGenerateConfigJson = Generate.Replace("A latte, slowly poured.", "{{input.whatTheyDo}}") });
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
        return (db, project, workflow, owner);
    }

    [Fact]
    public async Task The_run_card_names_each_field_and_prices_only_the_chosen_path()
    {
        (InferenceApiDbContext db, Project project, WorkflowDefinition workflow, Guid owner) = await BranchedAppAsync();
        using InferenceApiDbContext _ = db;
        var user = new FakeCurrentUser(owner);
        Mock<IWorkflowExecutionPreflight> preflight = new();
        var builder = new AssistantApprovalCardBuilder(db, TestTenancy.Access(db, user), user, preflight: preflight.Object,
            appInputs: new AppInputValueValidator(db));

        ApprovalCard card = (await builder.BuildAsync("ExecuteWorkflow", Args(
                ("workflowId", workflow.Id), ("regenerateVideoClips", false),
                ("brief", J("""{"inputs":{"liteMode":true,"whatTheyDo":"A barista pours latte art"}}"""))),
            new AssistantToolContext(owner, project.Id), null, CancellationToken.None)).Card!;

        string[] lines = [.. card.Changes!.Select(c => c.Text)];
        lines.Should().Contain("Lite mode (cheaper): \"yes\"");
        lines.Should().Contain("What happens in the video: \"A barista pours latte art\"");
        lines.Should().Contain(l => l.StartsWith("Takes the path these fields choose: 1 step", StringComparison.Ordinal));
        preflight.Verify(p => p.PriceStepsAsync(It.IsAny<Guid?>(),
            It.Is<IReadOnlyList<WorkflowStep>>(steps => steps.Single(x => x.StepOrder == 3).VideoGenerateConfigJson!.Contains("A barista pours latte art")),
            It.Is<IReadOnlyCollection<int>>(orders => orders.OrderBy(o => o).SequenceEqual(new[] { 1, 3 })),
            It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        preflight.Verify(p => p.PriceAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_run_tool_refuses_on_the_bound_chosen_path_before_anything_is_queued()
    {
        (InferenceApiDbContext db, Project project, WorkflowDefinition workflow, Guid owner) = await BranchedAppAsync();
        using InferenceApiDbContext _ = db;
        Mock<IWorkflowExecutionPreflight> preflight = new();
        preflight.Setup(p => p.CheckStepsAsync(It.IsAny<Guid?>(), It.IsAny<IReadOnlyList<WorkflowStep>>(), It.IsAny<IReadOnlyCollection<int>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowExecutionPreflightResult(false, false, 12, 1, 8, 12, false));
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner),
            appInputs: new AppInputValueValidator(db), executionPreflight: preflight.Object);

        object? raw = await provider.GetTools(new AssistantToolContext(owner, project.Id)).Single(t => t.Name == "ExecuteWorkflow").InvokeAsync(new AIFunctionArguments
        {
            ["workflowId"] = workflow.Id,
            ["regenerateVideoClips"] = false,
            ["brief"] = J("""{"inputs":{"liteMode":true,"whatTheyDo":"Latte art"}}"""),
        });

        JsonDocument.Parse(raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!)
            .RootElement.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        preflight.Verify(p => p.CheckStepsAsync(It.IsAny<Guid?>(), It.IsAny<IReadOnlyList<WorkflowStep>>(),
            It.Is<IReadOnlyCollection<int>>(orders => orders.OrderBy(o => o).SequenceEqual(new[] { 1, 3 })), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------ pure pieces

    [Fact]
    public void Run_lines_name_a_chosen_model_by_name_and_never_by_id()
    {
        Guid model = Guid.NewGuid();
        AppManifest manifest = Manifest(Input("videoModel", "model", "Video model", capability: "VideoGeneration"));

        string[] lines = [.. AssistantAppInputs.RunLines(manifest, Values(("videoModel", $"\"{model}\"")), new Dictionary<Guid, string> { [model] = "MiniMax Hailuo 3" })];

        lines.Should().Equal("Video model: \"MiniMax Hailuo 3\"");
        AssistantAppInputs.RunLines(manifest, Values(("videoModel", $"\"{Guid.NewGuid()}\"")), new Dictionary<Guid, string>())
            .Single().Should().NotContain("-");
    }

    [Theory]
    [InlineData(0.5, "cheapest")]
    [InlineData(1.0, "about the same price")]
    [InlineData(2.2, "about 2 times the price")]
    [InlineData(4.4, "about 4 times the price")]
    public void A_model_price_is_words_never_money(double index, string expected)
    {
        List<AppInputModelOption> options =
        [
            new(Guid.NewGuid(), "A", null, 0.5m, false, true),
            new(Guid.NewGuid(), "B", null, 1.0m, true, true),
            new(Guid.NewGuid(), "C", null, 4.4m, false, true),
        ];

        AssistantWorkflowTools.CostWords((decimal)index, options).Should().Be(expected);
    }

    [Fact]
    public void A_proposal_without_an_app_page_keeps_todays_token_and_one_with_a_page_binds_it()
    {
        AppManifest page = Manifest(Input("videoPrompt", "longText", "What happens"));

        AssistantAppInputs.WorkflowToken("abc", null, null).Should().Be("abc");
        string bound = AssistantAppInputs.WorkflowToken("abc", page, null);
        bound.Should().NotBe("abc");
        AssistantAppInputs.WorkflowToken("abc", page, "{}").Should().NotBe(bound, "a page stored since the proposal makes it stale");
    }

    private static InferenceApiDbContext InMemory() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AIFunctionArguments Args(params (string Key, object? Value)[] args)
    {
        AIFunctionArguments arguments = new();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        return arguments;
    }
}

internal static class AssistantAppInputsTestExtensions
{
    public static string VideoGenerateConfigJsonOrEmpty(this WorkflowDefinition workflow) =>
        workflow.Steps.SingleOrDefault()?.VideoGenerateConfigJson ?? string.Empty;
}
