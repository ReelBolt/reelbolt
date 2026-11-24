#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Activity;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>GET /api/v1/activity: one workspace read across job kinds (no migration, read-only).</summary>
public class ActivityFeedTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fake(Guid userId, Guid? org, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = org;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private readonly Clock _clock = new();
    private readonly InferenceApiDbContext _db =
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _project = Guid.NewGuid();
    private DateTime Now => _clock.Now.UtcDateTime;

    public ActivityFeedTests() => AddProject(_project, _org, "Launch film");

    private void AddProject(Guid id, Guid org, string name)
    {
        _db.Projects.Add(new Project { Id = id, Name = name, OrganizationId = org, OwnerId = _user });
        _db.SaveChanges();
    }

    private ActivityFeedService Service(OrganizationRole? role = OrganizationRole.Member, bool billing = false, Guid? org = null, bool noOrg = false) =>
        new(_db, new Fake(_user, noOrg ? null : org ?? _org, role), Options.Create(new BillingOptions { Enabled = billing }), _clock);

    private async Task<ActivityFeedDto> Feed(int limit = 100, OrganizationRole? role = OrganizationRole.Member, bool billing = false) =>
        await Service(role, billing).GetAsync(limit, default);

    private (Guid Exec, Guid Def) AddRun(
        WorkflowDefinitionKind kind = WorkflowDefinitionKind.Standard,
        ExecutionStatus status = ExecutionStatus.Running,
        string name = "My workflow",
        string? templateKey = null,
        DateTime? queued = null,
        DateTime? completed = null,
        Guid? org = null,
        Guid? project = null,
        Guid? initiatedBy = null,
        string? stepConfig = null,
        int steps = 3,
        int currentStepOrder = 0)
    {
        Guid def = Guid.NewGuid();
        Guid proj = project ?? _project;
        _db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = def, ProjectId = proj, Name = name, Kind = kind, TemplateKey = templateKey });
        Guid? current = null;
        for (int i = 1; i <= steps; i++)
        {
            Guid sid = Guid.NewGuid();
            if (i == currentStepOrder) current = sid;
            _db.WorkflowSteps.Add(new WorkflowStep
            {
                Id = sid,
                WorkflowDefinitionId = def,
                StepOrder = i,
                StepType = kind == WorkflowDefinitionKind.TimelineRender ? StepType.TimelineRender : StepType.Agent,
                TimelineRenderConfigJson = kind == WorkflowDefinitionKind.TimelineRender && i == 1 ? stepConfig : null
            });
        }

        Guid exec = Guid.NewGuid();
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = exec,
            WorkflowDefinitionId = def,
            ProjectId = proj,
            OrganizationId = org ?? _org,
            Status = status,
            QueuedAt = queued ?? Now.AddMinutes(-5),
            StartedAt = status == ExecutionStatus.Queued ? null : queued ?? Now.AddMinutes(-5),
            CompletedAt = completed,
            CurrentStepId = current,
            InitiatedByUserId = initiatedBy
        });
        _db.SaveChanges();
        return (exec, def);
    }

    private static string RenderConfig(Guid timeline, string? audioJobKind = null) =>
        JsonSerializer.Serialize(new TimelineRenderStepConfig(timeline, AudioJobKind: audioJobKind),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } });

    private static ActivityItemDto Of(ActivityFeedDto feed, Guid exec) => feed.Items.Single(i => i.ExecutionId == exec);

    [Fact]
    public async Task Another_orgs_run_is_never_listed()
    {
        Guid otherProject = Guid.NewGuid();
        AddProject(otherProject, Guid.NewGuid(), "Not yours");
        AddRun(org: Guid.NewGuid(), project: otherProject);
        (Guid mine, _) = AddRun();

        ActivityFeedDto feed = await Feed();

        feed.Items.Select(i => i.ExecutionId).Should().Equal(mine);
        feed.ActiveCount.Should().Be(1);
    }

    [Fact]
    public async Task Billing_role_and_no_workspace_get_an_empty_list()
    {
        AddRun();

        (await Feed(role: OrganizationRole.Billing)).Should().BeEquivalentTo(new { Items = Array.Empty<ActivityItemDto>(), ActiveCount = 0 });
        (await Service(noOrg: true).GetAsync(50, default)).Items.Should().BeEmpty();
        (await Service(role: null).GetAsync(50, default)).Items.Should().BeEmpty();
        (await Feed(role: OrganizationRole.Owner)).Items.Should().HaveCount(1);
        (await Feed(role: OrganizationRole.Admin)).Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Every_job_kind_maps_to_its_type_title_and_subject()
    {
        Guid timeline = Guid.NewGuid();
        _db.EditTimelines.Add(new EditTimelineEntity { Id = timeline, ProjectId = _project, Name = "Main cut" });
        Guid doc = Guid.NewGuid();
        _db.EditImageDocuments.Add(new EditImageDocument { Id = doc, ProjectId = _project, Name = "Poster" });
        _db.SaveChanges();

        (Guid run, _) = AddRun(name: "Promo");
        (Guid build, _) = AddRun(name: "Profile", templateKey: WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey);
        (Guid rev, _) = AddRun(WorkflowDefinitionKind.CompositionRevision, name: "Revise");
        Guid revSource = Guid.NewGuid();
        _db.CompositionRevisions.Add(new CompositionRevision { Id = Guid.NewGuid(), ProjectId = _project, OrganizationId = _org, ExecutionId = rev, SourceExecutionId = revSource });
        (Guid change, _) = AddRun(WorkflowDefinitionKind.CompositionReplay, name: "Replay");
        Guid changeSource = Guid.NewGuid();
        _db.StepChangeRequests.Add(new StepChangeRequest { Id = Guid.NewGuid(), ProjectId = _project, OrganizationId = _org, ExecutionId = change, SourceExecutionId = changeSource });
        (Guid replayRev, _) = AddRun(WorkflowDefinitionKind.CompositionReplay, name: "Replay 2");
        _db.CompositionRevisions.Add(new CompositionRevision { Id = Guid.NewGuid(), ProjectId = _project, OrganizationId = _org, ExecutionId = replayRev, SourceExecutionId = revSource });
        (Guid graphic, _) = AddRun(WorkflowDefinitionKind.GraphicDesign, name: "Design");
        _db.EditImageGraphicRequests.Add(new EditImageGraphicRequest { Id = Guid.NewGuid(), ProjectId = _project, OrganizationId = _org, ExecutionId = graphic, DocumentId = doc });
        (Guid render, _) = AddRun(WorkflowDefinitionKind.TimelineRender, name: "Render: x", stepConfig: RenderConfig(timeline), steps: 1);
        (Guid preview, _) = AddRun(WorkflowDefinitionKind.TimelineRender, stepConfig: RenderConfig(timeline, "preview"), steps: 1);
        (Guid loudness, _) = AddRun(WorkflowDefinitionKind.TimelineRender, stepConfig: RenderConfig(timeline, "loudness"), steps: 1);
        (Guid audition, _) = AddRun(WorkflowDefinitionKind.TimelineRender, stepConfig: RenderConfig(timeline, "audition"), steps: 1);
        (Guid objects, _) = AddRun(WorkflowDefinitionKind.ObjectAnalysis, name: "hidden");
        Guid file = Guid.NewGuid();
        _db.ObjectEffectRequests.Add(new ObjectEffectRequest
        {
            Id = Guid.NewGuid(),
            ProjectId = _project,
            OrganizationId = _org,
            ExecutionId = objects,
            Prompt = "the red car",
            SourceKind = ObjectEffectRequest.SourceFile,
            SourceId = file.ToString(),
            TimelineId = timeline
        });
        (Guid cleanup, _) = AddRun(WorkflowDefinitionKind.AudioCleanup, name: "cleanup");
        (Guid clip, _) = AddRun(WorkflowDefinitionKind.SubjectClip, name: "Avatar clip: Anna, Espresso machine");
        (Guid unknown, _) = AddRun((WorkflowDefinitionKind)99, name: "mystery");
        _db.SaveChanges();

        ActivityFeedDto feed = await Feed();

        ActivityItemDto i = Of(feed, run);
        i.Type.Should().Be("workflowRun");
        i.Title.Should().Be("Promo");
        i.ProjectName.Should().Be("Launch film");
        i.WorkflowId.Should().NotBeNull();
        Of(feed, build).Type.Should().Be("designSystemBuild");
        Of(feed, rev).Type.Should().Be("revision");
        Of(feed, rev).Subject.SourceExecutionId.Should().Be(revSource);
        Of(feed, change).Type.Should().Be("changeRequest");
        Of(feed, change).Subject.SourceExecutionId.Should().Be(changeSource);
        Of(feed, replayRev).Type.Should().Be("revision");
        Of(feed, graphic).Type.Should().Be("graphic");
        Of(feed, graphic).Subject.DocumentId.Should().Be(doc);
        Of(feed, graphic).Title.Should().Be("Poster");
        Of(feed, render).Type.Should().Be("render");
        Of(feed, render).Title.Should().Be("Main cut", "the title is the timeline name, not the internal workflow name");
        Of(feed, render).Subject.TimelineId.Should().Be(timeline);
        Of(feed, preview).Type.Should().Be("preview");
        Of(feed, loudness).Type.Should().Be("loudness");
        Of(feed, audition).Type.Should().Be("audition");
        Of(feed, objects).Type.Should().Be("objectAnalysis");
        Of(feed, objects).Title.Should().Be("the red car");
        Of(feed, objects).Subject.MediaRef.Should().Be($"file:{file}");
        Of(feed, cleanup).Type.Should().Be("audioCleanup");
        Of(feed, clip).Type.Should().Be("subjectClip", "the web's own job type for a clip with avatars");
        Of(feed, clip).Title.Should().Be("Anna, Espresso machine", "titled with the names it shows, as the editor tracks it");
        Of(feed, unknown).Type.Should().Be("hiddenRun");
        Of(feed, unknown).Subject.KindNumber.Should().Be(99);
        Of(feed, unknown).Title.Should().BeNull();
    }

    [Fact]
    public async Task A_snapshot_definition_is_never_listed_and_the_run_is_listed_once_under_its_source()
    {
        (Guid exec, Guid def) = AddRun(name: "Source flow");
        Guid snapshot = Guid.NewGuid();
        _db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = snapshot, ProjectId = _project, Name = "snap", Kind = WorkflowDefinitionKind.ExecutionSnapshot });
        _db.WorkflowExecutions.Single(e => e.Id == exec).SnapshotDefinitionId = snapshot;
        _db.SaveChanges();
        // A stray row that points at the snapshot kind is hidden too.
        AddRun(WorkflowDefinitionKind.ExecutionSnapshot);

        ActivityFeedDto feed = await Feed();

        feed.Items.Should().ContainSingle().Which.WorkflowId.Should().Be(def);
    }

    [Fact]
    public async Task Window_keeps_old_running_work_and_drops_old_finished_work()
    {
        (Guid oldRunning, _) = AddRun(queued: Now.AddDays(-6));
        (Guid tooOldRunning, _) = AddRun(queued: Now.AddDays(-8));
        (Guid freshDone, _) = AddRun(status: ExecutionStatus.Passed, queued: Now.AddHours(-30), completed: Now.AddHours(-23));
        (Guid oldDone, _) = AddRun(status: ExecutionStatus.Passed, queued: Now.AddHours(-30), completed: Now.AddHours(-25));

        ActivityFeedDto feed = await Feed();

        feed.Items.Select(i => i.ExecutionId).Should().BeEquivalentTo([oldRunning, freshDone]);
        feed.Items.Select(i => i.ExecutionId).Should().NotContain([tooOldRunning, oldDone]);
        feed.ActiveCount.Should().Be(1);
    }

    [Fact]
    public async Task Order_is_running_then_waiting_oldest_first_then_finished_newest_first_and_limit_applies_after_ordering()
    {
        (Guid done1, _) = AddRun(status: ExecutionStatus.Passed, completed: Now.AddHours(-3));
        (Guid done2, _) = AddRun(status: ExecutionStatus.Failed, completed: Now.AddHours(-1));
        (Guid waitingNew, _) = AddRun(status: ExecutionStatus.Queued, queued: Now.AddMinutes(-1));
        (Guid waitingOld, _) = AddRun(status: ExecutionStatus.Queued, queued: Now.AddMinutes(-30));
        (Guid running, _) = AddRun(status: ExecutionStatus.Running, queued: Now.AddMinutes(-10));

        ActivityFeedDto feed = await Feed();
        feed.Items.Select(i => i.ExecutionId).Should().Equal(running, waitingOld, waitingNew, done2, done1);
        feed.Items.Select(i => i.State).Should().Equal("running", "waiting", "waiting", "failed", "done");

        ActivityFeedDto top2 = await Feed(limit: 2);
        top2.Items.Select(i => i.ExecutionId).Should().Equal(running, waitingOld);
        top2.ActiveCount.Should().Be(3, "the active count is taken before the limit");

        ActivityFeedService.ClampLimit(null).Should().Be(50);
        ActivityFeedService.ClampLimit(0).Should().Be(1);
        ActivityFeedService.ClampLimit(-4).Should().Be(1);
        ActivityFeedService.ClampLimit(1000).Should().Be(100);
        ActivityFeedService.ClampLimit(7).Should().Be(7);
    }

    [Fact]
    public async Task Step_cancel_and_started_by_you_are_set_from_the_run_and_the_role()
    {
        (Guid mine, _) = AddRun(initiatedBy: _user, steps: 4, currentStepOrder: 2);
        (Guid theirs, _) = AddRun(initiatedBy: Guid.NewGuid(), status: ExecutionStatus.Queued);
        (Guid done, _) = AddRun(status: ExecutionStatus.Passed, completed: Now.AddMinutes(-1));

        ActivityFeedDto feed = await Feed();
        ActivityItemDto m = Of(feed, mine);
        m.StartedByYou.Should().BeTrue();
        m.Cancellable.Should().BeTrue();
        m.Step.Should().BeEquivalentTo(new ActivityStepDto(2, 4, "Agent"));
        Of(feed, theirs).StartedByYou.Should().BeFalse();
        Of(feed, done).Cancellable.Should().BeFalse();
        Of(feed, done).Step.Should().BeNull();
    }

    [Fact]
    public async Task Failure_code_and_refund_come_from_the_failed_step_and_credits_are_null_with_billing_off()
    {
        (Guid platform, Guid defA) = AddRun(status: ExecutionStatus.Failed, completed: Now.AddMinutes(-1));
        (Guid user, Guid defB) = AddRun(status: ExecutionStatus.Failed, completed: Now.AddMinutes(-2));
        AddFailedResult(platform, defA, FailureClass.PlatformProviderError, "PROVIDER_DOWN", 1500);
        AddFailedResult(user, defB, FailureClass.UserInput, "BAD_INPUT", 2500);
        // A superseded result is not part of the run.
        _db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = user,
            WorkflowStepId = Guid.NewGuid(),
            CreditsChargedMillis = 99000,
            SupersededAt = Now
        });
        _db.SaveChanges();

        ActivityFeedDto on = await Feed(billing: true);
        Of(on, platform).Failure.Should().BeEquivalentTo(new ActivityFailureDto("PROVIDER_DOWN", true));
        Of(on, user).Failure.Should().BeEquivalentTo(new ActivityFailureDto("BAD_INPUT", false));
        Of(on, platform).Credits.ChargedCredits.Should().Be(1.5);
        Of(on, user).Credits.ChargedCredits.Should().Be(2.5);

        ActivityFeedDto off = await Feed(billing: false);
        Of(off, platform).Credits.Should().BeEquivalentTo(new ActivityCreditsDto(null, null));
        Of(off, platform).Failure.Should().BeEquivalentTo(new ActivityFailureDto("PROVIDER_DOWN", null));
    }

    private void AddFailedResult(Guid exec, Guid def, FailureClass cls, string code, long millis)
    {
        Guid step = _db.WorkflowSteps.First(s => s.WorkflowDefinitionId == def).Id;
        _db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = exec,
            WorkflowStepId = step,
            Status = StepStatus.Failed,
            FailureClass = cls,
            FailureCode = code,
            CreditsChargedMillis = millis,
            ExecutedAt = Now.AddMinutes(-3)
        });
        _db.SaveChanges();
    }

    [Fact]
    public void Refund_rule_matches_the_engines_for_every_failure_class()
    {
        foreach (FailureClass c in Enum.GetValues<FailureClass>())
            ActivityFailureRefund.IsRefunded(c).Should().Be(CreditGate.IsRefundable(c), c.ToString());
    }

    [Fact]
    public async Task Media_analysis_is_listed_with_the_file_name_and_the_hold_while_active()
    {
        Guid file = Guid.NewGuid();
        _db.ProjectFiles.Add(new ProjectFile { Id = file, ProjectId = _project, OriginalFileName = "interview.mp4", StorageKey = "secret/key/interview.mp4" });
        Guid running = Guid.NewGuid(), ready = Guid.NewGuid(), oldReady = Guid.NewGuid(), foreign = Guid.NewGuid();
        MediaAnalysisRequest Row(Guid id, string state, DateTime updated, Guid? org = null, string? mediaRef = null) => new()
        {
            Id = id,
            RequestId = Guid.NewGuid(),
            ProjectId = _project,
            OrganizationId = org ?? _org,
            MediaRef = mediaRef ?? $"file:{file}",
            Kind = MediaAnalysisKinds.Transcript,
            State = state,
            HoldMillis = 4000,
            CreatedAt = updated.AddMinutes(-2),
            UpdatedAt = updated,
            RequestedByUserId = _user
        };
        _db.MediaAnalysisRequests.AddRange(
            Row(running, MediaAnalysisStates.Running, Now.AddMinutes(-1)),
            Row(ready, MediaAnalysisStates.Ready, Now.AddHours(-1), mediaRef: $"step_result:{Guid.NewGuid()}"),
            Row(oldReady, MediaAnalysisStates.Ready, Now.AddHours(-30)),
            Row(foreign, MediaAnalysisStates.Running, Now.AddMinutes(-1), org: Guid.NewGuid()));
        _db.SaveChanges();

        ActivityFeedDto feed = await Feed(billing: true);

        feed.Items.Select(i => i.Id).Should().Equal($"analysis:{running}", $"analysis:{ready}");
        ActivityItemDto r = feed.Items[0];
        r.Type.Should().Be("mediaAnalysis");
        r.State.Should().Be("running");
        r.Title.Should().Be("interview.mp4");
        r.Subject.AnalysisKind.Should().Be("transcript");
        r.Credits.HeldCredits.Should().Be(4);
        r.Cancellable.Should().BeFalse();
        r.StartedByYou.Should().BeTrue();
        feed.Items[1].Title.Should().BeNull("a step-result reference has no file name");
        feed.Items[1].State.Should().Be("done");
        feed.ActiveCount.Should().Be(1);
        JsonSerializer.Serialize(feed).Should().NotContain("secret/key");
    }

    [Fact]
    public async Task Held_credits_of_a_run_are_its_open_reserves_and_null_with_billing_off()
    {
        (Guid open, _) = AddRun();
        (Guid closed, _) = AddRun();
        Guid openReserve = Guid.NewGuid(), closedReserve = Guid.NewGuid();
        CreditLedgerEntry Entry(Guid id, CreditLedgerEntryKind kind, Guid exec, long reserved, Guid? refId = null) => new()
        {
            Id = id,
            OrganizationId = _org,
            AccountId = Guid.NewGuid(),
            Kind = kind,
            ExecutionId = exec,
            ReservedDeltaMillis = reserved,
            RefEntryId = refId,
            IdempotencyKey = Guid.NewGuid().ToString()
        };
        _db.CreditLedgerEntries.AddRange(
            Entry(openReserve, CreditLedgerEntryKind.Reserve, open, 2500),
            Entry(closedReserve, CreditLedgerEntryKind.Reserve, closed, 9000),
            Entry(Guid.NewGuid(), CreditLedgerEntryKind.Settle, closed, -9000, closedReserve));
        _db.SaveChanges();

        ActivityFeedDto on = await Feed(billing: true);
        Of(on, open).Credits.HeldCredits.Should().Be(2.5);
        Of(on, closed).Credits.HeldCredits.Should().BeNull();
        (await Feed(billing: false)).Items.Should().OnlyContain(i => i.Credits.HeldCredits == null);
    }
}
