using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>What a run page says about the design the run was pinned to (advice, never a block).</summary>
public sealed class DesignSystemRunWarningsTests : IDisposable
{
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase($"run-warnings-{Guid.NewGuid():N}")
        .Options);

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly DateTime _queuedAt = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    private static string Doc(bool withToken)
    {
        JsonObject document = DesignDocument.New("Seed");
        if (withToken)
        {
            DesignDocument.SetToken(
                document, "color.brand", "color",
                new JsonObject { ["colorSpace"] = "srgb", ["hex"] = "#112233" },
                new TokenProvenance("manual", null, null, null), 1.0);
        }

        return document.ToJsonString();
    }

    private (DesignSystem System, DesignSystemVersion Approved) SeedDesign(bool approvedHasTokens)
    {
        var approved = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            VersionNumber = 3,
            State = DesignVersionState.Approved,
            DocumentJson = Doc(approvedHasTokens),
            DocumentDigest = new string('a', 64),
            Origin = DesignVersionOrigin.Import,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = _queuedAt.AddHours(-2),
        };
        var system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            Kind = DesignSystemKinds.Design,
            Name = "design",
            CurrentVersionId = approved.Id,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = _queuedAt.AddHours(-3),
            UpdatedAt = _queuedAt.AddHours(-2),
        };
        approved.DesignSystemId = system.Id;
        _db.DesignSystems.Add(system);
        _db.DesignSystemVersions.Add(approved);
        _db.SaveChanges();
        return (system, approved);
    }

    private void SeedDraft(DesignSystem system, int number, DateTime createdAt, DesignVersionState state = DesignVersionState.Draft)
    {
        _db.DesignSystemVersions.Add(new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            VersionNumber = number,
            State = state,
            DocumentJson = Doc(true),
            DocumentDigest = new string('b', 64),
            Origin = DesignVersionOrigin.Import,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = createdAt,
        });
        _db.SaveChanges();
    }

    private WorkflowExecution Run(DesignSystemVersion? pinned, string? briefJson = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = _projectId,
        QueuedAt = _queuedAt,
        RunBriefJson = briefJson,
        DesignSystemPinJson = pinned is null
            ? null
            : DesignSystemPinJson.Serialize(new DesignSystemPin(
                1,
                new DesignSystemPinMember(pinned.Id, pinned.VersionNumber, pinned.DocumentDigest, null, "project"),
                null)),
    };

    private Task<System.Collections.Generic.IReadOnlyList<string>> Warnings(WorkflowExecution run) =>
        DesignSystemRunWarnings.ForExecutionAsync(_db, run, CancellationToken.None);

    [Fact]
    public async Task A_run_pinned_to_an_empty_design_says_the_brand_was_not_applied()
    {
        var (_, approved) = SeedDesign(approvedHasTokens: false);

        var warnings = await Warnings(Run(approved));

        warnings.Should().ContainSingle().Which.Should().Contain("version 3").And.Contain("no colours, fonts");
    }

    [Fact]
    public async Task A_run_pinned_to_a_good_design_with_no_draft_has_no_warning()
    {
        var (_, approved) = SeedDesign(approvedHasTokens: true);

        (await Warnings(Run(approved))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_newer_draft_waiting_at_start_is_named_with_the_version_the_run_used()
    {
        var (system, approved) = SeedDesign(approvedHasTokens: true);
        SeedDraft(system, 4, _queuedAt.AddMinutes(-10));

        var warnings = await Warnings(Run(approved));

        warnings.Should().ContainSingle().Which.Should().Contain("version 4").And.Contain("used version 3");
    }

    [Fact]
    public async Task A_draft_created_after_the_run_started_or_already_decided_is_not_mentioned()
    {
        var (system, approved) = SeedDesign(approvedHasTokens: true);
        SeedDraft(system, 4, _queuedAt.AddMinutes(10));
        SeedDraft(system, 5, _queuedAt.AddMinutes(-10), DesignVersionState.Rejected);

        (await Warnings(Run(approved))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_with_no_approved_design_and_a_waiting_draft_says_so()
    {
        var (system, _) = SeedDesign(approvedHasTokens: true);
        SeedDraft(system, 4, _queuedAt.AddMinutes(-10));

        var warnings = await Warnings(Run(pinned: null));

        warnings.Should().ContainSingle().Which.Should().Contain("no approved design").And.Contain("version 4");
    }

    [Fact]
    public async Task A_run_that_chose_no_design_system_is_never_warned()
    {
        var (system, _) = SeedDesign(approvedHasTokens: false);
        SeedDraft(system, 4, _queuedAt.AddMinutes(-10));
        string brief = RunBriefJson.Serialize(new StoredRunBrief(DesignSystem: RunBrief.DesignSystemNone));

        (await Warnings(Run(pinned: null, brief))).Should().BeEmpty();
    }
}
