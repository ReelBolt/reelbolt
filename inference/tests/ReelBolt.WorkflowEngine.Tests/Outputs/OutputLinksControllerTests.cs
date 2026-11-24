#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// <c>GET /api/v1/output-links/{token}</c>: anonymous, but a link is only ever honoured after the
/// database says its user may still view the project, and every failure is the same empty 404.
/// </summary>
public class OutputLinksControllerTests
{
    private readonly IConfirmationTokenService _tokens = ConfirmationTokenService.CreateEphemeral();

    private OutputLinkService Links(OutputsWorld w) =>
        new(_tokens, new ConfigurationBuilder().Build(), w.Clock);

    private OutputLinksController Controller(OutputsWorld w, string? range = null)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = OutputsWorld.Bucket })
            .Build();
        var controller = new OutputLinksController(Links(w), new OutputLinkResolver(w.Db, w.Storage), w.S3.Object, config, w.Clock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (range is not null) controller.Request.Headers.Range = range;
        return controller;
    }

    private string Mint(OutputsWorld w, OutputLinkTarget target, Guid? user = null)
    {
        string url = Links(w).Create(target, user ?? w.UserId, null).Url;
        return url[(url.LastIndexOf('/') + 1)..];
    }

    private OutputLinkTarget RenderTarget(OutputsWorld w) => new(w.Project.Id, OutputLinkKind.StepResult, w.Render.Id, null);

    [Fact]
    public async Task A_link_to_a_held_output_answers_exactly_like_an_ended_link()
    {
        using OutputsWorld w = new();
        string token = Mint(w, RenderTarget(w));
        w.Project.CsamOutputHoldAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await w.Db.SaveChangesAsync();

        OutputLinksController heldController = Controller(w);
        IActionResult held = await heldController.Get(token, CancellationToken.None);
        OutputLinksController endedController = Controller(w);
        IActionResult ended = await endedController.Get("not-a-live-link", CancellationToken.None);

        ShouldBeTheOneNotFound(held);
        held.GetType().Should().Be(ended.GetType());
        ((Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)held).StatusCode
            .Should().Be(((Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)ended).StatusCode);
        heldController.Response.Headers.Select(h => $"{h.Key}={h.Value}").Order()
            .Should().Equal(endedController.Response.Headers.Select(h => $"{h.Key}={h.Value}").Order());
        w.S3.Invocations.Should().BeEmpty();
    }

    private static async Task<byte[]> BodyOf(IActionResult result)
    {
        FileStreamResult file = result.Should().BeOfType<FileStreamResult>().Subject;
        using MemoryStream copy = new();
        await file.FileStream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static void ShouldBeTheOneNotFound(IActionResult result) =>
        result.Should().BeOfType<NotFoundResult>("every failure answers the same empty 404");

    [Fact]
    public void The_route_is_anonymous_and_is_the_documented_path()
    {
        typeof(OutputLinksController).GetCustomAttributes(typeof(AllowAnonymousAttribute), true).Should().NotBeEmpty();
        var route = (RouteAttribute)typeof(OutputLinksController).GetCustomAttributes(typeof(RouteAttribute), true).Single();
        route.Template.Should().Be("api/v1/output-links");
    }

    [Fact]
    public async Task A_valid_render_link_streams_the_requested_range_with_safe_headers()
    {
        using OutputsWorld w = new();
        OutputLinksController controller = Controller(w, "bytes=0-99");

        IActionResult result = await controller.Get(Mint(w, RenderTarget(w)), CancellationToken.None);

        byte[] body = await BodyOf(result);
        body.Should().Equal(OutputsWorld.Bytes(1000).Take(100));
        IHeaderDictionary h = controller.Response.Headers;
        controller.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        h.ContentRange.ToString().Should().Be("bytes 0-99/1000");
        h.AcceptRanges.ToString().Should().Be("bytes");
        h.CacheControl.ToString().Should().Be("private, no-store");
        h["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        h.ContentSecurityPolicy.ToString().Should().Contain("sandbox");
        ((FileStreamResult)result).ContentType.Should().Be("video/mp4");
    }

    [Fact]
    public async Task A_valid_link_without_a_range_streams_the_whole_object()
    {
        using OutputsWorld w = new();
        IActionResult result = await Controller(w).Get(Mint(w, RenderTarget(w)), CancellationToken.None);
        (await BodyOf(result)).Length.Should().Be(1000);
    }

    [Fact]
    public async Task A_project_file_link_streams_the_file_under_the_safe_content_policy()
    {
        using OutputsWorld w = new();
        OutputLinksController controller = Controller(w);
        IActionResult result = await controller.Get(
            Mint(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.ProjectFile, w.File.Id, null)), CancellationToken.None);

        (await BodyOf(result)).Length.Should().Be(400);
        ((FileStreamResult)result).ContentType.Should().Be("audio/mpeg");
        controller.Response.Headers.ContentDisposition.ToString().Should().StartWith("inline");
    }

    [Fact]
    public async Task A_contact_sheet_link_streams_that_sheet_of_the_density()
    {
        using OutputsWorld w = new();
        (_, MediaDerivative strip) = w.AddRenderDerivatives();
        IActionResult result = await Controller(w).Get(
            Mint(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.Derivative, strip.Id, 1)), CancellationToken.None);

        (await BodyOf(result)).Should().Equal(OutputsWorld.Bytes(41));
    }

    [Fact]
    public async Task A_thumbnail_link_streams_the_thumbnail()
    {
        using OutputsWorld w = new();
        (MediaDerivative thumb, _) = w.AddRenderDerivatives();
        IActionResult result = await Controller(w).Get(
            Mint(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.Derivative, thumb.Id, null)), CancellationToken.None);

        (await BodyOf(result)).Length.Should().Be(30);
        ((FileStreamResult)result).ContentType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task An_expired_link_is_404()
    {
        using OutputsWorld w = new();
        string token = Mint(w, RenderTarget(w));
        w.Clock.Now = w.Clock.Now.AddMinutes(16);

        ShouldBeTheOneNotFound(await Controller(w).Get(token, CancellationToken.None));
    }

    [Fact]
    public async Task A_tampered_link_is_404()
    {
        using OutputsWorld w = new();
        string token = Mint(w, RenderTarget(w));
        int at = token.IndexOf('.') + 1;
        string tampered = token[..at] + (token[at] == 'A' ? 'B' : 'A') + token[(at + 1)..];

        ShouldBeTheOneNotFound(await Controller(w).Get(tampered, CancellationToken.None));
        ShouldBeTheOneNotFound(await Controller(w).Get("not-a-token", CancellationToken.None));
    }

    [Fact]
    public async Task A_user_removed_from_the_organization_after_issue_gets_404()
    {
        using OutputsWorld w = new();
        string token = Mint(w, RenderTarget(w));
        w.Db.OrganizationMemberships.RemoveRange(w.Db.OrganizationMemberships.ToList());
        await w.Db.SaveChangesAsync();

        ShouldBeTheOneNotFound(await Controller(w).Get(token, CancellationToken.None));
    }

    [Fact]
    public async Task A_user_demoted_to_billing_after_issue_gets_404()
    {
        using OutputsWorld w = new();
        string token = Mint(w, RenderTarget(w));
        w.Db.OrganizationMemberships.Single().Role = OrganizationRole.Billing;
        await w.Db.SaveChangesAsync();

        ShouldBeTheOneNotFound(await Controller(w).Get(token, CancellationToken.None));
    }

    [Fact]
    public async Task A_user_whose_tokens_were_invalidated_after_issue_gets_404()
    {
        using OutputsWorld w = new();
        string token = Mint(w, RenderTarget(w));
        w.Db.ApplicationUsers.Single().TokensInvalidatedAt = w.Clock.Now.UtcDateTime.AddSeconds(5);
        await w.Db.SaveChangesAsync();

        ShouldBeTheOneNotFound(await Controller(w).Get(token, CancellationToken.None));
    }

    [Fact]
    public async Task Tokens_invalidated_before_issue_do_not_affect_the_link()
    {
        using OutputsWorld w = new();
        w.Db.ApplicationUsers.Single().TokensInvalidatedAt = w.Clock.Now.UtcDateTime.AddHours(-1);
        await w.Db.SaveChangesAsync();

        (await Controller(w).Get(Mint(w, RenderTarget(w)), CancellationToken.None)).Should().BeOfType<FileStreamResult>();
    }

    [Fact]
    public async Task A_deleted_user_gets_404()
    {
        using OutputsWorld w = new();
        string token = Mint(w, RenderTarget(w));
        w.Db.ApplicationUsers.RemoveRange(w.Db.ApplicationUsers.ToList());
        await w.Db.SaveChangesAsync();

        ShouldBeTheOneNotFound(await Controller(w).Get(token, CancellationToken.None));
    }

    [Fact]
    public async Task A_link_to_another_organizations_project_is_404()
    {
        using OutputsWorld w = new();
        (Project theirs, WorkflowStepResult theirRender) = w.AddForeignProject();

        // Correctly signed for our user, but the user is not a member of that organization.
        ShouldBeTheOneNotFound(await Controller(w).Get(
            Mint(w, new OutputLinkTarget(theirs.Id, OutputLinkKind.StepResult, theirRender.Id, null)), CancellationToken.None));
    }

    [Fact]
    public async Task A_link_pairing_our_project_with_another_projects_render_is_404()
    {
        using OutputsWorld w = new();
        (_, WorkflowStepResult theirRender) = w.AddForeignProject();

        ShouldBeTheOneNotFound(await Controller(w).Get(
            Mint(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.StepResult, theirRender.Id, null)), CancellationToken.None));
    }

    [Theory]
    [InlineData("agentFiles/x.mp4")]
    [InlineData("outputFiles-tmp/x.mp4")]
    [InlineData("userFiles/x.mp4")]
    public async Task A_render_key_outside_the_projects_output_prefix_is_404(string suffix)
    {
        using OutputsWorld w = new();
        string key = $"projects/{w.Project.Id}/{suffix}";
        w.Storage.Objects[key] = OutputsWorld.Bytes(10);
        w.Render.OutputStorageKey = key;
        await w.Db.SaveChangesAsync();

        ShouldBeTheOneNotFound(await Controller(w).Get(Mint(w, RenderTarget(w)), CancellationToken.None));
    }

    [Fact]
    public async Task A_project_file_whose_key_names_another_project_is_404()
    {
        using OutputsWorld w = new();
        (Project theirs, _) = w.AddForeignProject();
        string key = $"projects/{theirs.Id}/userFiles/secret.mp3";
        w.Storage.Objects[key] = OutputsWorld.Bytes(10);
        w.File.StorageKey = key;
        await w.Db.SaveChangesAsync();

        ShouldBeTheOneNotFound(await Controller(w).Get(
            Mint(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.ProjectFile, w.File.Id, null)), CancellationToken.None));
    }

    [Fact]
    public async Task A_sheet_index_past_the_density_or_on_a_non_filmstrip_row_is_404()
    {
        using OutputsWorld w = new();
        (MediaDerivative thumb, MediaDerivative strip) = w.AddRenderDerivatives();

        ShouldBeTheOneNotFound(await Controller(w).Get(
            Mint(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.Derivative, strip.Id, 2)), CancellationToken.None));
        ShouldBeTheOneNotFound(await Controller(w).Get(
            Mint(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.Derivative, thumb.Id, 0)), CancellationToken.None));
    }

    [Fact]
    public async Task A_render_missing_from_storage_is_404()
    {
        using OutputsWorld w = new();
        w.Storage.Objects.Remove(w.RenderKey);

        ShouldBeTheOneNotFound(await Controller(w).Get(Mint(w, RenderTarget(w)), CancellationToken.None));
    }

    [Fact]
    public async Task A_pruned_take_is_404()
    {
        using OutputsWorld w = new();
        string token = Mint(w, RenderTarget(w));
        w.Render.OutputStorageKey = null;
        await w.Db.SaveChangesAsync();

        ShouldBeTheOneNotFound(await Controller(w).Get(token, CancellationToken.None));
    }
}
