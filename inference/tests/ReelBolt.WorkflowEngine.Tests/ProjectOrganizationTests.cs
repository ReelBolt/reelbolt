using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Controllers.Filters;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Project organization (tags, folders, bulk organize): tenancy, validation and caps, uniqueness, cascades,
/// ETag movement, permissions and the additive <see cref="ProjectResponse"/> fields. Runs on SQLite in memory
/// with a real tenant filter, so "another organization's id" really is invisible to the caller.
/// </summary>
public sealed class ProjectOrganizationTests : IDisposable
{
    private static readonly Guid OrgA = Guid.Parse("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("22222222-0000-0000-0000-0000000000b2");
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;
    private readonly TestTenancy.MutableTenant _tenant = new();
    private readonly Guid _userId = Guid.NewGuid();
    private Guid _actingOrg = OrgA;
    private OrganizationRole _role = OrganizationRole.Owner;

    public ProjectOrganizationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options, _tenant);
        _db.Database.EnsureCreated();
        using var engineDb = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(
            new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>().UseSqlite(_connection).Options);
        engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
        // Create provisions the auto templates, which need one built-in agent per type.
        foreach (AgentType type in Enum.GetValues<AgentType>())
            _db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), AgentType = type, IsBuiltIn = true, CreatedAt = Old });
        _db.SaveChanges();
    }

    /// <summary>The cascade rules live in the schema (ON DELETE), so those tests switch FK enforcement on after seeding the parents.</summary>
    private void EnforceForeignKeys()
    {
        _tenant.ActAsOrganization(null);
        foreach (Guid org in new[] { OrgA, OrgB })
            if (!_db.Organizations.IgnoreQueryFilters().Any(o => o.Id == org))
                _db.Organizations.Add(new Organization { Id = org, Name = "o" + org, Slug = "o-" + org, Kind = OrganizationKind.Team, CreatedAt = Old, UpdatedAt = Old });
        if (!_db.ApplicationUsers.Any(u => u.Id == _userId))
            _db.ApplicationUsers.Add(new ApplicationUser { Id = _userId, Email = "t@example.com", DisplayName = "t", CreatedAt = Old });
        _db.SaveChanges();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        _tenant.ActAsOrganization(_actingOrg);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // --- harness ----------------------------------------------------------------------------------

    private sealed class FakeCurrentUser(Guid userId, Guid organizationId, OrganizationRole role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private (ProjectsController Projects, ProjectTagsController Tags, ProjectFoldersController Folders) Act(Guid org, OrganizationRole role = OrganizationRole.Owner)
    {
        _actingOrg = org;
        _role = role;
        _tenant.ActAsOrganization(org);
        _db.ChangeTracker.Clear();
        FakeCurrentUser user = new(_userId, org, role);
        IProjectAccessService access = new ProjectAccessService(_db, user);
        return (
            new ProjectsController(_db, user, access,
                new WorkflowTemplateProvisioningService(_db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build())),
            new ProjectTagsController(_db, user, access),
            new ProjectFoldersController(_db, user, access));
    }

    private Project SeedProject(Guid org, string name = "p")
    {
        Project p = new() { Id = Guid.NewGuid(), Name = name, OwnerId = _userId, OrganizationId = org, CreatedAt = Old, UpdatedAt = Old };
        _db.Projects.Add(p);
        _db.SaveChanges();
        return p;
    }

    private ProjectTag SeedTag(Guid org, string name, string color = "gray")
    {
        ProjectTag t = new() { Id = Guid.NewGuid(), OrganizationId = org, Name = name, NameKey = name.ToLowerInvariant(), Color = color, CreatedAt = Old };
        _db.ProjectTags.Add(t);
        _db.SaveChanges();
        return t;
    }

    private ProjectFolder SeedFolder(Guid org, string name)
    {
        ProjectFolder f = new() { Id = Guid.NewGuid(), OrganizationId = org, Name = name, NameKey = name.ToLowerInvariant(), CreatedAt = Old };
        _db.ProjectFolders.Add(f);
        _db.SaveChanges();
        return f;
    }

    private void Link(Guid projectId, Guid tagId)
    {
        _db.ProjectTagLinks.Add(new ProjectTagLink { ProjectId = projectId, TagId = tagId });
        _db.SaveChanges();
    }

    private static string Code(object? result)
    {
        object body = result switch
        {
            ObjectResult o => o.Value!,
            _ => throw new InvalidOperationException($"No body on {result?.GetType().Name}")
        };
        return (string)body.GetType().GetProperty("code")!.GetValue(body)!;
    }

    private static void ShouldBeSameNotFound(IActionResult? result) => result.Should().BeOfType<NotFoundResult>();

    /// <summary>Row state straight from storage, unfiltered, so a "nothing written" assertion cannot be fooled by the tenant filter.</summary>
    private async Task<(int Tags, int Links, Dictionary<Guid, Guid?> Folders, Dictionary<Guid, DateTime> Updated)> Snapshot()
    {
        _tenant.ActAsOrganization(null);
        _db.ChangeTracker.Clear();
        int tags = await _db.ProjectTags.IgnoreQueryFilters().CountAsync();
        int links = await _db.ProjectTagLinks.CountAsync();
        List<Project> projects = await _db.Projects.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        _tenant.ActAsOrganization(_actingOrg);
        return (tags, links, projects.ToDictionary(p => p.Id, p => p.FolderId), projects.ToDictionary(p => p.Id, p => p.UpdatedAt));
    }

    private async Task<Project> Reload(Guid id)
    {
        _db.ChangeTracker.Clear();
        return await _db.Projects.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private static async Task<string?> ETagOf(IActionResult result)
    {
        ServiceCollection services = new();
        services.AddOptions();
        services.AddControllers();
        DefaultHttpContext http = new() { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = "GET";
        http.Response.Body = new MemoryStream();
        ActionContext ctx = new(http, new RouteData(), new ActionDescriptor());
        ResultExecutingContext executing = new(ctx, [], result, new object());
        await new ConditionalGetAttribute().OnResultExecutionAsync(executing,
            () => Task.FromResult(new ResultExecutedContext(ctx, [], result, new object())));
        return http.Response.Headers.ETag.ToString();
    }

    private static async Task<string?> ListETag(ProjectsController c) =>
        await ETagOf((await c.List(null, null, false, CancellationToken.None)).Result!);

    private static async Task<string?> GetETag(ProjectsController c, Guid id) =>
        await ETagOf((await c.Get(id, CancellationToken.None)).Result!);

    // --- 1. tenancy -------------------------------------------------------------------------------

    [Fact]
    public async Task A_foreign_tag_or_folder_or_project_answers_the_same_404_as_a_missing_id_and_writes_nothing()
    {
        Project aProject = SeedProject(OrgA);
        Project bProject = SeedProject(OrgB);
        ProjectTag bTag = SeedTag(OrgB, "Acme");
        ProjectFolder bFolder = SeedFolder(OrgB, "Clients");
        ProjectTag aTag = SeedTag(OrgA, "Mine");
        var before = await Snapshot();
        var (projects, tags, folders) = Act(OrgA);
        Guid missing = Guid.NewGuid();

        foreach (Guid id in new[] { bTag.Id, missing })
        {
            ShouldBeSameNotFound((await projects.SetOrganization(aProject.Id, new ProjectOrganizationRequest([id], null), default)).Result);
            ShouldBeSameNotFound(await projects.Organize(new OrganizeProjectsRequest([aProject.Id], [id], null), default));
            ShouldBeSameNotFound(await projects.Organize(new OrganizeProjectsRequest([aProject.Id], null, [id]), default));
            ShouldBeSameNotFound((await projects.Create(new CreateProjectRequest("x", null, [id]), default)).Result);
            ShouldBeSameNotFound((await tags.Update(id, new ProjectTagRequest("Renamed", null), default)).Result);
            ShouldBeSameNotFound(await tags.Delete(id, default));
        }
        foreach (Guid id in new[] { bFolder.Id, missing })
        {
            ShouldBeSameNotFound((await projects.SetOrganization(aProject.Id, new ProjectOrganizationRequest([], id), default)).Result);
            ShouldBeSameNotFound(await projects.Organize(new OrganizeProjectsRequest([aProject.Id], null, null, id), default));
            ShouldBeSameNotFound((await projects.Create(new CreateProjectRequest("x", null, null, id), default)).Result);
            ShouldBeSameNotFound((await folders.Rename(id, new ProjectFolderRequest("Renamed"), default)).Result);
            ShouldBeSameNotFound(await folders.Delete(id, default));
        }
        foreach (Guid id in new[] { bProject.Id, missing })
        {
            ShouldBeSameNotFound((await projects.SetOrganization(id, new ProjectOrganizationRequest([aTag.Id], null), default)).Result);
            ShouldBeSameNotFound(await projects.Organize(new OrganizeProjectsRequest([id], [aTag.Id], null), default));
        }

        var after = await Snapshot();
        after.Should().BeEquivalentTo(before);
        (await _db.ProjectTags.IgnoreQueryFilters().SingleAsync(t => t.Id == bTag.Id)).Name.Should().Be("Acme");
        (await _db.ProjectFolders.IgnoreQueryFilters().CountAsync(f => f.Id == bFolder.Id)).Should().Be(1);
        (await _db.Projects.IgnoreQueryFilters().CountAsync(p => p.Name == "x")).Should().Be(0);
    }

    [Fact]
    public async Task Bulk_organize_with_one_foreign_id_among_valid_ones_writes_nothing()
    {
        Project p1 = SeedProject(OrgA);
        Project p2 = SeedProject(OrgA);
        Project foreign = SeedProject(OrgB);
        ProjectTag tag = SeedTag(OrgA, "Mine");
        ProjectFolder folder = SeedFolder(OrgA, "F");
        var before = await Snapshot();
        var (projects, _, _) = Act(OrgA);

        ShouldBeSameNotFound(await projects.Organize(new OrganizeProjectsRequest([p1.Id, foreign.Id, p2.Id], [tag.Id], null, folder.Id), default));

        (await Snapshot()).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Bulk_organize_with_a_foreign_tag_among_valid_ones_writes_nothing()
    {
        Project p1 = SeedProject(OrgA);
        ProjectTag mine = SeedTag(OrgA, "Mine");
        ProjectTag foreign = SeedTag(OrgB, "Theirs");
        var before = await Snapshot();
        var (projects, _, _) = Act(OrgA);

        ShouldBeSameNotFound(await projects.Organize(new OrganizeProjectsRequest([p1.Id], [mine.Id, foreign.Id], null), default));

        (await Snapshot()).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Tag_and_folder_lists_and_project_filters_never_show_the_other_organization()
    {
        ProjectTag aTag = SeedTag(OrgA, "Acme");
        ProjectTag bTag = SeedTag(OrgB, "Acme");
        SeedFolder(OrgA, "Clients");
        ProjectFolder bFolder = SeedFolder(OrgB, "Clients");
        Project bProject = SeedProject(OrgB);
        Link(bProject.Id, bTag.Id);
        bProject.FolderId = bFolder.Id;
        _db.SaveChanges();
        var (projects, tags, folders) = Act(OrgA);

        var tagRows = (List<ProjectTagResponse>)((OkObjectResult)(await tags.List(default)).Result!).Value!;
        tagRows.Select(t => t.Id).Should().Equal(aTag.Id);
        tagRows.Single().ProjectCount.Should().Be(0);
        var folderRows = (List<ProjectFolderResponse>)((OkObjectResult)(await folders.List(default)).Result!).Value!;
        folderRows.Should().ContainSingle().Which.ProjectCount.Should().Be(0);

        var byTag = (List<ProjectResponse>)((OkObjectResult)(await projects.List([bTag.Id], null, false, default)).Result!).Value!;
        byTag.Should().BeEmpty();
        var byFolder = (List<ProjectResponse>)((OkObjectResult)(await projects.List(null, bFolder.Id, false, default)).Result!).Value!;
        byFolder.Should().BeEmpty();
        var all = (List<ProjectResponse>)((OkObjectResult)(await projects.List(null, null, false, default)).Result!).Value!;
        all.Should().BeEmpty();
    }

    [Fact]
    public void Tenant_filter_is_present_on_tags_and_folders_and_links_are_guarded_by_the_project()
    {
        var tag = _db.Model.FindEntityType(typeof(ProjectTag))!;
        var folder = _db.Model.FindEntityType(typeof(ProjectFolder))!;
        tag.GetDeclaredQueryFilters().Select(f => f.Key).Should().Contain(InferenceApiDbContext.TenantFilterName);
        folder.GetDeclaredQueryFilters().Select(f => f.Key).Should().Contain(InferenceApiDbContext.TenantFilterName);
        _db.Model.FindEntityType(typeof(ProjectTagLink))!.GetDeclaredQueryFilters().Should().BeEmpty("links are guarded at the project, never directly");
        _db.Model.FindEntityType(typeof(ProjectTag))!.GetTableName().Should().Be("project_tags");
        _db.Model.FindEntityType(typeof(ProjectFolder))!.GetTableName().Should().Be("project_folders");
    }

    // --- 2. validation and caps -------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("bad\nname")]
    [InlineData("bad\u0007name")]
    [InlineData("bad\u202Ename")]
    [InlineData("zero\u200Bwidth")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 41
    public async Task Tag_create_rejects_invalid_names(string? name)
    {
        var (_, tags, _) = Act(OrgA);
        var result = (await tags.Create(new ProjectTagRequest(name, "red"), default)).Result;
        result.Should().BeOfType<BadRequestObjectResult>();
        Code(result).Should().Be("TAG_NAME_INVALID");
        (await _db.ProjectTags.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Tag_name_is_trimmed_and_40_characters_is_allowed()
    {
        var (_, tags, _) = Act(OrgA);
        string forty = new('a', 40);
        var created = (await tags.Create(new ProjectTagRequest("  " + forty + "  ", "red"), default)).Result;
        created.Should().BeOfType<CreatedResult>();
        ((ProjectTagResponse)((CreatedResult)created!).Value!).Name.Should().Be(forty);
    }

    [Theory]
    [InlineData("chartreuse")]
    [InlineData("#ff0000")]
    [InlineData("RED!")]
    public async Task Tag_create_rejects_a_colour_outside_the_palette(string color)
    {
        var (_, tags, _) = Act(OrgA);
        var result = (await tags.Create(new ProjectTagRequest("Acme", color), default)).Result;
        result.Should().BeOfType<BadRequestObjectResult>();
        Code(result).Should().Be("TAG_COLOR_INVALID");
    }

    [Fact]
    public async Task Every_palette_word_is_accepted_and_a_missing_colour_defaults_to_gray()
    {
        var (_, tags, _) = Act(OrgA);
        ProjectOrganizationRules.Palette.Should().Equal("gray", "red", "orange", "yellow", "green", "teal", "blue", "indigo", "violet", "pink");
        foreach (string word in ProjectOrganizationRules.Palette)
            (await tags.Create(new ProjectTagRequest("t-" + word, word), default)).Result.Should().BeOfType<CreatedResult>();
        var def = (CreatedResult)(await tags.Create(new ProjectTagRequest("no colour", null), default)).Result!;
        ((ProjectTagResponse)def.Value!).Color.Should().Be("gray");
    }

    [Theory]
    [InlineData("")]
    [InlineData("tab\there")]
    public async Task Folder_create_rejects_invalid_names(string name)
    {
        var (_, _, folders) = Act(OrgA);
        var result = (await folders.Create(new ProjectFolderRequest(name), default)).Result;
        result.Should().BeOfType<BadRequestObjectResult>();
        Code(result).Should().Be("FOLDER_NAME_INVALID");
    }

    [Fact]
    public async Task Folder_name_length_limit_is_60()
    {
        var (_, _, folders) = Act(OrgA);
        (await folders.Create(new ProjectFolderRequest(new string('f', 60)), default)).Result.Should().BeOfType<CreatedResult>();
        var result = (await folders.Create(new ProjectFolderRequest(new string('g', 61)), default)).Result;
        Code(result).Should().Be("FOLDER_NAME_INVALID");
    }

    [Fact]
    public async Task A_case_insensitive_duplicate_tag_is_409_TAG_NAME_TAKEN_on_create_and_rename_but_the_same_name_may_live_in_another_org()
    {
        SeedTag(OrgB, "Acme");
        var (_, tags, _) = Act(OrgA);
        (await tags.Create(new ProjectTagRequest("Acme", "red"), default)).Result.Should().BeOfType<CreatedResult>();

        var dup = (await tags.Create(new ProjectTagRequest("  ACME ", "blue"), default)).Result;

        dup.Should().BeOfType<ConflictObjectResult>();
        Code(dup).Should().Be("TAG_NAME_TAKEN");

        (_, tags, _) = Act(OrgA); // a new request: the failed insert must not linger in the change tracker
        var other = (CreatedResult)(await tags.Create(new ProjectTagRequest("Other", "blue"), default)).Result!;
        Guid otherId = ((ProjectTagResponse)other.Value!).Id;
        var rename = (await tags.Update(otherId, new ProjectTagRequest("aCmE", null), default)).Result;
        rename.Should().BeOfType<ConflictObjectResult>();
        Code(rename).Should().Be("TAG_NAME_TAKEN");

        // Renaming a tag to a different casing of its own name is not a clash.
        (await tags.Update(otherId, new ProjectTagRequest("OTHER", "pink"), default)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_case_insensitive_duplicate_folder_is_409_FOLDER_NAME_TAKEN()
    {
        var (_, _, folders) = Act(OrgA);
        (await folders.Create(new ProjectFolderRequest("Clients"), default)).Result.Should().BeOfType<CreatedResult>();
        var dup = (await folders.Create(new ProjectFolderRequest("clients"), default)).Result;
        dup.Should().BeOfType<ConflictObjectResult>();
        Code(dup).Should().Be("FOLDER_NAME_TAKEN");

        (_, _, folders) = Act(OrgA);
        var second = (CreatedResult)(await folders.Create(new ProjectFolderRequest("Two"), default)).Result!;
        var rename = (await folders.Rename(((ProjectFolderResponse)second.Value!).Id, new ProjectFolderRequest("CLIENTS"), default)).Result;
        Code(rename).Should().Be("FOLDER_NAME_TAKEN");
    }

    [Fact]
    public async Task The_201st_tag_is_409_TAG_LIMIT_REACHED_and_other_orgs_tags_do_not_count()
    {
        for (int i = 0; i < ProjectOrganizationRules.MaxTagsPerOrganization; i++)
            _db.ProjectTags.Add(new ProjectTag { Id = Guid.NewGuid(), OrganizationId = OrgA, Name = $"t{i}", NameKey = $"t{i}", CreatedAt = Old });
        for (int i = 0; i < 5; i++)
            _db.ProjectTags.Add(new ProjectTag { Id = Guid.NewGuid(), OrganizationId = OrgB, Name = $"b{i}", NameKey = $"b{i}", CreatedAt = Old });
        _db.SaveChanges();

        var (_, tagsA, _) = Act(OrgA);
        var full = (await tagsA.Create(new ProjectTagRequest("one more", null), default)).Result;
        full.Should().BeOfType<ConflictObjectResult>();
        Code(full).Should().Be("TAG_LIMIT_REACHED");

        var (_, tagsB, _) = Act(OrgB);
        (await tagsB.Create(new ProjectTagRequest("fine", null), default)).Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task The_501st_folder_is_409_FOLDER_LIMIT_REACHED()
    {
        for (int i = 0; i < ProjectOrganizationRules.MaxFoldersPerOrganization; i++)
            _db.ProjectFolders.Add(new ProjectFolder { Id = Guid.NewGuid(), OrganizationId = OrgA, Name = $"f{i}", NameKey = $"f{i}", CreatedAt = Old });
        _db.SaveChanges();

        var (_, _, folders) = Act(OrgA);
        var full = (await folders.Create(new ProjectFolderRequest("one more"), default)).Result;
        full.Should().BeOfType<ConflictObjectResult>();
        Code(full).Should().Be("FOLDER_LIMIT_REACHED");
    }

    [Fact]
    public async Task A_project_takes_20_tags_and_the_21st_is_409_PROJECT_TAG_LIMIT_REACHED_on_every_path()
    {
        List<ProjectTag> tagRows = Enumerable.Range(0, 21).Select(i => SeedTag(OrgA, $"t{i}")).ToList();
        Project p = SeedProject(OrgA);
        var (projects, _, _) = Act(OrgA);

        var ok = await projects.SetOrganization(p.Id, new ProjectOrganizationRequest(tagRows.Take(20).Select(t => t.Id).ToList(), null), default);
        ok.Result.Should().BeOfType<OkObjectResult>();
        ((ProjectResponse)((OkObjectResult)ok.Result!).Value!).Tags!.Count.Should().Be(20);

        var over = (await projects.SetOrganization(p.Id, new ProjectOrganizationRequest(tagRows.Select(t => t.Id).ToList(), null), default)).Result;
        over.Should().BeOfType<ConflictObjectResult>();
        Code(over).Should().Be("PROJECT_TAG_LIMIT_REACHED");

        var bulk = await projects.Organize(new OrganizeProjectsRequest([p.Id], [tagRows[20].Id], null), default);
        bulk.Should().BeOfType<ConflictObjectResult>();
        Code(bulk).Should().Be("PROJECT_TAG_LIMIT_REACHED");
        (await Snapshot()).Links.Should().Be(20, "the refused bulk add wrote nothing");

        var create = (await projects.Create(new CreateProjectRequest("x", null, tagRows.Select(t => t.Id).ToList()), default)).Result;
        Code(create).Should().Be("PROJECT_TAG_LIMIT_REACHED");
    }

    [Fact]
    public async Task Bulk_organize_accepts_100_projects_and_refuses_0_and_101_with_nothing_written()
    {
        List<Project> hundred = Enumerable.Range(0, 100).Select(i => SeedProject(OrgA, $"p{i}")).ToList();
        ProjectTag tag = SeedTag(OrgA, "Bulk");
        var (projects, _, _) = Act(OrgA);

        foreach (Guid[] ids in new[] { Array.Empty<Guid>(), hundred.Select(p => p.Id).Append(Guid.NewGuid()).ToArray() })
        {
            var refused = await projects.Organize(new OrganizeProjectsRequest(ids, [tag.Id], null), default);
            refused.Should().BeOfType<BadRequestObjectResult>();
            Code(refused).Should().Be("PROJECT_IDS_INVALID");
        }
        (await Snapshot()).Links.Should().Be(0);

        (await projects.Organize(new OrganizeProjectsRequest(hundred.Select(p => p.Id).ToList(), [tag.Id], null), default))
            .Should().BeOfType<NoContentResult>();
        (await Snapshot()).Links.Should().Be(100);
    }

    [Fact]
    public async Task Bulk_organize_refuses_setting_and_clearing_a_folder_at_once()
    {
        Project p = SeedProject(OrgA);
        ProjectFolder f = SeedFolder(OrgA, "F");
        var (projects, _, _) = Act(OrgA);
        var result = await projects.Organize(new OrganizeProjectsRequest([p.Id], null, null, f.Id, ClearFolder: true), default);
        result.Should().BeOfType<BadRequestObjectResult>();
        Code(result).Should().Be("FOLDER_CHOICE_INVALID");
    }

    // --- 3. uniqueness ----------------------------------------------------------------------------

    [Fact]
    public async Task Creating_Acme_then_ACME_is_one_201_and_one_409_never_a_500()
    {
        var (_, tags, folders) = Act(OrgA);
        var results = new List<IActionResult?> { (await tags.Create(new ProjectTagRequest("Acme", null), default)).Result };
        (_, tags, folders) = Act(OrgA);
        results.Add((await tags.Create(new ProjectTagRequest("ACME", null), default)).Result);
        results.Count(r => r is CreatedResult).Should().Be(1);
        results.Count(r => r is ConflictObjectResult).Should().Be(1);
        (await _db.ProjectTags.CountAsync()).Should().Be(1);

        (_, _, folders) = Act(OrgA);
        var folderResults = new List<IActionResult?> { (await folders.Create(new ProjectFolderRequest("Acme"), default)).Result };
        (_, _, folders) = Act(OrgA);
        folderResults.Add((await folders.Create(new ProjectFolderRequest("ACME"), default)).Result);
        folderResults.Count(r => r is CreatedResult).Should().Be(1);
        folderResults.Count(r => r is ConflictObjectResult).Should().Be(1);
    }

    [Fact]
    public async Task A_concurrent_loser_that_passes_the_app_checks_is_mapped_from_the_unique_violation_to_409()
    {
        // Two writers both pass the count check; the second insert hits the unique index. The first row is added
        // through a separate context on the same connection so this context's save is the racing loser.
        var (_, tags, _) = Act(OrgA);
        using (InferenceApiDbContext other = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options))
        {
            other.ProjectTags.Add(new ProjectTag { Id = Guid.NewGuid(), OrganizationId = OrgA, Name = "Acme", NameKey = "acme", CreatedAt = Old });
            other.SaveChanges();
        }
        // The tenant-filtered count in Create sees the row, but the unique index is the real guard: assert the mapping.
        var result = (await tags.Create(new ProjectTagRequest("aCME", null), default)).Result;
        result.Should().BeOfType<ConflictObjectResult>();
        Code(result).Should().Be("TAG_NAME_TAKEN");
    }

    [Fact]
    public void IsUniqueViolation_recognises_postgres_23505_and_nothing_else()
    {
        DbUpdateException unique = new("x", new Npgsql.PostgresException("dup", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation));
        DbUpdateException other = new("x", new Npgsql.PostgresException("fk", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.ForeignKeyViolation));
        ProjectOrganizationRules.IsUniqueViolation(unique).Should().BeTrue();
        ProjectOrganizationRules.IsUniqueViolation(other).Should().BeFalse();
        ProjectOrganizationRules.IsUniqueViolation(new DbUpdateException("x", new InvalidOperationException("UNIQUE"))).Should().BeFalse();
    }

    // --- 4. cascades ------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_project_removes_its_links_and_keeps_the_tag()
    {
        Project p = SeedProject(OrgA);
        ProjectTag t = SeedTag(OrgA, "Keep");
        Link(p.Id, t.Id);
        EnforceForeignKeys();
        var (projects, _, _) = Act(OrgA);

        (await projects.Delete(p.Id, default)).Should().BeOfType<NoContentResult>();

        (await Snapshot()).Links.Should().Be(0);
        (await Snapshot()).Tags.Should().Be(1);
    }

    [Fact]
    public async Task Deleting_a_tag_removes_only_its_links_and_keeps_the_projects()
    {
        Project p = SeedProject(OrgA);
        ProjectTag doomed = SeedTag(OrgA, "Doomed");
        ProjectTag other = SeedTag(OrgA, "Other");
        Link(p.Id, doomed.Id);
        Link(p.Id, other.Id);
        EnforceForeignKeys();
        var (_, tags, _) = Act(OrgA);

        (await tags.Delete(doomed.Id, default)).Should().BeOfType<NoContentResult>();

        _db.ChangeTracker.Clear();
        (await _db.ProjectTagLinks.Select(l => l.TagId).ToListAsync()).Should().Equal(other.Id);
        (await _db.Projects.CountAsync(x => x.Id == p.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_a_folder_clears_folder_id_and_keeps_the_projects()
    {
        ProjectFolder f = SeedFolder(OrgA, "F");
        Project p = SeedProject(OrgA);
        p.FolderId = f.Id;
        _db.SaveChanges();
        var (_, _, folders) = Act(OrgA);

        (await folders.Delete(f.Id, default)).Should().BeOfType<NoContentResult>();

        Project after = await Reload(p.Id);
        after.FolderId.Should().BeNull();
        (await _db.ProjectFolders.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Transfer_drops_links_and_folder()
    {
        _db.Organizations.Add(new Organization { Id = OrgB, Name = "B", Slug = "b", Kind = OrganizationKind.Team, CreatedAt = Old, UpdatedAt = Old });
        _db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = OrgB, UserId = _userId, Role = OrganizationRole.Admin });
        _db.SaveChanges();
        ProjectFolder f = SeedFolder(OrgA, "F");
        ProjectTag t = SeedTag(OrgA, "T");
        Project p = SeedProject(OrgA);
        p.FolderId = f.Id;
        _db.SaveChanges();
        Link(p.Id, t.Id);
        var (projects, _, _) = Act(OrgA);

        (await projects.Transfer(p.Id, new TransferProjectRequest(OrgB), default)).Should().BeOfType<OkObjectResult>();

        Project moved = await Reload(p.Id);
        moved.OrganizationId.Should().Be(OrgB);
        moved.FolderId.Should().BeNull();
        (await Snapshot()).Links.Should().Be(0);
        (await Snapshot()).Tags.Should().Be(1, "the tag stays with the source organization");
    }

    // --- 5. ETag ----------------------------------------------------------------------------------

    [Fact]
    public async Task List_and_get_validators_change_after_tag_assign_rename_folder_move_folder_delete_and_tag_delete()
    {
        Project p = SeedProject(OrgA);
        ProjectTag tag = SeedTag(OrgA, "Acme");
        ProjectFolder folder = SeedFolder(OrgA, "F");
        var (projects, tags, folders) = Act(OrgA);

        string? listTag = await ListETag(projects);
        string? getTag = await GetETag(projects, p.Id);
        listTag.Should().NotBeNullOrEmpty();
        (await ListETag(projects)).Should().Be(listTag, "an unchanged list keeps its validator");

        async Task AssertMoved(string step)
        {
            string? l = await ListETag(projects);
            string? g = await GetETag(projects, p.Id);
            l.Should().NotBe(listTag, $"list after {step}");
            g.Should().NotBe(getTag, $"get after {step}");
            listTag = l;
            getTag = g;
        }

        await Task.Delay(5);
        (await projects.SetOrganization(p.Id, new ProjectOrganizationRequest([tag.Id], null), default)).Result.Should().BeOfType<OkObjectResult>();
        await AssertMoved("tag assign");

        await Task.Delay(5);
        (await tags.Update(tag.Id, new ProjectTagRequest("Renamed", null), default)).Result.Should().BeOfType<OkObjectResult>();
        await AssertMoved("tag rename");

        await Task.Delay(5);
        (await projects.SetOrganization(p.Id, new ProjectOrganizationRequest([tag.Id], folder.Id), default)).Result.Should().BeOfType<OkObjectResult>();
        await AssertMoved("folder move");

        await Task.Delay(5);
        (await folders.Delete(folder.Id, default)).Should().BeOfType<NoContentResult>();
        await AssertMoved("folder delete");

        await Task.Delay(5);
        (await tags.Delete(tag.Id, default)).Should().BeOfType<NoContentResult>();
        await AssertMoved("tag delete");
    }

    [Fact]
    public async Task Bulk_organize_moves_the_validators_of_every_affected_project()
    {
        Project a = SeedProject(OrgA);
        Project b = SeedProject(OrgA);
        ProjectTag tag = SeedTag(OrgA, "T");
        var (projects, _, _) = Act(OrgA);
        string? before = await ListETag(projects);

        (await projects.Organize(new OrganizeProjectsRequest([a.Id, b.Id], [tag.Id], null), default)).Should().BeOfType<NoContentResult>();

        (await ListETag(projects)).Should().NotBe(before);
        (await Reload(a.Id)).UpdatedAt.Should().BeAfter(Old);
        (await Reload(b.Id)).UpdatedAt.Should().BeAfter(Old);
    }

    // --- 6. permissions and list filters ----------------------------------------------------------

    [Fact]
    public async Task A_billing_only_role_cannot_create_rename_or_assign_but_a_member_can()
    {
        Project p = SeedProject(OrgA);
        ProjectTag t = SeedTag(OrgA, "T");
        ProjectFolder f = SeedFolder(OrgA, "F");
        var before = await Snapshot();

        var (projects, tags, folders) = Act(OrgA, OrganizationRole.Billing);
        static void Denied(IActionResult? r) =>
            (r is ForbidResult || r is StatusCodeResult { StatusCode: 403 } || r is ObjectResult { StatusCode: 403 }).Should().BeTrue($"got {r?.GetType().Name}");
        Denied((await tags.Create(new ProjectTagRequest("N", null), default)).Result);
        Denied((await tags.Update(t.Id, new ProjectTagRequest("R", null), default)).Result);
        Denied(await tags.Delete(t.Id, default));
        Denied((await folders.Create(new ProjectFolderRequest("N"), default)).Result);
        Denied((await folders.Rename(f.Id, new ProjectFolderRequest("R"), default)).Result);
        Denied(await folders.Delete(f.Id, default));
        Denied((await projects.SetOrganization(p.Id, new ProjectOrganizationRequest([t.Id], null), default)).Result);
        Denied(await projects.Organize(new OrganizeProjectsRequest([p.Id], [t.Id], null), default));
        Denied((await projects.Create(new CreateProjectRequest("x", null, [t.Id]), default)).Result);
        (await Snapshot()).Should().BeEquivalentTo(before);

        var (mProjects, mTags, mFolders) = Act(OrgA, OrganizationRole.Member);
        (await mTags.Create(new ProjectTagRequest("N", null), default)).Result.Should().BeOfType<CreatedResult>();
        (await mTags.Update(t.Id, new ProjectTagRequest("R", null), default)).Result.Should().BeOfType<OkObjectResult>();
        (await mFolders.Create(new ProjectFolderRequest("N"), default)).Result.Should().BeOfType<CreatedResult>();
        (await mFolders.Rename(f.Id, new ProjectFolderRequest("R"), default)).Result.Should().BeOfType<OkObjectResult>();
        (await mProjects.SetOrganization(p.Id, new ProjectOrganizationRequest([t.Id], f.Id), default)).Result.Should().BeOfType<OkObjectResult>();
        (await mProjects.Organize(new OrganizeProjectsRequest([p.Id], null, [t.Id]), default)).Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task List_filters_combine_tags_with_AND_and_support_folder_and_unfiled()
    {
        ProjectTag red = SeedTag(OrgA, "Red");
        ProjectTag blue = SeedTag(OrgA, "Blue");
        ProjectFolder f1 = SeedFolder(OrgA, "F1");
        Project both = SeedProject(OrgA, "both");
        Project onlyRed = SeedProject(OrgA, "onlyRed");
        Project plain = SeedProject(OrgA, "plain");
        Link(both.Id, red.Id);
        Link(both.Id, blue.Id);
        Link(onlyRed.Id, red.Id);
        both.FolderId = f1.Id;
        _db.SaveChanges();
        var (projects, _, _) = Act(OrgA);

        async Task<List<string>> Names(Guid[]? tagIds, Guid? folder, bool unfiled) =>
            ((List<ProjectResponse>)((OkObjectResult)(await projects.List(tagIds, folder, unfiled, default)).Result!).Value!)
                .Select(r => r.Name).OrderBy(n => n).ToList();

        (await Names([red.Id], null, false)).Should().Equal("both", "onlyRed");
        (await Names([red.Id, blue.Id], null, false)).Should().Equal("both");
        (await Names([red.Id, blue.Id, Guid.NewGuid()], null, false)).Should().BeEmpty();
        (await Names(null, f1.Id, false)).Should().Equal("both");
        (await Names(null, null, true)).Should().Equal("onlyRed", "plain");
        (await Names([red.Id], null, true)).Should().Equal("onlyRed");
        (await Names(null, null, false)).Should().HaveCount(3);
    }

    // --- 7. response shape ------------------------------------------------------------------------

    [Fact]
    public async Task ProjectResponse_carries_folderId_and_sorted_tags_and_keeps_the_old_fields()
    {
        ProjectFolder f = SeedFolder(OrgA, "F");
        ProjectTag zed = SeedTag(OrgA, "zed", "red");
        ProjectTag alpha = SeedTag(OrgA, "Alpha", "teal");
        Project p = SeedProject(OrgA, "proj");
        var (projects, _, _) = Act(OrgA);

        var set = (ProjectResponse)((OkObjectResult)(await projects.SetOrganization(p.Id, new ProjectOrganizationRequest([zed.Id, alpha.Id], f.Id), default)).Result!).Value!;
        var got = (ProjectResponse)((OkObjectResult)(await projects.Get(p.Id, default)).Result!).Value!;

        foreach (ProjectResponse r in new[] { set, got })
        {
            r.Id.Should().Be(p.Id);
            r.Name.Should().Be("proj");
            r.Status.Should().Be("Draft");
            r.CreatedAt.Should().Be(Old);
            r.FolderId.Should().Be(f.Id);
            r.Tags!.Select(t => (t.Name, t.Color)).Should().Equal(("Alpha", "teal"), ("zed", "red"));
        }

        var created = (ProjectResponse)((CreatedAtActionResult)(await projects.Create(
            new CreateProjectRequest("new", "d", [alpha.Id], f.Id), default)).Result!).Value!;
        created.FolderId.Should().Be(f.Id);
        created.Tags.Should().ContainSingle().Which.Id.Should().Be(alpha.Id);
        created.Description.Should().Be("d");

        var plain = (ProjectResponse)((CreatedAtActionResult)(await projects.Create(new CreateProjectRequest("plain", null), default)).Result!).Value!;
        plain.FolderId.Should().BeNull();
        plain.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task Tag_and_folder_lists_report_project_counts()
    {
        ProjectTag t = SeedTag(OrgA, "T");
        ProjectFolder f = SeedFolder(OrgA, "F");
        Project p1 = SeedProject(OrgA);
        Project p2 = SeedProject(OrgA);
        Link(p1.Id, t.Id);
        Link(p2.Id, t.Id);
        p1.FolderId = f.Id;
        _db.SaveChanges();
        var (_, tags, folders) = Act(OrgA);

        ((List<ProjectTagResponse>)((OkObjectResult)(await tags.List(default)).Result!).Value!).Single().ProjectCount.Should().Be(2);
        ((List<ProjectFolderResponse>)((OkObjectResult)(await folders.List(default)).Result!).Value!).Single().ProjectCount.Should().Be(1);
    }

    [Fact]
    public async Task Too_many_tag_filters_is_a_400()
    {
        var (projects, _, _) = Act(OrgA);
        Guid[] ids = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray();
        var result = (await projects.List(ids, null, false, default)).Result;
        result.Should().BeOfType<BadRequestObjectResult>();
        Code(result).Should().Be("TOO_MANY_TAG_FILTERS");
        (await projects.List(ids.Take(20).ToArray(), null, false, default)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Tag_rename_conflict_leaves_project_updated_at_untouched()
    {
        ProjectTag a = SeedTag(OrgA, "A");
        SeedTag(OrgA, "B");
        Project p = SeedProject(OrgA);
        Link(p.Id, a.Id);
        var (_, tags, _) = Act(OrgA);

        var result = (await tags.Update(a.Id, new ProjectTagRequest("b", null), default)).Result;
        Code(result).Should().Be("TAG_NAME_TAKEN");
        (await Reload(p.Id)).UpdatedAt.Should().Be(Old);
    }

    [Fact]
    public async Task Folder_rename_conflict_leaves_project_updated_at_untouched()
    {
        ProjectFolder a = SeedFolder(OrgA, "A");
        SeedFolder(OrgA, "B");
        Project p = SeedProject(OrgA);
        p.FolderId = a.Id;
        _db.SaveChanges();
        var (_, _, folders) = Act(OrgA);

        var result = (await folders.Rename(a.Id, new ProjectFolderRequest("b"), default)).Result;
        Code(result).Should().Be("FOLDER_NAME_TAKEN");
        (await Reload(p.Id)).UpdatedAt.Should().Be(Old);
    }
}
