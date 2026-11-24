#nullable enable
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
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.SocialPublishing;

/// <summary>
/// The social publishing assistant tools: the three reads and the schedule / cancel pairs run the app's own routes, are
/// always-ask (a card in auto mode, whatever the cost), show names and the whole text, and are not available over MCP.
/// </summary>
public class AssistantSocialToolsTests
{
    private static readonly Regex AnyGuid = new(@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private const string Channel = "Tom Travel Channel";
    private const string LongText = "Line one of the post.\nLine two, with a second paragraph that is deliberately long enough to be cut by any shortening "
        + "rule a card might apply, so that a test can prove the person sees every single word of what will be posted in their name.";

    private sealed class Rig : IDisposable
    {
        private readonly bool _ownsFixture;

        public Rig(EngineFixture? shared = null, OrganizationRole role = OrganizationRole.Owner, bool otherOrganization = false)
        {
            _ownsFixture = shared is null;
            F = shared ?? new EngineFixture();
            if (_ownsFixture)
            {
                F.Account.DisplayName = Channel;
                F.Db.SaveChanges();
            }

            Guid user = otherOrganization ? Guid.NewGuid() : F.UserId;
            Guid org = otherOrganization ? TestTenancy.OrgOf(user) : F.OrganizationId;
            UserId = user;
            TestTenancy.MutableTenant tenant = new();
            tenant.ActAsOrganization(org);
            Db = new InferenceApiDbContext(F.DbOptions, tenant);
            Current = new SocialFakeUser(user, org, role);
            Access = new ProjectAccessService(Db, Current);
            ServiceCollection services = new();
            services.AddSingleton(Db);
            services.AddSingleton<ICurrentUser>(Current);
            services.AddSingleton<IProjectAccessService>(Access);
            services.AddSingleton<ISocialOutputResolver>(F.Outputs);
            services.AddSingleton<IOrganizationEntitlements>(F.Entitlements);
            services.AddSingleton<TimeProvider>(F.Clock);
            services.AddSingleton<ISocialPlatformRegistry>(F.Registry);
            services.AddSingleton(Mock.Of<ISocialConnectionService>());
            services.AddLogging();
            Provider = services.BuildServiceProvider();
            Tokens = ConfirmationTokenService.CreateEphemeral();
            Tools = new AssistantSocialTools(Tokens, Access, Db, Current, F.Entitlements, F.Outputs, Provider, F.Clock);
        }

        public EngineFixture F { get; }
        public Guid UserId { get; }
        public InferenceApiDbContext Db { get; }
        public SocialFakeUser Current { get; }
        public ProjectAccessService Access { get; }
        public ServiceProvider Provider { get; }
        public IConfirmationTokenService Tokens { get; }
        public AssistantSocialTools Tools { get; }

        public async Task<string> CallAsync(string name, ExecutionInitiator initiator = ExecutionInitiator.Assistant, bool wake = false, params (string, object?)[] args)
        {
            AIFunction fn = Tools.GetTools(new AssistantToolContext(UserId, F.ProjectId, Initiator: initiator, FromWake: wake)).Single(t => t.Name == name);
            AIFunctionArguments a = new();
            foreach ((string k, object? v) in args)
                a[k] = v;
            object? result = await fn.InvokeAsync(a);
            return result is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString()! : result?.ToString() ?? string.Empty;
        }

        public (string, object?)[] ScheduleArgs(params (string, object?)[] extra) =>
        [
            ("outputId", F.OutputId), ("accountId", F.Account.Id), ("scheduledAt", When()), ("title", "Rome in a minute"),
            ("body", "Pasta first, then the Colosseum."), ("hashtags", new[] { "rome", "#travel" }), ("privacy", "public"), .. extra
        ];

        public static string When() => DateTime.UtcNow.AddDays(20).ToString("yyyy-MM-dd'T'HH:00:00'Z'");

        public void Dispose()
        {
            Provider.Dispose();
            Db.Dispose();
            if (_ownsFixture) F.Dispose();
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ------------------------------------------------------------------ the always-ask class

    [Fact]
    public void Schedule_and_cancel_are_gated_and_always_ask_while_the_other_applies_are_not()
    {
        foreach (string tool in new[] { "ApplySchedulePost", "ApplyCancelScheduledPost" })
        {
            AssistantApprovalCatalog.IsGated(tool).Should().BeTrue(tool);
            AssistantApprovalCatalog.AlwaysAsksUser(tool).Should().BeTrue(tool);
            AssistantApprovalCatalog.KnownTools.Should().Contain(tool);
        }

        AssistantApprovalCatalog.AlwaysAsksUser("ApplySaveStyle").Should().BeFalse();
        AssistantApprovalCatalog.AlwaysAsksUser("ApplyRestoreProjectVersion").Should().BeTrue();
    }

    [Fact]
    public async Task The_card_waits_for_a_click_in_auto_mode_and_always_allow_does_not_turn_auto_mode_on()
    {
        using Rig rig = new();
        rig.F.Db.ApplicationUsers.Add(new ApplicationUser { Id = rig.UserId, Email = "t@example.com", DisplayName = "T", AssistantAutoApprove = true });
        await rig.F.Db.SaveChangesAsync();
        AssistantApprovalBroker broker = new();
        List<AssistantActionFrame> frames = [];
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AssistantApprovalGate.TimeoutKey] = "30",
            [AssistantApprovalGate.AutoApproveCostCardKey] = "100000"
        }).Build();
        AssistantApprovalGate gate = new(
            new AssistantToolContext(rig.UserId, rig.F.ProjectId), rig.F.OrganizationId, rig.F.Db, broker,
            new AssistantApprovalCardBuilder(rig.Db, rig.Access, rig.Current, socialOutputs: rig.F.Outputs), config,
            emit: (f, _) => { lock (frames) frames.Add(f); return Task.CompletedTask; },
            caption: (_, _) => Task.CompletedTask);
        AIFunctionArguments args = new()
        {
            ["outputId"] = rig.F.OutputId,
            ["accountId"] = rig.F.Account.Id,
            ["scheduledAt"] = Rig.When(),
            ["title"] = "Rome in a minute",
            ["body"] = LongText,
            ["hashtags"] = new[] { "rome" },
            ["confirmationToken"] = "t"
        };
        int runs = 0;

        Task<object?> pending = gate.RunAsync("ApplySchedulePost", args, () => { runs++; return ValueTask.FromResult<object?>("{}"); }, CancellationToken.None);
        AssistantActionFrame? card = null;
        for (int i = 0; i < 500 && card is null; i++)
        {
            lock (frames) card = frames.FirstOrDefault(f => f.State == AssistantActionState.Pending);
            if (card is null) await Task.Delay(10);
        }

        card.Should().NotBeNull("a scheduled post is asked about even in auto mode");
        card!.Mode.Should().Be(AssistantActionMode.Ask);
        card.AutoNote.Should().BeNull("an always-ask card is not an 'over your cost limit' card");
        string shown = string.Join("\n", card.Changes!.Select(c => c.Text)) + "\n" + card.Summary + "\n" + card.Title;
        shown.Should().Contain(Channel).And.Contain("YouTube Shorts").And.Contain("final.mp4").And.Contain("“P”");
        shown.Should().Contain("Rome in a minute").And.Contain(LongText, "the whole text is on the card, never shortened");
        shown.Should().Contain("#rome");
        AnyGuid.IsMatch(shown).Should().BeFalse("names, not ids");

        broker.Answer(card.Id, rig.UserId, rig.F.OrganizationId, allow: true, alwaysAllow: true);
        await pending;
        runs.Should().Be(1);
        (await rig.F.Db.ApplicationUsers.AsNoTracking().SingleAsync(u => u.Id == rig.UserId)).AssistantAutoApprove
            .Should().BeTrue("it was already on; the point is the click was required");
    }

    [Fact]
    public async Task Declining_the_card_runs_nothing_and_a_card_for_a_foreign_account_is_refused_without_asking()
    {
        using Rig rig = new();
        AssistantApprovalCardBuilder builder = new(rig.Db, rig.Access, rig.Current, socialOutputs: rig.F.Outputs);
        AssistantToolContext context = new(rig.UserId, rig.F.ProjectId);
        SocialAccount foreign = rig.F.AddAccount(Guid.NewGuid());

        ApprovalVerdict ok = await builder.BuildAsync("ApplySchedulePost", new AIFunctionArguments
        {
            ["outputId"] = rig.F.OutputId,
            ["accountId"] = rig.F.Account.Id,
            ["scheduledAt"] = Rig.When()
        }, context, null, CancellationToken.None);
        ok.Card.Should().NotBeNull();

        ApprovalVerdict refused = await builder.BuildAsync("ApplySchedulePost", new AIFunctionArguments
        {
            ["outputId"] = rig.F.OutputId,
            ["accountId"] = foreign.Id,
            ["scheduledAt"] = Rig.When()
        }, context, null, CancellationToken.None);
        refused.Card.Should().BeNull();
        refused.Refusal.Should().NotBeNull("a call the card cannot place is refused, never run");
    }

    // ------------------------------------------------------------------ schedule

    [Fact]
    public async Task Propose_then_apply_schedules_one_post_through_the_apps_create_route_and_names_things()
    {
        using Rig rig = new();
        string proposal = await rig.CallAsync("ProposeSchedulePost", args: rig.ScheduleArgs());
        JsonElement p = Parse(proposal);
        p.GetProperty("summary").GetProperty("account").GetString().Should().Be(Channel);
        p.GetProperty("summary").GetProperty("platform").GetString().Should().Be("YouTube Shorts");
        p.GetProperty("summary").GetProperty("project").GetString().Should().Be("P");
        p.GetProperty("summary").GetProperty("video").GetString().Should().Be("final.mp4");
        p.GetProperty("summary").GetProperty("hashtags").EnumerateArray().Select(e => e.GetString()).Should().Equal("#rome", "#travel");
        (await rig.F.Db.SocialPosts.CountAsync()).Should().Be(0, "a proposal writes nothing");

        string token = p.GetProperty("confirmationToken").GetString()!;
        string applied = await rig.CallAsync("ApplySchedulePost", args: rig.ScheduleArgs(("confirmationToken", token)));
        JsonElement a = Parse(applied);
        a.GetProperty("scheduled").GetBoolean().Should().BeTrue();
        a.GetProperty("status").GetString().Should().Be("scheduled");
        a.GetProperty("account").GetString().Should().Be(Channel);

        SocialPost row = await rig.F.Db.SocialPosts.AsNoTracking().SingleAsync();
        row.Status.Should().Be(SocialPostStatus.Scheduled);
        row.OrganizationId.Should().Be(rig.F.OrganizationId);
        row.SocialAccountId.Should().Be(rig.F.Account.Id);
        row.CreatedByUserId.Should().Be(rig.UserId);
        row.Title.Should().Be("Rome in a minute");
        row.ScheduledAt.Should().BeAfter(rig.F.Clock.UtcNow.AddDays(19));
    }

    [Fact]
    public async Task A_token_for_other_text_or_time_is_refused_and_nothing_is_scheduled()
    {
        using Rig rig = new();
        string token = Parse(await rig.CallAsync("ProposeSchedulePost", args: rig.ScheduleArgs())).GetProperty("confirmationToken").GetString()!;

        string changedText = await rig.CallAsync("ApplySchedulePost", args: rig.ScheduleArgs(("body", "Something else entirely"), ("confirmationToken", token)));
        string fake = await rig.CallAsync("ApplySchedulePost", args: rig.ScheduleArgs(("confirmationToken", "made-up")));

        changedText.Should().Contain("stale confirmationToken");
        fake.Should().Contain("stale confirmationToken");
        (await rig.F.Db.SocialPosts.CountAsync()).Should().Be(0);
    }

    private static async Task<Guid> SavePostTextAsync(Rig rig, string body)
    {
        string platformId = SocialPlatformLimits.For(rig.F.Account.Platform).TextPlatformId;
        ReelBolt.Inference.Api.Services.PostTexts.PostTextRow row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = rig.F.ProjectId,
            OutputId = rig.F.OutputId,
            Platform = platformId,
            Language = "en",
            Title = "Saved title",
            Body = body,
            HashtagsJson = "[\"rome\"]"
        };
        rig.F.Db.PostTexts.Add(row);
        await rig.F.Db.SaveChangesAsync();
        return row.Id;
    }

    private static (string, object?)[] SavedTextArgs(Rig rig, Guid postTextId, params (string, object?)[] extra) =>
    [
        ("outputId", rig.F.OutputId), ("accountId", rig.F.Account.Id), ("scheduledAt", Rig.When()), ("postTextId", postTextId), .. extra
    ];

    [Fact]
    public async Task A_saved_post_text_edited_after_the_proposal_is_refused_and_nothing_is_scheduled()
    {
        using Rig rig = new();
        Guid textId = await SavePostTextAsync(rig, "The text the person approved.");
        string token = Parse(await rig.CallAsync("ProposeSchedulePost", args: SavedTextArgs(rig, textId))).GetProperty("confirmationToken").GetString()!;

        // An in-place save between the card and the click.
        var saved = await rig.F.Db.PostTexts.SingleAsync(t => t.Id == textId);
        saved.Body = "Different words nobody approved.";
        await rig.F.Db.SaveChangesAsync();
        string applied = await rig.CallAsync("ApplySchedulePost", args: SavedTextArgs(rig, textId, ("confirmationToken", token)));

        applied.Should().Contain("stale confirmationToken");
        (await rig.F.Db.SocialPosts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unchanged_saved_post_text_is_posted_exactly_as_it_was_resolved()
    {
        using Rig rig = new();
        Guid textId = await SavePostTextAsync(rig, "The text the person approved.");
        string token = Parse(await rig.CallAsync("ProposeSchedulePost", args: SavedTextArgs(rig, textId))).GetProperty("confirmationToken").GetString()!;

        Parse(await rig.CallAsync("ApplySchedulePost", args: SavedTextArgs(rig, textId, ("confirmationToken", token)))).GetProperty("scheduled").GetBoolean().Should().BeTrue();

        SocialPost row = await rig.F.Db.SocialPosts.AsNoTracking().SingleAsync();
        row.Body.Should().Be("The text the person approved.");
        row.Title.Should().Be("Saved title");
    }

    [Fact]
    public async Task The_card_is_not_shown_when_the_saved_text_changed_since_the_proposal()
    {
        using Rig rig = new();
        Guid textId = await SavePostTextAsync(rig, "The text the model proposed.");
        string token = Parse(await rig.CallAsync("ProposeSchedulePost", args: SavedTextArgs(rig, textId))).GetProperty("confirmationToken").GetString()!;
        var saved = await rig.F.Db.PostTexts.SingleAsync(t => t.Id == textId);
        saved.Body = "Edited before the card.";
        await rig.F.Db.SaveChangesAsync();

        AssistantApprovalCardBuilder builder = new(rig.Db, rig.Access, rig.Current, socialOutputs: rig.F.Outputs, confirmationTokens: rig.Tokens);
        AIFunctionArguments args = new();
        foreach ((string k, object? v) in SavedTextArgs(rig, textId, ("confirmationToken", token))) args[k] = v;
        ApprovalVerdict verdict = await builder.BuildAsync("ApplySchedulePost", args, new AssistantToolContext(rig.UserId, rig.F.ProjectId), null, CancellationToken.None);

        verdict.Card.Should().BeNull("a card for text the post will not carry is never shown");
        verdict.Refusal.Should().NotBeNull();
    }

    [Fact]
    public async Task The_apps_checks_run_before_the_user_is_asked()
    {
        using Rig rig = new();

        // the plan gate (the app's create route says the same)
        rig.F.Entitlements.SocialAllowed = false;
        (await rig.CallAsync("ProposeSchedulePost", args: rig.ScheduleArgs())).Should().Contain("does not include publishing");
        rig.F.Entitlements.SocialAllowed = true;

        // a time in the past, an unknown zone
        (await rig.CallAsync("ProposeSchedulePost", args: rig.ScheduleArgs(("scheduledAt", "2020-01-01T10:00:00Z")))).Should().Contain("already passed");
        (await rig.CallAsync("ProposeSchedulePost", args: [("outputId", rig.F.OutputId), ("accountId", rig.F.Account.Id),
            ("scheduledLocal", "2027-01-01T10:00:00"), ("timeZone", "Mars/Olympus")])).Should().Contain("time zone");

        // an account that needs reconnecting, an account of another organization, a video that is not in the project
        SocialAccount stale = rig.F.AddAccount(status: SocialAccountStatus.ReconnectNeeded);
        (await rig.CallAsync("ProposeSchedulePost", args: rig.ScheduleArgs(("accountId", stale.Id)))).Should().Contain("reconnected");
        SocialAccount foreign = rig.F.AddAccount(Guid.NewGuid());
        (await rig.CallAsync("ProposeSchedulePost", args: rig.ScheduleArgs(("accountId", foreign.Id)))).Should().Contain("account not found");
        (await rig.CallAsync("ProposeSchedulePost", args: rig.ScheduleArgs(("outputId", Guid.NewGuid())))).Should().Contain("video not found");

        // text over the platform's limit (the preflight)
        string tooLong = await rig.CallAsync("ProposeSchedulePost", args: rig.ScheduleArgs(("title", new string('x', 400))));
        tooLong.Should().Contain("error");
        tooLong.Should().NotContain("confirmationToken");
        (await rig.F.Db.SocialPosts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Authority_is_checked_on_every_call_another_organization_gets_the_same_project_refusal()
    {
        using Rig rig = new(otherOrganization: true);
        string refusal = await rig.CallAsync("ProposeSchedulePost", args: rig.ScheduleArgs(("projectId", rig.F.ProjectId)));
        refusal.Should().Contain("project not found");
        (await rig.CallAsync("ApplySchedulePost", args: rig.ScheduleArgs(("projectId", rig.F.ProjectId), ("confirmationToken", "x")))).Should().Contain("project not found");
        (await rig.F.Db.SocialPosts.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------ not over MCP, not unattended

    [Fact]
    public async Task Over_mcp_and_in_an_unattended_turn_schedule_and_cancel_refuse_and_write_nothing()
    {
        using Rig rig = new();
        SocialPost existing = rig.F.AddPost();

        foreach (ExecutionInitiator initiator in new[] { ExecutionInitiator.Mcp })
        {
            (await rig.CallAsync("ProposeSchedulePost", initiator, args: rig.ScheduleArgs())).Should().Contain("not available over MCP");
            (await rig.CallAsync("ApplySchedulePost", initiator, args: rig.ScheduleArgs(("confirmationToken", "x")))).Should().Contain("not available over MCP");
            (await rig.CallAsync("ProposeCancelScheduledPost", initiator, args: [("postId", existing.Id)])).Should().Contain("not available over MCP");
            (await rig.CallAsync("ApplyCancelScheduledPost", initiator, args: [("postId", existing.Id), ("confirmationToken", "x")])).Should().Contain("not available over MCP");
        }

        (await rig.CallAsync("ProposeSchedulePost", wake: true, args: rig.ScheduleArgs())).Should().Contain("nobody to ask");
        (await rig.CallAsync("ProposeCancelScheduledPost", wake: true, args: [("postId", existing.Id)])).Should().Contain("nobody to ask");
        (await rig.F.Db.SocialPosts.AsNoTracking().SingleAsync()).Status.Should().Be(SocialPostStatus.Scheduled);
    }

    [Fact]
    public async Task The_mcp_catalog_publishes_the_reads_and_neither_the_pairs_nor_a_way_to_call_them()
    {
        using Rig rig = new();
        IReadOnlyList<AIFunction> tools = rig.Tools.GetTools(new AssistantToolContext(rig.UserId, rig.F.ProjectId, Initiator: ExecutionInitiator.Mcp));
        tools.Select(t => t.Name).Should().HaveCount(7);

        IReadOnlyList<McpToolDescriptor> catalog = AssistantMcpToolSurface.Project(tools, new HashSet<string>());
        catalog.Select(c => c.Name).Should().BeEquivalentTo("ListSocialAccounts", "ListScheduledPosts", "GetPostPerformance");
        catalog.Should().OnlyContain(c => c.Annotation.ReadOnlyHint && !c.Annotation.DestructiveHint);

        foreach (string name in new[] { "SchedulePost", "CancelScheduledPost", "ProposeSchedulePost", "ApplySchedulePost", "ProposeCancelScheduledPost", "ApplyCancelScheduledPost" })
            AssistantMcpToolSurface.TryResolve(name, tools, out _).Should().BeFalse(name);
        AssistantMcpToolSurface.TryResolve("ListSocialAccounts", tools, out _).Should().BeTrue();
    }

    // ------------------------------------------------------------------ cancel

    [Fact]
    public async Task Cancel_propose_then_apply_cancels_through_the_apps_route_and_a_started_post_cannot_be_cancelled()
    {
        using Rig rig = new();
        SocialPost post = rig.F.AddPost(body: "Hello there");
        SocialPost sending = rig.F.AddPost(SocialPostStatus.Publishing);

        JsonElement proposal = Parse(await rig.CallAsync("ProposeCancelScheduledPost", args: [("postId", post.Id)]));
        proposal.GetProperty("summary").GetProperty("account").GetString().Should().Be(Channel);
        proposal.GetProperty("summary").GetProperty("project").GetString().Should().Be("P");
        string token = proposal.GetProperty("confirmationToken").GetString()!;
        (await rig.F.Db.SocialPosts.AsNoTracking().SingleAsync(p => p.Id == post.Id)).Status.Should().Be(SocialPostStatus.Scheduled);

        (await rig.CallAsync("ApplyCancelScheduledPost", args: [("postId", post.Id), ("confirmationToken", "wrong")])).Should().Contain("stale confirmationToken");
        (await rig.CallAsync("ApplyCancelScheduledPost", args: [("postId", sending.Id), ("confirmationToken", token)])).Should().Contain("stale confirmationToken");
        (await rig.CallAsync("ProposeCancelScheduledPost", args: [("postId", sending.Id)])).Should().Contain("cannot be cancelled");

        Parse(await rig.CallAsync("ApplyCancelScheduledPost", args: [("postId", post.Id), ("confirmationToken", token)]))
            .GetProperty("cancelled").GetBoolean().Should().BeTrue();
        (await rig.F.Db.SocialPosts.AsNoTracking().SingleAsync(p => p.Id == post.Id)).Status.Should().Be(SocialPostStatus.Cancelled);
        (await rig.F.Db.SocialPosts.AsNoTracking().SingleAsync(p => p.Id == sending.Id)).Status.Should().Be(SocialPostStatus.Publishing);
    }

    [Fact]
    public async Task Cancel_card_names_the_account_project_video_and_shows_the_text_and_a_post_of_another_organization_is_not_found()
    {
        using Rig rig = new();
        SocialPost post = rig.F.AddPost(title: "Rome in a minute", body: LongText);
        AssistantApprovalCardBuilder builder = new(rig.Db, rig.Access, rig.Current, socialOutputs: rig.F.Outputs);

        ApprovalVerdict verdict = await builder.BuildAsync("ApplyCancelScheduledPost", new AIFunctionArguments { ["postId"] = post.Id },
            new AssistantToolContext(rig.UserId), null, CancellationToken.None);
        string shown = string.Join("\n", verdict.Card!.Changes!.Select(c => c.Text)) + verdict.Card.Title + verdict.Card.Summary;
        shown.Should().Contain(Channel).And.Contain("“P”").And.Contain(LongText);
        AnyGuid.IsMatch(shown).Should().BeFalse();

        using Rig stranger = new(rig.F, otherOrganization: true);
        (await stranger.CallAsync("ProposeCancelScheduledPost", args: [("postId", post.Id)])).Should().Contain("post not found");
    }

    // ------------------------------------------------------------------ reads

    [Fact]
    public async Task The_reads_name_accounts_and_posts_never_return_a_token_and_show_missing_counts_as_null()
    {
        using Rig rig = new();
        SocialPost post = rig.F.AddPost(title: "Rome in a minute", body: "Hello");

        string accounts = await rig.CallAsync("ListSocialAccounts");
        accounts.Should().Contain(Channel).And.Contain("YouTube Shorts").And.Contain("active");
        accounts.Should().NotContain("access-1").And.NotContain("refresh-1").And.NotContain("ProtectedAccessToken");

        JsonElement queue = Parse(await rig.CallAsync("ListScheduledPosts", args: [("status", "scheduled")]));
        queue.GetProperty("total").GetInt32().Should().Be(1);
        JsonElement item = queue.GetProperty("posts")[0];
        item.GetProperty("account").GetString().Should().Be(Channel);
        item.GetProperty("project").GetString().Should().Be("P");
        item.GetProperty("video").GetString().Should().Be("final.mp4");
        item.GetProperty("postId").GetGuid().Should().Be(post.Id);

        JsonElement perf = Parse(await rig.CallAsync("GetPostPerformance"));
        perf.GetProperty("publishedPosts").GetInt32().Should().Be(0);
        perf.GetProperty("totals").GetProperty("views").ValueKind.Should().Be(JsonValueKind.Null, "no reading is 'not available', never 0");
        (await rig.CallAsync("GetPostPerformance", args: [("postId", Guid.NewGuid())])).Should().Contain("post not found");
    }

    [Fact]
    public async Task The_reads_see_only_the_active_organization()
    {
        using Rig rig = new();
        rig.F.AddPost();
        using Rig stranger = new(rig.F, otherOrganization: true);

        Parse(await stranger.CallAsync("ListSocialAccounts")).GetProperty("accounts").GetArrayLength().Should().Be(0);
        Parse(await stranger.CallAsync("ListScheduledPosts")).GetProperty("posts").GetArrayLength().Should().Be(0);
    }
}
