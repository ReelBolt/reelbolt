using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.EditTimelines;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.History;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>
/// Restore part 2 (contract section 8 steps 5-6): the Workflow and Timeline kind handlers. Runs on
/// SQLite so the real <see cref="WorkflowStepReplacer"/> (bulk delete) and
/// <see cref="EditTimelineCommit"/> (transaction, snapshots) are what the tests exercise.
/// </summary>
public class ProjectHistoryRestoreWorkflowTimelineTests
{
    private static readonly IReadOnlyList<ProjectHistoryRestoreItemRef> NoItems = [];

    // ─────────────────────────────────────────── workflows ───────────────────────────────────────────

    [Fact]
    public async Task Workflow_round_trip_restores_steps_and_definition_fields()
    {
        using Fixture f = new();
        WorkflowDefinition workflow = f.SeedWorkflow("Launch reel", label: "Write", egress: false);
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        string captured = await f.WorkflowDocumentAsync(v1, workflow.Id);

        // Edit the live workflow: rename it, turn input off then on, and change the step.
        WorkflowDefinition live = await f.Db.WorkflowDefinitions.SingleAsync(w => w.Id == workflow.Id);
        live.Name = "Edited";
        live.RequiresUserInput = false;
        WorkflowStep step = await f.Db.WorkflowSteps.SingleAsync(s => s.WorkflowDefinitionId == workflow.Id);
        step.Label = "Edited step";
        step.VideoAnalyzeConfigJson = """{"changed":true}""";
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = await f.RestoreItemAsync(v1, ProjectVersionItemKind.Workflow, workflow.Id);

        result.Restored.Should().ContainSingle(e => e.Key == workflow.Id.ToString("D"));
        f.Db.ChangeTracker.Clear();
        WorkflowDefinition restored = await f.Db.WorkflowDefinitions
            .AsNoTracking()
            .Include(w => w.Steps)
            .ThenInclude(s => s.AgentDefinition)
            .SingleAsync(w => w.Id == workflow.Id);
        restored.Name.Should().Be("Launch reel");
        restored.RequiresUserInput.Should().BeTrue();
        restored.Steps.Should().ContainSingle();
        restored.Steps.Single().Label.Should().Be("Write");
        restored.Steps.Single().VideoAnalyzeConfigJson.Should().BeNull();
        ProjectHistoryDocuments.Workflow(restored).Should().Be(captured);
    }

    [Fact]
    public async Task Deleted_workflow_is_recreated_with_the_same_id_and_standard_kind()
    {
        using Fixture f = new();
        WorkflowDefinition workflow = f.SeedWorkflow("Gone soon");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        string captured = await f.WorkflowDocumentAsync(v1, workflow.Id);

        f.Db.WorkflowSteps.RemoveRange(f.Db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == workflow.Id));
        f.Db.WorkflowDefinitions.Remove(workflow);
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = await f.RestoreItemAsync(v1, ProjectVersionItemKind.Workflow, workflow.Id);

        result.Restored.Should().ContainSingle(e => e.Key == workflow.Id.ToString("D"));
        f.Db.ChangeTracker.Clear();
        WorkflowDefinition recreated = await f.Db.WorkflowDefinitions
            .AsNoTracking()
            .Include(w => w.Steps)
            .ThenInclude(s => s.AgentDefinition)
            .SingleAsync(w => w.Id == workflow.Id);
        recreated.Kind.Should().Be(WorkflowDefinitionKind.Standard);
        recreated.ProjectId.Should().Be(f.Project.Id);
        recreated.Steps.Should().ContainSingle();
        ProjectHistoryDocuments.Workflow(recreated).Should().Be(captured);
    }

    [Fact]
    public async Task Workflow_whose_agent_is_no_longer_authorized_is_skipped_and_left_alone()
    {
        using Fixture f = new();
        WorkflowDefinition workflow = f.SeedWorkflow("Needs an agent");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        // The agent is no longer built-in and now belongs to another organization.
        f.Agent.IsBuiltIn = false;
        f.Agent.OrganizationId = Guid.NewGuid();
        WorkflowStep liveStep = await f.Db.WorkflowSteps.SingleAsync(s => s.WorkflowDefinitionId == workflow.Id);
        liveStep.Label = "left alone";
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = await f.RestoreItemAsync(v1, ProjectVersionItemKind.Workflow, workflow.Id);

        result.Restored.Should().BeEmpty();
        result.Skipped.Should().ContainSingle(s =>
            s.Kind == ProjectVersionItemKind.Workflow
            && s.Key == workflow.Id.ToString("D")
            && s.Reason == ProjectHistoryRestoreReasons.WorkflowInvalid);
        f.Db.ChangeTracker.Clear();
        (await f.Db.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowDefinitionId == workflow.Id))
            .Label.Should().Be("left alone");
    }

    [Fact]
    public async Task Egress_consent_is_kept_when_the_live_step_already_holds_it()
    {
        using Fixture f = new();
        WorkflowDefinition workflow = f.SeedWorkflow("Egress", egress: true);
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        ProjectHistoryRestoreResult result = await f.RestoreItemAsync(v1, ProjectVersionItemKind.Workflow, workflow.Id);

        result.Notices.Should().BeEmpty();
        f.Db.ChangeTracker.Clear();
        WorkflowStep restored = await f.Db.WorkflowSteps.AsNoTracking()
            .SingleAsync(s => s.WorkflowDefinitionId == workflow.Id);
        AssistantEgressConsentGuard.HasConsent(restored.VideoAnalyzeConfigJson!).Should().BeTrue();
    }

    [Fact]
    public async Task Egress_consent_is_reset_when_the_live_step_no_longer_holds_it()
    {
        using Fixture f = new();
        WorkflowDefinition workflow = f.SeedWorkflow("Egress", egress: true);
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        WorkflowStep liveStep = await f.Db.WorkflowSteps.SingleAsync(s => s.WorkflowDefinitionId == workflow.Id);
        liveStep.VideoAnalyzeConfigJson = """{"allowSourceMediaEgress":false}""";
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = await f.RestoreItemAsync(v1, ProjectVersionItemKind.Workflow, workflow.Id);

        result.Notices.Should().ContainSingle(n =>
            n.Kind == ProjectVersionItemKind.Workflow
            && n.Key == workflow.Id.ToString("D")
            && n.Code == ProjectHistoryRestoreNotices.EgressConsentReset);
        f.Db.ChangeTracker.Clear();
        WorkflowStep restored = await f.Db.WorkflowSteps.AsNoTracking()
            .SingleAsync(s => s.WorkflowDefinitionId == workflow.Id);
        AssistantEgressConsentGuard.HasConsent(restored.VideoAnalyzeConfigJson!).Should().BeFalse();
    }

    [Fact]
    public async Task Egress_consent_is_reset_when_the_live_step_config_differs_apart_from_the_flag()
    {
        using Fixture f = new();
        WorkflowDefinition workflow = f.SeedWorkflow("Egress", egress: true);
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        // The live step still grants consent, but its config is no longer the one the grant was given for.
        WorkflowStep liveStep = await f.Db.WorkflowSteps.SingleAsync(s => s.WorkflowDefinitionId == workflow.Id);
        liveStep.VideoAnalyzeConfigJson = """{"allowSourceMediaEgress":true,"model":"other"}""";
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = await f.RestoreItemAsync(v1, ProjectVersionItemKind.Workflow, workflow.Id);

        result.Notices.Should().ContainSingle(n => n.Code == ProjectHistoryRestoreNotices.EgressConsentReset);
        f.Db.ChangeTracker.Clear();
        WorkflowStep restored = await f.Db.WorkflowSteps.AsNoTracking()
            .SingleAsync(s => s.WorkflowDefinitionId == workflow.Id);
        AssistantEgressConsentGuard.HasConsent(restored.VideoAnalyzeConfigJson!).Should().BeFalse();
    }

    // ──────────────────────────────────────────── timelines ──────────────────────────────────────────

    [Fact]
    public async Task Timeline_round_trip_restores_the_document_as_a_new_revision()
    {
        using Fixture f = new();
        EditTimelineEntity timeline = f.SeedTimeline("Cut", revision: 1);
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        JsonNode captured = await f.TimelineDocumentAsync(v1, timeline.Id);

        // The live timeline moved on: another revision and a different document.
        EditTimeline changed = TimelineRetentionTestKit.Blank(
            f.Project.Id, timeline.Id, "Changed") with
        {
            Settings = TimelineRetentionTestKit.Blank(f.Project.Id, timeline.Id, "Changed").Settings
                with
            { Width = 1280 }
        };
        timeline.DocumentJson = JsonSerializer.Serialize(changed, EditTimelineJson.Options);
        timeline.Name = "Changed";
        timeline.Revision = 2;
        string beforeRestore = timeline.DocumentJson;
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = await f.RestoreItemAsync(v1, ProjectVersionItemKind.Timeline, timeline.Id);

        result.Restored.Should().ContainSingle(e => e.Key == timeline.Id.ToString("D"));
        f.Db.ChangeTracker.Clear();
        EditTimelineEntity restored = await f.Db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timeline.Id);
        restored.Revision.Should().Be(3);
        restored.Name.Should().Be("Cut");
        JsonNode.DeepEquals(JsonNode.Parse(restored.DocumentJson), captured).Should().BeTrue();

        EditTimelineSnapshot snapshot = await f.Db.EditTimelineSnapshots.AsNoTracking()
            .SingleAsync(s => s.TimelineId == timeline.Id && s.Revision == 2);
        snapshot.Cause.Should().Be(EditTimelineSnapshotCause.BeforeRestore);
        JsonNode.DeepEquals(JsonNode.Parse(snapshot.DocumentJson), JsonNode.Parse(beforeRestore)).Should().BeTrue();

        EditTimelineOp op = await f.Db.EditTimelineOps.AsNoTracking()
            .SingleAsync(o => o.TimelineId == timeline.Id && o.Revision == 3);
        op.OpsJson.Should().Contain("\"restore\"");
    }

    [Fact]
    public async Task Soft_deleted_timeline_is_undeleted_and_then_restored()
    {
        using Fixture f = new();
        EditTimelineEntity timeline = f.SeedTimeline("Trashed");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        JsonNode captured = await f.TimelineDocumentAsync(v1, timeline.Id);

        timeline.DeletedAt = DateTime.UtcNow;
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = await f.RestoreItemAsync(v1, ProjectVersionItemKind.Timeline, timeline.Id);

        result.Restored.Should().ContainSingle();
        f.Db.ChangeTracker.Clear();
        EditTimelineEntity restored = await f.Db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timeline.Id);
        restored.DeletedAt.Should().BeNull();
        restored.Revision.Should().Be(2);
        JsonNode.DeepEquals(JsonNode.Parse(restored.DocumentJson), captured).Should().BeTrue();
    }

    [Fact]
    public async Task Hard_deleted_timeline_is_recreated_at_revision_one()
    {
        using Fixture f = new();
        EditTimelineEntity timeline = f.SeedTimeline("Purged");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        JsonNode captured = await f.TimelineDocumentAsync(v1, timeline.Id);

        f.Db.EditTimelines.Remove(timeline);
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = await f.RestoreItemAsync(v1, ProjectVersionItemKind.Timeline, timeline.Id);

        result.Restored.Should().ContainSingle();
        f.Db.ChangeTracker.Clear();
        EditTimelineEntity recreated = await f.Db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timeline.Id);
        recreated.Revision.Should().Be(1);
        recreated.Name.Should().Be("Purged");
        JsonNode.DeepEquals(JsonNode.Parse(recreated.DocumentJson), captured).Should().BeTrue();
        (await f.Db.EditTimelineSnapshots.AsNoTracking().AnyAsync(s => s.TimelineId == timeline.Id && s.Revision == 1))
            .Should().BeTrue();
        (await f.Db.EditTimelineOps.AsNoTracking().AnyAsync(o => o.TimelineId == timeline.Id && o.Revision == 1))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Timeline_that_fails_the_validator_is_skipped()
    {
        using Fixture f = new();
        EditTimelineEntity timeline = f.SeedTimeline("Broken later");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();
        string before = timeline.DocumentJson;

        // Corrupt the stored body without touching the item row: tracks null is a validator violation.
        string sha = v1.Items.Single(i => i.Kind == ProjectVersionItemKind.Timeline).DocumentSha256;
        ProjectHistoryDocument document = await f.Db.ProjectHistoryDocuments
            .SingleAsync(d => d.ProjectId == f.Project.Id && d.Sha256 == sha);
        JsonObject broken = JsonNode.Parse(document.Json)!.AsObject();
        broken["document"]!["tracks"] = null;
        document.Json = broken.ToJsonString();
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = await f.RestoreItemAsync(v1, ProjectVersionItemKind.Timeline, timeline.Id);

        result.Restored.Should().BeEmpty();
        result.Skipped.Should().ContainSingle(s =>
            s.Kind == ProjectVersionItemKind.Timeline
            && s.Key == timeline.Id.ToString("D")
            && s.Reason == ProjectHistoryRestoreReasons.TimelineInvalid);
        f.Db.ChangeTracker.Clear();
        (await f.Db.EditTimelines.AsNoTracking().SingleAsync(t => t.Id == timeline.Id))
            .DocumentJson.Should().Be(before);
    }

    // ────────────────────────────────────────── project scope ────────────────────────────────────────

    [Fact]
    public async Task Project_scope_removes_added_workflow_and_soft_deletes_added_timeline()
    {
        using Fixture f = new();
        WorkflowDefinition keptWorkflow = f.SeedWorkflow("Kept");
        EditTimelineEntity keptTimeline = f.SeedTimeline("Kept cut");
        await f.Db.SaveChangesAsync();
        ProjectVersion v1 = await f.CaptureAsync();

        WorkflowDefinition addedWorkflow = f.SeedWorkflow("Added later");
        EditTimelineEntity addedTimeline = f.SeedTimeline("Added cut");
        await f.Db.SaveChangesAsync();

        ProjectHistoryRestoreResult result = (await f.Restore.RestoreAsync(
            f.Project.Id, v1.Id, ProjectHistoryRestoreScope.Project, NoItems,
            f.Project.OwnerId, CancellationToken.None))!;

        result.Removed.Should().Contain(e => e.Kind == ProjectVersionItemKind.Workflow && e.Key == addedWorkflow.Id.ToString("D"));
        result.Removed.Should().Contain(e => e.Kind == ProjectVersionItemKind.Timeline && e.Key == addedTimeline.Id.ToString("D"));
        // All six kinds have a handler, so a clean project scope leaves nothing skipped.
        result.Skipped.Should().BeEmpty();
        f.Db.ChangeTracker.Clear();
        (await f.Db.WorkflowDefinitions.AsNoTracking().AnyAsync(w => w.Id == addedWorkflow.Id)).Should().BeFalse();
        (await f.Db.WorkflowDefinitions.AsNoTracking().AnyAsync(w => w.Id == keptWorkflow.Id)).Should().BeTrue();
        (await f.Db.EditTimelines.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == addedTimeline.Id))
            .DeletedAt.Should().NotBeNull();
        (await f.Db.EditTimelines.AsNoTracking().AnyAsync(t => t.Id == keptTimeline.Id)).Should().BeTrue();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection _connection;

        public Fixture()
        {
            _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
            _connection.Open();

            DbContextOptionsBuilder<InferenceApiDbContext> builder = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseSqlite(_connection)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            Db = new InferenceApiDbContext(builder.Options);
            Db.Database.EnsureCreated();

            // The API context treats the workflow tables as engine-owned (ExcludeFromMigrations), so
            // EnsureCreated does not make them; the engine model does.
            using (WorkflowEngineDbContext engine = new(
                       new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options))
            {
                engine.GetService<IRelationalDatabaseCreator>().CreateTables();
            }

            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "P",
                OwnerId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid()
            };
            Agent = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Writer",
                Description = "d",
                SystemPrompt = "p",
                AgentType = AgentType.AuthorAgent,
                IsBuiltIn = true
            };
            Db.Projects.Add(Project);
            Db.AgentDefinitions.Add(Agent);
            Db.SaveChanges();

            Service = new ProjectHistoryService(
                Db, Store, Options.Create(new ProjectHistoryOptions()),
                NullLogger<ProjectHistoryService>.Instance);
            Restore = new ProjectHistoryRestoreService(
                Db, Service, InProcessProjectRestoreLock.Shared,
                [
                    new FileRestoreKindHandler(Db, Store),
                    new ProjectRestoreKindHandler(Db),
                    new PronunciationRestoreKindHandler(Db),
                    new DesignSystemRestoreKindHandler(Db),
                    new WorkflowRestoreKindHandler(Db, new WorkflowEditDiffService(Db)),
                    new TimelineRestoreKindHandler(Db, Store, NullLogger<TimelineRestoreKindHandler>.Instance)
                ],
                NullLogger<ProjectHistoryRestoreService>.Instance);
        }

        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public AgentDefinition Agent { get; }
        public HistoryTestStore Store { get; } = new();
        public ProjectHistoryService Service { get; }
        public ProjectHistoryRestoreService Restore { get; }

        public WorkflowDefinition SeedWorkflow(string name, string label = "Write", bool egress = false)
        {
            WorkflowDefinition workflow = new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                ProjectId = Project.Id,
                RequiresUserInput = true,
                CreatedVia = WorkflowCreatedVia.User
            };
            workflow.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                AgentDefinitionId = Agent.Id,
                StepOrder = 1,
                StepType = StepType.Agent,
                Label = label,
                VideoAnalyzeConfigJson = egress ? """{"allowSourceMediaEgress":true}""" : null
            });
            Db.WorkflowDefinitions.Add(workflow);
            return workflow;
        }

        public EditTimelineEntity SeedTimeline(string name, int revision = 1)
        {
            Guid id = Guid.NewGuid();
            EditTimeline document = TimelineRetentionTestKit.Blank(Project.Id, id, name);
            EditTimelineEntity row = new()
            {
                Id = id,
                ProjectId = Project.Id,
                Name = name,
                DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options),
                Revision = revision,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.EditTimelines.Add(row);
            return row;
        }

        public async Task<ProjectVersion> CaptureAsync() =>
            (await Service.CaptureAsync(
                Project.Id, ProjectVersionCause.Named, Guid.NewGuid().ToString("N")[..8],
                null, CancellationToken.None))!;

        public async Task<ProjectHistoryRestoreResult> RestoreItemAsync(
            ProjectVersion version, ProjectVersionItemKind kind, Guid key) =>
            (await Restore.RestoreAsync(
                Project.Id, version.Id, ProjectHistoryRestoreScope.Item,
                [new(kind, key.ToString("D"))], Project.OwnerId, CancellationToken.None))!;

        public async Task<string> WorkflowDocumentAsync(ProjectVersion version, Guid workflowId) =>
            await DocumentAsync(version, ProjectVersionItemKind.Workflow, workflowId);

        public async Task<JsonNode> TimelineDocumentAsync(ProjectVersion version, Guid timelineId) =>
            JsonNode.Parse(await DocumentAsync(version, ProjectVersionItemKind.Timeline, timelineId))!["document"]!;

        private async Task<string> DocumentAsync(
            ProjectVersion version, ProjectVersionItemKind kind, Guid key)
        {
            ProjectVersionItem item = version.Items.Single(
                i => i.Kind == kind && i.ItemKey == key.ToString("D"));
            return await Db.ProjectHistoryDocuments
                .AsNoTracking()
                .Where(d => d.ProjectId == Project.Id && d.Sha256 == item.DocumentSha256)
                .Select(d => d.Json)
                .SingleAsync();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }
}
