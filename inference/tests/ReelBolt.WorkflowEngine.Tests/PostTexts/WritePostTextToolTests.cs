#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.PostTexts;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PostTexts;

/// <summary>WritePostText through the real PostTextsController path, with a fake writer and fake charges (no paid calls).</summary>
public class WritePostTextToolTests
{
    private sealed class Writer : IPostTextWriter
    {
        public int Calls { get; private set; }
        public PostTextWriteRequest? Last { get; private set; }

        public Task<PostTextWriteResult?> WriteAsync(PostTextWriteRequest request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            PostTextChecked draft = PostTextValidator.Check(new PostTextDraft("x", null, "Calm day", ["lake"]), PostTextPlatformLimits.Find("x")!);
            return Task.FromResult<PostTextWriteResult?>(new PostTextWriteResult([draft], false, false, null));
        }
    }

    private static (AIFunction Tool, Writer Writer, Billing.FakeNonWorkflowCharges Charges, InferenceApiDbContext Db, Guid ProjectId, Guid OutputId) Build(bool refuse = false)
    {
        Guid owner = Guid.NewGuid();
        var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var project = new Project { Id = Guid.NewGuid(), Name = "Trip", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner) };
        db.Projects.Add(project);
        var file = new ProjectFile { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = "render.mp4", MimeType = "video/mp4", StorageKey = "k" };
        db.ProjectFiles.Add(file);
        db.SaveChanges();

        var user = new Mock<ICurrentUser>();
        user.SetupGet(x => x.UserId).Returns(owner);
        user.SetupGet(x => x.IsAuthenticated).Returns(true);
        user.SetupGet(x => x.OrganizationId).Returns(TestTenancy.OrgOf(owner));
        user.SetupGet(x => x.OrgRole).Returns(ReelBolt.Shared.Tenancy.OrganizationRole.Member);
        var writer = new Writer();
        var charges = new Billing.FakeNonWorkflowCharges { RefuseHolds = refuse };
        IProjectAccessService access = TestTenancy.Access(db, user.Object);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(user.Object);
        services.AddSingleton(access);
        services.AddSingleton<IPostTextWriter>(writer);
        services.AddSingleton(new PostTextContextBuilder(db));
        services.AddSingleton(new PostTextRateLimiter(Microsoft.Extensions.Options.Options.Create(new PostTextOptions())));
        services.AddSingleton<INonWorkflowChargeService>(charges);
        services.AddLogging();
        IServiceProvider provider = services.BuildServiceProvider();

        var tools = new AssistantPostTextTools(access, provider);
        AIFunction tool = tools.GetTools(new AssistantToolContext(owner)).Single();
        return (tool, writer, charges, db, project.Id, file.Id);
    }

    private static async Task<JsonNode> Call(AIFunction tool, Guid outputId, Guid projectId, string? hashtagSource = null)
    {
        object? raw = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["outputId"] = outputId,
            ["platforms"] = new[] { "x" },
            ["projectId"] = projectId,
            ["hashtagSource"] = hashtagSource,
        });
        string json = raw is JsonElement e ? (e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()) : (string)raw!;
        return JsonNode.Parse(json)!;
    }

    [Fact]
    public async Task The_tool_runs_the_app_write_route_holds_and_settles_credits_and_saves_nothing()
    {
        var s = Build();
        using InferenceApiDbContext _ = s.Db;

        JsonNode answer = await Call(s.Tool, s.OutputId, s.ProjectId);

        ((int)answer["httpStatus"]!).Should().Be(200);
        answer["result"]!["drafts"]!.AsArray().Should().ContainSingle();
        s.Charges.Holds.Should().ContainSingle().Which.Source.Should().Be(UsageSource.PostText);
        s.Charges.Settled.Should().ContainSingle();
        s.Db.PostTexts.Count().Should().Be(0);
        s.Writer.Last!.Context.Used.Transcript.Should().BeFalse("the tool never reads the video");
        s.Writer.Last.WebHashtags.Should().BeFalse();
    }

    [Fact]
    public async Task Web_hashtags_without_a_search_provider_fall_back_and_an_empty_balance_is_the_routes_402()
    {
        var web = Build();
        using InferenceApiDbContext _ = web.Db;
        JsonNode ok = await Call(web.Tool, web.OutputId, web.ProjectId, "web");
        // No web search provider is configured here, so the route skips the search and falls back to the model's tags.
        ((int)ok["httpStatus"]!).Should().Be(200);
        web.Writer.Calls.Should().Be(1);
        web.Writer.Last!.WebHashtags.Should().BeFalse();

        var broke = Build(refuse: true);
        using InferenceApiDbContext _2 = broke.Db;
        JsonNode refused = await Call(broke.Tool, broke.OutputId, broke.ProjectId);
        ((int)refused["httpStatus"]!).Should().Be(402);
        broke.Writer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_foreign_output_is_404_and_the_description_has_no_readVideo()
    {
        var s = Build();
        using InferenceApiDbContext _ = s.Db;
        JsonNode answer = await Call(s.Tool, Guid.NewGuid(), s.ProjectId);
        ((int)answer["httpStatus"]!).Should().Be(404);
        s.Tool.JsonSchema.GetRawText().Should().NotContain("readVideo");
        s.Tool.Description.Should().Contain("web search provider").And.Contain("spends credits").And.NotContainEquivalentOf("trending now");
    }
}
