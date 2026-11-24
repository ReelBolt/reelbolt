#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Tests.Avatars;
using ReelBolt.WorkflowEngine.Tests.Billing;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using Rig = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarBuildRig;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// Lane L7 of docs/design/avatar-objects.md: the avatar group of the assistant and MCP (objects from files and from a
/// video, variants of objects, library imports, clips with avatars, the library list) and the ONE per-call media rule
/// every surface asks. Names in, names out; a person's avatar is never made here; a card is built from what the tool
/// resolves; the statement and the media permission come from the card in chat and from the caller over MCP.
/// </summary>
public class AssistantAvatarObjectToolsTests
{
    private static readonly Regex GuidShape = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-", RegexOptions.Compiled);

    private static IConfiguration Switches(bool people = true, bool objects = true) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Avatars:Enabled"] = people ? "true" : "false",
            ["Avatars:Objects:Enabled"] = objects ? "true" : "false"
        })
        .Build();

    private static AssistantAvatarTools Tools(
        Rig rig, InferenceApiDbContext db, ICurrentUser user, IConfirmationTokenService tokens,
        ISubjectClipService? clips = null, AvatarLibraryService? library = null, bool people = true, bool objects = true) =>
        new(db, TestTenancy.Access(db, user), rig.H.Storage.Object, Switches(people, objects), NullLogger<AssistantAvatarTools>.Instance,
            tokens, user, rig.Creator(db), rig.Service(db), clips, library, rig.H.Entitlements, new MediaEgressCallPermission(db));

    private static async Task<(JsonElement Json, string Raw)> InvokeAsync(
        AssistantAvatarTools tools, AssistantToolContext context, string name, params (string Key, object? Value)[] args)
    {
        AIFunction fn = tools.GetTools(context).Single(t => t.Name == name);
        AIFunctionArguments arguments = new();
        foreach ((string key, object? value) in args) arguments[key] = value;
        return Parse(await fn.InvokeAsync(arguments));
    }

    private static (JsonElement Json, string Raw) Parse(object? raw)
    {
        string text = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } s => s.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw!.ToString()!
        };
        return (JsonDocument.Parse(text).RootElement.Clone(), text);
    }

    private static string? Code(JsonElement json) => json.TryGetProperty("code", out JsonElement c) ? c.GetString() : null;

    private static string? Error(JsonElement json) => json.TryGetProperty("error", out JsonElement e) ? e.GetString() : null;

    private static AvatarFileArg[] KettleFiles(string? secondRole = "Detail") =>
        [new AvatarFileArg("kettle-front.png"), new AvatarFileArg("kettle-side.png", secondRole)];

    private static int Objects(Rig rig)
    {
        using InferenceApiDbContext db = rig.H.NewDb();
        return db.ProjectAvatars.Count(a => a.SubjectKind == AvatarSubjectKind.Object);
    }

    private static int Jobs(Rig rig)
    {
        using InferenceApiDbContext db = rig.H.NewDb();
        return db.AvatarBuildJobs.Count();
    }

    // ================================================================== the one media rule

    private static DefaultHttpContext Request(string path, string method = "POST", params Claim[] claims)
    {
        DefaultHttpContext http = new();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), .. claims], "Bearer"));
        http.Request.Method = method;
        http.Request.Path = path;
        return http;
    }

    private static InferenceApiDbContext Db() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase("media-rule-" + Guid.NewGuid()).Options);

    [Fact]
    public async Task A_person_signed_in_to_the_app_gives_it_on_each_surfaces_own_route_and_nowhere_else()
    {
        using InferenceApiDbContext db = Db();
        var rule = new MediaEgressCallPermission(db);
        Guid p = Guid.NewGuid();
        Guid u = Guid.NewGuid();
        (MediaEgressSurface Surface, string Own)[] surfaces =
        [
            (MediaEgressSurface.PictureOperation, $"/api/v1/projects/{p}/image-documents/{Guid.NewGuid()}/operations"),
            (MediaEgressSurface.SubjectClip, $"/api/v1/projects/{p}/subject-clips"),
            (MediaEgressSurface.SubjectClip, $"/api/v1/projects/{p}/subject-clips/estimate"),
            (MediaEgressSurface.AvatarVariant, $"/api/v1/projects/{p}/avatars/{Guid.NewGuid()}/variants"),
            (MediaEgressSurface.ObjectExtraction, $"/api/v1/projects/{p}/avatars/extract-object"),
        ];

        foreach ((MediaEgressSurface surface, string own) in surfaces)
        {
            (await rule.GrantorAsync(MediaEgressCall.FromRequest(surface, u, p, Request(own)), CancellationToken.None))
                .Should().Be(MediaEgressPermission.ByApp, own);
            (await rule.GrantorAsync(MediaEgressCall.FromRequest(surface, u, p, Request(own.ToUpperInvariant() + "/")), CancellationToken.None))
                .Should().Be(MediaEgressPermission.ByApp, "case and a trailing slash do not matter");
            (await rule.GrantorAsync(MediaEgressCall.FromRequest(surface, u, p, Request(own, "GET")), CancellationToken.None))
                .Should().BeNull("only the POST of the route");
            (await rule.GrantorAsync(MediaEgressCall.FromRequest(surface, u, p, Request("/api/v1/assistant/chat")), CancellationToken.None))
                .Should().BeNull("the assistant runs a surface inside its chat request, whose path is not the surface's");
            (await rule.GrantorAsync(MediaEgressCall.FromRequest(surface, u, p, Request("/api/v1/assistant/tool-call")), CancellationToken.None))
                .Should().BeNull("the MCP bridge likewise");
        }

        // A route of the one surface is not a route of another, and the run surface never grants to a request.
        (await rule.GrantorAsync(MediaEgressCall.FromRequest(MediaEgressSurface.ObjectExtraction, u, p, Request(surfaces[3].Own)), CancellationToken.None))
            .Should().BeNull();
        (await rule.GrantorAsync(MediaEgressCall.FromRequest(MediaEgressSurface.WorkflowRun, u, p,
            Request($"/api/v1/projects/{p}/workflows/{Guid.NewGuid()}/execute")), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Every_other_caller_on_the_route_is_refused_unless_its_own_rule_says_yes()
    {
        using InferenceApiDbContext db = Db();
        Guid org = Guid.NewGuid();
        Guid scoped = Guid.NewGuid();
        Guid unscoped = Guid.NewGuid();
        db.ApiKeys.Add(new ApiKey { Id = scoped, OrganizationId = org, Name = "with", Scopes = [ApiScopes.RunsExecute, ApiScopes.MediaEgress] });
        db.ApiKeys.Add(new ApiKey { Id = unscoped, OrganizationId = org, Name = "without", Scopes = [ApiScopes.RunsExecute] });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var rule = new MediaEgressCallPermission(db);
        Guid p = Guid.NewGuid();
        string own = $"/api/v1/projects/{p}/subject-clips";
        async Task<string?> Grantor(HttpContext? http) =>
            await rule.GrantorAsync(MediaEgressCall.FromRequest(MediaEgressSurface.SubjectClip, Guid.NewGuid(), p, http), CancellationToken.None);

        (await Grantor(Request(own, "POST", new Claim(AuthClaims.ApiKeyId, scoped.ToString())))).Should().Be(MediaEgressPermission.ByApiKey);
        (await Grantor(Request(own, "POST", new Claim(AuthClaims.ApiKeyId, unscoped.ToString())))).Should().BeNull("a key needs media.egress");
        (await Grantor(Request(own, "POST", new Claim(AuthClaims.ApiKeyId, Guid.NewGuid().ToString())))).Should().BeNull("an unknown key holds no scope");
        (await Grantor(Request(own, "POST", new Claim(AuthClaims.Scope, "media.egress")))).Should().BeNull("an MCP OAuth token is not a session");
        (await Grantor(Request(own, "POST", new Claim(OnBehalfOfTokenIssuer.ActorClaim, "workflow-engine")))).Should().BeNull("the engine acting for someone");
        DefaultHttpContext anonymous = new();
        anonymous.Request.Method = "POST";
        anonymous.Request.Path = own;
        (await Grantor(anonymous)).Should().BeNull("nobody is signed in");
        (await Grantor(null)).Should().BeNull("no request at all (a background worker)");

        // Without a database the key and MCP rules cannot be read: refused (fail closed).
        (await new MediaEgressCallPermission().GrantorAsync(MediaEgressCall.FromRequest(MediaEgressSurface.SubjectClip, Guid.NewGuid(), p,
            Request(own, "POST", new Claim(AuthClaims.ApiKeyId, scoped.ToString()))), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task In_chat_only_a_card_that_was_shown_and_allowed_gives_it_and_over_MCP_only_the_users_switch()
    {
        using InferenceApiDbContext db = Db();
        Guid on = Guid.NewGuid();
        Guid off = Guid.NewGuid();
        db.ApplicationUsers.Add(new ApplicationUser { Id = on, Email = "on@example.com", McpMediaEgressEnabled = true });
        db.ApplicationUsers.Add(new ApplicationUser { Id = off, Email = "off@example.com", McpMediaEgressEnabled = false });
        await db.SaveChangesAsync();
        var rule = new MediaEgressCallPermission(db);
        Guid p = Guid.NewGuid();

        MediaEgressCall chat = MediaEgressCall.FromTool(MediaEgressSurface.SubjectClip, new AssistantToolContext(on, p, ChatApproved: true), p);
        chat.Via.Should().Be(MediaEgressVia.AssistantChat);
        (await rule.GrantorAsync(chat, CancellationToken.None)).Should().BeNull("no card was allowed for this call");
        using (AssistantMediaEgressApproval.Enter())
        {
            (await rule.GrantorAsync(chat, CancellationToken.None)).Should().Be(MediaEgressPermission.ByAssistantCard);
            (await rule.GrantorAsync(chat with { ChatApproved = false }, CancellationToken.None))
                .Should().BeNull("a context the chat controller did not build (the workflow-assistant route) is never a card");
        }

        MediaEgressCall mcp(Guid user) => MediaEgressCall.FromTool(
            MediaEgressSurface.ObjectExtraction, new AssistantToolContext(user, p, Initiator: ExecutionInitiator.Mcp), p);
        mcp(on).Via.Should().Be(MediaEgressVia.Mcp);
        (await rule.GrantorAsync(mcp(on), CancellationToken.None)).Should().Be(MediaEgressPermission.ByMcp);
        (await rule.GrantorAsync(mcp(off), CancellationToken.None)).Should().BeNull();
        (await rule.GrantorAsync(mcp(Guid.NewGuid()), CancellationToken.None)).Should().BeNull("a missing row is off");
        using (AssistantMediaEgressApproval.Enter())
            (await rule.GrantorAsync(mcp(off), CancellationToken.None)).Should().BeNull("a card mark is not the MCP switch");

        // The switch is read on every call, never cached.
        ApplicationUser row = await db.ApplicationUsers.SingleAsync(u => u.Id == on);
        row.McpMediaEgressEnabled = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await rule.GrantorAsync(mcp(on), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public void The_surfaces_are_append_only_and_the_old_seams_are_gone()
    {
        ((int)MediaEgressSurface.PictureOperation).Should().Be(0);
        ((int)MediaEgressSurface.SubjectClip).Should().Be(1);
        ((int)MediaEgressSurface.AvatarVariant).Should().Be(2);
        ((int)MediaEgressSurface.ObjectExtraction).Should().Be(3);
        ((int)MediaEgressSurface.WorkflowRun).Should().Be(4);
        typeof(MediaEgressCallPermission).Assembly.GetTypes()
            .Where(t => t.Name.Contains("MediaEgressCall") && t.IsInterface).Should().ContainSingle("one interface for every surface");
    }

    // ================================================================== CreateObjectAvatar

    [Fact]
    public async Task In_chat_an_object_is_made_from_files_by_path_once_the_card_that_showed_the_statement_was_allowed()
    {
        Rig rig = new();
        rig.H.AddImage("kettle-front.png", salt: 3);
        rig.H.AddImage("kettle-side.png", salt: 4);
        using InferenceApiDbContext db = rig.H.NewDb();
        var tokens = ConfirmationTokenService.CreateEphemeral();
        H.User user = rig.H.Owner();
        AssistantAvatarTools tools = Tools(rig, db, user, tokens);
        var chat = new AssistantToolContext(user.UserId, rig.H.Project.Id, ChatApproved: true);
        (string, object?)[] args = [("name", "Kettle"), ("files", KettleFiles()), ("description", "A red kettle")];

        (JsonElement proposal, string proposalRaw) = await InvokeAsync(tools, chat, "ProposeCreateObjectAvatar", args);
        Error(proposal).Should().BeNull(proposalRaw);
        proposal.GetProperty("statement").GetString().Should().Be(AvatarObjectStatement.English);
        proposal.GetProperty("files").EnumerateArray().Select(f => (f.GetProperty("path").GetString(), f.GetProperty("role").GetString()))
            .Should().Equal(("kettle-front.png", "Front"), ("kettle-side.png", "Detail"));
        string token = proposal.GetProperty("confirmationToken").GetString()!;

        // Without the card's tick nothing is made; a tick in another language is not this statement.
        (JsonElement unticked, _) = await InvokeAsync(tools, chat, "ApplyCreateObjectAvatar", [.. args, ("confirmationToken", token)]);
        Code(unticked).Should().Be("object_statement_required");
        using (AssistantStatementApproval.Enter(AssistantAvatarTools.CardStatement("it")))
            Code((await InvokeAsync(tools, chat, "ApplyCreateObjectAvatar", [.. args, ("confirmationToken", token)])).Json).Should().Be("object_statement_required");
        Objects(rig).Should().Be(0);

        JsonElement applied;
        string appliedRaw;
        using (AssistantStatementApproval.Enter(AssistantAvatarTools.CardStatement("en")))
            (applied, appliedRaw) = await InvokeAsync(tools, chat, "ApplyCreateObjectAvatar", [.. args, ("confirmationToken", token)]);

        applied.GetProperty("created").GetBoolean().Should().BeTrue(appliedRaw);
        (applied.GetProperty("name").GetString(), applied.GetProperty("kind").GetString(), applied.GetProperty("pictures").GetInt32())
            .Should().Be(("Kettle", "object", 2));
        using InferenceApiDbContext check = rig.H.NewDb();
        ProjectAvatar kettle = await check.ProjectAvatars.SingleAsync(a => a.Name == "Kettle");
        (kettle.SubjectKind, kettle.ConsentStatementVersion, kettle.ConsentAttestedByUserId).Should()
            .Be((AvatarSubjectKind.Object, AvatarObjectStatement.Version, user.UserId));
        (await check.ProjectAvatarReferences.Where(r => r.AvatarId == kettle.Id).OrderBy(r => r.Ordinal).Select(r => r.Role).ToListAsync())
            .Should().Equal(AvatarReferenceRole.Front, AvatarReferenceRole.Detail);
        GuidShape.IsMatch(proposalRaw.Replace(token, string.Empty)).Should().BeFalse("names, never ids");
        GuidShape.IsMatch(appliedRaw).Should().BeFalse("names, never ids");
    }

    [Fact]
    public async Task Over_MCP_the_caller_passes_the_statement_verbatim_and_without_it_is_answered_with_the_text()
    {
        Rig rig = new();
        rig.H.AddImage("kettle-front.png", salt: 3);
        rig.H.AddImage("kettle-side.png", salt: 4);
        using InferenceApiDbContext db = rig.H.NewDb();
        H.User user = rig.H.Owner();
        AssistantAvatarTools tools = Tools(rig, db, user, ConfirmationTokenService.CreateEphemeral());
        var mcp = new AssistantToolContext(user.UserId, rig.H.Project.Id, Initiator: ExecutionInitiator.Mcp);
        AssistantMcpToolSurface.TryResolve("CreateObjectAvatar", tools.GetTools(mcp), out McpToolCallPlan plan).Should().BeTrue();

        AIFunctionArguments without = new() { ["name"] = "Kettle", ["files"] = KettleFiles() };
        (JsonElement refused, _) = Parse(await AssistantMcpToolSurface.InvokeAsync(plan, without, CancellationToken.None));
        Code(refused).Should().Be("object_statement_required");
        refused.GetProperty("requiredStatement").GetString().Should().Be(AvatarObjectStatement.English);
        refused.TryGetProperty("confirmationToken", out _).Should().BeFalse();

        AIFunctionArguments paraphrased = new() { ["name"] = "Kettle", ["files"] = KettleFiles(), ["statement"] = "I confirm it shows no person.", ["locale"] = "en" };
        Code(Parse(await AssistantMcpToolSurface.InvokeAsync(plan, paraphrased, CancellationToken.None)).Json).Should().Be("object_statement_required");

        AIFunctionArguments italian = new() { ["name"] = "Bollitore", ["files"] = KettleFiles(), ["statement"] = AvatarObjectStatement.Italian, ["locale"] = "it" };
        (JsonElement created, string raw) = Parse(await AssistantMcpToolSurface.InvokeAsync(plan, italian, CancellationToken.None));
        created.GetProperty("created").GetBoolean().Should().BeTrue(raw);
        using InferenceApiDbContext check = rig.H.NewDb();
        (await check.ProjectAvatars.SingleAsync(a => a.Name == "Bollitore")).ConsentLocale.Should().Be("it");
    }

    [Fact]
    public async Task Making_an_object_is_refused_in_order_and_writes_nothing()
    {
        Rig rig = new();
        rig.H.AddImage("kettle-front.png", salt: 3);
        rig.H.AddImage("kettle-side.png", salt: 4);
        rig.H.AddFile("hum.wav", "audio/wav", [1, 2, 3, 4]);
        using InferenceApiDbContext db = rig.H.NewDb();
        var tokens = ConfirmationTokenService.CreateEphemeral();
        H.User owner = rig.H.Owner();
        Guid project = rig.H.Project.Id;
        async Task<string?> Refusal(AssistantAvatarTools tools, ICurrentUser user, params (string, object?)[] args) =>
            Code((await InvokeAsync(tools, new AssistantToolContext(user.UserId, project, ChatApproved: true), "ProposeCreateObjectAvatar", args)).Json);
        (string, object?)[] good = [("name", "Kettle"), ("files", KettleFiles())];

        (await Refusal(Tools(rig, db, owner, tokens, objects: false), owner, good)).Should().Be(AssistantAvatarTools.NotAvailableCode);
        H.User stranger = H.Stranger();
        (await InvokeAsync(Tools(rig, db, stranger, tokens), new AssistantToolContext(stranger.UserId, project, ChatApproved: true),
            "ProposeCreateObjectAvatar", good)).Json.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        H.User unverified = rig.H.Owner(emailVerified: false);
        (await Refusal(Tools(rig, db, unverified, tokens), unverified, good)).Should().Be("EMAIL_NOT_VERIFIED");

        AssistantAvatarTools tools = Tools(rig, db, owner, tokens);
        rig.H.Entitlements.Set = new OrganizationEntitlementSet { AvatarsAllowed = true, AvatarObjectsAllowed = false };
        (JsonElement plan, _) = await InvokeAsync(tools, new AssistantToolContext(owner.UserId, project, ChatApproved: true), "ProposeCreateObjectAvatar", good);
        (Code(plan), plan.GetProperty("feature").GetString()).Should().Be(("PLAN_NOT_ENTITLED", "avatar_objects"));
        rig.H.Entitlements.Set = new OrganizationEntitlementSet { AvatarsAllowed = true, AvatarObjectsAllowed = true };

        (await Refusal(tools, owner, ("name", "Kettle"), ("files", new[] { new AvatarFileArg("nope.png") }))).Should().Be("file_not_found");
        (await Refusal(tools, owner, ("name", "Kettle"), ("files", new[] { new AvatarFileArg("hum.wav") }))).Should().Be("reference_kind_not_allowed");
        (await Refusal(tools, owner, ("name", "Kettle"), ("files", KettleFiles("Face")))).Should().Be("role_invalid", "a picture of an object is Front, Angle or Detail");
        (await Refusal(tools, owner, ("name", ""), ("files", KettleFiles()))).Should().Be("name_invalid");

        // A token minted for one name never makes another.
        var chat = new AssistantToolContext(owner.UserId, project, ChatApproved: true);
        string token = (await InvokeAsync(tools, chat, "ProposeCreateObjectAvatar", good)).Json.GetProperty("confirmationToken").GetString()!;
        using (AssistantStatementApproval.Enter(AssistantAvatarTools.CardStatement("en")))
        {
            (JsonElement stale, _) = await InvokeAsync(tools, chat, "ApplyCreateObjectAvatar", ("name", "Teapot"), ("files", KettleFiles()), ("confirmationToken", token));
            Error(stale).Should().Be(AssistantAvatarTools.StaleTokenMessage);
        }

        // The shared creator's checks still apply at apply: a picture that shows a person is refused.
        rig.H.PersonCheck = new H.FakePersonCheck { Answer = _ => ObjectPersonCheckVerdict.Person };
        AssistantAvatarTools checking = Tools(rig, db, owner, tokens);
        string checkedToken = (await InvokeAsync(checking, chat, "ProposeCreateObjectAvatar", good)).Json.GetProperty("confirmationToken").GetString()!;
        using (AssistantStatementApproval.Enter(AssistantAvatarTools.CardStatement("en")))
            Code((await InvokeAsync(checking, chat, "ApplyCreateObjectAvatar", [.. good, ("confirmationToken", checkedToken)])).Json)
                .Should().Be("object_reference_shows_person");
        Objects(rig).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_object_card_shows_the_files_and_the_statement_and_always_waits_for_the_persons_click(bool auto)
    {
        Rig rig = new();
        rig.H.AddImage("kettle-front.png", salt: 3);
        rig.H.AddImage("kettle-side.png", salt: 4);
        using InferenceApiDbContext db = rig.H.NewDb();
        H.User user = rig.H.Owner();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user.UserId, Email = "owner@example.com", AssistantAutoApprove = auto });
        await db.SaveChangesAsync();
        var tokens = ConfirmationTokenService.CreateEphemeral();
        AssistantAvatarTools tools = Tools(rig, db, user, tokens);
        var chat = new AssistantToolContext(user.UserId, rig.H.Project.Id, ChatApproved: true);
        var cards = new AssistantApprovalCardBuilder(db, TestTenancy.Access(db, user), user, avatarTools: tools);
        var broker = new AssistantApprovalBroker();
        var frames = new List<AssistantActionFrame>();
        var gate = new AssistantApprovalGate(chat, user.OrganizationId, db, broker, cards, null, (f, _) =>
        {
            lock (frames) frames.Add(f);
            return Task.CompletedTask;
        });

        string token = (await InvokeAsync(tools, chat, "ProposeCreateObjectAvatar", ("name", "Kettle"), ("files", KettleFiles())))
            .Json.GetProperty("confirmationToken").GetString()!;
        AIFunctionArguments args = new() { ["name"] = "Kettle", ["files"] = KettleFiles(), ["confirmationToken"] = token };
        AIFunction apply = tools.GetTools(chat).Single(t => t.Name == "ApplyCreateObjectAvatar");
        Task<object?> pending = gate.RunAsync("ApplyCreateObjectAvatar", args, () => apply.InvokeAsync(args), CancellationToken.None);

        AssistantActionFrame card = await WaitAsync(frames, AssistantActionState.Pending);
        card.Mode.Should().Be(AssistantActionMode.Ask, "making an object is the member's own attestation: never auto");
        card.Title.Should().Contain("Kettle");
        card.Cost.Should().Be("Free");
        card.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("kettle-front.png") && t.Contains("Front"))
            .And.Contain(t => t.Contains("kettle-side.png") && t.Contains("Detail"))
            .And.Contain(AssistantApprovalCardBuilder.StatementLine("en"));
        GuidShape.IsMatch(Visible(card.Title, card.Summary, card.ProjectName, card.Cost, card.Changes)).Should().BeFalse("a card never shows an id");
        Objects(rig).Should().Be(0);

        broker.Answer(card.Id, user.UserId, user.OrganizationId!.Value, allow: true, alwaysAllow: false);
        (JsonElement done, string raw) = Parse(await pending);
        done.GetProperty("created").GetBoolean().Should().BeTrue("allowing the card is the tick: " + raw);
        AssistantStatementApproval.Approved.Should().BeNull("the mark ends with the call");
        Objects(rig).Should().Be(1);
    }

    /// <summary>What a person reads on a card (the project and document ids beside it only steer the screen's refresh).</summary>
    private static string Visible(params object?[] parts) => JsonSerializer.Serialize(parts);

    private static async Task<AssistantActionFrame> WaitAsync(List<AssistantActionFrame> frames, string state)
    {
        for (int i = 0; i < 500; i++)
        {
            lock (frames)
            {
                if (frames.FirstOrDefault(f => f.State == state) is { } found) return found;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("no " + state + " frame");
    }

    // ================================================================== ExtractObjectFromVideo

    private static Rig VideoRig(bool consent, out ProjectFile clip)
    {
        Rig rig = new()
        {
            Charges = new FakeNonWorkflowCharges
            {
                AvailableMillis = 1_000_000,
                EstimateMillis = 4_000,
                EstimateBy = r => r.Unit == UsageKind.SegmentationImages ? 1_000 : 4_000
            },
            Describer = new Rig.IOutputDescriberDouble(),
            SegmentationProvider = new ResolvedImageAnalysisProvider(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi,
                InferenceProviderCapability.Segmentation, "https://queue.fal.run", "fal-ai/sam-3-1/image", "key", 60),
            SegmenterClients = new Rig.FakeSegmenter(),
            Presigned = new Mock<ReelBolt.Shared.Storage.IPresignedUrlService>()
        };
        rig.Presigned.Setup(p => p.CreateGetAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync((Guid _, string key, TimeSpan _) => "https://signed.example/" + key);
        if (consent) rig.GrantMediaConsent();
        clip = rig.H.AddProbedMedia("shots/kettle-shot.mp4", "video/mp4", 12, "h264");
        rig.Describer!.Sources[clip.Id] = new OutputSource(
            clip.Id, "file", clip.OriginalFileName, "video/mp4", clip.SizeBytes, clip.StorageKey,
            new MediaSourceRef(MediaSourceKind.ProjectFile, clip.Id), clip.Id, 12.0);
        return rig;
    }

    [Fact]
    public async Task An_object_is_pulled_out_of_a_video_by_path_and_without_the_projects_agreement_only_under_the_one_rule()
    {
        Rig rig = VideoRig(consent: false, out ProjectFile clip);
        using InferenceApiDbContext db = rig.H.NewDb();
        H.User user = rig.H.Owner();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user.UserId, Email = "owner@example.com", McpMediaEgressEnabled = false });
        await db.SaveChangesAsync();
        var tokens = ConfirmationTokenService.CreateEphemeral();
        AssistantAvatarTools tools = Tools(rig, db, user, tokens);
        var chat = new AssistantToolContext(user.UserId, rig.H.Project.Id, ChatApproved: true);
        (string, object?)[] args =
        [
            ("video", clip.OriginalPath), ("objectDescription", "the red kettle"), ("name", "Kettle"),
            ("atSeconds", new[] { 2.0, 6.0 }), ("allowMediaEgress", true)
        ];

        (JsonElement proposal, string raw) = await InvokeAsync(tools, chat, "ProposeExtractObjectFromVideo", args);
        Error(proposal).Should().BeNull(raw);
        (proposal.GetProperty("findsObject").GetBoolean(), proposal.GetProperty("moments").GetInt32()).Should().Be((true, 2));
        string token = proposal.GetProperty("confirmationToken").GetString()!;

        // The statement is confirmed, but no card allowed sending the frames for this call: refused, nothing queued.
        using (AssistantStatementApproval.Enter(AssistantAvatarTools.CardStatement("en")))
        {
            (JsonElement refused, _) = await InvokeAsync(tools, chat, "ApplyExtractObjectFromVideo", [.. args, ("confirmationToken", token)]);
            Code(refused).Should().Be(AssistantAvatarTools.MediaNotAllowedCode);
        }

        Jobs(rig).Should().Be(0);

        // Over MCP with the user's switch off: refused; on: allowed, recorded on the job as a per-call permission only.
        var mcp = new AssistantToolContext(user.UserId, rig.H.Project.Id, Initiator: ExecutionInitiator.Mcp);
        AssistantMcpToolSurface.TryResolve("ExtractObjectFromVideo", tools.GetTools(mcp), out McpToolCallPlan plan).Should().BeTrue();
        AIFunctionArguments mcpArgs = new();
        foreach ((string k, object? v) in args) mcpArgs[k] = v;
        mcpArgs["statement"] = AvatarObjectStatement.English;
        mcpArgs["locale"] = "en";
        Code(Parse(await AssistantMcpToolSurface.InvokeAsync(plan, mcpArgs, CancellationToken.None)).Json).Should().Be(AssistantAvatarTools.MediaNotAllowedCode);
        ApplicationUser row = await db.ApplicationUsers.SingleAsync(u => u.Id == user.UserId);
        row.McpMediaEgressEnabled = true;
        await db.SaveChangesAsync();
        (JsonElement started, string startedRaw) = Parse(await AssistantMcpToolSurface.InvokeAsync(plan, mcpArgs, CancellationToken.None));
        started.GetProperty("started").GetBoolean().Should().BeTrue(startedRaw);
        GuidShape.IsMatch(startedRaw).Should().BeFalse("a build is followed by name in ListProjectAvatars, never by id");
        using InferenceApiDbContext check = rig.H.NewDb();
        AvatarBuildJob job = await check.AvatarBuildJobs.SingleAsync();
        (job.Kind, job.Name, job.AllowedBy).Should().Be((AvatarBuildKinds.Extraction, "Kettle", AvatarBuildJob.AllowedByCall));
        (await check.Projects.SingleAsync(p => p.Id == rig.H.Project.Id)).SubjectMediaConsentAt.Should().BeNull("one call never records the project's agreement");
    }

    [Fact]
    public async Task The_extraction_card_prices_the_object_finding_model_and_says_the_frames_are_sent()
    {
        Rig rig = VideoRig(consent: true, out ProjectFile clip);
        using InferenceApiDbContext db = rig.H.NewDb();
        H.User user = rig.H.Owner();
        AssistantAvatarTools tools = Tools(rig, db, user, ConfirmationTokenService.CreateEphemeral());
        var chat = new AssistantToolContext(user.UserId, rig.H.Project.Id, ChatApproved: true);
        var cards = new AssistantApprovalCardBuilder(db, TestTenancy.Access(db, user), user, avatarTools: tools);
        AIFunctionArguments args = new()
        {
            ["video"] = "shots/kettle-shot.mp4",
            ["objectDescription"] = "the red kettle",
            ["name"] = "Kettle",
            ["confirmationToken"] = "t"
        };

        ApprovalVerdict verdict = await cards.BuildAsync("ApplyExtractObjectFromVideo", args, chat, null, CancellationToken.None);

        ApprovalCard card = verdict.Card!;
        card.Risk.Should().Be(AssistantActionRisk.Paid);
        card.CostCredits.Should().BeGreaterThan(0);
        card.Statement.Should().NotBeNull();
        card.AllowsMediaEgress.Should().BeFalse("the project's own agreement is recorded");
        card.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("object-finding service"))
            .And.Contain(t => t.Contains("the red kettle"));
        AssistantApprovalCatalog.AlwaysAsksUser("ApplyExtractObjectFromVideo").Should().BeTrue();

        args["video"] = "not-there.mp4";
        (await cards.BuildAsync("ApplyExtractObjectFromVideo", args, chat, null, CancellationToken.None)).Card.Should().BeNull("a call the tool would refuse gets no card");
    }

    // ================================================================== CreateAvatarVariant

    [Fact]
    public async Task A_variant_of_an_object_is_made_from_files_or_by_the_picture_model_and_a_persons_never()
    {
        Rig rig = new();
        rig.GrantMediaConsent();
        (Guid car, _) = await rig.CarAsync();
        rig.H.AddImage("car-red.png", salt: 77);
        Guid anna = await rig.H.CreateReadyAsync("Anna", rig.H.AddImage("anna.png", salt: 9));
        using InferenceApiDbContext db = rig.H.NewDb();
        H.User user = rig.H.Owner();
        var tokens = ConfirmationTokenService.CreateEphemeral();
        AssistantAvatarTools tools = Tools(rig, db, user, tokens);
        var chat = new AssistantToolContext(user.UserId, rig.H.Project.Id, ChatApproved: true);

        // A person: refused with where to do it.
        (JsonElement person, _) = await InvokeAsync(tools, chat, "ProposeCreateAvatarVariant",
            ("original", "Anna"), ("label", "Summer"), ("description", "short hair"), ("instruction", "cut the hair"));
        (Code(person), Error(person)).Should().Be(("VARIANT_OF_PERSON_IN_APP", AssistantAvatarTools.PersonInAppMessage));

        // Files and an instruction together, or neither: refused.
        Code((await InvokeAsync(tools, chat, "ProposeCreateAvatarVariant", ("original", "Car"), ("label", "Red"), ("description", "red"))).Json)
            .Should().Be("variant_source_invalid");

        // From files: free, at once, grouped under the original.
        (string, object?)[] fromFiles = [("original", "Car"), ("label", "Red"), ("description", "matte red paint"), ("files", new[] { new AvatarFileArg("car-red.png") })];
        (JsonElement proposal, string raw) = await InvokeAsync(tools, chat, "ProposeCreateAvatarVariant", fromFiles);
        (proposal.GetProperty("name").GetString(), proposal.GetProperty("madeBy").GetString(), proposal.GetProperty("credits").GetString())
            .Should().Be(("Car · Red", "files", "Free"), raw);
        JsonElement made;
        string madeRaw;
        using (AssistantStatementApproval.Enter(AssistantAvatarTools.CardStatement("en")))
            (made, madeRaw) = await InvokeAsync(tools, chat, "ApplyCreateAvatarVariant",
                [.. fromFiles, ("confirmationToken", proposal.GetProperty("confirmationToken").GetString())]);
        (made.GetProperty("name").GetString(), made.GetProperty("variantOf").GetString(), made.GetProperty("variantLabel").GetString())
            .Should().Be(("Car · Red", "Car", "Red"), madeRaw);
        using (InferenceApiDbContext check = rig.H.NewDb())
        {
            ProjectAvatar red = await check.ProjectAvatars.SingleAsync(a => a.Name == "Car · Red");
            (red.VariantOfAvatarId, red.VariantDescription).Should().Be((car, "matte red paint"));
        }

        // By the picture model: priced, then a background build.
        (string, object?)[] byModel = [("original", "Car"), ("label", "Blue"), ("description", "deep blue paint"), ("instruction", "paint it deep blue")];
        (JsonElement priced, string pricedRaw) = await InvokeAsync(tools, chat, "ProposeCreateAvatarVariant", byModel);
        priced.GetProperty("madeBy").GetString().Should().Be("pictureModel", pricedRaw);
        JsonElement started;
        string startedRaw;
        using (AssistantStatementApproval.Enter(AssistantAvatarTools.CardStatement("en")))
            (started, startedRaw) = await InvokeAsync(tools, chat, "ApplyCreateAvatarVariant",
                [.. byModel, ("confirmationToken", priced.GetProperty("confirmationToken").GetString())]);
        started.GetProperty("started").GetBoolean().Should().BeTrue(startedRaw);
        Jobs(rig).Should().Be(1);
        GuidShape.IsMatch(madeRaw + startedRaw).Should().BeFalse("names, never ids");
        _ = anna;
    }

    [Fact]
    public async Task A_variant_made_by_the_picture_model_without_the_projects_agreement_asks_on_its_card_and_needs_the_card()
    {
        Rig rig = new();
        await rig.CarAsync();
        using InferenceApiDbContext db = rig.H.NewDb();
        H.User user = rig.H.Owner();
        var tokens = ConfirmationTokenService.CreateEphemeral();
        AssistantAvatarTools tools = Tools(rig, db, user, tokens);
        var chat = new AssistantToolContext(user.UserId, rig.H.Project.Id, ChatApproved: true);
        (string, object?)[] byModel = [("original", "Car"), ("label", "Blue"), ("description", "deep blue paint"), ("instruction", "paint it deep blue")];

        Code((await InvokeAsync(tools, chat, "ProposeCreateAvatarVariant", byModel)).Json).Should().Be("CONSENT_REQUIRED",
            "without the project's agreement a call must say allowMediaEgress");

        (string, object?)[] allowing = [.. byModel, ("allowMediaEgress", true)];
        string token = (await InvokeAsync(tools, chat, "ProposeCreateAvatarVariant", allowing)).Json.GetProperty("confirmationToken").GetString()!;
        var cards = new AssistantApprovalCardBuilder(db, TestTenancy.Access(db, user), user, avatarTools: tools);
        AIFunctionArguments cardArgs = new();
        foreach ((string k, object? v) in allowing) cardArgs[k] = v;
        cardArgs["confirmationToken"] = token;
        ApprovalCard card = (await cards.BuildAsync("ApplyCreateAvatarVariant", cardArgs, chat, null, CancellationToken.None)).Card!;
        card.AllowsMediaEgress.Should().BeTrue();
        card.Changes!.Select(c => c.Text).Should().Contain(AssistantApprovalCardBuilder.MediaForThisCallLine);

        using (AssistantStatementApproval.Enter(AssistantAvatarTools.CardStatement("en")))
        {
            Code((await InvokeAsync(tools, chat, "ApplyCreateAvatarVariant", [.. allowing, ("confirmationToken", token)])).Json)
                .Should().Be(AssistantAvatarTools.MediaNotAllowedCode, "the media card was not allowed");
            using (AssistantMediaEgressApproval.Enter())
                (await InvokeAsync(tools, chat, "ApplyCreateAvatarVariant", [.. allowing, ("confirmationToken", token)])).Json
                    .GetProperty("started").GetBoolean().Should().BeTrue();
        }

        using InferenceApiDbContext check = rig.H.NewDb();
        (await check.AvatarBuildJobs.SingleAsync()).AllowedBy.Should().Be(AvatarBuildJob.AllowedByCall);
        (await check.Projects.SingleAsync(p => p.Id == rig.H.Project.Id)).SubjectMediaConsentAt.Should().BeNull();
    }

    // ================================================================== the workspace library

    [Fact]
    public async Task The_library_is_listed_by_name_and_an_item_is_imported_by_name_once()
    {
        var l = new AvatarLibraryHarness();
        (Guid machineItem, _) = await l.PublishObjectAsync("Espresso machine", l.Harness.AddImage("machine.png", salt: 5));
        using (InferenceApiDbContext seed = l.NewDb())
        {
            seed.AvatarLibraryItems.Add(new AvatarLibraryItem
            {
                Id = Guid.NewGuid(),
                OrganizationId = l.OrganizationId,
                SubjectKind = AvatarSubjectKind.Object,
                Name = "Espresso machine · Red",
                ConsentStatement = "statement",
                ConsentStatementVersion = "avatar-object-workspace-2026-11",
                ConsentLocale = "en",
                ConsentAttestedByUserId = l.Harness.OwnerId,
                ConsentAttestedAt = DateTime.UtcNow,
                RetentionUntil = ProjectAvatar.NoRetention,
                VariantOfItemId = machineItem,
                VariantLabel = "Red",
                VariantDescription = "red enamel"
            });
            await seed.SaveChangesAsync();
        }

        Project second = l.AddProject("Second");
        using InferenceApiDbContext db = l.NewDb();
        H.User user = l.Harness.Owner();
        var tokens = ConfirmationTokenService.CreateEphemeral();
        Rig rig = new();
        var tools = new AssistantAvatarTools(db, TestTenancy.Access(db, user), l.Harness.Storage.Object, Switches(), NullLogger<AssistantAvatarTools>.Instance,
            tokens, user, rig.Creator(db), null, null, l.Library(db), l.Harness.Entitlements, new MediaEgressCallPermission(db));
        var chat = new AssistantToolContext(user.UserId, second.Id, ChatApproved: true);

        (JsonElement list, string listRaw) = await InvokeAsync(tools, chat, "ListAvatarLibrary");
        JsonElement item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Espresso machine");
        (item.GetProperty("kind").GetString(), item.GetProperty("usable").GetBoolean(), item.GetProperty("inThisProject").GetBoolean(), item.GetProperty("variants").GetInt32())
            .Should().Be(("object", true, false, 1));
        JsonElement red = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Espresso machine · Red");
        (red.GetProperty("variantOf").GetString(), red.GetProperty("variantLabel").GetString(), red.GetProperty("variantDescription").GetString())
            .Should().Be(("Espresso machine", "Red", "red enamel"), "a variant is shown under its original's name");
        GuidShape.IsMatch(listRaw).Should().BeFalse();

        Code((await InvokeAsync(tools, chat, "ProposeImportLibraryAvatar", ("name", "Kettle"))).Json).Should().Be("library_item_not_found");
        (JsonElement proposal, _) = await InvokeAsync(tools, chat, "ProposeImportLibraryAvatar", ("name", "espresso machine"));
        string token = proposal.GetProperty("confirmationToken").GetString()!;
        (JsonElement imported, string importedRaw) = await InvokeAsync(tools, chat, "ApplyImportLibraryAvatar", ("name", "espresso machine"), ("confirmationToken", token));
        (imported.GetProperty("imported").GetBoolean(), imported.GetProperty("name").GetString(), imported.GetProperty("fromLibrary").GetBoolean())
            .Should().Be((true, "Espresso machine", true), importedRaw);
        GuidShape.IsMatch(importedRaw).Should().BeFalse();

        (JsonElement again, _) = await InvokeAsync(tools, chat, "ProposeImportLibraryAvatar", ("name", "Espresso machine"));
        again.GetProperty("alreadyImported").GetBoolean().Should().BeTrue();
        again.TryGetProperty("confirmationToken", out _).Should().BeFalse();
        AssistantApprovalCatalog.AlwaysAsksUser("ApplyImportLibraryAvatar").Should().BeTrue();
    }

    // ================================================================== GenerateSubjectClip

    private sealed class RecordingClips : ISubjectClipService
    {
        public List<(SubjectClipRequest Request, bool MediaAllowed, SubjectClipActor Actor)> Estimates { get; } = [];
        public List<(SubjectClipRequest Request, bool MediaAllowed, SubjectClipActor Actor)> Starts { get; } = [];
        public SubjectClipEstimate Estimate { get; set; } = new(true, 12.5, 20.0, 500, true, "References", false);

        public Task<SubjectClipOutcome<SubjectClipEstimate>> EstimateAsync(Project project, SubjectClipActor actor, SubjectClipRequest request, bool mediaAllowed, CancellationToken ct)
        {
            Estimates.Add((request, mediaAllowed, actor));
            SubjectClipEstimate estimate = project.SubjectMediaConsentAt is null && !mediaAllowed
                ? Estimate with { UnavailableReason = "consent", UnavailableCode = SubjectClipCodes.ConsentRequired }
                : Estimate;
            return Task.FromResult(SubjectClipOutcome<SubjectClipEstimate>.Of(estimate));
        }

        public Task<SubjectClipOutcome<SubjectClipStarted>> StartAsync(Project project, SubjectClipActor actor, SubjectClipRequest request, bool mediaAllowed, CancellationToken ct)
        {
            Starts.Add((request, mediaAllowed, actor));
            return Task.FromResult(SubjectClipOutcome<SubjectClipStarted>.Of(new SubjectClipStarted(Guid.NewGuid(), "queued", 20.0)));
        }

        public Task<SubjectClipStatus?> GetAsync(Guid projectId, Guid executionId, CancellationToken ct) => Task.FromResult<SubjectClipStatus?>(null);

        public Task<IReadOnlyList<SubjectClipStatus>> ListAsync(Guid projectId, bool activeOnly, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SubjectClipStatus>>([]);

        public Task<SubjectClipOutcome<SubjectClipStopped>> CancelAsync(Guid projectId, Guid executionId, Guid userId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task A_clip_is_proposed_by_names_priced_by_the_clip_service_carded_with_each_subject_and_started_once()
    {
        Rig rig = new();
        rig.GrantMediaConsent();
        (Guid car, _) = await rig.CarAsync();
        Guid anna = await rig.H.CreateReadyAsync("Anna", rig.H.AddImage("anna.png", salt: 9));
        rig.H.AddFile("voice/hello.wav", "audio/wav", [1, 2, 3, 4]);
        using InferenceApiDbContext db = rig.H.NewDb();
        H.User user = rig.H.Owner();
        var clips = new RecordingClips();
        AssistantAvatarTools tools = Tools(rig, db, user, ConfirmationTokenService.CreateEphemeral(), clips: clips);
        var chat = new AssistantToolContext(user.UserId, rig.H.Project.Id, ChatApproved: true);
        (string, object?)[] args =
        [
            ("subjects", new[] { "anna", "Car" }), ("prompt", "She drives off"), ("durationSeconds", 8), ("ratio", "9:16"),
            ("speech", "voice/hello.wav")
        ];

        Code((await InvokeAsync(tools, chat, "ProposeGenerateSubjectClip", ("subjects", new[] { "Bruno" }), ("prompt", "x"))).Json)
            .Should().Be("subject_not_found");
        Code((await InvokeAsync(tools, chat, "ProposeGenerateSubjectClip", [.. args, ("model", "No such model")])).Json)
            .Should().Be(SubjectClipCodes.ModelNotFound);

        (JsonElement proposal, string raw) = await InvokeAsync(tools, chat, "ProposeGenerateSubjectClip", args);
        Error(proposal).Should().BeNull(raw);
        proposal.GetProperty("credits").GetDouble().Should().Be(20.0, "the clip service's high figure");
        proposal.GetProperty("subjects").EnumerateArray().Select(s => s.GetProperty("name").GetString()).Should().Equal("Anna", "Car");
        SubjectClipRequest asked = clips.Estimates.Last().Request;
        asked.SubjectIds.Should().Equal(anna, car);
        (asked.DurationSeconds, asked.Ratio, asked.SpeechFileId is not null).Should().Be((8, "9:16", true));

        var cards = new AssistantApprovalCardBuilder(db, TestTenancy.Access(db, user), user, avatarTools: tools);
        AIFunctionArguments cardArgs = new();
        foreach ((string k, object? v) in args) cardArgs[k] = v;
        cardArgs["confirmationToken"] = "t";
        ApprovalCard card = (await cards.BuildAsync("ApplyGenerateSubjectClip", cardArgs, chat, null, CancellationToken.None)).Card!;
        (card.Risk, card.CostCredits, card.AllowsMediaEgress).Should().Be((AssistantActionRisk.Paid, 20.0, false));
        card.Changes!.Select(c => c.Text).Should().Contain(t => t.Contains("Anna") && t.Contains("(person)"))
            .And.Contain(t => t.Contains("Car") && t.Contains("(object)"))
            .And.Contain(t => t.Contains("voice/hello.wav"));
        GuidShape.IsMatch(Visible(card.Title, card.Summary, card.ProjectName, card.Cost, card.Changes, card.Statement)).Should().BeFalse("a card never shows an id");

        (JsonElement started, string startedRaw) = await InvokeAsync(tools, chat, "ApplyGenerateSubjectClip",
            [.. args, ("confirmationToken", proposal.GetProperty("confirmationToken").GetString())]);
        started.GetProperty("started").GetBoolean().Should().BeTrue(startedRaw);
        started.GetProperty("executionId").GetGuid().Should().NotBeEmpty("the run's handle, for GetExecutionStatus and WatchExecution");
        clips.Starts.Should().ContainSingle();
        (clips.Starts[0].MediaAllowed, clips.Starts[0].Actor.Initiator).Should().Be((false, ExecutionInitiator.Assistant),
            "the project's agreement is recorded: the call asks for nothing more");
    }

    [Fact]
    public async Task A_clip_of_a_person_where_this_deployment_makes_none_is_refused_at_the_proposal_with_the_services_code()
    {
        Rig rig = new();
        rig.GrantMediaConsent();
        await rig.H.CreateReadyAsync("Anna", rig.H.AddImage("anna.png", salt: 9));
        using InferenceApiDbContext db = rig.H.NewDb();
        H.User user = rig.H.Owner();
        var clips = new RecordingClips
        {
            Estimate = new(true, null, null, null, false, null, false,
                "Clips that show a person are not available in this workspace yet. A clip of objects only can be made.",
                SubjectClipCodes.GenerationDisabled)
        };
        AssistantAvatarTools tools = Tools(rig, db, user, ConfirmationTokenService.CreateEphemeral(), clips: clips);
        var chat = new AssistantToolContext(user.UserId, rig.H.Project.Id, ChatApproved: true);

        (JsonElement refused, string raw) = await InvokeAsync(tools, chat, "ProposeGenerateSubjectClip", ("subjects", new[] { "Anna" }), ("prompt", "She waves"));

        Code(refused).Should().Be(SubjectClipCodes.GenerationDisabled, raw);
        refused.TryGetProperty("confirmationToken", out _).Should().BeFalse("nothing can be applied");
        clips.Starts.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_the_projects_agreement_a_clip_needs_allowMediaEgress_and_then_the_card_in_chat_or_the_switch_over_MCP()
    {
        Rig rig = new();
        await rig.CarAsync();
        using InferenceApiDbContext db = rig.H.NewDb();
        H.User user = rig.H.Owner();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user.UserId, Email = "owner@example.com", McpMediaEgressEnabled = true });
        await db.SaveChangesAsync();
        var clips = new RecordingClips();
        AssistantAvatarTools tools = Tools(rig, db, user, ConfirmationTokenService.CreateEphemeral(), clips: clips);
        var chat = new AssistantToolContext(user.UserId, rig.H.Project.Id, ChatApproved: true);

        Code((await InvokeAsync(tools, chat, "ProposeGenerateSubjectClip", ("subjects", new[] { "Car" }), ("prompt", "It drives off"))).Json)
            .Should().Be(SubjectClipCodes.ConsentRequired);

        (string, object?)[] allowing = [("subjects", new[] { "Car" }), ("prompt", "It drives off"), ("allowMediaEgress", true)];
        string token = (await InvokeAsync(tools, chat, "ProposeGenerateSubjectClip", allowing)).Json.GetProperty("confirmationToken").GetString()!;
        Code((await InvokeAsync(tools, chat, "ApplyGenerateSubjectClip", [.. allowing, ("confirmationToken", token)])).Json)
            .Should().Be(AssistantAvatarTools.MediaNotAllowedCode);
        clips.Starts.Should().BeEmpty();
        using (AssistantMediaEgressApproval.Enter())
            (await InvokeAsync(tools, chat, "ApplyGenerateSubjectClip", [.. allowing, ("confirmationToken", token)])).Json
                .GetProperty("started").GetBoolean().Should().BeTrue();
        clips.Starts.Single().MediaAllowed.Should().BeTrue();

        // The token was minted for a call that says allowMediaEgress: dropping the word at apply is refused, nothing runs.
        Code((await InvokeAsync(tools, chat, "ApplyGenerateSubjectClip", ("subjects", new[] { "Car" }), ("prompt", "It drives off"), ("confirmationToken", token))).Json)
            .Should().Be(SubjectClipCodes.ConsentRequired);
        clips.Starts.Should().ContainSingle();

        // Over MCP: the user's switch is on, so the collapsed call starts it, recorded as the connected tool's run.
        var mcp = new AssistantToolContext(user.UserId, rig.H.Project.Id, Initiator: ExecutionInitiator.Mcp);
        AssistantMcpToolSurface.TryResolve("GenerateSubjectClip", tools.GetTools(mcp), out McpToolCallPlan plan).Should().BeTrue();
        AIFunctionArguments mcpArgs = new();
        foreach ((string k, object? v) in allowing) mcpArgs[k] = v;
        Parse(await AssistantMcpToolSurface.InvokeAsync(plan, mcpArgs, CancellationToken.None)).Json.GetProperty("started").GetBoolean().Should().BeTrue();
        (clips.Starts.Last().MediaAllowed, clips.Starts.Last().Actor.Initiator).Should().Be((true, ExecutionInitiator.Mcp));
    }

    // ================================================================== the whole group

    private static readonly string[] Pairs =
        ["CreateObjectAvatar", "ExtractObjectFromVideo", "CreateAvatarVariant", "ImportLibraryAvatar", "GenerateSubjectClip"];

    [Fact]
    public void Every_new_tool_is_classified_carded_annotated_collapsed_and_captured_on_purpose()
    {
        foreach (string pair in Pairs)
        {
            string apply = "Apply" + pair;
            string propose = "Propose" + pair;
            AssistantApprovalCatalog.KnownTools.Should().Contain(apply);
            AssistantPlatformTools.Classify(propose)!.Kind.Should().Be(AssistantToolCostKind.Free, propose);
            AssistantPlatformTools.Classify(apply).Should().NotBeNull(apply);
            AssistantToolAnnotations.For(apply).Should().Match<AssistantToolAnnotation>(a => a.DestructiveHint && !a.ReadOnlyHint, apply);
            AssistantToolAnnotations.For(propose).ReadOnlyHint.Should().BeTrue(propose);
            AssistantMcpToolSurface.Collapsed.Should().ContainSingle(c => c.Name == pair && c.ProposeName == propose && c.ApplyName == apply);
            (ProjectHistoryAutoCapture.AssistantWriteTools.Contains(apply) || ProjectHistoryAutoCapture.AssistantApplyToolsLeftOut.ContainsKey(apply))
                .Should().BeTrue(apply);
        }

        AssistantPlatformTools.Classify("ApplyCreateObjectAvatar")!.Kind.Should().Be(AssistantToolCostKind.Free);
        AssistantPlatformTools.Classify("ApplyImportLibraryAvatar")!.Kind.Should().Be(AssistantToolCostKind.Free);
        AssistantPlatformTools.Classify("ApplyGenerateSubjectClip")!.Kind.Should().Be(AssistantToolCostKind.PlatformBilled);
        AssistantToolAnnotations.For("ListAvatarLibrary").Should().Match<AssistantToolAnnotation>(a => a.ReadOnlyHint && !a.DestructiveHint && !a.OpenWorldHint);
        foreach (string always in new[] { "ApplyCreateObjectAvatar", "ApplyExtractObjectFromVideo", "ApplyCreateAvatarVariant", "ApplyImportLibraryAvatar" })
            AssistantApprovalCatalog.AlwaysAsksUser(always).Should().BeTrue(always);
        AssistantApprovalCatalog.AlwaysAsksUser("ApplyGenerateSubjectClip").Should().BeFalse("a clip asks always only when it allows sending media");
        AssistantMcpToolSurface.NotOverMcp.Should().NotContain(n => Pairs.Any(p => n.EndsWith(p, StringComparison.Ordinal)));
    }
}
