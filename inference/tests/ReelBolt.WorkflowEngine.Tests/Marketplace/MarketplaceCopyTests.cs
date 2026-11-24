using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Marketplace;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class MarketplaceCopyTests
{
    private sealed class FakeUser(Guid org) : ICurrentUser
    {
        public Guid UserId { get; set; } = Guid.NewGuid();
        public string Email => "copier@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId { get; set; } = org;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
        public bool EmailVerified { get; set; } = true;
    }

    private sealed class FakeAccess(Guid orgId) : IProjectAccessService
    {
        public int Calls;
        public ProjectAccessOutcome Outcome = ProjectAccessOutcome.Found;

        public Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ProjectAccessResult(
                Outcome, Outcome == ProjectAccessOutcome.Found ? new Project { Id = projectId, OrganizationId = orgId } : null));
        }

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission) => GetAsync(projectId, permission, default).Result;
        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) => Outcome;
    }

    private sealed class FakeReadiness : IProjectReadinessFacts
    {
        public Task<ProjectReadinessSnapshot> LoadAsync(Project project, CancellationToken ct) =>
            Task.FromResult(new ProjectReadinessSnapshot(0, 0, 0, 0, new HashSet<InferenceProviderCapability>(), false, false));
    }

    private sealed class FakeEvents : IProductEventEmitter
    {
        public readonly List<(string Event, string? Detail)> Rows = [];
        public Task FirstAsync(Guid userId, Guid? organizationId, string eventName, CancellationToken ct) => Task.CompletedTask;

        public Task AppendAsync(Guid userId, Guid? organizationId, string eventName, string? detail, CancellationToken ct)
        {
            lock (Rows)
                Rows.Add((eventName, detail));
            return Task.CompletedTask;
        }
    }

    private sealed class Harness
    {
        public readonly string DbName = Guid.NewGuid().ToString();
        public readonly InferenceApiDbContext Db;
        public readonly MarketplaceOptions Options = new() { Enabled = true };
        public readonly FakeEvents Events = new();
        public readonly Guid AuthorOrg = Guid.NewGuid();
        public readonly Guid CopierOrg = Guid.NewGuid();
        public readonly Guid Project;
        public readonly Guid RecipeId = Guid.NewGuid();
        public readonly FakeUser User;
        public readonly FakeAccess Access;
        public readonly PortableRecipe Recipe;
        public readonly WorkflowDefinition Source;

        public Harness(Func<PortableRecipe, PortableRecipe>? tweak = null, Func<PortableRecipeResult>? build = null, Func<string, string>? rawTweak = null)
        {
            Db = NewDb();
            User = new FakeUser(CopierOrg);
            Access = new FakeAccess(CopierOrg);
            Project = Guid.NewGuid();

            Source = SnapshotFixtures.Workflow();
            WorkflowTemplateDefinition template = Shareable().First();
            Source.Steps = SnapshotFixtures.FromTemplate(template);
            PortableRecipeResult built = build?.Invoke() ?? PortableRecipeBuilder.Build(Source, Source.Steps.ToList(), SnapshotFixtures.BuiltIns(), null);
            Recipe = tweak is null ? built.Recipe! : tweak(built.Recipe!);

            Db.Projects.Add(new Project { Id = Project, Name = "Target", OwnerId = User.UserId, OrganizationId = CopierOrg });
            Db.AgentDefinitions.AddRange(SnapshotFixtures.BuiltIns().Values);
            Guid versionId = Guid.NewGuid();
            Db.MarketplaceRecipes.Add(new MarketplaceRecipe
            {
                Id = RecipeId,
                AuthorOrgId = AuthorOrg,
                AuthorUserId = Guid.NewGuid(),
                Status = RecipeStatus.Listed,
                Name = "Promo recipe",
                Description = "d",
                Category = "promo",
                CurrentVersionId = versionId
            });
            Db.MarketplaceRecipeVersions.Add(new MarketplaceRecipeVersion { Id = versionId, RecipeId = RecipeId, N = 1, SnapshotJson = rawTweak is null ? Recipe.ToCanonicalJson() : rawTweak(Recipe.ToCanonicalJson()) });
            Db.SaveChanges();
        }

        public InferenceApiDbContext NewDb() =>
            new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(DbName).Options);

        public Guid AddProject(Guid org)
        {
            Guid id = Guid.NewGuid();
            Db.Projects.Add(new Project { Id = id, Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = org });
            Db.SaveChanges();
            return id;
        }

        public MarketplaceCopyService Service(InferenceApiDbContext? db = null, FakeUser? user = null, FakeAccess? access = null)
        {
            db ??= Db;
            return new MarketplaceCopyService(
                db, user ?? User, access ?? Access, new FakeReadiness(), new StepProviderOwnershipValidator(db),
                Microsoft.Extensions.Options.Options.Create(Options), NullLogger<MarketplaceCopyService>.Instance, null, Events);
        }
    }

    /// <summary>The built-in templates the publish catalog lets through (a recipe is built and is a coherent workflow).</summary>
    private static IEnumerable<WorkflowTemplateDefinition> Shareable() =>
        WorkflowTemplateCatalog.GetAll().Where(t =>
        {
            PortableRecipeResult r = PortableRecipeBuilder.Build(SnapshotFixtures.Workflow(), SnapshotFixtures.FromTemplate(t), SnapshotFixtures.BuiltIns(), null);
            return r.Recipe is not null && PortableRecipeValidator.Validate(r.Recipe).Count == 0;
        });

    private static CopyOutcome Ok(MarketplaceResult<CopyOutcome> r)
    {
        r.Error.Should().BeNull();
        return r.Value!;
    }

    private static async Task<List<WorkflowStep>> StepsOf(InferenceApiDbContext db, Guid workflowId) =>
        await db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == workflowId).Include(s => s.AgentDefinition).OrderBy(s => s.StepOrder).ToListAsync();

    // ---------------------------------------------------------------- flag

    [Fact]
    public async Task Flag_off_is_a_bare_404_and_the_route_sits_behind_the_flag_filter()
    {
        Harness h = new();
        h.Options.Enabled = false;
        (await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Error.Should().BeEquivalentTo(new MarketplaceError(404));

        typeof(MarketplaceCopyController).GetCustomAttributes(typeof(TypeFilterAttribute), true)
            .Cast<TypeFilterAttribute>().Should().Contain(a => a.ImplementationType == typeof(MarketplaceEnabledFilter));
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task Copy_makes_an_ordinary_workflow_counts_one_organization_and_authorizes_once()
    {
        Harness h = new();
        CopyOutcome o = Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, new CopyRecipeRequest("  My <b>copy</b> "), default));

        o.AlreadyCopied.Should().BeFalse();
        o.Body.Name.Should().Be("My copy");
        h.Access.Calls.Should().Be(1);

        WorkflowDefinition wf = await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync(w => w.ProjectId == h.Project);
        wf.Id.Should().Be(o.Body.WorkflowId);
        wf.CreatedVia.Should().Be(WorkflowCreatedVia.Marketplace);
        wf.TemplateKey.Should().Be("market:" + h.RecipeId.ToString("N"));
        wf.Steps.Select(s => s.StepOrder).Should().Equal(h.Recipe.Steps.Select(s => s.Order).Order());
        wf.Steps.Should().OnlyContain(s => s.WorkflowDefinitionId == wf.Id);

        (await h.Db.MarketplaceRecipes.SingleAsync()).CopyOrgs.Should().Be(1);
        MarketplaceCopy copy = await h.Db.MarketplaceCopies.SingleAsync();
        copy.OrgId.Should().Be(h.CopierOrg);
        copy.WorkflowId.Should().Be(wf.Id);
        h.Events.Rows.Should().ContainSingle().Which.Should().Be((MarketplaceCopyService.CopiedEvent, wf.TemplateKey));
        (await h.Db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Without_a_name_the_recipe_name_is_used_and_a_taken_name_gets_a_number()
    {
        Harness h = new();
        Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Body.Name.Should().Be("Promo recipe");
        h.Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = Guid.NewGuid(), ProjectId = h.Project, Name = "Mine" });
        await h.Db.SaveChangesAsync();
        Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, new CopyRecipeRequest("Mine"), default)).Body.Name.Should().Be("Mine (2)");
    }

    [Fact]
    public async Task Same_request_twice_returns_the_same_workflow_with_200_not_a_second_copy()
    {
        Harness h = new();
        CopyOutcome first = Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, new CopyRecipeRequest("Again"), default));
        CopyOutcome second = Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, new CopyRecipeRequest("Again"), default));

        second.AlreadyCopied.Should().BeTrue();
        second.Body.WorkflowId.Should().Be(first.Body.WorkflowId);
        (await h.Db.WorkflowDefinitions.CountAsync(w => w.ProjectId == h.Project)).Should().Be(1);
        (await h.Db.MarketplaceRecipes.SingleAsync()).CopyOrgs.Should().Be(1);
        h.Events.Rows.Should().HaveCount(1);

        // A repeat of a request whose name was numbered (it was taken) is still the same copy.
        h.Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = Guid.NewGuid(), ProjectId = h.Project, Name = "Taken" });
        await h.Db.SaveChangesAsync();
        CopyOutcome numbered = Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, new CopyRecipeRequest("Taken"), default));
        Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, new CopyRecipeRequest("Taken"), default)).Body.WorkflowId.Should().Be(numbered.Body.WorkflowId);
    }

    [Fact]
    public async Task A_second_project_of_the_same_organization_does_not_count_twice()
    {
        Harness h = new();
        Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, null, default));
        Guid second = h.AddProject(h.CopierOrg);
        Ok(await h.Service().CopyAsync(second, h.RecipeId, null, default)).AlreadyCopied.Should().BeFalse();

        (await h.Db.WorkflowDefinitions.CountAsync(w => w.TemplateKey != null)).Should().Be(2);
        (await h.Db.MarketplaceCopies.CountAsync()).Should().Be(1);
        (await h.Db.MarketplaceRecipes.SingleAsync()).CopyOrgs.Should().Be(1);
    }

    [Fact]
    public async Task A_different_organization_increments()
    {
        Harness h = new();
        Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, null, default));

        Guid otherOrg = Guid.NewGuid();
        Guid otherProject = h.AddProject(otherOrg);
        FakeUser other = new(otherOrg);
        Ok(await h.Service(user: other, access: new FakeAccess(otherOrg)).CopyAsync(otherProject, h.RecipeId, null, default));

        (await h.Db.MarketplaceRecipes.SingleAsync()).CopyOrgs.Should().Be(2);
        (await h.Db.MarketplaceCopies.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_copies_count_each_organization_once()
    {
        Harness h = new();
        Guid[] orgs = [h.CopierOrg, Guid.NewGuid(), Guid.NewGuid()];
        List<Task> tasks = [];
        foreach (Guid org in orgs)
        {
            Guid project = org == h.CopierOrg ? h.Project : h.AddProject(org);
            for (int i = 0; i < 2; i++)
            {
                InferenceApiDbContext db = h.NewDb();
                MarketplaceCopyService service = h.Service(db, new FakeUser(org), new FakeAccess(org));
                tasks.Add(Task.Run(async () => Ok(await service.CopyAsync(project, h.RecipeId, new CopyRecipeRequest("Race"), default))));
            }
        }

        await Task.WhenAll(tasks);

        InferenceApiDbContext verify = h.NewDb();
        (await verify.MarketplaceRecipes.SingleAsync()).CopyOrgs.Should().Be(3);
        (await verify.MarketplaceCopies.CountAsync()).Should().Be(3);
        (await verify.WorkflowDefinitions.CountAsync(w => w.TemplateKey != null)).Should().Be(3);
    }

    // ---------------------------------------------------------------- refusals

    [Theory]
    [InlineData(RecipeStatus.Draft, false)]
    [InlineData(RecipeStatus.Pending, false)]
    [InlineData(RecipeStatus.Rejected, false)]
    [InlineData(RecipeStatus.Removed, false)]
    [InlineData(RecipeStatus.Unpublished, false)]
    [InlineData(RecipeStatus.Listed, true)]
    public async Task Only_a_listed_recipe_that_is_not_hidden_can_be_copied(RecipeStatus status, bool hidden)
    {
        Harness h = new();
        MarketplaceRecipe r = await h.Db.MarketplaceRecipes.SingleAsync();
        r.Status = status;
        r.HiddenByReports = hidden;
        await h.Db.SaveChangesAsync();

        MarketplaceResult<CopyOutcome> result = await h.Service().CopyAsync(h.Project, h.RecipeId, null, default);
        if (status == RecipeStatus.Listed && !hidden)
        {
            result.Error.Should().BeNull();
            return;
        }

        result.Error.Should().BeEquivalentTo(new MarketplaceError(404));
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await h.Db.MarketplaceCopies.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_hidden_listed_recipe_and_an_unknown_recipe_are_404()
    {
        Harness h = new();
        (await h.Db.MarketplaceRecipes.SingleAsync()).HiddenByReports = true;
        await h.Db.SaveChangesAsync();
        (await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Error!.Status.Should().Be(404);
        (await h.Service().CopyAsync(h.Project, Guid.NewGuid(), null, default)).Error!.Status.Should().Be(404);
    }

    [Fact]
    public async Task A_viewer_is_refused_and_a_foreign_project_is_404_and_nothing_is_written()
    {
        Harness h = new();
        h.Access.Outcome = ProjectAccessOutcome.Forbidden;
        (await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Error!.Status.Should().Be(403);
        h.Access.Outcome = ProjectAccessOutcome.NotFound;
        (await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Error!.Status.Should().Be(404);

        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await h.Db.MarketplaceCopies.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Unverified_email_is_403()
    {
        Harness h = new();
        h.User.EmailVerified = false;
        MarketplaceError e = (await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Error!;
        e.Status.Should().Be(403);
        e.Code.Should().Be("EMAIL_NOT_VERIFIED");
        h.Access.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_snapshot_of_an_unknown_schema_version_is_refused()
    {
        Harness h = new(r => r with { SchemaVersion = 2 });
        MarketplaceError e = (await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Error!;
        e.Status.Should().Be(409);
        e.Code.Should().Be("RECIPE_UNSUPPORTED");

        PortableRecipeMaterializer.Materialize(h.Recipe, h.RecipeId, h.Project, "x", SnapshotFixtures.BuiltIns().Values.ToDictionary(a => a.AgentType))
            .Value.Should().BeNull();
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_snapshot_that_names_an_id_never_becomes_a_workflow()
    {
        Harness h = new(r => r with { Steps = [.. r.Steps.Select((s, i) => i == 0 ? s with { Label = "see 3f2504e0-4f89-11d3-9a0c-0305e82c3301" } : s)] });
        MarketplaceError e = (await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Error!;
        e.Status.Should().Be(409);
        e.Code.Should().Be("RECIPE_UNAVAILABLE");
        (await h.Db.MarketplaceAudit.SingleAsync()).Action.Should().Be("copy_snapshot_invalid");
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await h.Db.MarketplaceCopies.CountAsync()).Should().Be(0);
        (await h.Db.MarketplaceRecipes.SingleAsync()).CopyOrgs.Should().Be(0);
    }

    public static IEnumerable<object[]> LegacySnapshots() =>
    [
        Case("bad expression", r => Edit(r, s => s with { ConditionExpression = "a; DROP TABLE x\u0000" })),
        Case("loop bounds", r => Edit(r, s => s with { MaxIterations = 99 })),
        Case("pass mark", r => Edit(r, s => s with { MinScore = 50 })),
        Case("not shareable type", r => Edit(r, s => s with { StepType = StepType.TimelineRender })),
        Case("custom agent", r => Edit(r, s => s with { BuiltInAgentType = null })),
        Case("too many steps", r => r with { Steps = [.. Enumerable.Range(1, 41).Select(i => r.Steps[0] with { Order = i })] }),
        Case("bad mapping", r => Edit(r, s => s with { InputMapping = new JsonObject { ["x"] = "3f2504e0-4f89-11d3-9a0c-0305e82c3301" } }))
    ];

    private static object[] Case(string name, Func<PortableRecipe, PortableRecipe> craft) => [name, craft];

    private static PortableRecipe Edit(PortableRecipe r, Func<PortableStep, PortableStep> change) =>
        r with { Steps = [.. r.Steps.Select((s, i) => i == 0 ? change(s) : s)] };

    [Theory]
    [MemberData(nameof(LegacySnapshots))]
    public async Task A_crafted_or_legacy_snapshot_is_rechecked_and_refused_with_an_audit_row(string _, Func<PortableRecipe, PortableRecipe> craft)
    {
        Harness h = new(craft);
        MarketplaceError e = (await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Error!;

        e.Status.Should().Be(409);
        e.Code.Should().Be("RECIPE_UNAVAILABLE");
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await h.Db.WorkflowSteps.CountAsync()).Should().Be(0);
        (await h.Db.MarketplaceCopies.CountAsync()).Should().Be(0);
        (await h.Db.MarketplaceRecipes.SingleAsync()).CopyOrgs.Should().Be(0);
        (await h.Db.MarketplaceAudit.SingleAsync()).Action.Should().Be("copy_snapshot_invalid");
        h.Events.Rows.Should().BeEmpty();
    }

    public static IEnumerable<object[]> UnreadableSnapshots() =>
    [
        ["undefined step type name", @"""stepType"":""[A-Za-z]+""", @"""stepType"":""NoSuchStepType"""],
        ["undefined step type number", @"""stepType"":""[A-Za-z]+""", @"""stepType"":9999"],
        ["undefined agent name", @"""builtInAgentType"":""[A-Za-z]+""", @"""builtInAgentType"":""NoSuchAgent"""],
        ["undefined agent number", @"""builtInAgentType"":""[A-Za-z]+""", @"""builtInAgentType"":9999"],
        ["not json", @"^[\s\S]*$", "{ not json"]
    ];

    [Theory]
    [MemberData(nameof(UnreadableSnapshots))]
    public async Task A_stored_snapshot_that_does_not_deserialize_is_refused_cleanly_and_audited(string _, string pattern, string replacement)
    {
        Harness h = new(rawTweak: json =>
        {
            string changed = System.Text.RegularExpressions.Regex.Replace(json, pattern, replacement, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            changed.Should().NotBe(json, "the crafted snapshot must differ from the valid one");
            return changed;
        });
        MarketplaceError e = (await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Error!;

        e.Status.Should().Be(409);
        e.Code.Should().Be("RECIPE_UNAVAILABLE");
        (await h.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await h.Db.WorkflowSteps.CountAsync()).Should().Be(0);
        (await h.Db.MarketplaceCopies.CountAsync()).Should().Be(0);
        (await h.Db.MarketplaceRecipes.SingleAsync()).CopyOrgs.Should().Be(0);
        (await h.Db.MarketplaceAudit.SingleAsync()).Action.Should().Be("copy_snapshot_invalid");
    }

    // ---------------------------------------------------------------- needs

    [Fact]
    public async Task Needs_list_the_open_slots_and_what_the_project_lacks_without_any_id()
    {
        Harness h = new(r => r with
        {
            Slots = [.. r.Slots, new PortableSlot(PortableSlotKinds.Voice, r.Steps[0].Order, "projectVoiceId", "Narration voice"),
                new PortableSlot(PortableSlotKinds.Provider, r.Steps[0].Order, "providerId", "Model provider")]
        });
        CopyOutcome o = Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, null, default));

        o.Body.Needs.Should().Contain(n => n.Need == "voice" && n.Action == "choose-voice" && n.Message.Contains("narration voice") && n.StepOrder == h.Recipe.Steps[0].Order);
        o.Body.Needs.Should().Contain(n => n.Need == "provider" && n.Action == "connect-provider");

        IReadOnlyList<AppStepFacts> facts = [.. h.Recipe.Steps.OrderBy(s => s.Order).Select(s => AppStepFacts.From(
            s.StepType, s.BuiltInAgentType, s.StepType == StepType.VideoAnalyze ? s.Config?.ToJsonString() : null))];
        IEnumerable<string> readinessNeeds = ProjectReadinessFacts.Evaluate(
            facts, new ProjectReadinessSnapshot(0, 0, 0, 0, new HashSet<InferenceProviderCapability>(), false, false)).Select(m => m.Need);
        o.Body.Needs.Where(n => n.StepOrder is null).Select(n => n.Need).Should().Equal(readinessNeeds);
        JsonSerializer.Serialize(o.Body.Needs).Should().NotMatchRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-");
    }

    // ---------------------------------------------------------------- result of the copy

    [Fact]
    public async Task Open_slots_stay_empty_egress_is_off_and_no_source_id_reaches_the_copy()
    {
        Harness h = new();
        Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, null, default));

        WorkflowDefinition wf = await h.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync(w => w.TemplateKey != null);
        string everything = JsonSerializer.Serialize(new
        {
            wf.Name,
            wf.AppManifestJson,
            steps = wf.Steps.Select(s => new
            {
                s.Label,
                s.ConditionExpression,
                s.LoopSourceExpression,
                s.InputMappingJson,
                s.ParallelAgentIdsJson,
                s.SelectedPriorStepOrdersJson,
                s.ExtractConfigJson,
                s.VideoAnalyzeConfigJson,
                s.VideoCompileConfigJson,
                s.EditRoomConfigJson,
                s.GraphicsRoomConfigJson,
                s.ColorGradeRoomConfigJson,
                s.ConditionalConfigJson,
                s.ReviewLoopConfigJson,
                s.VideoGenerateConfigJson,
                s.VoiceoverConfigJson,
                s.ThumbnailGenerateConfigJson,
                s.DailiesRoomConfigJson,
                s.DesignSystemConfigJson,
                s.ExtractFrameConfigJson,
                s.AudioGenerateConfigJson,
                s.PinInsertConfigJson
            })
        });
        foreach (Guid source in h.Source.Steps.Select(s => s.Id).Append(h.Source.Id).Append(h.Source.ProjectId))
            everything.Should().NotContain(source.ToString());
        wf.Steps.Select(s => s.Id).Intersect(h.Source.Steps.Select(s => s.Id)).Should().BeEmpty();
        wf.ProjectId.Should().Be(h.Project);
    }

    [Fact]
    public void Egress_consent_is_false_on_a_copied_step_even_when_the_original_granted_it()
    {
        WorkflowDefinition source = SnapshotFixtures.Workflow();
        List<WorkflowStep> steps = SnapshotFixtures.FromTemplate(WorkflowTemplateCatalog.GetByKey("green-screen-insert")!);
        WorkflowStep step = steps.First(x => x.StepType == StepType.VideoAnalyze);
        JsonObject config = JsonNode.Parse(step.VideoAnalyzeConfigJson!)!.AsObject();
        config["allowSourceMediaEgress"] = true;
        step.VideoAnalyzeConfigJson = config.ToJsonString();
        PortableRecipeResult built = PortableRecipeBuilder.Build(source, steps, SnapshotFixtures.BuiltIns(), null);
        built.Recipe.Should().NotBeNull(string.Join("; ", built.Refusals.Select(r => r.Message)));

        MaterializeResult copy = PortableRecipeMaterializer.Materialize(
            built.Recipe!, Guid.NewGuid(), Guid.NewGuid(), "x", SnapshotFixtures.BuiltIns().Values.ToDictionary(a => a.AgentType));
        copy.Errors.Should().BeEmpty();
        JsonNode copied = JsonNode.Parse(copy.Value!.Steps.First(x => x.StepType == StepType.VideoAnalyze).VideoAnalyzeConfigJson!)!;
        copied["allowSourceMediaEgress"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task The_copy_is_lint_clean_structurally_and_a_later_removal_of_the_recipe_leaves_it_untouched()
    {
        Harness h = new();
        Guid workflowId = Ok(await h.Service().CopyAsync(h.Project, h.RecipeId, null, default)).Body.WorkflowId;

        List<WorkflowStep> steps = await StepsOf(h.Db, workflowId);
        StepGraphLinter.Evaluate(steps)
            .Where(f => PortableRecipeValidator.StructuralRules.Contains(f.RuleId) && f.Severity == LintSeverity.Error)
            .Should().BeEmpty();
        string before = JsonSerializer.Serialize(steps.Select(s => new { s.StepOrder, s.StepType, s.Label, s.AgentDefinitionId, s.ConditionExpression, s.InputMappingJson }));

        MarketplaceRecipe recipe = await h.Db.MarketplaceRecipes.SingleAsync();
        recipe.Status = RecipeStatus.Removed;
        await h.Db.SaveChangesAsync();
        h.Db.MarketplaceRecipeVersions.RemoveRange(h.Db.MarketplaceRecipeVersions);
        h.Db.MarketplaceRecipes.Remove(recipe);
        await h.Db.SaveChangesAsync();

        InferenceApiDbContext verify = h.NewDb();
        WorkflowDefinition kept = await verify.WorkflowDefinitions.SingleAsync(w => w.Id == workflowId);
        kept.TemplateKey.Should().StartWith("market:");
        List<WorkflowStep> after = await StepsOf(verify, workflowId);
        JsonSerializer.Serialize(after.Select(s => new { s.StepOrder, s.StepType, s.Label, s.AgentDefinitionId, s.ConditionExpression, s.InputMappingJson }))
            .Should().Be(before);
    }

    // ---------------------------------------------------------------- round trip (the plan's step 6)

    public static IEnumerable<object[]> ShareableTemplateKeys() => Shareable().Select(t => new object[] { t.Key });

    [Fact]
    public void The_catalog_has_shareable_templates_to_round_trip()
    {
        Shareable().Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ShareableTemplateKeys))]
    public void Every_shareable_template_round_trips_through_the_snapshot_into_an_empty_project(string key)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(key)!;
        WorkflowDefinition source = SnapshotFixtures.Workflow();
        List<WorkflowStep> originals = SnapshotFixtures.FromTemplate(template);
        source.Steps = originals;
        IReadOnlyDictionary<Guid, AgentDefinition> sourceAgents = SnapshotFixtures.BuiltIns();

        PortableRecipeResult built = PortableRecipeBuilder.Build(source, originals, sourceAgents, null);
        built.Recipe.Should().NotBeNull();
        Guid projectId = Guid.NewGuid();
        MaterializeResult copy = PortableRecipeMaterializer.Materialize(
            built.Recipe!, Guid.NewGuid(), projectId, template.Name, sourceAgents.Values.ToDictionary(a => a.AgentType));
        copy.Errors.Should().BeEmpty();
        MaterializedRecipe m = copy.Value!;

        // Same behaviour as the original: order, types, agents, labels, expressions, loops, branches and every non-slot config.
        m.Steps.Select(s => s.StepOrder).Should().Equal(originals.Select(s => s.StepOrder));
        for (int i = 0; i < originals.Count; i++)
        {
            WorkflowStep a = originals[i];
            WorkflowStep b = m.Steps[i];
            b.StepType.Should().Be(a.StepType);
            sourceAgents[a.AgentDefinitionId].AgentType.Should().Be(sourceAgents[b.AgentDefinitionId].AgentType);
            b.Label.Should().Be(a.Label);
            b.ConditionExpression.Should().Be(a.ConditionExpression);
            b.LoopSourceExpression.Should().Be(a.LoopSourceExpression);
            b.LoopTargetStepOrder.Should().Be(a.LoopTargetStepOrder);
            b.MaxIterations.Should().Be(a.MaxIterations);
            b.MinScore.Should().Be(a.MinScore);
            b.TrueBranchStepOrder.Should().Be(a.TrueBranchStepOrder);
            b.FalseBranchStepOrder.Should().Be(a.FalseBranchStepOrder);
            b.CacheMode.Should().Be(a.CacheMode);
            b.AgentInputContextMode.Should().Be(a.AgentInputContextMode);
            Canon(b.ParallelAgentIdsJson).Should().Be(Canon(a.ParallelAgentIdsJson));
            Canon(StepConfigColumns.Get(b)).Should().Be(Canon(StepConfigColumns.Get(a)), $"step {a.StepOrder} ({a.StepType}) config");
        }

        StepGraphLinter.Evaluate(m.Steps)
            .Where(f => PortableRecipeValidator.StructuralRules.Contains(f.RuleId) && f.Severity == LintSeverity.Error)
            .Should().BeEmpty();

        // No id of the source workflow appears anywhere in the copied rows.
        string rows = JsonSerializer.Serialize(m.Steps.Select(s => new
        {
            s.Label,
            s.InputMappingJson,
            s.ParallelAgentIdsJson,
            s.ConditionExpression,
            Config = StepConfigColumns.Get(s)
        })) + m.Workflow.AppManifestJson + m.Workflow.Name;
        foreach (Guid id in originals.Select(s => s.Id).Append(source.Id).Append(source.ProjectId))
            rows.Should().NotContain(id.ToString());
        m.Steps.Select(s => s.Id).Concat([m.Workflow.Id]).Intersect(originals.Select(s => s.Id).Append(source.Id)).Should().BeEmpty();
        m.Workflow.ProjectId.Should().Be(projectId);
    }

    /// <summary>Canonical JSON with the egress consent removed (the copy turns it off on purpose) and the slot-path placeholders (the snapshot drops them); null stays null.</summary>
    private static string? Canon(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        JsonNode? node = PortableRecipeJson.Canonicalize(JsonNode.Parse(json));
        Strip(node);
        return node?.ToJsonString();
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(kv => kv.Key).Where(k => k.Equals("allowSourceMediaEgress", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("placeholders", StringComparison.OrdinalIgnoreCase)).ToList())
                    obj.Remove(key);
                foreach (KeyValuePair<string, JsonNode?> kv in obj.ToList())
                    Strip(kv.Value);
                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr)
                    Strip(item);
                break;
        }
    }

    [Fact]
    public void A_talking_head_needs_a_lip_sync_model_and_a_video_provider_alone_does_not_make_it_ready()
    {
        string talking = "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"avatarAudio\":{\"from\":\"Step\",\"stepOrder\":1}}";
        string plain = "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\"}";
        IReadOnlyList<AppStepFacts> talkingSteps = [AppStepFacts.From(StepType.VideoGenerate, null, null, talking)];
        IReadOnlyList<AppStepFacts> plainSteps = [AppStepFacts.From(StepType.VideoGenerate, null, null, plain)];
        ProjectReadinessSnapshot Snapshot(params InferenceProviderCapability[] have) =>
            new(0, 0, 0, 0, new HashSet<InferenceProviderCapability>(have), false, false);

        ProjectReadinessFacts.Evaluate(talkingSteps, Snapshot(InferenceProviderCapability.VideoGeneration)).Select(m => m.Need).Should().Equal("lipSync");
        ProjectReadinessFacts.Evaluate(talkingSteps, Snapshot(InferenceProviderCapability.LipSync)).Should().BeEmpty();
        ProjectReadinessFacts.Evaluate(plainSteps, Snapshot(InferenceProviderCapability.LipSync)).Select(m => m.Need).Should().Equal("videoGeneration");
        ProjectReadinessFacts.Evaluate(plainSteps, Snapshot(InferenceProviderCapability.VideoGeneration)).Should().BeEmpty();
    }
}
