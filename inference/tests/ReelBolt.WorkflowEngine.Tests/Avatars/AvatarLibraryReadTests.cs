#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Reading the workspace avatar library (lane L9 of docs/design/avatar-objects.md): the list envelope, one item, a
/// picture; who may read; that another organization's item does not exist; that an item of a kind whose switch is off
/// is absent; and the <c>library</c> block of the project requirements.
/// </summary>
public class AvatarLibraryReadTests
{
    [Fact]
    public async Task The_list_answers_the_items_by_name_and_the_library_requirements()
    {
        var l = new L();
        (Guid machine, _) = await l.PublishObjectAsync("Machine");
        (Guid anna, _) = await l.PublishPersonAsync("Anna");
        Project second = l.AddProject();

        (int status, JsonElement body) = H.Read(await l.Controller().List(null, CancellationToken.None));

        status.Should().Be(200);
        JsonElement[] items = [.. body.GetProperty("items").EnumerateArray()];
        items.Select(i => i.GetProperty("id").GetGuid()).Should().Equal(anna, machine);
        items.Should().OnlyContain(i => i.GetProperty("canManage").GetBoolean() && !i.GetProperty("imported").GetBoolean()
                                        && i.GetProperty("importCount").GetInt32() == 1 && i.GetProperty("usable").GetBoolean());
        items[0].GetProperty("primaryReferenceId").GetGuid().Should().NotBeEmpty();
        JsonElement library = body.GetProperty("library");
        library.GetProperty("enabled").GetBoolean().Should().BeTrue();
        library.GetProperty("canManage").GetBoolean().Should().BeTrue();
        library.GetProperty("consentStatementVersion").GetString().Should().Be("avatar-consent-workspace-2026-11");
        library.GetProperty("consentStatements").GetProperty("en").GetString().Should().Be(AvatarWorkspaceConsentStatement.English);
        library.GetProperty("consentStatements").GetProperty("it").GetString().Should().Be(AvatarWorkspaceConsentStatement.Italian);
        library.GetProperty("objectStatementVersion").GetString().Should().Be("avatar-object-workspace-2026-11");
        library.GetProperty("objectStatements").GetProperty("en").GetString().Should().Be(AvatarWorkspaceObjectStatement.English);
        library.GetProperty("maxItemsPerKind").GetInt32().Should().Be(100);

        // With a project, each item says whether that project already holds an import.
        JsonElement[] forSource = [.. H.Read(await l.Controller().List(l.Project.Id, CancellationToken.None)).Body.GetProperty("items").EnumerateArray()];
        forSource.Should().OnlyContain(i => i.GetProperty("imported").GetBoolean());
        JsonElement[] forSecond = [.. H.Read(await l.Controller().List(second.Id, CancellationToken.None)).Body.GetProperty("items").EnumerateArray()];
        forSecond.Should().OnlyContain(i => !i.GetProperty("imported").GetBoolean());
        H.Read(await l.Controller().List(Guid.NewGuid(), CancellationToken.None)).Status.Should().Be(404, "an unknown project is refused as its own routes refuse it");

        (int one, JsonElement item) = H.Read(await l.Controller().Get(anna, l.Project.Id, CancellationToken.None));
        one.Should().Be(200);
        item.GetProperty("name").GetString().Should().Be("Anna");
        item.GetProperty("imported").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Any_member_reads_only_an_owner_or_admin_manages_and_billing_reads_nothing()
    {
        var l = new L();
        (Guid anna, _) = await l.PublishPersonAsync("Anna");

        (int status, JsonElement body) = H.Read(await l.Controller(user: l.Harness.Member()).List(null, CancellationToken.None));
        status.Should().Be(200);
        JsonElement[] listed = [.. body.GetProperty("items").EnumerateArray()];
        listed.Should().ContainSingle().Which.GetProperty("canManage").GetBoolean().Should().BeFalse();
        body.GetProperty("library").GetProperty("canManage").GetBoolean().Should().BeFalse();
        H.Read(await l.Controller(user: l.Harness.Member()).Get(anna, null, CancellationToken.None)).Body
            .GetProperty("canManage").GetBoolean().Should().BeFalse();
        H.Read(await l.Controller(user: l.Harness.InRole(OrganizationRole.Admin)).Get(anna, null, CancellationToken.None)).Body
            .GetProperty("canManage").GetBoolean().Should().BeTrue();

        H.Read(await l.Controller(user: l.Harness.InRole(OrganizationRole.Billing)).List(null, CancellationToken.None)).Status.Should().Be(403);
        H.Read(await l.Controller(user: l.Harness.InRole(OrganizationRole.Billing)).Get(anna, null, CancellationToken.None)).Status.Should().Be(403);

        // A member may not rename, confirm again, withdraw or delete: 403 inside their own organization.
        AvatarLibraryController asMember = l.Controller(user: l.Harness.Member());
        H.Read(await asMember.Update(anna, new(Name: "Other"), CancellationToken.None)).Status.Should().Be(403);
        H.Read(await asMember.Reconfirm(anna, new(AvatarWorkspaceConsentStatement.English, "en", true, 30), CancellationToken.None)).Status.Should().Be(403);
        H.Read(await asMember.RevokeConsent(anna, CancellationToken.None)).Status.Should().Be(403);
        H.Read(await asMember.Delete(anna, CancellationToken.None)).Status.Should().Be(403);
        using InferenceApiDbContext db = l.NewDb();
        AvatarLibraryItem item = await db.AvatarLibraryItems.SingleAsync();
        item.Name.Should().Be("Anna");
        item.ConsentRevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_item_does_not_exist_for_anyone()
    {
        var l = new L();
        (Guid anna, _) = await l.PublishPersonAsync("Anna");
        Guid reference;
        using (InferenceApiDbContext db = l.NewDb())
            reference = (await db.AvatarLibraryReferences.SingleAsync()).Id;

        // An Owner of ANOTHER organization: the item is 404 on every route, never 403.
        AvatarLibraryController stranger = l.Controller(user: H.Stranger());
        H.Read(await stranger.List(null, CancellationToken.None)).Body.GetProperty("items").GetArrayLength().Should().Be(0);
        H.Read(await stranger.Get(anna, null, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await stranger.Picture(anna, reference, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await stranger.Update(anna, new(Name: "Mine"), CancellationToken.None)).Status.Should().Be(404);
        H.Read(await stranger.Reconfirm(anna, new(AvatarWorkspaceConsentStatement.English, "en", true, 30), CancellationToken.None)).Status.Should().Be(404);
        H.Read(await stranger.RevokeConsent(anna, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await stranger.Delete(anna, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await stranger.List(l.Project.Id, CancellationToken.None)).Status.Should().Be(404, "the project is another organization's too");

        // And it cannot be imported into the other organization's project.
        H.User other = H.Stranger();
        Project theirs = l.AddProject("Theirs", other.UserId, other.OrganizationId!.Value);
        (await l.ImportResultAsync(anna, theirs, other)).Status.Should().Be(404);

        using InferenceApiDbContext read = l.NewDb();
        AvatarLibraryItem item = await read.AvatarLibraryItems.SingleAsync();
        item.Name.Should().Be("Anna");
        item.ConsentRevokedAt.Should().BeNull();
        (await read.ProjectAvatars.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_table_itself_is_tenant_filtered()
    {
        var l = new L();
        await l.PublishPersonAsync("Anna");
        var tenant = new TestTenancy.MutableTenant();
        using var db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(l.Harness.DatabaseName).Options, tenant);

        (await db.AvatarLibraryItems.CountAsync()).Should().Be(1, "a system scope sees every row");
        tenant.ActAsOrganization(Guid.NewGuid());
        (await db.AvatarLibraryItems.CountAsync()).Should().Be(0, "another organization's library is invisible without any explicit check");
        tenant.ActAsOrganization(null);
        (await db.AvatarLibraryItems.CountAsync()).Should().Be(0, "no organization and no system scope fails closed");
        tenant.ActAsOrganization(l.OrganizationId);
        (await db.AvatarLibraryItems.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task While_neither_switch_is_on_every_route_is_404_and_a_kind_whose_switch_is_off_is_absent()
    {
        var l = new L();
        (Guid anna, Guid annaAvatar) = await l.PublishPersonAsync("Anna");
        (Guid machine, _) = await l.PublishObjectAsync("Machine");
        Project second = l.AddProject();

        // People off: the person's item is absent; the object's stays.
        l.Harness.Options.Enabled = false;
        H.Read(await l.Controller().List(null, CancellationToken.None)).Body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).Should().Equal(machine);
        H.Read(await l.Controller().Get(anna, null, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await l.Controller().Update(anna, new(Name: "Other"), CancellationToken.None)).Status.Should().Be(404);
        H.Read(await l.Controller().RevokeConsent(anna, CancellationToken.None)).Status.Should().Be(404);
        (await l.ImportResultAsync(anna, second)).Status.Should().Be(404);
        H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, annaAvatar), CancellationToken.None)).Status.Should().Be(404);
        (await l.ImportResultAsync(machine, second)).Status.Should().Be(201);

        // Objects off: the other way round.
        l.Harness.Options.Enabled = true;
        l.Harness.Options.Objects.Enabled = false;
        H.Read(await l.Controller().List(null, CancellationToken.None)).Body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).Should().Equal(anna);
        H.Read(await l.Controller().Get(machine, null, CancellationToken.None)).Status.Should().Be(404);

        // Neither: nothing answers, delete included.
        l.Harness.Options.Enabled = false;
        H.Read(await l.Controller().List(null, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await l.Controller().Get(anna, null, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, annaAvatar), CancellationToken.None)).Status.Should().Be(404);
        H.Read(await l.Controller().Update(anna, new(Name: "Other"), CancellationToken.None)).Status.Should().Be(404);
        H.Read(await l.Controller().Reconfirm(anna, new(AvatarWorkspaceConsentStatement.English, "en", true, 30), CancellationToken.None)).Status.Should().Be(404);
        H.Read(await l.Controller().RevokeConsent(anna, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await l.Controller().Delete(anna, CancellationToken.None)).Status.Should().Be(404);
        (await l.ImportResultAsync(anna, second)).Status.Should().Be(404);

        // With one switch on, an item of the OTHER kind can still be deleted: removing a likeness is never blocked.
        l.Harness.Options.Objects.Enabled = true;
        H.Read(await l.Controller().Delete(anna, CancellationToken.None)).Status.Should().Be(200);
    }

    [Fact]
    public async Task A_picture_is_served_under_the_safe_content_policy_and_nothing_else_is()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 4);
        ProjectFile clip = l.Harness.AddProbedMedia("anna.mp4", "video/mp4", 4, "h264");
        (Guid anna, _) = await l.PublishPersonAsync("Anna", face, clip);
        Guid picture, video;
        using (InferenceApiDbContext db = l.NewDb())
        {
            picture = (await db.AvatarLibraryReferences.SingleAsync(r => r.Kind == AvatarReferenceKind.Image)).Id;
            video = (await db.AvatarLibraryReferences.SingleAsync(r => r.Kind == AvatarReferenceKind.Video)).Id;
        }

        AvatarLibraryController controller = l.Controller(user: l.Harness.Member());
        IActionResult result = await controller.Picture(anna, picture, CancellationToken.None);

        FileStreamResult file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be("image/png");
        using (var copy = new MemoryStream())
        {
            await file.FileStream.CopyToAsync(copy);
            copy.ToArray().Should().Equal(l.Harness.Objects[face.StorageKey]);
        }

        controller.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        controller.Response.Headers["Content-Security-Policy"].ToString().Should().Contain("sandbox");
        controller.Response.Headers.CacheControl.ToString().Should().StartWith("private");
        controller.Response.Headers["Content-Disposition"].ToString().Should().NotContain("anna", "the file name does not leave with the picture");

        H.Read(await l.Controller().Picture(anna, video, CancellationToken.None)).Status.Should().Be(404, "pictures only");
        H.Read(await l.Controller().Picture(anna, Guid.NewGuid(), CancellationToken.None)).Status.Should().Be(404);
        H.Read(await l.Controller().Picture(Guid.NewGuid(), picture, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await l.Controller(user: l.Harness.InRole(OrganizationRole.Billing)).Picture(anna, picture, CancellationToken.None)).Status.Should().Be(403);
    }

    [Fact]
    public async Task The_project_requirements_carry_the_library_block()
    {
        var l = new L();

        JsonElement library = H.Read(await l.Harness.Controller().Requirements(l.Project.Id, CancellationToken.None)).Body.GetProperty("library");
        library.GetProperty("enabled").GetBoolean().Should().BeTrue();
        library.GetProperty("canManage").GetBoolean().Should().BeTrue();
        library.GetProperty("consentStatementVersion").GetString().Should().Be(AvatarWorkspaceConsentStatement.Version);
        library.GetProperty("consentStatements").GetProperty("it").GetString().Should().Be(AvatarWorkspaceConsentStatement.Italian);
        library.GetProperty("objectStatementVersion").GetString().Should().Be(AvatarWorkspaceObjectStatement.Version);
        library.GetProperty("objectStatements").GetProperty("it").GetString().Should().Be(AvatarWorkspaceObjectStatement.Italian);
        library.GetProperty("maxItemsPerKind").GetInt32().Should().Be(AvatarLibraryLimits.MaxItemsPerKind);

        H.Read(await l.Harness.Controller(user: l.Harness.Member()).Requirements(l.Project.Id, CancellationToken.None)).Body
            .GetProperty("library").GetProperty("canManage").GetBoolean().Should().BeFalse();
    }
}
