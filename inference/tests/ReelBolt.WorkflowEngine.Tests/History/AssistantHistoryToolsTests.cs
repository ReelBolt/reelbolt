using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>Project-versioning contract section 10: the assistant history tools, their card and the MCP rule.</summary>
public sealed class AssistantHistoryToolsTests : IDisposable
{
    private static readonly Regex AnyGuid = new(@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Project _project;
    private readonly Mock<IProjectHistoryService> _history = new();
    private readonly Mock<IProjectHistoryRestoreService> _restore = new();
    private readonly HashSet<ProjectPermission> _allowed = [ProjectPermission.View, ProjectPermission.Edit, ProjectPermission.Delete];
    private readonly Guid _versionId = Guid.NewGuid();
    private readonly Guid _safetyId = Guid.NewGuid();
    private readonly Guid _fileA = Guid.NewGuid();
    private readonly Guid _fileB = Guid.NewGuid();
    private readonly Guid _workflowId = Guid.NewGuid();

    public AssistantHistoryToolsTests()
    {
        _project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Launch Teaser",
            OwnerId = _userId,
            OrganizationId = TestTenancy.OrgOf(_userId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Projects.Add(_project);
        _db.ApplicationUsers.Add(new ApplicationUser { Id = _userId, Email = "t@example.com", DisplayName = "T", AssistantAutoApprove = true });
        _db.SaveChanges();

        ProjectHistoryVersionInfo v3 = Info(_versionId, 3, "Before the rewrite");
        ProjectHistoryVersionInfo safety = Info(_safetyId, 4, null);
        _history.Setup(h => h.ListVersionsAsync(_project.Id, It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, int _, int? before, CancellationToken _) =>
            {
                List<ProjectHistoryVersionInfo> all = [safety, v3];
                return new ProjectHistoryVersionsPage(
                    all.Where(v => before is null || v.Number < before).Take(1 + (before is null ? 5 : 0)).ToList(), 0, 30);
            });
        _history.Setup(h => h.GetVersionAsync(_project.Id, _versionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectHistoryVersionDetail(v3,
            [
                Item(ProjectVersionItemKind.File, _fileA.ToString("D"), "docs/readme.md"),
                Item(ProjectVersionItemKind.File, _fileB.ToString("D"), "notes/readme.md"),
                Item(ProjectVersionItemKind.Workflow, _workflowId.ToString("D"), "Intro"),
                Item(ProjectVersionItemKind.DesignSystem, "design-system", null)
            ]));
        _history.Setup(h => h.GetVersionInfoAsync(_project.Id, _safetyId, It.IsAny<CancellationToken>())).ReturnsAsync(safety);
        _history.Setup(h => h.CaptureAsync(_project.Id, ProjectVersionCause.Named, It.IsAny<string?>(), _userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectVersion { Id = Guid.NewGuid(), ProjectId = _project.Id, Number = 5, Name = "Milestone", ItemCount = 4 });
        _history.Setup(h => h.ComputeChangesAsync(_project.Id, _versionId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectHistoryChanges(
                [new ProjectHistoryItemChange(ProjectVersionItemKind.File, _fileA.ToString("D"), "docs/readme.md", null, "changed")], 3, false));
        _restore.Setup(r => r.PreviewAsync(_project.Id, _versionId, It.IsAny<ProjectHistoryRestoreScope>(),
                It.IsAny<IReadOnlyList<ProjectHistoryRestoreItemRef>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectHistoryRestorePreview(ProjectHistoryRestoreScope.Item,
            [
                new ProjectHistoryItemChange(ProjectVersionItemKind.File, _fileA.ToString("D"), "docs/readme.md", null, "changed"),
                new ProjectHistoryItemChange(ProjectVersionItemKind.File, _fileB.ToString("D"), "notes/old.md", null, "added"),
                new ProjectHistoryItemChange(ProjectVersionItemKind.Workflow, _workflowId.ToString("D"), "Intro", null, "removed")
            ], [], 0));
        _restore.Setup(r => r.RestoreAsync(_project.Id, _versionId, It.IsAny<ProjectHistoryRestoreScope>(),
                It.IsAny<IReadOnlyList<ProjectHistoryRestoreItemRef>>(), _userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectHistoryRestoreResult(_safetyId,
                [new ProjectHistoryRestoreEntry(ProjectVersionItemKind.File, _fileA.ToString("D"), "docs/readme.md")],
                [], [], []));
    }

    public void Dispose() => _db.Dispose();

    private static ProjectHistoryVersionInfo Info(Guid id, int number, string? name) =>
        new(id, number, name, name is null ? "auto" : "named", DateTime.UtcNow, null, "Tom", 4, 1024);

    private static ProjectHistoryItemInfo Item(ProjectVersionItemKind kind, string key, string? name) =>
        new(kind, key, name, null, null, 10, true, null);

    private IProjectAccessService Access()
    {
        var access = new Mock<IProjectAccessService>();
        access.Setup(a => a.Get(It.IsAny<Guid>(), It.IsAny<ProjectPermission>()))
            .Returns((Guid id, ProjectPermission p) =>
                id == _project.Id ? Result(p) : new ProjectAccessResult(ProjectAccessOutcome.Forbidden, null));
        access.Setup(a => a.GetAsync(It.IsAny<Guid>(), It.IsAny<ProjectPermission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, ProjectPermission p, CancellationToken _) =>
                id == _project.Id ? Result(p) : new ProjectAccessResult(ProjectAccessOutcome.Forbidden, null));
        return access.Object;
    }

    private ProjectAccessResult Result(ProjectPermission p) => _allowed.Contains(p)
        ? new ProjectAccessResult(ProjectAccessOutcome.Found, _project)
        : new ProjectAccessResult(ProjectAccessOutcome.Forbidden, null);

    private AssistantHistoryTools Tools(IConfirmationTokenService? tokens = null) =>
        new(tokens ?? ConfirmationTokenService.CreateEphemeral(), Access(), _history.Object, _restore.Object);

    private async Task<string> CallAsync(
        AssistantHistoryTools tools, ExecutionInitiator initiator, string name, params (string, object?)[] args)
    {
        AIFunction fn = tools.GetTools(new AssistantToolContext(_userId, _project.Id, Initiator: initiator))
            .Single(t => t.Name == name);
        var a = new AIFunctionArguments();
        foreach ((string k, object? v) in args)
            a[k] = v;
        object? result = await fn.InvokeAsync(a);
        return result is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString()! : result?.ToString() ?? string.Empty;
    }

    private static string Token(string json) => JsonDocument.Parse(json).RootElement.GetProperty("confirmationToken").GetString()!;

    [Fact]
    public async Task List_and_changes_name_things_and_carry_no_id()
    {
        AssistantHistoryTools tools = Tools();
        string list = await CallAsync(tools, ExecutionInitiator.Assistant, "ListProjectVersions");
        string changes = await CallAsync(tools, ExecutionInitiator.Assistant, "GetProjectChanges", ("fromVersion", 3));

        list.Should().Contain("Before the rewrite");
        changes.Should().Contain("docs/readme.md").And.Contain("\"unchangedCount\":3");
        AnyGuid.IsMatch(list).Should().BeFalse();
        AnyGuid.IsMatch(changes).Should().BeFalse();
    }

    [Fact]
    public async Task Save_proposes_then_captures_a_named_version()
    {
        AssistantHistoryTools tools = Tools();
        string proposal = await CallAsync(tools, ExecutionInitiator.Assistant, "ProposeSaveProjectVersion", ("name", "Milestone"));
        string applied = await CallAsync(tools, ExecutionInitiator.Assistant, "ApplySaveProjectVersion",
            ("name", "Milestone"), ("confirmationToken", Token(proposal)));

        applied.Should().Contain("\"versionNumber\":5");
        _history.Verify(h => h.CaptureAsync(_project.Id, ProjectVersionCause.Named, "Milestone", _userId, It.IsAny<CancellationToken>()), Times.Once);
        AnyGuid.IsMatch(applied).Should().BeFalse();
    }

    [Fact]
    public async Task Save_without_edit_authority_writes_nothing()
    {
        _allowed.Remove(ProjectPermission.Edit);
        AssistantHistoryTools tools = Tools();

        string proposal = await CallAsync(tools, ExecutionInitiator.Assistant, "ProposeSaveProjectVersion", ("name", "Milestone"));
        string applied = await CallAsync(tools, ExecutionInitiator.Assistant, "ApplySaveProjectVersion",
            ("name", "Milestone"), ("confirmationToken", "x"));

        proposal.Should().Contain("error");
        applied.Should().Contain("error");
        _history.Verify(h => h.CaptureAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Item_restore_resolves_names_server_side_and_calls_the_restore_service()
    {
        AssistantHistoryTools tools = Tools();
        string[] items = ["docs/readme.md", "workflow: Intro", "design system"];
        string proposal = await CallAsync(tools, ExecutionInitiator.Assistant, "ProposeRestoreProjectVersion",
            ("version", 3), ("scope", "item"), ("items", items));
        string applied = await CallAsync(tools, ExecutionInitiator.Assistant, "ApplyRestoreProjectVersion",
            ("version", 3), ("scope", "item"), ("items", items), ("confirmationToken", Token(proposal)));

        _restore.Verify(r => r.RestoreAsync(_project.Id, _versionId, ProjectHistoryRestoreScope.Item,
            It.Is<IReadOnlyList<ProjectHistoryRestoreItemRef>>(l =>
                l.Count == 3
                && l.Any(i => i.Kind == ProjectVersionItemKind.File && i.Key == _fileA.ToString("D"))
                && l.Any(i => i.Kind == ProjectVersionItemKind.DesignSystem && i.Key == "design-system")),
            _userId, It.IsAny<CancellationToken>()), Times.Once);
        applied.Should().Contain("\"safetyVersionNumber\":4").And.Contain("docs/readme.md");
        AnyGuid.IsMatch(proposal).Should().BeFalse();
        AnyGuid.IsMatch(applied).Should().BeFalse();
    }

    [Fact]
    public async Task An_ambiguous_item_name_is_refused_listing_the_candidates_by_name()
    {
        string text = await CallAsync(Tools(), ExecutionInitiator.Assistant, "ProposeRestoreProjectVersion",
            ("version", 3), ("scope", "item"), ("items", new[] { "readme.md" }));

        text.Should().Contain("more than one").And.Contain("docs/readme.md").And.Contain("notes/readme.md");
        AnyGuid.IsMatch(text).Should().BeFalse();
    }

    [Fact]
    public async Task Project_restore_needs_delete_authority_and_writes_nothing_without_it()
    {
        _allowed.Remove(ProjectPermission.Delete);
        AssistantHistoryTools tools = Tools();

        string proposal = await CallAsync(tools, ExecutionInitiator.Assistant, "ProposeRestoreProjectVersion",
            ("version", 3), ("scope", "project"));
        string applied = await CallAsync(tools, ExecutionInitiator.Assistant, "ApplyRestoreProjectVersion",
            ("version", 3), ("scope", "project"), ("confirmationToken", "x"));

        proposal.Should().Contain("error");
        applied.Should().Contain("error");
        _restore.Verify(r => r.RestoreAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ProjectHistoryRestoreScope>(),
            It.IsAny<IReadOnlyList<ProjectHistoryRestoreItemRef>>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Item_restore_needs_only_edit_authority()
    {
        _allowed.Remove(ProjectPermission.Delete);
        string proposal = await CallAsync(Tools(), ExecutionInitiator.Assistant, "ProposeRestoreProjectVersion",
            ("version", 3), ("scope", "item"), ("items", new[] { "docs/readme.md" }));

        proposal.Should().Contain("confirmationToken");
    }

    [Fact]
    public async Task Over_mcp_a_project_scope_restore_is_refused_and_items_are_allowed()
    {
        AssistantHistoryTools tools = Tools();

        string project = await CallAsync(tools, ExecutionInitiator.Mcp, "ProposeRestoreProjectVersion",
            ("version", 3), ("scope", "project"));
        string projectApply = await CallAsync(tools, ExecutionInitiator.Mcp, "ApplyRestoreProjectVersion",
            ("version", 3), ("scope", "project"), ("confirmationToken", "x"));
        string items = await CallAsync(tools, ExecutionInitiator.Mcp, "ProposeRestoreProjectVersion",
            ("version", 3), ("scope", "item"), ("items", new[] { "docs/readme.md" }));

        project.Should().Contain(AssistantHistoryTools.McpProjectScopeMessage[..30]);
        projectApply.Should().Contain("\"ok\":false");
        items.Should().Contain("confirmationToken");
        _restore.Verify(r => r.RestoreAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ProjectHistoryRestoreScope>(),
            It.IsAny<IReadOnlyList<ProjectHistoryRestoreItemRef>>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Restore_card_lists_every_changed_and_removed_item_and_the_safety_version()
    {
        var user = new FakeCurrentUser(_userId);
        var builder = new AssistantApprovalCardBuilder(_db, Access(), user, history: _history.Object, historyRestore: _restore.Object);
        var args = new AIFunctionArguments
        {
            ["version"] = 3,
            ["scope"] = "item",
            ["items"] = new[] { "docs/readme.md" },
            ["confirmationToken"] = "t"
        };

        ApprovalVerdict verdict = await builder.BuildAsync(
            "ApplyRestoreProjectVersion", args, new AssistantToolContext(_userId, _project.Id), null, CancellationToken.None);

        ApprovalCard card = verdict.Card!;
        card.Risk.Should().Be(AssistantActionRisk.Destructive);
        string lines = string.Join("\n", card.Changes!.Select(c => c.Text));
        lines.Should().Contain("docs/readme.md").And.Contain("notes/old.md").And.Contain("Intro").And.Contain("safety version");
        card.Changes!.Any(c => c.Kind == AssistantChangeKind.Remove).Should().BeTrue();
        AnyGuid.IsMatch(card.Title + card.Summary + lines).Should().BeFalse();
    }

    [Fact]
    public async Task Restore_tools_resolve_the_project_before_validating_the_scope()
    {
        Guid foreign = Guid.NewGuid();
        AssistantHistoryTools tools = Tools();

        string proposal = await CallAsync(tools, ExecutionInitiator.Assistant, "ProposeRestoreProjectVersion",
            ("version", 3), ("scope", "not-a-scope"), ("projectId", foreign));
        string applied = await CallAsync(tools, ExecutionInitiator.Assistant, "ApplyRestoreProjectVersion",
            ("version", 3), ("scope", "not-a-scope"), ("confirmationToken", "x"), ("projectId", foreign));

        // A foreign project answers the one project refusal; the scope is never validated first.
        proposal.Should().Contain(AssistantProjectScope.ProjectNotFoundMessage).And.NotContain("scope must be");
        applied.Should().Contain(AssistantProjectScope.ProjectNotFoundMessage).And.NotContain("scope must be");
        _restore.Verify(r => r.PreviewAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ProjectHistoryRestoreScope>(),
            It.IsAny<IReadOnlyList<ProjectHistoryRestoreItemRef>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Restore_card_lists_100_items_then_accounts_for_the_rest_with_totals()
    {
        List<ProjectHistoryItemChange> many = Enumerable.Range(1, 150)
            .Select(i => new ProjectHistoryItemChange(
                ProjectVersionItemKind.File, Guid.NewGuid().ToString("D"), $"docs/file-{i:000}.md", null,
                i <= 100 ? "changed" : "added"))
            .ToList();
        _restore.Setup(r => r.PreviewAsync(_project.Id, _versionId, It.IsAny<ProjectHistoryRestoreScope>(),
                It.IsAny<IReadOnlyList<ProjectHistoryRestoreItemRef>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectHistoryRestorePreview(ProjectHistoryRestoreScope.Item, many, [], 0));

        var builder = new AssistantApprovalCardBuilder(
            _db, Access(), new FakeCurrentUser(_userId), history: _history.Object, historyRestore: _restore.Object);
        var args = new AIFunctionArguments
        {
            ["version"] = 3,
            ["scope"] = "item",
            ["items"] = new[] { "docs/readme.md" },
            ["confirmationToken"] = "t"
        };

        ApprovalVerdict verdict = await builder.BuildAsync(
            "ApplyRestoreProjectVersion", args, new AssistantToolContext(_userId, _project.Id), null, CancellationToken.None);

        List<string> lines = verdict.Card!.Changes!.Select(c => c.Text).ToList();

        // The safety line and the totals are always there, before the item list (contract s10).
        lines[0].Should().Contain("safety version");
        lines[1].Should().Contain("100 restored/changed").And.Contain("50 removed").And.Contain("0 blocked");

        // Every one of the first 100 items is on its own line, and the rest is accounted for, never cut.
        lines.Count(l => l.Contains("docs/file-")).Should().Be(100);
        lines.Should().ContainSingle(l => l.Contains("and 50 more") && l.Contains("50 removed"));
        lines.Should().HaveCount(103, "two header lines, 100 items and the one 'and N more' line");
    }

    [Fact]
    public async Task Restore_always_shows_a_card_in_auto_mode_and_declining_runs_nothing()
    {
        var user = new FakeCurrentUser(_userId);
        var broker = new AssistantApprovalBroker();
        var frames = new List<AssistantActionFrame>();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [AssistantApprovalGate.TimeoutKey] = "30" }).Build();
        var gate = new AssistantApprovalGate(
            new AssistantToolContext(_userId, _project.Id), TestTenancy.OrgOf(_userId), _db, broker,
            new AssistantApprovalCardBuilder(_db, Access(), user, history: _history.Object, historyRestore: _restore.Object),
            config,
            emit: (f, _) => { lock (frames) frames.Add(f); return Task.CompletedTask; },
            caption: (_, _) => Task.CompletedTask);
        int runs = 0;
        var args = new AIFunctionArguments
        {
            ["version"] = 3,
            ["scope"] = "item",
            ["items"] = new[] { "docs/readme.md" },
            ["confirmationToken"] = "t"
        };

        Task<object?> pending = gate.RunAsync("ApplyRestoreProjectVersion", args, () => { runs++; return ValueTask.FromResult<object?>("{}"); }, CancellationToken.None);
        AssistantActionFrame card = null!;
        for (int i = 0; i < 500 && card is null; i++)
        {
            lock (frames) card = frames.FirstOrDefault(f => f.State == AssistantActionState.Pending)!;
            if (card is null)
                await Task.Delay(10);
        }

        card.Should().NotBeNull("a restore card is shown even in auto mode");
        card.Mode.Should().Be(AssistantActionMode.Ask);
        broker.Answer(card.Id, _userId, TestTenancy.OrgOf(_userId), allow: false, alwaysAllow: false);
        await pending;
        runs.Should().Be(0);
    }

    [Fact]
    public void Both_apply_tools_have_catalog_entries_and_only_restore_always_asks()
    {
        AssistantApprovalCatalog.KnownTools.Should().Contain(["ApplySaveProjectVersion", "ApplyRestoreProjectVersion"]);
        AssistantApprovalCatalog.AlwaysAsksUser("ApplyRestoreProjectVersion").Should().BeTrue();
        AssistantApprovalCatalog.AlwaysAsksUser("ApplySaveProjectVersion").Should().BeFalse();
    }

    [Fact]
    public void The_mcp_surface_collapses_the_pairs_with_the_right_hints()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        IReadOnlyList<AIFunction> registered = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(_userId))
            .GetTools(new AssistantToolContext(_userId));
        IReadOnlyList<McpToolDescriptor> catalog =
            AssistantMcpToolSurface.Project(registered, new HashSet<string>(StringComparer.Ordinal));

        catalog.Select(t => t.Name).Should().Contain(["SaveProjectVersion", "RestoreProjectVersion", "ListProjectVersions", "GetProjectChanges"]);
        catalog.Select(t => t.Name).Should().NotContain(["ApplyRestoreProjectVersion", "ProposeRestoreProjectVersion"]);
        catalog.Single(t => t.Name == "ListProjectVersions").Annotation.ReadOnlyHint.Should().BeTrue();
        catalog.Single(t => t.Name == "GetProjectChanges").Annotation.ReadOnlyHint.Should().BeTrue();
        catalog.Single(t => t.Name == "SaveProjectVersion").Annotation.DestructiveHint.Should().BeTrue();
        catalog.Single(t => t.Name == "RestoreProjectVersion").Annotation.DestructiveHint.Should().BeTrue();
    }
}
