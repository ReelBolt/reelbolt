using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>Tags and folder on the assistant's project tools (names only, bound into the token, scoped to the caller's workspace).</summary>
public class AssistantProjectOrganizationToolTests
{
    private const string Stale = "stale confirmationToken, re-propose and try again";

    private static InferenceApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<JsonElement> Call(AssistantToolProvider provider, Guid user, string tool, params (string Key, object? Value)[] arguments)
    {
        AIFunction fn = provider.GetTools(new AssistantToolContext(user)).Single(t => t.Name == tool);
        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in arguments) args[key] = value;
        object? raw = await fn.InvokeAsync(args);
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString()! : raw is JsonElement j ? j.GetRawText() : raw?.ToString() ?? "";
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string? ErrorOf(JsonElement r) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out JsonElement e) ? e.GetString() : null;
    private static string TokenOf(JsonElement r) => r.GetProperty("confirmationToken").GetString()!;

    private static Project Seed(InferenceApiDbContext db, Guid owner, string name = "Spring")
    {
        var p = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = owner,
            OrganizationId = TestTenancy.OrgOf(owner),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(p);
        db.SaveChanges();
        return p;
    }

    [Fact]
    public async Task Create_with_tags_and_folder_creates_them_only_in_the_callers_org()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid(), other = Guid.NewGuid();
        db.ProjectTags.Add(new ProjectTag { Id = Guid.NewGuid(), OrganizationId = TestTenancy.OrgOf(other), Name = "Acme", NameKey = "acme", Color = "red" });
        db.ProjectFolders.Add(new ProjectFolder { Id = Guid.NewGuid(), OrganizationId = TestTenancy.OrgOf(other), Name = "Clients", NameKey = "clients" });
        db.SaveChanges();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(me));

        JsonElement proposal = await Call(provider, me, "ProposeCreateProject", ("name", "Promo"), ("description", null), ("tags", new[] { "Acme", "2026" }), ("folder", "Clients"));
        ErrorOf(proposal).Should().BeNull();
        proposal.GetProperty("summary").GetString().Should().Contain("Tags: Acme, 2026.").And.Contain("Folder: Clients.").And.Contain("New tag: Acme will be created.");

        JsonElement applied = await Call(provider, me, "ApplyCreateProject", ("name", "Promo"), ("description", null),
            ("tags", new[] { "Acme", "2026" }), ("folder", "Clients"), ("confirmationToken", TokenOf(proposal)));
        ErrorOf(applied).Should().BeNull();

        Guid org = TestTenancy.OrgOf(me);
        Project created = db.Projects.Single(p => p.Name == "Promo");
        db.ProjectTags.Where(t => t.OrganizationId == org).Select(t => t.Name).Should().BeEquivalentTo(["Acme", "2026"]);
        db.ProjectTags.Where(t => t.OrganizationId == org).All(t => t.Color == "gray").Should().BeTrue();
        db.ProjectTags.Count(t => t.OrganizationId != org).Should().Be(1, "the other org's tag is untouched and never matched");
        db.ProjectTagLinks.Where(l => l.ProjectId == created.Id).Count().Should().Be(2);
        ProjectFolder folder = db.ProjectFolders.Single(f => f.Id == created.FolderId);
        folder.OrganizationId.Should().Be(org);
    }

    [Fact]
    public async Task Update_replaces_tags_then_clears_the_folder_and_ListProjects_filters_by_name()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Project project = Seed(db, me);
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(me));

        JsonElement p1 = await Call(provider, me, "ProposeUpdateProject", ("projectId", project.Id), ("name", "Spring"), ("description", null), ("tags", new[] { "Acme" }), ("folder", "Clients"));
        ErrorOf(await Call(provider, me, "ApplyUpdateProject", ("projectId", project.Id), ("name", "Spring"), ("description", null),
            ("tags", new[] { "Acme" }), ("folder", "Clients"), ("confirmationToken", TokenOf(p1)))).Should().BeNull();

        JsonElement listed = await Call(provider, me, "ListProjects", ("tag", "acme"), ("folder", "CLIENTS"));
        listed.GetArrayLength().Should().Be(1);
        listed[0].GetProperty("tags")[0].GetString().Should().Be("Acme");
        listed[0].GetProperty("folder").GetString().Should().Be("Clients");
        (await Call(provider, me, "ListProjects", ("tag", "nope"))).GetArrayLength().Should().Be(0);

        JsonElement p2 = await Call(provider, me, "ProposeUpdateProject", ("projectId", project.Id), ("name", "Spring"), ("description", null), ("tags", new[] { "Beta" }), ("folder", ""));
        ErrorOf(await Call(provider, me, "ApplyUpdateProject", ("projectId", project.Id), ("name", "Spring"), ("description", null),
            ("tags", new[] { "Beta" }), ("folder", ""), ("confirmationToken", TokenOf(p2)))).Should().BeNull();

        db.Projects.Single(p => p.Id == project.Id).FolderId.Should().BeNull();
        db.ProjectTagLinks.Where(l => l.ProjectId == project.Id).Select(l => l.Tag.Name).Should().BeEquivalentTo(["Beta"]);
    }

    [Fact]
    public async Task Apply_with_tags_or_folder_that_differ_from_the_proposal_is_refused()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        Project project = Seed(db, me);
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(me));

        JsonElement proposal = await Call(provider, me, "ProposeUpdateProject", ("projectId", project.Id), ("name", "Spring"), ("description", null), ("tags", new[] { "Acme" }), ("folder", "Clients"));
        JsonElement tampered = await Call(provider, me, "ApplyUpdateProject", ("projectId", project.Id), ("name", "Spring"), ("description", null),
            ("tags", new[] { "Acme", "Sneaky" }), ("folder", "Clients"), ("confirmationToken", TokenOf(proposal)));
        ErrorOf(tampered).Should().Be(Stale);
        JsonElement moved = await Call(provider, me, "ApplyUpdateProject", ("projectId", project.Id), ("name", "Spring"), ("description", null),
            ("tags", new[] { "Acme" }), ("folder", "Elsewhere"), ("confirmationToken", TokenOf(proposal)));
        ErrorOf(moved).Should().Be(Stale);
        db.ProjectTags.Should().BeEmpty();
        db.ProjectFolders.Should().BeEmpty();
    }

    [Fact]
    public async Task Caps_and_invalid_names_are_refused_on_propose()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(me));

        ErrorOf(await Call(provider, me, "ProposeCreateProject", ("name", "P"), ("description", null), ("tags", new[] { "a", "b", "c", "d", "e", "f" }))).Should().Contain("at most 5 new tags");
        ErrorOf(await Call(provider, me, "ProposeCreateProject", ("name", "P"), ("description", null), ("tags", Enumerable.Range(0, 21).Select(i => "t" + i).ToArray()))).Should().Contain("at most 20 tags");
        ErrorOf(await Call(provider, me, "ProposeCreateProject", ("name", "P"), ("description", null), ("tags", new[] { new string('x', 41) }))).Should().Contain("1 to 40");
        ErrorOf(await Call(provider, me, "ProposeCreateProject", ("name", "P"), ("description", null), ("folder", new string('x', 61)))).Should().Contain("1 to 60");
    }

    [Fact]
    public async Task The_card_shows_a_plain_line_for_tags_folder_and_new_tags()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        var user = new FakeCurrentUser(me);
        var builder = new AssistantApprovalCardBuilder(db, TestTenancy.Access(db, user), user);
        var args = new AIFunctionArguments { ["name"] = "Promo", ["tags"] = new[] { "Acme", "2026" }, ["folder"] = "Clients", ["confirmationToken"] = "t" };

        ApprovalVerdict verdict = await builder.BuildAsync("ApplyCreateProject", args, new AssistantToolContext(me), null, CancellationToken.None);

        verdict.Card!.Changes!.Select(c => c.Text).Should().Equal(
            "Tag “Acme” (new, will be created)",
            "Tag “2026” (new, will be created)",
            "Folder “Clients” (new, will be created)");
    }

    private sealed class RoleUser(Guid userId, ReelBolt.Shared.Tenancy.OrganizationRole role) : ReelBolt.Shared.Auth.ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole { get; } = role;
    }

    [Fact]
    public async Task A_role_without_org_edit_is_refused_on_propose_and_apply_and_nothing_is_written()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid me = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new RoleUser(me, ReelBolt.Shared.Tenancy.OrganizationRole.Billing));

        JsonElement proposal = await Call(provider, me, "ProposeCreateProject", ("name", "Promo"), ("description", null), ("tags", new[] { "Acme" }), ("folder", "Clients"));
        ErrorOf(proposal).Should().Contain("permission");
        JsonElement applied = await Call(provider, me, "ApplyCreateProject", ("name", "Promo"), ("description", null),
            ("tags", new[] { "Acme" }), ("folder", "Clients"), ("confirmationToken", "forged"));
        ErrorOf(applied).Should().NotBeNull();

        db.ProjectTags.Should().BeEmpty();
        db.ProjectFolders.Should().BeEmpty();
        db.Projects.Should().BeEmpty();
    }
}
