#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// saas-promotions T-004: the consent endpoints. The anonymous ones run through a real Kestrel
/// pipeline (model binding, content negotiation, authorization middleware) because what can break
/// them — a <c>415</c> on an RFC 8058 form post, an antiforgery or auth requirement — lives in the
/// pipeline, not in the action.
/// </summary>
public sealed class MarketingConsentControllerTests : IDisposable
{
    private const string V1 = "2026-10-marketing-v1";
    private readonly MarketingHarness _h = new();

    public void Dispose() => _h.Dispose();

    private sealed class FakeUser(Guid id, string email) : ICurrentUser
    {
        public Guid UserId => id;
        public string Email => email;
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
        public bool IsPlatformAdmin => false;
    }

    private MarketingConsentController Controller(InferenceApiDbContext db, ICurrentUser? user = null, string? realIp = null)
    {
        MarketingConsentController controller = new(_h.NewService(db), _h.Wording, user ?? new FakeUser(Guid.NewGuid(), "x@example.com"));
        DefaultHttpContext http = new();
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        if (realIp is not null) http.Request.Headers["X-Real-IP"] = realIp;
        http.Request.Headers.UserAgent = "UnitAgent";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // --- Direct action tests ------------------------------------------------------------------

    [Fact]
    public void Wording_returns_the_current_registry_entry()
    {
        using InferenceApiDbContext db = _h.NewDb();
        ObjectResult result = Controller(db).GetWording().Result.Should().BeOfType<OkObjectResult>().Subject;
        Json(result.Value).Should().Be(Json(new { version = V1, text = _h.Wording.CurrentText }));
    }

    [Fact]
    public async Task Put_consent_records_the_client_ip_from_X_Real_IP_and_returns_the_get_shape()
    {
        Guid user = _h.AddUser("me@example.com", _h.Now); // N-08: only an account verified in the database reaches consent
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingConsentController controller = Controller(db, new FakeUser(user, "me@example.com"), realIp: "192.0.2.44");

        IActionResult result = await controller.PutConsent(new MarketingConsentRequest(true, "settings", V1), CancellationToken.None);

        MarketingConsentView view = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<MarketingConsentView>().Subject;
        view.Status.Should().Be("optedIn");
        await using InferenceApiDbContext check = _h.NewDb();
        MarketingConsent row = check.MarketingConsents.Single();
        row.Ip.Should().Be("192.0.2.44");
        row.UserAgent.Should().Be("UnitAgent");

        IActionResult get = await Controller(_h.NewDb(), new FakeUser(user, "me@example.com")).GetConsent(CancellationToken.None);
        Json(get.Should().BeOfType<OkObjectResult>().Subject.Value).Should().Be(Json(view));
    }

    [Fact]
    public async Task Put_consent_without_X_Real_IP_uses_the_connection_address()
    {
        Guid user = _h.AddUser("me2@example.com", _h.Now); // N-08: only an account verified in the database reaches consent
        await using InferenceApiDbContext db = _h.NewDb();

        await Controller(db, new FakeUser(user, "me2@example.com")).PutConsent(new MarketingConsentRequest(true, "checkout", V1), CancellationToken.None);

        await using InferenceApiDbContext check = _h.NewDb();
        check.MarketingConsents.Single().Ip.Should().Be("10.0.0.1");
    }

    [Fact]
    public async Task Put_consent_with_a_stale_version_is_409_with_the_current_version()
    {
        Guid user = _h.AddUser("st@example.com", _h.Now); // N-08: only an account verified in the database reaches consent
        await using InferenceApiDbContext db = _h.NewDb();

        IActionResult result = await Controller(db, new FakeUser(user, "st@example.com"))
            .PutConsent(new MarketingConsentRequest(true, "acceptTerms", "2025-old"), CancellationToken.None);

        ObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        Json(conflict.Value).Should().Contain("\"currentWordingVersion\":\"" + V1 + "\"");
    }

    [Theory]
    [InlineData("siteFooter")]
    [InlineData("signup")]
    [InlineData("bogus")]
    [InlineData(null)]
    public async Task Put_consent_accepts_only_the_signed_in_sources(string? source)
    {
        Guid user = _h.AddUser("src@example.com", _h.Now); // N-08: only an account verified in the database reaches consent
        await using InferenceApiDbContext db = _h.NewDb();

        IActionResult result = await Controller(db, new FakeUser(user, "src@example.com"))
            .PutConsent(new MarketingConsentRequest(true, source, V1), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Subscribe_answers_identically_for_new_pending_opted_in_and_withdrawn_emails()
    {
        Guid optedUser = _h.AddUser("opted@example.com", _h.Now);
        Guid withdrawnUser = _h.AddUser("withdrawn@example.com", _h.Now);
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            MarketingConsentService s = _h.NewService(db);
            await s.SubscribeAsync("pending@example.com", V1, MarketingConsentSource.SiteFooter, new(null, null), CancellationToken.None);
            await s.SetAsync(optedUser, "opted@example.com", true, MarketingConsentSource.Settings, V1, new(null, null), CancellationToken.None);
            await s.SetAsync(withdrawnUser, "withdrawn@example.com", true, MarketingConsentSource.Settings, V1, new(null, null), CancellationToken.None);
            await s.SetAsync(withdrawnUser, "withdrawn@example.com", false, MarketingConsentSource.Settings, null, new(null, null), CancellationToken.None);
        }

        List<string> answers = [];
        foreach (string email in new[] { "fresh@example.com", "pending@example.com", "opted@example.com", "withdrawn@example.com" })
        {
            await using InferenceApiDbContext db = _h.NewDb();
            IActionResult result = await Controller(db).Subscribe(
                new MarketingSubscribeRequest(email, V1, "siteContact"), CancellationToken.None);
            ObjectResult obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
            obj.StatusCode.Should().Be(StatusCodes.Status202Accepted);
            answers.Add(obj.StatusCode + " " + Json(obj.Value));
        }

        answers.Distinct().Should().ContainSingle().Which.Should().Be("202 {\"status\":\"checkEmail\"}");
    }

    [Fact]
    public async Task Confirm_maps_invalid_to_400_and_expired_to_410()
    {
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingConsentController controller = Controller(db);

        IActionResult invalid = await controller.Confirm(new MarketingTokenRequest("nope"), CancellationToken.None);
        Json(invalid.Should().BeOfType<BadRequestObjectResult>().Subject.Value).Should().Be("{\"error\":\"invalid_token\"}");

        string expired = _h.Tokens.CreateConfirmToken(Guid.NewGuid(), _h.Clock.GetUtcNow().AddDays(-1));
        IActionResult gone = await controller.Confirm(new MarketingTokenRequest(expired), CancellationToken.None);
        ObjectResult goneObj = gone.Should().BeAssignableTo<ObjectResult>().Subject;
        goneObj.StatusCode.Should().Be(StatusCodes.Status410Gone);
        Json(goneObj.Value).Should().Be("{\"error\":\"token_expired\"}");
    }

    [Fact]
    public async Task Confirm_answers_opted_in()
    {
        await using (InferenceApiDbContext db = _h.NewDb())
            await _h.NewService(db).SubscribeAsync("ok@example.com", V1, MarketingConsentSource.SiteFooter, new(null, null), CancellationToken.None);
        Guid id;
        await using (InferenceApiDbContext db = _h.NewDb())
            id = db.MarketingConsents.Single().Id;

        await using InferenceApiDbContext db2 = _h.NewDb();
        IActionResult result = await Controller(db2, realIp: "198.51.100.1")
            .Confirm(new MarketingTokenRequest(_h.Tokens.CreateConfirmToken(id, _h.Clock.GetUtcNow().AddDays(7))), CancellationToken.None);

        Json(result.Should().BeOfType<OkObjectResult>().Subject.Value).Should().Be("{\"status\":\"optedIn\"}");
    }

    // --- The real pipeline --------------------------------------------------------------------

    /// <summary>Only this controller is discovered, so the host needs nothing the rest of the API needs.</summary>
    private sealed class OnlyMarketingController : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(MarketingConsentController);
    }

    private async Task<(WebApplication App, HttpClient Client)> StartHostAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().ConfigureApplicationPartManager(m =>
        {
            m.ApplicationParts.Clear();
            m.ApplicationParts.Add(new AssemblyPart(typeof(MarketingConsentController).Assembly));
            m.FeatureProviders.Clear();
            m.FeatureProviders.Add(new OnlyMarketingController());
        });
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped(_ => _h.NewDb());
        builder.Services.AddScoped(sp => _h.NewService(sp.GetRequiredService<InferenceApiDbContext>()));
        builder.Services.AddSingleton(_h.Wording);
        builder.Services.AddSingleton<ICurrentUser>(new FakeUser(Guid.NewGuid(), "anon@example.com"));

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private Guid SeedOptedIn(string email)
    {
        Guid user = _h.AddUser(email, _h.Now);
        using InferenceApiDbContext db = _h.NewDb();
        _h.NewService(db).SetAsync(user, email, true, MarketingConsentSource.Settings, V1, new(null, null), CancellationToken.None)
            .GetAwaiter().GetResult();
        using InferenceApiDbContext check = _h.NewDb();
        return check.MarketingConsents.Single(c => c.Email == email).Id;
    }

    private MarketingConsentStatus StatusOf(Guid id)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsents.AsNoTracking().Single(c => c.Id == id).Status;
    }

    [Fact]
    public async Task Unsubscribe_accepts_json_urlencoded_one_click_and_multipart_one_click()
    {
        Guid a = SeedOptedIn("json@example.com");
        Guid b = SeedOptedIn("form@example.com");
        Guid c = SeedOptedIn("multi@example.com");
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        HttpResponseMessage json = await client.PostAsJsonAsync("/api/v1/marketing/unsubscribe", new { token = _h.Tokens.CreateUnsubscribeToken(a) });

        HttpResponseMessage form = await client.PostAsync(
            "/api/v1/marketing/unsubscribe?token=" + Uri.EscapeDataString(_h.Tokens.CreateUnsubscribeToken(b)),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]));

        using MultipartFormDataContent multipart = new() { { new StringContent("One-Click"), "List-Unsubscribe" } };
        HttpResponseMessage multi = await client.PostAsync(
            "/api/v1/marketing/unsubscribe?token=" + Uri.EscapeDataString(_h.Tokens.CreateUnsubscribeToken(c)), multipart);

        foreach (HttpResponseMessage response in new[] { json, form, multi })
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await response.Content.ReadAsStringAsync()).Should().Be("{\"status\":\"unsubscribed\"}");
            response.Headers.Contains("Set-Cookie").Should().BeFalse();
        }

        StatusOf(a).Should().Be(MarketingConsentStatus.Withdrawn);
        StatusOf(b).Should().Be(MarketingConsentStatus.Withdrawn);
        StatusOf(c).Should().Be(MarketingConsentStatus.Withdrawn);
        await app.StopAsync();
    }

    [Fact]
    public async Task Unsubscribe_answers_200_for_a_deleted_row_and_400_for_an_invalid_token()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        HttpResponseMessage deleted = await client.PostAsync(
            "/api/v1/marketing/unsubscribe?token=" + Uri.EscapeDataString(_h.Tokens.CreateUnsubscribeToken(Guid.NewGuid())),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]));
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage invalid = await client.PostAsJsonAsync("/api/v1/marketing/unsubscribe", new { token = "u1.k1.bad.bad" });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).Should().Be("{\"error\":\"invalid_token\"}");

        HttpResponseMessage none = await client.PostAsync("/api/v1/marketing/unsubscribe",
            new StringContent("", Encoding.UTF8, "text/plain"));
        none.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        HttpResponseMessage get = await client.GetAsync(
            "/api/v1/marketing/unsubscribe?token=" + Uri.EscapeDataString(_h.Tokens.CreateUnsubscribeToken(Guid.NewGuid())));
        get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "GET never unsubscribes");
        await app.StopAsync();
    }

    [Fact]
    public async Task The_anonymous_endpoints_need_no_session_and_the_signed_in_ones_do()
    {
        (WebApplication app, HttpClient client) = await StartHostAsync();
        await using WebApplication _ = app;
        using HttpClient __ = client;

        (await client.GetAsync("/api/v1/marketing/wording")).StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage subscribe = await client.PostAsJsonAsync("/api/v1/marketing/subscribe",
            new { email = "pipe@example.com", wordingVersion = V1, source = "siteFooter" });
        subscribe.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await subscribe.Content.ReadAsStringAsync()).Should().Be("{\"status\":\"checkEmail\"}");

        HttpResponseMessage confirm = await client.PostAsJsonAsync("/api/v1/marketing/confirm", new { token = "x" });
        confirm.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // No authentication scheme is registered here, so an [Authorize] action is never reached
        // anonymously (the authorization middleware refuses it before the action runs).
        HttpResponseMessage consent = await client.GetAsync("/api/v1/marketing/consent");
        consent.StatusCode.Should().NotBe(HttpStatusCode.OK);
        await app.StopAsync();
    }
}
