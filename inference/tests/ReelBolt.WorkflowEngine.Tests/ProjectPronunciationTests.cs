#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Pronunciation;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The project pronunciation glossary: validator, service upsert rules and the controller boundary.</summary>
public class ProjectPronunciationTests
{
    private sealed class FakeUser(Guid userId, OrganizationRole role = OrganizationRole.Member) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => role;
    }

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Project _project;

    private readonly PronunciationTestGate _gate = new();
    private ModerationAttribution Attr => ProjectPronunciationService.Attribution(_project.OrganizationId, _project.Id, _owner);

    public ProjectPronunciationTests()
    {
        _project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = _owner, OrganizationId = TestTenancy.OrgOf(_owner) };
        _db.Projects.Add(_project);
        _db.SaveChanges();
    }

    private ProjectPronunciationController Controller(Guid? user = null, OrganizationRole role = OrganizationRole.Member)
    {
        var u = new FakeUser(user ?? _owner, role);
        return new(new ProjectAccessService(_db, u), new ProjectPronunciationService(_db, _gate));
    }

    [Theory]
    [InlineData("Novara", "Novàra", true)]
    [InlineData("Novara", "Novára", true)]
    [InlineData("Novara", "nˈovara", true)]
    [InlineData("Novara", "NOVÀRA", true)]
    [InlineData("Xiaomi", "Sciaomi", true)]
    [InlineData("Nike", "Naiki", true)]
    [InlineData("Novara", "Nova ra", true)]
    [InlineData("N", "Novàra", false)]
    [InlineData("Nov\u0000ara", "Novàra", false)]
    [InlineData("Novara", "Nov\u200bàra", false)]
    [InlineData("Novara", "", false)]
    [InlineData("Novara", "Nov<ara>", false)]
    [InlineData("Novara", "Novara1", false)]
    [InlineData("Novara", "novara.com", false)]
    [InlineData("Novara", "http://x", false)]
    [InlineData("Novara", "no.va.ra", false)]
    [InlineData("Novara", "Novara [x]", false)]
    [InlineData("Novara", "'-", false)]
    [InlineData("Novara", "Novàra Novàra Novàra Novàra", false)]
    public void Validator_accepts_and_rejects(string term, string spelling, bool valid) =>
        (PronunciationGlossaryValidator.Validate(term, spelling).Count == 0).Should().Be(valid);

    [Fact]
    public void Validator_rejects_overlong_values()
    {
        PronunciationGlossaryValidator.Validate(new string('a', 61), "ok").Should().NotBeEmpty();
        PronunciationGlossaryValidator.Validate("ok", new string('a', 121)).Should().NotBeEmpty();
        PronunciationGlossaryValidator.Validate(new string('a', 60), new string('a', 120)).Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_creates_updates_and_reports_unchanged_case_insensitively()
    {
        var svc = new ProjectPronunciationService(_db, _gate);
        (await svc.UpsertAsync(_project.Id, "Novara", "Novàra", false, Attr, default)).Status.Should().Be(PronunciationSaveStatus.Created);
        (await svc.UpsertAsync(_project.Id, "NOVARA", "Novàra", false, Attr, default)).Status.Should().Be(PronunciationSaveStatus.Unchanged);
        PronunciationSaveResult updated = await svc.UpsertAsync(_project.Id, "novara", "Novára", false, Attr, default);
        updated.Status.Should().Be(PronunciationSaveStatus.Updated);
        (await _db.ProjectPronunciationEntries.CountAsync()).Should().Be(1);
        updated.Entry!.Spelling.Should().Be("Novára");
    }

    [Fact]
    public async Task Upsert_normalises_to_nfc()
    {
        var svc = new ProjectPronunciationService(_db, _gate);
        await svc.UpsertAsync(_project.Id, "Novára", "Novàra", false, Attr, default);
        _db.ProjectPronunciationEntries.Single().Term.Should().Be("Novára");
    }

    [Fact]
    public async Task Limit_is_200_entries()
    {
        for (int i = 0; i < 199; i++)
            _db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry { Id = Guid.NewGuid(), ProjectId = _project.Id, Term = $"term{i}", Spelling = "x" });
        _db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry { Id = Guid.NewGuid(), ProjectId = _project.Id, Term = "Novara", Spelling = "Novara" });
        await _db.SaveChangesAsync();
        var svc = new ProjectPronunciationService(_db, _gate);
        (await svc.UpsertAsync(_project.Id, "extra", "éxtra", false, Attr, default)).Status.Should().Be(PronunciationSaveStatus.Full);
        (await svc.UpsertAsync(_project.Id, "Novara", "Novàra", false, Attr, default)).Status.Should().Be(PronunciationSaveStatus.Updated);
    }

    [Fact]
    public async Task Controller_create_409_on_duplicate_and_400_on_invalid()
    {
        ProjectPronunciationController c = Controller();
        var first = await c.Create(_project.Id, new("Novara", "Novàra"), default);
        ((ObjectResult)first.Result!).StatusCode.Should().Be(201);
        var dup = await c.Create(_project.Id, new("novara", "Novàra"), default);
        dup.Result.Should().BeOfType<ConflictObjectResult>();
        var bad = await c.Create(_project.Id, new("N", "x"), default);
        bad.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Controller_update_and_delete_work_and_rename_conflict_is_409()
    {
        ProjectPronunciationController c = Controller();
        await c.Create(_project.Id, new("Novara", "Novàra"), default);
        await c.Create(_project.Id, new("Garanzie", "Garanzìe"), default);
        Guid id = _db.ProjectPronunciationEntries.First(e => e.Term == "Garanzie").Id;
        (await c.Update(_project.Id, id, new("novara", "Novàra"), default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await c.Update(_project.Id, id, new("Garanzie", "Garanzíe", true), default)).Value!.CaseSensitive.Should().BeTrue();
        (await c.Delete(_project.Id, id, default)).Should().BeOfType<NoContentResult>();
        (await c.Delete(_project.Id, id, default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Cross_tenant_is_404_and_billing_role_cannot_write()
    {
        Guid stranger = Guid.NewGuid();
        (await Controller(stranger).List(_project.Id, default)).Result.Should().BeOfType<NotFoundResult>();
        (await Controller(stranger).Create(_project.Id, new("Novara", "Novàra"), default)).Result.Should().BeOfType<NotFoundResult>();

        ActionResult<PronunciationEntryResponse> r = await Controller(_owner, OrganizationRole.Billing)
            .Create(_project.Id, new("Novara", "Novàra"), default);
        r.Result.Should().BeOfType<ForbidResult>();
        _db.ProjectPronunciationEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_flagged_spelling_is_refused_on_every_save_path_and_nothing_is_written()
    {
        _gate.BlockWhenContains = "badword";
        ProjectPronunciationController c = Controller();

        var created = await c.Create(_project.Id, new("Novara", "badword"), default);
        var body = created.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        body.Value!.ToString().Should().Contain("CONTENT_BLOCKED");
        _db.ProjectPronunciationEntries.Should().BeEmpty();

        var svc = new ProjectPronunciationService(_db, _gate);
        (await svc.UpsertAsync(_project.Id, "Novara", "badword", false, Attr, default)).Status.Should().Be(PronunciationSaveStatus.Blocked);
        _db.ProjectPronunciationEntries.Should().BeEmpty();

        await c.Create(_project.Id, new("Novara", "Novàra"), default);
        Guid id = _db.ProjectPronunciationEntries.Single().Id;
        (await c.Update(_project.Id, id, new("Novara", "badword"), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        _db.ProjectPronunciationEntries.Single().Spelling.Should().Be("Novàra", "a refused update leaves the stored spelling alone");
        (await svc.UpsertAsync(_project.Id, "Novara", "badword", false, Attr, default)).Status.Should().Be(PronunciationSaveStatus.Blocked);
        _db.ProjectPronunciationEntries.Single().Spelling.Should().Be("Novàra");
    }

    [Fact]
    public async Task The_spelling_is_what_gets_moderated_with_the_generated_text_attribution()
    {
        var svc = new ProjectPronunciationService(_db, _gate);
        await svc.UpsertAsync(_project.Id, "Novara", "Novàra", false, Attr, default);
        _gate.Seen.Should().ContainSingle().Which.Input.Text.Should().Be("Novàra");
        _gate.Seen[0].Attribution.Surface.Should().Be(ModerationSurface.GeneratedText);
        _gate.Seen[0].Attribution.ProjectId.Should().Be(_project.Id);
        _gate.Seen[0].Attribution.OrganizationId.Should().Be(_project.OrganizationId);
    }

    [Fact]
    public async Task When_moderation_is_off_or_unavailable_the_save_goes_through()
    {
        _gate.Unavailable = true;
        var svc = new ProjectPronunciationService(_db, _gate);
        (await svc.UpsertAsync(_project.Id, "Novara", "Novàra", false, Attr, default)).Status.Should().Be(PronunciationSaveStatus.Created);
    }
}

/// <summary>A moderation gate double for the glossary save paths: blocks a spelling containing a marker word.</summary>
public sealed class PronunciationTestGate : IContentModerationGate
{
    public string? BlockWhenContains { get; set; }
    public bool Unavailable { get; set; }
    public List<(ModerationInput Input, ModerationAttribution Attribution)> Seen { get; } = [];

    public Task<ModerationDecision> ModerateAsync(ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
    {
        Seen.Add((input, attribution));
        if (Unavailable)
            return Task.FromResult(new ModerationDecision(ModerationAction.Allow, false, false, null, string.Empty, "h"));
        bool block = BlockWhenContains is not null && (input.Text ?? string.Empty).Contains(BlockWhenContains, StringComparison.Ordinal);
        return Task.FromResult(new ModerationDecision(
            block ? ModerationAction.Block : ModerationAction.Allow, true, false, null, block ? "Blocked." : string.Empty, "h"));
    }

    public Task<bool> ScreenGenerationPromptAsync(string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> ScreenGeneratedTextAsync(string text, string site, ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);
    public Task<bool> ScreenPublishAsync(ModerationInput input, ModerationAttribution attribution, CancellationToken ct) => Task.FromResult(true);
}
