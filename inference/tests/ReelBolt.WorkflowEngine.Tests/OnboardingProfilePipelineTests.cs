#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The welcome answers and the visit beacon through the real ASP.NET Core pipeline (routing, authentication,
/// model binding and JSON), as <see cref="Marketing.Links.TrackedLinkPipelineTests"/> does for tracked
/// links: what the web app and the marketing site actually send and get on the wire
/// (docs/design/new-user-funnel.md section 6).
/// </summary>
public sealed class OnboardingProfilePipelineTests : IAsyncLifetime
{
    private const string Vid = "f47ac10b-58cc-4372-a567-0e02b2c3d479";

    private readonly string _dbName = "onboarding-pipeline-" + Guid.NewGuid().ToString("N");
    private readonly RecordingProductEventLog _log = new();
    private readonly Guid _user = Guid.NewGuid();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private sealed class OnlyTheseControllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(OnboardingController) || typeInfo.AsType() == typeof(TelemetryVisitController);
    }

    /// <summary>Signs a request in as the user id in <c>X-Test-User</c>; without the header the request is anonymous.</summary>
    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string user = Request.Headers["X-Test-User"].ToString();
            if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            ClaimsIdentity identity = new([new Claim("sub", user)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class HeaderUser(IHttpContextAccessor http) : ICurrentUser
    {
        public Guid UserId => Guid.TryParse(http.HttpContext?.User.FindFirst("sub")?.Value, out Guid id) ? id : Guid.Empty;
        public string Email => "person@example.test";
        public bool IsAuthenticated => http.HttpContext?.User.Identity?.IsAuthenticated == true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
    }

    private InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(_dbName).Options);

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m =>
        {
            m.ApplicationParts.Clear();
            m.ApplicationParts.Add(new AssemblyPart(typeof(OnboardingController).Assembly));
            m.FeatureProviders.Clear();
            m.FeatureProviders.Add(new OnlyTheseControllers());
        });
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped(_ => NewDb());
        builder.Services.AddScoped<ICurrentUser, HeaderUser>();
        builder.Services.AddSingleton<IProductEventLog>(_log);
        // Client ingest stays off: the welcome answers and their events must not depend on it.
        builder.Services.Configure<AlphaTelemetryOptions>(o => o.VisitIngestEnabled = true);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();
        string address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string? json, bool signedIn = true)
    {
        using HttpRequestMessage request = new(method, path);
        if (signedIn) request.Headers.Add("X-Test-User", _user.ToString());
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public async Task The_two_answers_are_saved_and_answered_in_the_contracts_shape()
    {
        (HttpStatusCode status, JsonElement body) = await Send(
            HttpMethod.Put, "/api/v1/onboarding/profile", "{\"persona\":\"creator\",\"material\":\"footage\"}");

        status.Should().Be(HttpStatusCode.OK);
        Names(body).Should().Equal("profile", "starterTemplateKey");
        JsonElement profile = body.GetProperty("profile");
        Names(profile).Should().Equal("persona", "material", "suggestedPersona", "answeredAt", "skippedAt", "pickedPlan");
        profile.GetProperty("persona").GetString().Should().Be("creator");
        profile.GetProperty("material").GetString().Should().Be("footage");
        profile.GetProperty("suggestedPersona").ValueKind.Should().Be(JsonValueKind.Null);
        profile.GetProperty("answeredAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        profile.GetProperty("skippedAt").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("starterTemplateKey").GetString().Should().Be("edit-my-video");

        _log.Rows.Select(r => (r.UserId, r.Event, r.Detail, r.Source)).Should().Equal(
            ((Guid?)_user, "onboarding.profile_saved", "creator", "server"));
    }

    [Fact]
    public async Task The_skip_is_saved_and_get_reads_the_suggested_persona_and_the_profile_back()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            // What the Go API wrote at account creation.
            db.UserOnboardingProfiles.Add(new UserOnboardingProfile { UserId = _user, SuggestedPersona = "nonprofit" });
            await db.SaveChangesAsync();
        }

        (HttpStatusCode getStatus, JsonElement before) = await Send(HttpMethod.Get, "/api/v1/onboarding", null);
        getStatus.Should().Be(HttpStatusCode.OK);
        Names(before).Should().Equal("tour", "checklist", "profile", "starterTemplateKey");
        before.GetProperty("profile").GetProperty("suggestedPersona").GetString().Should().Be("nonprofit");
        before.GetProperty("profile").GetProperty("persona").ValueKind.Should().Be(JsonValueKind.Null);
        before.GetProperty("starterTemplateKey").ValueKind.Should().Be(JsonValueKind.Null);

        (HttpStatusCode status, JsonElement body) = await Send(HttpMethod.Put, "/api/v1/onboarding/profile", "{\"skipped\":true}");
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("profile").GetProperty("skippedAt").ValueKind.Should().Be(JsonValueKind.String);
        body.GetProperty("profile").GetProperty("answeredAt").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("profile").GetProperty("suggestedPersona").GetString().Should().Be("nonprofit");
        body.GetProperty("starterTemplateKey").ValueKind.Should().Be(JsonValueKind.Null, "the key is always present, null while there is no material");

        (_, JsonElement after) = await Send(HttpMethod.Get, "/api/v1/onboarding", null);
        after.GetProperty("profile").GetRawText().Should().Be(body.GetProperty("profile").GetRawText());
        _log.Rows.Select(r => r.Event).Should().Equal("onboarding.profile_skipped");
    }

    private static readonly string[] ProfileMembers = ["persona", "material", "suggestedPersona", "answeredAt", "skippedAt", "pickedPlan"];

    private static void ShouldBeTheEmptyProfile(JsonElement body)
    {
        // The web welcome flow never starts unless the key is there: an object of nulls, never a missing
        // key, never a JSON null.
        body.TryGetProperty("profile", out JsonElement profile).Should().BeTrue("the key is always present");
        profile.ValueKind.Should().Be(JsonValueKind.Object);
        Names(profile).Should().Equal(ProfileMembers);
        foreach (string member in ProfileMembers)
        {
            profile.GetProperty(member).ValueKind.Should().Be(JsonValueKind.Null, $"nothing is known about this person, so {member} is null");
        }

        body.TryGetProperty("starterTemplateKey", out JsonElement key).Should().BeTrue();
        key.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Get_answers_a_profile_of_nulls_for_a_person_with_no_profile_row()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            // Somebody else has answered; this person has no row at all.
            db.UserOnboardingProfiles.Add(new UserOnboardingProfile { UserId = Guid.NewGuid(), Persona = "agency", Material = "code", AnsweredAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        (HttpStatusCode status, JsonElement body) = await Send(HttpMethod.Get, "/api/v1/onboarding", null);

        status.Should().Be(HttpStatusCode.OK);
        Names(body).Should().Equal("tour", "checklist", "profile", "starterTemplateKey");
        ShouldBeTheEmptyProfile(body);
        await using InferenceApiDbContext after = NewDb();
        after.UserOnboardingProfiles.Should().ContainSingle("reading the profile never creates a row");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tour\":{\"status\":\"skipped\",\"version\":1}}")]
    [InlineData("{\"checklistDismissed\":true,\"marks\":[\"open_result\"]}")]
    public async Task The_checklist_write_answers_the_profile_of_nulls_too(string json)
    {
        (HttpStatusCode status, JsonElement body) = await Send(HttpMethod.Put, "/api/v1/onboarding", json);

        status.Should().Be(HttpStatusCode.OK);
        Names(body).Should().Equal("tour", "checklist", "profile", "starterTemplateKey");
        ShouldBeTheEmptyProfile(body);
        _log.Rows.Should().BeEmpty("the checklist write is not a profile answer");
    }

    [Fact]
    public async Task The_checklist_write_answers_the_stored_profile_and_never_changes_it()
    {
        (_, JsonElement saved) = await Send(HttpMethod.Put, "/api/v1/onboarding/profile", "{\"persona\":\"business\",\"material\":\"code\"}");

        (HttpStatusCode status, JsonElement body) = await Send(
            HttpMethod.Put, "/api/v1/onboarding", "{\"tour\":{\"status\":\"completed\",\"version\":2}}");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("tour").GetProperty("status").GetString().Should().Be("completed");
        body.GetProperty("profile").GetRawText().Should().Be(saved.GetProperty("profile").GetRawText());
        body.GetProperty("profile").GetProperty("persona").GetString().Should().Be("business");
        body.GetProperty("starterTemplateKey").GetString().Should().Be("quick-win-promo");
        (await Send(HttpMethod.Get, "/api/v1/onboarding", null)).Body.GetProperty("profile").GetRawText()
            .Should().Be(saved.GetProperty("profile").GetRawText());
    }

    [Theory]
    [InlineData("{\"persona\":\"influencer\",\"material\":\"footage\"}", "invalid_persona")]
    [InlineData("{\"persona\":\"creator\",\"material\":\"photos\"}", "invalid_material")]
    [InlineData("{\"persona\":\"creator\"}", "invalid_material")]
    [InlineData("{}", "invalid_persona")]
    [InlineData("{\"skipped\":true,\"persona\":\"creator\",\"material\":\"footage\"}", "invalid_profile")]
    public async Task A_body_outside_the_closed_sets_is_400_with_the_house_error_and_writes_nothing(string json, string error)
    {
        (HttpStatusCode status, JsonElement body) = await Send(HttpMethod.Put, "/api/v1/onboarding/profile", json);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().Should().Be(error);
        await using InferenceApiDbContext db = NewDb();
        db.UserOnboardingProfiles.Should().BeEmpty();
        _log.Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"skipped\":\"yes\"}")]
    [InlineData("{\"persona\":7,\"material\":\"footage\"}")]
    [InlineData("null")]
    public async Task A_body_that_is_not_the_request_is_400_before_the_action_runs(string json)
    {
        (HttpStatusCode status, _) = await Send(HttpMethod.Put, "/api/v1/onboarding/profile", json);

        status.Should().Be(HttpStatusCode.BadRequest);
        await using InferenceApiDbContext db = NewDb();
        db.UserOnboardingProfiles.Should().BeEmpty();
        _log.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task The_profile_routes_need_a_signed_in_user()
    {
        (await Send(HttpMethod.Put, "/api/v1/onboarding/profile", "{\"skipped\":true}", signedIn: false)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Get, "/api/v1/onboarding", null, signedIn: false)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, "/api/v1/onboarding/profile", "{\"skipped\":true}")).Status.Should().Be(HttpStatusCode.MethodNotAllowed);
        _log.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task The_visit_beacon_takes_the_new_fields_anonymously_and_stores_the_first_touch()
    {
        const string body = "{\"event\":\"site.page_view\",\"visitorId\":\"" + Vid + "\",\"detail\":\"for-creators\","
            + "\"source\":\"youtube\",\"medium\":\"video\",\"campaign\":\"launch-2026-11\",\"content\":\"yt-demo\",\"term\":\"vlog\","
            + "\"landingPath\":\"/for/creators?utm_source=youtube\",\"referrerHost\":\"www.youtube.com\",\"somethingNew\":1}";

        (HttpStatusCode status, JsonElement answer) = await Send(HttpMethod.Post, "/api/v1/telemetry/visit", body, signedIn: false);

        status.Should().Be(HttpStatusCode.NoContent);
        answer.ValueKind.Should().Be(JsonValueKind.Undefined);
        ProductEvent row = _log.Rows.Single();
        (row.Event, row.Detail, row.Source, row.UserId, row.OrganizationId, row.VisitorId)
            .Should().Be(("site.page_view", "for-creators", "client", null, null, Guid.Parse(Vid)));
        VisitorAttribution touch = _log.FirstTouches.Values.Single();
        (touch.SourceLabel, touch.UtmSource, touch.UtmMedium, touch.UtmCampaign, touch.UtmContent, touch.UtmTerm, touch.LandingPath, touch.ReferrerHost)
            .Should().Be(("youtube", "youtube", "video", "launch-2026-11", "yt-demo", "vlog", "/for/creators", "www.youtube.com"));
    }

    [Theory]
    [InlineData("{\"event\":\"site.page_view\",\"visitorId\":\"" + Vid + "\"}", "invalid_detail")]
    [InlineData("{\"event\":\"site.cta_click\",\"visitorId\":\"" + Vid + "\",\"detail\":\"Hero Button\"}", "invalid_detail")]
    [InlineData("{\"event\":\"site.scroll\",\"visitorId\":\"" + Vid + "\",\"detail\":\"home\"}", "unknown_event")]
    [InlineData("{\"event\":\"signup.submitted\",\"visitorId\":\"not-a-uuid\"}", "invalid_visitor_id")]
    [InlineData("{\"event\":", "invalid_body")]
    public async Task A_refused_beacon_is_400_with_its_reason_and_writes_nothing(string json, string error)
    {
        (HttpStatusCode status, JsonElement body) = await Send(HttpMethod.Post, "/api/v1/telemetry/visit", json, signedIn: false);

        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().Should().Be(error);
        _log.Rows.Should().BeEmpty();
        _log.FirstTouches.Should().BeEmpty();
    }
}
