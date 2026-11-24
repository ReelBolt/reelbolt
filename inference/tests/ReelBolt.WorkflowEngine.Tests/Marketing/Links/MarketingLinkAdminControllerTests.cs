#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing.Links;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Acquisition;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing.Links;

/// <summary>
/// The platform-admin tracked-link routes (docs/design/new-user-funnel.md section 6): list, create, edit
/// and the per-link funnel. The service runs for real on a relational test database; the funnel reader
/// is a double here (its SQL is PostgreSQL-only and has its own real-Postgres test).
/// </summary>
public sealed class MarketingLinkAdminControllerTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly MarketingHarness _h = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly FakeFunnel _funnel = new();
    private readonly Guid _admin;

    public MarketingLinkAdminControllerTests()
    {
        _admin = _h.AddUser("admin@example.test", _h.Now);
    }

    public void Dispose()
    {
        foreach (IDisposable d in _disposables) d.Dispose();
        _h.Dispose();
    }

    private sealed class FakeUser(Guid id, bool admin, OrganizationRole? orgRole = null) : ICurrentUser
    {
        public Guid UserId => id;
        public string Email => "admin@example.test";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => orgRole is null ? null : Guid.NewGuid();
        public OrganizationRole? OrgRole => orgRole;
        public bool IsPlatformAdmin => admin;
    }

    private sealed class FakeFunnel : ITrackedLinkFunnelReader
    {
        public List<DateOnly> Asked { get; } = [];
        public TrackedLinkFunnel Result { get; set; } = new([], new FunnelAccountSteps(0, 0, 0, 0, 0, 0, 0));

        public Task<TrackedLinkFunnel> ReadAsync(DateOnly sinceDay, CancellationToken ct)
        {
            Asked.Add(sinceDay);
            return Task.FromResult(Result);
        }
    }

    private InferenceApiDbContext Db()
    {
        InferenceApiDbContext db = _h.NewDb();
        _disposables.Add(db);
        return db;
    }

    private MarketingLinkAdminController Controller(
        bool admin = true, bool urlCapture = false, OrganizationRole? orgRole = null, bool visitIngest = false)
        => new(
            new TrackedLinkService(Db(), _h.Clock),
            _funnel,
            Options.Create(new AttributionOptions { UrlCaptureEnabled = urlCapture }),
            Options.Create(new AlphaTelemetryOptions { VisitIngestEnabled = visitIngest }),
            new FakeUser(_admin, admin, orgRole),
            _h.Clock);

    private static MarketingLinkCreateRequest Body(
        string? code = "hn-launch",
        string? channel = "hackernews",
        string? medium = "community",
        string? campaign = "launch-2026-11",
        string? placement = "Show HN, first comment",
        string? destinationPath = "/for/software-teams",
        string? persona = "software",
        long? costMinor = null,
        string? costCurrency = null)
        => new(code, channel, medium, campaign, placement, destinationPath, persona, costMinor, costCurrency);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement AsJson(IActionResult result)
        => JsonSerializer.SerializeToElement(result.Should().BeOfType<OkObjectResult>().Subject.Value, Web);

    private static void ShouldRefuse(IActionResult result, int status, string error, string? field = null)
    {
        ObjectResult refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(status);
        JsonElement body = JsonSerializer.SerializeToElement(refusal.Value, Web);
        body.GetProperty("error").GetString().Should().Be(error);
        if (field is not null) body.GetProperty("field").GetString().Should().Be(field);
    }

    private async Task<Guid> CreateAsync(MarketingLinkCreateRequest? body = null)
        => AsJson(await Controller().Create(body ?? Body(), CancellationToken.None)).GetProperty("id").GetGuid();

    private List<MarketingLink> Links()
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingLinks.AsNoTracking().OrderBy(l => l.Code).ToList();
    }

    private void AddStat(Guid linkId, DateOnly day, int clicks, int botHits)
    {
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingLinkDailyStats.Add(new MarketingLinkDailyStat { LinkId = linkId, Day = day, Clicks = clicks, BotHits = botHits });
        db.SaveChanges();
    }

    // ---- authority ----

    [Fact]
    public async Task Every_action_refuses_a_caller_who_is_not_a_platform_admin_and_does_nothing()
    {
        Guid id = await CreateAsync();

        (await Controller(admin: false).List(CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await Controller(admin: false).Create(Body(code: "second"), CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await Controller(admin: false).Patch(id, Json("""{"archived":true}"""), CancellationToken.None)).Should().BeOfType<ForbidResult>();
        (await Controller(admin: false).Funnel(30, CancellationToken.None)).Should().BeOfType<ForbidResult>();

        MarketingLink only = Links().Single();
        only.Code.Should().Be("hn-launch");
        only.ArchivedAt.Should().BeNull();
        _funnel.Asked.Should().BeEmpty("the funnel is not even read for a caller without platform authority");
    }

    [Fact]
    public async Task An_organization_owner_is_not_a_platform_admin()
    {
        (await Controller(admin: false, orgRole: OrganizationRole.Owner).List(CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
        (await Controller(admin: false, orgRole: OrganizationRole.Owner).Create(Body(), CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
        Links().Should().BeEmpty();
    }

    [Fact]
    public void The_controller_requires_a_signed_in_caller_on_the_marketing_admin_route()
    {
        Type controller = typeof(MarketingLinkAdminController);
        controller.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        controller.GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/v1/marketing/admin/links");
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Should().BeEmpty("no links admin action is anonymous");
        controller.GetMethod(nameof(MarketingLinkAdminController.Patch))!.GetCustomAttribute<HttpPatchAttribute>()!.Template
            .Should().Be("{id:guid}");
        controller.GetMethod(nameof(MarketingLinkAdminController.Funnel))!.GetCustomAttribute<HttpGetAttribute>()!.Template
            .Should().Be("funnel");
    }

    // ---- create ----

    [Fact]
    public async Task Create_returns_the_link_in_the_shape_of_the_contract()
    {
        JsonElement link = AsJson(await Controller().Create(Body(), CancellationToken.None));

        link.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "code", "channel", "medium", "campaign", "placement", "destinationPath", "persona",
            "costMinor", "costCurrency", "createdAt", "archivedAt", "shortPath", "longPath", "totalClicks");
        link.GetProperty("id").GetGuid().Should().NotBeEmpty();
        link.GetProperty("code").GetString().Should().Be("hn-launch");
        link.GetProperty("channel").GetString().Should().Be("hackernews");
        link.GetProperty("medium").GetString().Should().Be("community");
        link.GetProperty("campaign").GetString().Should().Be("launch-2026-11");
        link.GetProperty("placement").GetString().Should().Be("Show HN, first comment");
        link.GetProperty("destinationPath").GetString().Should().Be("/for/software-teams");
        link.GetProperty("persona").GetString().Should().Be("software");
        link.GetProperty("costMinor").ValueKind.Should().Be(JsonValueKind.Null);
        link.GetProperty("costCurrency").ValueKind.Should().Be(JsonValueKind.Null);
        link.GetProperty("createdAt").GetDateTimeOffset().Should().Be(_h.Clock.Now);
        link.GetProperty("archivedAt").ValueKind.Should().Be(JsonValueKind.Null);
        link.GetProperty("shortPath").GetString().Should().Be("/l/hn-launch");
        link.GetProperty("longPath").GetString().Should().Be(
            "/for/software-teams?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&for=software");
        link.GetProperty("totalClicks").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Create_stores_the_link_trimmed_with_its_creator()
    {
        await Controller().Create(
            Body(code: " hn-launch ", placement: "  Show HN  ", persona: " ", costMinor: 12500, costCurrency: "EUR"),
            CancellationToken.None);

        MarketingLink stored = Links().Single();
        stored.Code.Should().Be("hn-launch");
        stored.Placement.Should().Be("Show HN");
        stored.Persona.Should().BeNull("a blank persona is no persona");
        stored.CostMinor.Should().Be(12500);
        stored.CostCurrency.Should().Be("EUR");
        stored.CreatedByUserId.Should().Be(_admin);
        stored.CreatedAt.Should().Be(_h.Now);
        stored.ArchivedAt.Should().BeNull();
    }

    public static TheoryData<string, MarketingLinkCreateRequest> InvalidBodies() => new()
    {
        { "code_invalid", Body(code: null) },
        { "code_invalid", Body(code: "") },
        { "code_invalid", Body(code: "a") },
        { "code_invalid", Body(code: "HN-Launch") },
        { "code_invalid", Body(code: "hn_launch") },
        { "code_invalid", Body(code: "hn launch") },
        { "code_invalid", Body(code: new string('a', 41)) },
        { "channel_invalid", Body(channel: null) },
        { "channel_invalid", Body(channel: "twitter") },
        { "channel_invalid", Body(channel: "HackerNews") },
        { "medium_invalid", Body(medium: null) },
        { "medium_invalid", Body(medium: "cpc") },
        { "campaign_invalid", Body(campaign: null) },
        { "campaign_invalid", Body(campaign: "Launch 2026") },
        { "campaign_invalid", Body(campaign: new string('a', 65)) },
        { "placement_invalid", Body(placement: null) },
        { "placement_invalid", Body(placement: "   ") },
        { "placement_invalid", Body(placement: new string('a', 161)) },
        { "placement_invalid", Body(placement: "two\nlines") },
        { "destination_path_invalid", Body(destinationPath: null) },
        { "destination_path_invalid", Body(destinationPath: "for/creators") },
        { "destination_path_invalid", Body(destinationPath: "//evil.example") },
        { "destination_path_invalid", Body(destinationPath: "https://evil.example/") },
        { "destination_path_invalid", Body(destinationPath: "/pricing?plan=pro") },
        { "destination_path_invalid", Body(destinationPath: "/l/other-link") },
        { "persona_invalid", Body(persona: "developer") },
        { "persona_invalid", Body(persona: "Creator") },
        { "cost_invalid", Body(costMinor: 100) },
        { "cost_invalid", Body(costCurrency: "EUR") },
        { "cost_invalid", Body(costMinor: -1, costCurrency: "EUR") },
        { "cost_invalid", Body(costMinor: 100, costCurrency: "eur") },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Create_refuses_a_field_outside_its_closed_set_or_shape_and_writes_nothing(
        string error, MarketingLinkCreateRequest body)
    {
        ShouldRefuse(await Controller().Create(body, CancellationToken.None), 400, error);
        Links().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_accepts_every_channel_medium_and_persona_of_the_vocabulary()
    {
        // Each row also passes the table's CHECK constraints, which are built from the same lists.
        int n = 0;
        foreach (string channel in AcquisitionVocabulary.LinkChannels)
        {
            (await Controller().Create(Body(code: $"link-{n++}", channel: channel), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();
        }

        foreach (string medium in AcquisitionVocabulary.LinkMedia)
        {
            (await Controller().Create(Body(code: $"link-{n++}", medium: medium), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();
        }

        foreach (string persona in AcquisitionVocabulary.Personas)
        {
            (await Controller().Create(Body(code: $"link-{n++}", persona: persona), CancellationToken.None))
                .Should().BeOfType<OkObjectResult>();
        }

        (await Controller().Create(Body(code: $"link-{n++}", persona: null), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        Links().Should().HaveCount(n);
    }

    [Fact]
    public async Task A_code_that_already_names_a_link_is_409_link_code_taken()
    {
        await CreateAsync();

        ShouldRefuse(
            await Controller().Create(Body(channel: "reddit", placement: "another place"), CancellationToken.None),
            409, "link_code_taken", "code");

        Links().Single().Channel.Should().Be("hackernews", "the first link is untouched");
    }

    [Fact]
    public async Task An_archived_links_code_is_never_reused()
    {
        Guid id = await CreateAsync();
        await Controller().Patch(id, Json("""{"archived":true}"""), CancellationToken.None);

        ShouldRefuse(await Controller().Create(Body(), CancellationToken.None), 409, "link_code_taken");
    }

    // ---- list ----

    [Fact]
    public async Task List_returns_every_link_newest_first_with_its_total_clicks()
    {
        Guid older = await CreateAsync(Body(code: "older"));
        _h.Clock.Advance(TimeSpan.FromHours(1));
        Guid newer = await CreateAsync(Body(code: "newer", persona: null));
        DateOnly today = DateOnly.FromDateTime(_h.Now);
        AddStat(older, today.AddDays(-1), clicks: 4, botHits: 9);
        AddStat(older, today, clicks: 3, botHits: 1);

        JsonElement list = AsJson(await Controller().List(CancellationToken.None));

        list.EnumerateObject().Select(p => p.Name).Should().Equal("items");
        JsonElement[] items = list.GetProperty("items").EnumerateArray().ToArray();
        items.Select(i => i.GetProperty("code").GetString()).Should().Equal("newer", "older");
        items[0].GetProperty("id").GetGuid().Should().Be(newer);
        items[0].GetProperty("totalClicks").GetInt64().Should().Be(0);
        items[1].GetProperty("totalClicks").GetInt64().Should().Be(7, "clicks of every day; bot hits are not clicks");
        items[1].EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "code", "channel", "medium", "campaign", "placement", "destinationPath", "persona",
            "costMinor", "costCurrency", "createdAt", "archivedAt", "shortPath", "longPath", "totalClicks");
        items[0].GetProperty("longPath").GetString().Should().NotContain("&for=");
    }

    [Fact]
    public async Task List_is_empty_when_there_are_no_links()
    {
        AsJson(await Controller().List(CancellationToken.None)).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // ---- patch ----

    [Fact]
    public async Task Patch_changes_the_editable_fields_and_returns_the_link()
    {
        Guid id = await CreateAsync();
        AddStat(id, DateOnly.FromDateTime(_h.Now), clicks: 5, botHits: 2);

        JsonElement link = AsJson(await Controller().Patch(
            id,
            Json("""{"placement":" Show HN, top ","destinationPath":"/for/creators","persona":"creator","costMinor":2500,"costCurrency":"USD"}"""),
            CancellationToken.None));

        link.GetProperty("placement").GetString().Should().Be("Show HN, top");
        link.GetProperty("destinationPath").GetString().Should().Be("/for/creators");
        link.GetProperty("persona").GetString().Should().Be("creator");
        link.GetProperty("costMinor").GetInt64().Should().Be(2500);
        link.GetProperty("costCurrency").GetString().Should().Be("USD");
        link.GetProperty("totalClicks").GetInt64().Should().Be(5);
        link.GetProperty("longPath").GetString().Should().Be(
            "/for/creators?utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&for=creator");

        MarketingLink stored = Links().Single();
        stored.Placement.Should().Be("Show HN, top");
        stored.DestinationPath.Should().Be("/for/creators");
        stored.Persona.Should().Be("creator");
        stored.CostMinor.Should().Be(2500);
        stored.CostCurrency.Should().Be("USD");
    }

    [Fact]
    public async Task Patch_leaves_an_absent_field_alone_and_clears_one_sent_as_null()
    {
        Guid id = await CreateAsync(Body(costMinor: 2500, costCurrency: "USD"));

        await Controller().Patch(id, Json("""{"placement":"elsewhere"}"""), CancellationToken.None);
        MarketingLink untouched = Links().Single();
        untouched.Persona.Should().Be("software");
        untouched.CostMinor.Should().Be(2500);
        untouched.CostCurrency.Should().Be("USD");

        await Controller().Patch(id, Json("""{"persona":null,"costMinor":null,"costCurrency":null}"""), CancellationToken.None);
        MarketingLink cleared = Links().Single();
        cleared.Persona.Should().BeNull();
        cleared.CostMinor.Should().BeNull();
        cleared.CostCurrency.Should().BeNull();
        cleared.Placement.Should().Be("elsewhere");
    }

    [Fact]
    public async Task Patch_archives_once_and_un_archives()
    {
        Guid id = await CreateAsync();
        _h.Clock.Advance(TimeSpan.FromHours(2));
        DateTime archivedAt = _h.Now;

        JsonElement archived = AsJson(await Controller().Patch(id, Json("""{"archived":true}"""), CancellationToken.None));
        archived.GetProperty("archivedAt").GetDateTimeOffset().Should().Be(_h.Clock.Now);

        _h.Clock.Advance(TimeSpan.FromHours(2));
        await Controller().Patch(id, Json("""{"archived":true}"""), CancellationToken.None);
        Links().Single().ArchivedAt.Should().Be(archivedAt, "archiving an archived link keeps the first time");

        JsonElement restored = AsJson(await Controller().Patch(id, Json("""{"archived":false}"""), CancellationToken.None));
        restored.GetProperty("archivedAt").ValueKind.Should().Be(JsonValueKind.Null);
        Links().Single().ArchivedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("code", "other-code")]
    [InlineData("channel", "reddit")]
    [InlineData("medium", "social")]
    [InlineData("campaign", "another-campaign")]
    public async Task Code_channel_medium_and_campaign_are_immutable(string field, string value)
    {
        Guid id = await CreateAsync();

        IActionResult result = await Controller().Patch(
            id, Json($$"""{"{{field}}":"{{value}}","placement":"changed"}"""), CancellationToken.None);

        ShouldRefuse(result, 400, "field_immutable", field);
        MarketingLink stored = Links().Single();
        stored.Code.Should().Be("hn-launch");
        stored.Channel.Should().Be("hackernews");
        stored.Medium.Should().Be("community");
        stored.Campaign.Should().Be("launch-2026-11");
        stored.Placement.Should().Be("Show HN, first comment", "a refused edit writes nothing, not even its valid part");
    }

    [Fact]
    public async Task An_immutable_field_cannot_be_cleared_either()
    {
        Guid id = await CreateAsync();
        ShouldRefuse(await Controller().Patch(id, Json("""{"campaign":null}"""), CancellationToken.None), 400, "field_immutable", "campaign");
    }

    [Fact]
    public async Task Patch_accepts_a_whole_link_echoed_back_and_ignores_fields_it_does_not_edit()
    {
        Guid id = await CreateAsync();
        JsonElement current = AsJson(await Controller().List(CancellationToken.None)).GetProperty("items")[0];
        Dictionary<string, JsonElement> echoed = current.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        echoed["placement"] = JsonSerializer.SerializeToElement("Show HN, edited");
        echoed["totalClicks"] = JsonSerializer.SerializeToElement(999);
        echoed["shortPath"] = JsonSerializer.SerializeToElement("/l/something-else");

        JsonElement link = AsJson(await Controller().Patch(id, JsonSerializer.SerializeToElement(echoed), CancellationToken.None));

        link.GetProperty("placement").GetString().Should().Be("Show HN, edited");
        link.GetProperty("code").GetString().Should().Be("hn-launch");
        link.GetProperty("shortPath").GetString().Should().Be("/l/hn-launch");
        link.GetProperty("totalClicks").GetInt64().Should().Be(0);
    }

    public static TheoryData<string, string> InvalidPatches() => new()
    {
        { "placement_invalid", """{"placement":""}""" },
        { "placement_invalid", """{"placement":null}""" },
        { "placement_invalid", """{"placement":7}""" },
        { "destination_path_invalid", """{"destinationPath":"//evil.example"}""" },
        { "destination_path_invalid", """{"destinationPath":"https://evil.example/"}""" },
        { "destination_path_invalid", """{"destinationPath":null}""" },
        { "persona_invalid", """{"persona":"developer"}""" },
        { "persona_invalid", """{"persona":3}""" },
        { "cost_invalid", """{"costMinor":100}""" },
        { "cost_invalid", """{"costCurrency":"EUR"}""" },
        { "cost_invalid", """{"costMinor":-5,"costCurrency":"EUR"}""" },
        { "cost_invalid", """{"costMinor":1.5,"costCurrency":"EUR"}""" },
        { "cost_invalid", """{"costMinor":"100","costCurrency":"EUR"}""" },
        { "cost_invalid", """{"costMinor":100,"costCurrency":"euro"}""" },
        { "invalid_body", """{"archived":"yes"}""" },
        { "invalid_body", """["archived"]""" },
        { "invalid_body", "\"archived\"" },
    };

    [Theory]
    [MemberData(nameof(InvalidPatches))]
    public async Task Patch_refuses_an_invalid_edit_and_writes_nothing(string error, string json)
    {
        Guid id = await CreateAsync();

        ShouldRefuse(await Controller().Patch(id, Json(json), CancellationToken.None), 400, error);

        MarketingLink stored = Links().Single();
        stored.Placement.Should().Be("Show HN, first comment");
        stored.DestinationPath.Should().Be("/for/software-teams");
        stored.Persona.Should().Be("software");
        stored.CostMinor.Should().BeNull();
        stored.CostCurrency.Should().BeNull();
        stored.ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task Patch_of_an_unknown_link_is_404()
    {
        await CreateAsync();
        (await Controller().Patch(Guid.NewGuid(), Json("""{"archived":true}"""), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
        Links().Single().ArchivedAt.Should().BeNull();
    }

    // ---- funnel ----

    [Fact]
    public async Task Funnel_returns_the_shape_of_the_contract()
    {
        Guid linkId = Guid.NewGuid();
        _funnel.Result = new TrackedLinkFunnel(
            [
                new TrackedLinkFunnelRow(
                    linkId, "hn-launch", "hackernews", "community", "launch-2026-11", "Show HN", "/for/software-teams",
                    Archived: false, CostMinor: 2500, CostCurrency: "USD", Clicks: 120, BotHits: 14, Visitors: 31,
                    new FunnelAccountSteps(9, 8, 7, 6, 5, 4, 1)),
            ],
            new FunnelAccountSteps(20, 18, 11, 10, 9, 3, 2));

        JsonElement body = AsJson(await Controller().Funnel(30, CancellationToken.None));

        body.EnumerateObject().Select(p => p.Name).Should().Equal(
            "days", "generatedAt", "urlCaptureEnabled", "visitIngestEnabled", "items", "unattributed");
        body.GetProperty("days").GetInt32().Should().Be(30);
        body.GetProperty("generatedAt").GetDateTimeOffset().Should().Be(_h.Clock.Now);
        body.GetProperty("urlCaptureEnabled").GetBoolean().Should().BeFalse();
        body.GetProperty("visitIngestEnabled").GetBoolean().Should().BeFalse();

        JsonElement item = body.GetProperty("items").EnumerateArray().Single();
        item.EnumerateObject().Select(p => p.Name).Should().Equal(
            "linkId", "code", "channel", "medium", "campaign", "placement", "destinationPath", "archived",
            "costMinor", "costCurrency", "clicks", "botHits", "visitors", "signups", "verified", "onboarded",
            "firstProject", "firstRun", "firstVideo", "paid");
        item.GetProperty("linkId").GetGuid().Should().Be(linkId);
        item.GetProperty("code").GetString().Should().Be("hn-launch");
        item.GetProperty("archived").GetBoolean().Should().BeFalse();
        item.GetProperty("costMinor").GetInt64().Should().Be(2500);
        item.GetProperty("costCurrency").GetString().Should().Be("USD");
        new[] { "clicks", "botHits", "visitors", "signups", "verified", "onboarded", "firstProject", "firstRun", "firstVideo", "paid" }
            .Select(name => item.GetProperty(name).GetInt64())
            .Should().Equal(120, 14, 31, 9, 8, 7, 6, 5, 4, 1);

        JsonElement unattributed = body.GetProperty("unattributed");
        unattributed.EnumerateObject().Select(p => p.Name).Should().Equal(
            "signups", "verified", "onboarded", "firstProject", "firstRun", "firstVideo", "paid");
        unattributed.EnumerateObject().Select(p => p.Value.GetInt64()).Should().Equal(20, 18, 11, 10, 9, 3, 2);
    }

    [Fact]
    public async Task Funnel_defaults_to_thirty_days_and_the_window_is_whole_utc_days_including_today()
    {
        MethodInfo funnel = typeof(MarketingLinkAdminController).GetMethod(nameof(MarketingLinkAdminController.Funnel))!;
        funnel.GetParameters().Single(p => p.Name == "days").DefaultValue.Should().Be(30);
        MarketingLinkAdminController.DefaultFunnelDays.Should().Be(30);

        await Controller().Funnel(MarketingLinkAdminController.DefaultFunnelDays, CancellationToken.None);
        await Controller().Funnel(1, CancellationToken.None);

        DateOnly today = DateOnly.FromDateTime(_h.Now);
        _funnel.Asked.Should().Equal(today.AddDays(-29), today);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-7, 1)]
    [InlineData(1, 1)]
    [InlineData(90, 90)]
    [InlineData(365, 365)]
    [InlineData(366, 365)]
    [InlineData(100000, 365)]
    public async Task Funnel_clamps_days_to_one_through_three_hundred_sixty_five(int asked, int used)
    {
        JsonElement body = AsJson(await Controller().Funnel(asked, CancellationToken.None));

        body.GetProperty("days").GetInt32().Should().Be(used);
        _funnel.Asked.Single().Should().Be(DateOnly.FromDateTime(_h.Now).AddDays(-(used - 1)));
    }

    [Fact]
    public async Task Funnel_reports_whether_url_capture_is_on()
    {
        AsJson(await Controller(urlCapture: true).Funnel(30, CancellationToken.None))
            .GetProperty("urlCaptureEnabled").GetBoolean().Should().BeTrue();
        new AttributionOptions().UrlCaptureEnabled.Should().BeFalse("URL capture is off until the controller turns it on");
        AttributionOptions.SectionName.Should().Be("Attribution");
    }

    [Fact]
    public async Task Funnel_reports_whether_the_visit_beacon_is_accepted_so_zero_visitors_can_be_read()
    {
        // Off is the shipped default: no visitor row is ever written, so every link reads 0 visitors.
        // The page must be able to say "the switch is off" instead of "nobody accepted cookies".
        AsJson(await Controller().Funnel(30, CancellationToken.None))
            .GetProperty("visitIngestEnabled").GetBoolean().Should().BeFalse();
        JsonElement on = AsJson(await Controller(visitIngest: true).Funnel(30, CancellationToken.None));
        on.GetProperty("visitIngestEnabled").GetBoolean().Should().BeTrue();
        on.GetProperty("urlCaptureEnabled").GetBoolean().Should().BeFalse("the two switches are independent");
        new AlphaTelemetryOptions().VisitIngestEnabled.Should().BeFalse();
    }

    [Fact]
    public void The_visit_switch_in_the_response_is_the_one_the_beacon_endpoint_obeys()
    {
        // One option, read by both: the endpoint answers 404 on it, the funnel reports it.
        IConfiguration configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Telemetry:VisitIngestEnabled"] = "true" })
            .Build();
        configured.GetSection(AlphaTelemetryOptions.SectionName).Get<AlphaTelemetryOptions>()!.VisitIngestEnabled.Should().BeTrue();
        typeof(MarketingLinkAdminController).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType).Should().Contain(typeof(IOptions<AlphaTelemetryOptions>));
        typeof(ReelBolt.Inference.Api.Controllers.TelemetryVisitController).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType).Should().Contain(typeof(IOptions<AlphaTelemetryOptions>));
    }

    // ---- configuration ----

    [Fact]
    public void The_shipped_appsettings_bind_both_switches_to_their_defaults()
    {
        IConfiguration shipped = new ConfigurationBuilder().AddJsonFile(AppSettingsPath()).Build();
        shipped.GetSection(MarketingLinksOptions.SectionName).Get<MarketingLinksOptions>()!.Enabled.Should().BeTrue();
        shipped.GetSection(AttributionOptions.SectionName).Get<AttributionOptions>()!.UrlCaptureEnabled.Should().BeFalse();
    }

    [Fact]
    public void The_compose_style_environment_keys_override_the_shipped_values()
    {
        IConfiguration configured = new ConfigurationBuilder()
            .AddJsonFile(AppSettingsPath())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // What Marketing__Links__Enabled=false and Attribution__UrlCaptureEnabled=true become.
                ["Marketing:Links:Enabled"] = "false",
                ["Attribution:UrlCaptureEnabled"] = "true",
            })
            .Build();
        configured.GetSection(MarketingLinksOptions.SectionName).Get<MarketingLinksOptions>()!.Enabled.Should().BeFalse();
        configured.GetSection(AttributionOptions.SectionName).Get<AttributionOptions>()!.UrlCaptureEnabled.Should().BeTrue();
    }

    private static string AppSettingsPath()
    {
        const string relative = "inference/src/ReelBolt.Inference.Api/appsettings.json";
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, relative))) dir = Path.GetDirectoryName(dir);
        return dir is null ? throw new FileNotFoundException(relative) : Path.Combine(dir, relative);
    }
}
