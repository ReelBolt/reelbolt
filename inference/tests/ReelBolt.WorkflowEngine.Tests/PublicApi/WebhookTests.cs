using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Controllers.PublicApi;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.Webhooks;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.ApiKeys;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>
/// Webhooks (T-009): Standard Webhooks signing against the spec vector, endpoint rules (secret handling, URL checks,
/// restricted keys, tenant isolation), the run-event consumer, the run-request hook, and the delivery worker (retries,
/// redirects, disable after 72 h with the admin notification, purge). In-memory provider.
/// </summary>
public sealed class WebhookTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IWebhookHttpClientFactory
    {
        public HttpClient Create() => new(handler, disposeHandler: false);
    }

    private sealed class PublicResolver : IWebhookHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
            Task.FromResult(host.StartsWith("internal", StringComparison.Ordinal)
                ? new[] { IPAddress.Parse("10.0.0.5") }
                : new[] { IPAddress.Parse("93.184.216.34") });
    }

    private const string Url = "https://hooks.example.com/reelbolt";

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Clock _clock = new();
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _otherOrg = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _member = Guid.NewGuid();
    private readonly Guid _service = Guid.NewGuid();
    private readonly Guid _key = Guid.NewGuid();
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly PublicApiOptions _options = new() { Enabled = true };
    private readonly WebhookSecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly Mock<IPublishEndpoint> _publish = new();
    private readonly List<UserActivityNotificationRequested> _notifications = [];

    public WebhookTests()
    {
        _publish.Setup(p => p.Publish(It.IsAny<UserActivityNotificationRequested>(), It.IsAny<CancellationToken>()))
            .Callback<UserActivityNotificationRequested, CancellationToken>((m, _) => _notifications.Add(m))
            .Returns(Task.CompletedTask);

        using InferenceApiDbContext db = NewDb();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Organizations.Add(new Organization { Id = _otherOrg, Name = "x", Slug = "x", Kind = OrganizationKind.Team });
        foreach ((Guid id, bool svc) in new[] { (_owner, false), (_admin, false), (_member, false), (_service, true) })
            db.ApplicationUsers.Add(new ApplicationUser { Id = id, Email = $"{id:N}@example.com", DisplayName = "u", EmailVerifiedAt = DateTime.UtcNow, IsServiceAccount = svc });
        db.OrganizationMemberships.AddRange(
            new OrganizationMembership { OrganizationId = _org, UserId = _owner, Role = OrganizationRole.Owner },
            new OrganizationMembership { OrganizationId = _org, UserId = _admin, Role = OrganizationRole.Admin },
            new OrganizationMembership { OrganizationId = _org, UserId = _member, Role = OrganizationRole.Member },
            new OrganizationMembership { OrganizationId = _org, UserId = _service, Role = OrganizationRole.Admin });
        foreach (Guid p in new[] { _projectA, _projectB })
            db.Projects.Add(new Project { Id = p, Name = "p", OwnerId = _owner, OrganizationId = _org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    private DbContextOptions<InferenceApiDbContext> DbOptions() => new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(_dbName)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    private InferenceApiDbContext NewDb() => new(DbOptions());

    private WebhookEndpointService NewService(InferenceApiDbContext db) =>
        new(db, _protector, new WebhookUrlValidator(new PublicResolver()), Options.Create(_options), _clock);

    private WebhookActor Actor(IReadOnlyList<Guid>? restricted = null, Guid? org = null) => new(org ?? _org, _owner, _key, restricted);

    private WebhooksController NewController(InferenceApiDbContext db, Guid[]? restricted = null, Guid? org = null) => new(
        NewService(db), new ApiKeyTestSupport.FakeUser(org ?? _org, OrganizationRole.Member, _service),
        new ApiKeyTestSupport.FakeKeyContext(_key, restricted))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private async Task<WebhookEndpointView> CreateAsync(string[]? events = null, Guid[]? projects = null, WebhookActor? actor = null, string url = Url)
    {
        using InferenceApiDbContext db = NewDb();
        WebhookResult<WebhookEndpointView> result = await NewService(db).CreateAsync(
            actor ?? Actor(), new WebhookEndpointCreate(url, "d", events, projects), default);
        result.Failure.Should().BeNull(result.Failure?.Message);
        return result.Value!;
    }

    private ServiceProvider Provider(StubHandler? handler = null)
    {
        ServiceCollection s = new();
        s.AddScoped(_ => NewDb());
        s.AddSingleton(_publish.Object);
        s.AddScoped(sp => new WebhookEventWriter(sp.GetRequiredService<InferenceApiDbContext>(), null, _clock));
        s.AddSingleton<IWebhookHttpClientFactory>(new StubFactory(handler ?? new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))));
        return s.BuildServiceProvider();
    }

    private WebhookDeliveryWorker NewWorker(ServiceProvider sp) => new(
        sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(_options), sp.GetRequiredService<IWebhookHttpClientFactory>(),
        _protector, NullLogger<WebhookDeliveryWorker>.Instance, _clock);

    private async Task<WebhookDelivery> SeedDeliveryAsync(
        Guid endpointId, string type = WebhookEventTypes.RunSucceeded, Guid? projectId = null)
    {
        using InferenceApiDbContext db = NewDb();
        WebhookEndpoint endpoint = await db.WebhookEndpoints.SingleAsync(e => e.Id == endpointId);
        WebhookDelivery d = WebhookEventWriter.NewDelivery(endpoint, type, $"{type}:{Guid.NewGuid():N}", "{\"type\":\"x\"}", _clock.Now.UtcDateTime, projectId);
        db.WebhookDeliveries.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private WebhookDelivery Delivery(string id) { using InferenceApiDbContext db = NewDb(); return db.WebhookDeliveries.Single(d => d.Id == id); }

    // ------------------------------------------------------------------ signing

    [Fact]
    public void Signer_matches_the_standard_webhooks_test_vector()
    {
        string signature = StandardWebhookSigner.Sign(
            "whsec_" + "MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", "msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, "{\"test\": 2432232314}");
        signature.Should().Be("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=");
    }

    [Fact]
    public void Generated_secret_is_whsec_plus_32_base64_bytes_and_two_secrets_give_two_signatures()
    {
        string a = StandardWebhookSigner.GenerateSecret();
        string b = StandardWebhookSigner.GenerateSecret();
        a.Should().StartWith("whsec_");
        Convert.FromBase64String(a["whsec_".Length..]).Should().HaveCount(32);
        a.Should().NotBe(b);

        string header = StandardWebhookSigner.SignAll([a, b], "msg_1", 100, "{}");
        header.Split(' ').Should().HaveCount(2).And.OnlyContain(s => s.StartsWith("v1,"));
    }

    // ------------------------------------------------------------------ endpoint service

    [Fact]
    public async Task Secret_is_encrypted_at_rest_and_returned_only_at_create_and_rotate()
    {
        WebhookEndpointView created = await CreateAsync();
        created.Secret.Should().StartWith("whsec_");

        using (InferenceApiDbContext db = NewDb())
        {
            WebhookEndpoint row = db.WebhookEndpoints.Single();
            row.SecretEncrypted.Should().NotContain(created.Secret!).And.NotBeEmpty();
            _protector.TryUnprotect(row.SecretEncrypted, out string plain).Should().BeTrue();
            plain.Should().Be(created.Secret);

            WebhooksController controller = NewController(db);
            JsonSerializer.Serialize((await controller.List(default) as OkObjectResult)!.Value, WebhookEventWriter.Json)
                .Should().NotContain("whsec_").And.NotContain("\"secret\"");
            JsonSerializer.Serialize((await controller.Get(created.Id, default) as OkObjectResult)!.Value, WebhookEventWriter.Json)
                .Should().NotContain("whsec_");

            ObjectResult rotated = (ObjectResult)await controller.RotateSecret(created.Id, false, default);
            ((WebhookSecretView)rotated.Value!).Secret.Should().StartWith("whsec_").And.NotBe(created.Secret);
        }

        using InferenceApiDbContext read = NewDb();
        WebhookEndpoint after = read.WebhookEndpoints.Single();
        after.SecretPreviousEncrypted.Should().NotBeNull();
        after.SecretRotatedAt.Should().Be(_clock.Now.UtcDateTime);
    }

    [Theory]
    [InlineData("http://hooks.example.com/x")]
    [InlineData("https://user:pw@hooks.example.com/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://localhost/x")]
    [InlineData("https://internal.example.com/x")]
    [InlineData("not a url")]
    public async Task Create_refuses_urls_that_are_not_public_https(string url)
    {
        using InferenceApiDbContext db = NewDb();
        WebhookResult<WebhookEndpointView> result = await NewService(db).CreateAsync(Actor(), new WebhookEndpointCreate(url, null, null, null), default);
        result.Failure.Should().NotBeNull();
        result.Failure!.Status.Should().Be(400);
        result.Failure.Code.Should().Be("webhook_url_not_public");
        db.WebhookEndpoints.Count().Should().Be(0);
    }

    [Fact]
    public async Task Create_validates_events_description_limit_and_defaults()
    {
        WebhookEndpointView first = await CreateAsync();
        first.Events.Should().Equal("run.succeeded", "run.failed", "run.cancelled");

        using InferenceApiDbContext db = NewDb();
        WebhookEndpointService service = NewService(db);
        (await service.CreateAsync(Actor(), new WebhookEndpointCreate(Url, null, ["run.exploded"], null), default)).Failure!.Code.Should().Be("invalid_request");
        (await service.CreateAsync(Actor(), new WebhookEndpointCreate(Url, new string('x', 201), null, null), default)).Failure!.Status.Should().Be(400);
        (await service.CreateAsync(Actor(), new WebhookEndpointCreate(Url, null, null, [Guid.NewGuid()]), default)).Failure!.Status.Should().Be(404);

        _options.Webhooks.MaxEndpointsPerOrg = 1;
        WebhookFailure full = (await service.CreateAsync(Actor(), new WebhookEndpointCreate(Url, null, null, null), default)).Failure!;
        full.Status.Should().Be(409);
        full.Code.Should().Be("webhook_limit_reached");
    }

    [Fact]
    public async Task Patch_changes_fields_and_reenabling_clears_the_failing_window()
    {
        WebhookEndpointView created = await CreateAsync();
        using (InferenceApiDbContext db = NewDb())
        {
            WebhookEndpoint row = db.WebhookEndpoints.Single();
            row.Enabled = false;
            row.DisabledReason = "x";
            row.FailingSince = _clock.Now.UtcDateTime.AddHours(-80);
            db.SaveChanges();
        }

        using InferenceApiDbContext write = NewDb();
        WebhookResult<WebhookEndpointView> patched = await NewService(write).UpdateAsync(Actor(), created.Id,
            new WebhookEndpointPatch("https://other.example.com/hook", "new", ["run.step_completed"], [_projectA], true), default);
        patched.Failure.Should().BeNull();
        patched.Value!.Enabled.Should().BeTrue();
        patched.Value.FailingSince.Should().BeNull();
        patched.Value.DisabledReason.Should().BeNull();
        patched.Value.Url.Should().Be("https://other.example.com/hook");
        patched.Value.Events.Should().Equal("run.step_completed");
        patched.Value.ProjectIds.Should().Equal(_projectA);

        WebhookFailure bad = (await NewService(write).UpdateAsync(Actor(), created.Id, new WebhookEndpointPatch("https://internal.example.com/", null, null, null, null), default)).Failure!;
        bad.Code.Should().Be("webhook_url_not_public");
    }

    [Fact]
    public async Task Another_organizations_endpoint_is_404_on_every_route()
    {
        WebhookEndpointView created = await CreateAsync();
        WebhookDelivery delivery = await SeedDeliveryAsync(created.Id);
        WebhookActor foreign = Actor(org: _otherOrg);

        using InferenceApiDbContext db = NewDb();
        WebhookEndpointService service = NewService(db);
        (await service.ListAsync(foreign, default)).Should().BeEmpty();
        (await service.GetAsync(foreign, created.Id, default)).Failure!.Status.Should().Be(404);
        (await service.UpdateAsync(foreign, created.Id, new WebhookEndpointPatch(null, "x", null, null, null), default)).Failure!.Status.Should().Be(404);
        (await service.RotateSecretAsync(foreign, created.Id, false, default)).Failure!.Status.Should().Be(404);
        (await service.TestAsync(foreign, created.Id, default)).Failure!.Status.Should().Be(404);
        (await service.ListDeliveriesAsync(foreign, created.Id, null, null, default)).Failure!.Status.Should().Be(404);
        (await service.RedeliverAsync(foreign, created.Id, delivery.Id, default)).Failure!.Status.Should().Be(404);
        (await service.DeleteAsync(foreign, created.Id, default)).Failure!.Status.Should().Be(404);
        db.WebhookEndpoints.Count().Should().Be(1);
    }

    [Fact]
    public async Task Restricted_key_only_creates_and_sees_endpoints_inside_its_projects()
    {
        Guid[] mine = [_projectA];
        WebhookEndpointView all = await CreateAsync();
        WebhookEndpointView both = await CreateAsync(projects: [_projectA, _projectB]);
        WebhookEndpointView inside = await CreateAsync(projects: [_projectA], actor: Actor(mine));

        using InferenceApiDbContext db = NewDb();
        WebhooksController controller = NewController(db, mine);

        // cannot create an all-projects endpoint, nor one reaching outside
        ObjectResult noProjects = (ObjectResult)await controller.Create(new WebhookEndpointCreate(Url, null, null, null), default);
        noProjects.StatusCode.Should().Be(403);
        ObjectResult outside = (ObjectResult)await controller.Create(new WebhookEndpointCreate(Url, null, null, [_projectB]), default);
        outside.StatusCode.Should().Be(404);

        // sees only the one inside its list
        PublicApiPage<WebhookEndpointView> page = (PublicApiPage<WebhookEndpointView>)((OkObjectResult)await controller.List(default)).Value!;
        page.Data.Select(e => e.Id).Should().Equal(inside.Id);
        ((ObjectResult)await controller.Get(all.Id, default)).StatusCode.Should().Be(404);
        ((ObjectResult)await controller.Get(both.Id, default)).StatusCode.Should().Be(404);
        ((ObjectResult)await controller.Update(both.Id, new WebhookEndpointPatch(null, "x", null, null, null), default)).StatusCode.Should().Be(404);
        ((ObjectResult)await controller.RotateSecret(all.Id, false, default)).StatusCode.Should().Be(404);
        ((ObjectResult)await controller.Delete(both.Id, default)).StatusCode.Should().Be(404);
        ((ObjectResult)await controller.Update(inside.Id, new WebhookEndpointPatch(null, null, null, [_projectB], null), default)).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task A_key_acts_only_on_the_endpoints_it_created_and_never_reads_a_url_query()
    {
        Guid otherKey = Guid.NewGuid();
        // created by a dashboard Admin, with a token in the query and a fragment
        WebhookEndpointView admin = await CreateAsync(
            actor: new WebhookActor(_org, _owner, null, null), url: "https://hooks.example.com/hook?token=admin-secret#frag");
        WebhookEndpointView own = await CreateAsync(
            actor: new WebhookActor(_org, _owner, _key, null), url: "https://hooks.example.com/own?k=1#frag");
        WebhookEndpointView foreign = await CreateAsync(actor: new WebhookActor(_org, _owner, otherKey, null));

        WebhookActor actor = new(_org, _owner, _key, null);
        using InferenceApiDbContext db = NewDb();
        WebhookEndpointService service = NewService(db);

        // the list still shows every endpoint, but a key-actor view drops the query string and fragment
        List<WebhookEndpointView> listed = (await service.ListAsync(actor, default)).ToList();
        listed.Select(e => e.Id).Should().BeEquivalentTo([admin.Id, own.Id, foreign.Id]);
        listed.Single(e => e.Id == admin.Id).Url.Should().Be("https://hooks.example.com/hook");
        listed.Single(e => e.Id == own.Id).Url.Should().Be("https://hooks.example.com/own");
        (await service.GetAsync(actor, admin.Id, default)).Value!.Url.Should().Be("https://hooks.example.com/hook");

        // every action on an endpoint the key did not create answers 404, though list shows it
        (await service.UpdateAsync(actor, admin.Id, new WebhookEndpointPatch(null, "x", null, null, null), default)).Failure!.Status.Should().Be(404);
        (await service.DeleteAsync(actor, admin.Id, default)).Failure!.Status.Should().Be(404);
        (await service.RotateSecretAsync(actor, admin.Id, false, default)).Failure!.Status.Should().Be(404);
        (await service.TestAsync(actor, admin.Id, default)).Failure!.Status.Should().Be(404);
        (await service.ListDeliveriesAsync(actor, admin.Id, null, null, default)).Failure!.Status.Should().Be(404);
        (await service.RedeliverAsync(actor, admin.Id, "msg_unknown", default)).Failure!.Status.Should().Be(404);
        (await service.TestAsync(actor, own.Id, default)).Failure.Should().BeNull("its own endpoint is fine");

        // the dashboard (session) actor is unchanged: it manages anything in the organization
        WebhookActor session = new(_org, _owner, null, null);
        (await service.UpdateAsync(session, admin.Id, new WebhookEndpointPatch(null, "y", null, null, null), default)).Failure.Should().BeNull();
    }

    [Fact]
    public async Task A_restricted_key_sees_only_deliveries_of_its_projects()
    {
        Guid[] mine = [_projectA];
        WebhookActor key = Actor(mine);
        WebhookEndpointView endpoint = await CreateAsync(projects: [_projectA], actor: key);
        WebhookDelivery inA = await SeedDeliveryAsync(endpoint.Id, projectId: _projectA);
        WebhookDelivery inB = await SeedDeliveryAsync(endpoint.Id, projectId: _projectB);
        WebhookDelivery noProject = await SeedDeliveryAsync(endpoint.Id);
        using (InferenceApiDbContext write = NewDb())
        {
            write.WebhookDeliveries.Single(d => d.Id == inA.Id).Status = WebhookDelivery.Abandoned;
            write.SaveChanges();
        }

        using InferenceApiDbContext db = NewDb();
        WebhookEndpointService service = NewService(db);

        WebhookDeliveryPage page = (await service.ListDeliveriesAsync(key, endpoint.Id, 10, null, default)).Value!;
        page.Data.Select(d => d.Id).Should().Equal([inA.Id], "project B and a projectless delivery are hidden");

        WebhookDeliveryPage all = (await service.ListDeliveriesAsync(Actor(), endpoint.Id, 10, null, default)).Value!;
        all.Data.Select(d => d.Id).Should().BeEquivalentTo([inA.Id, inB.Id, noProject.Id]);

        (await service.RedeliverAsync(key, endpoint.Id, inB.Id, default)).Failure!.Status.Should().Be(404);
        (await service.RedeliverAsync(key, endpoint.Id, noProject.Id, default)).Failure!.Status.Should().Be(404);
        (await service.RedeliverAsync(key, endpoint.Id, inA.Id, default)).Value.Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://hooks.example.com:443/x")]
    [InlineData("https://hooks.example.com:8443/x")]
    public async Task Ports_443_and_8443_are_allowed(string url)
    {
        WebhookEndpointView created = await CreateAsync(url: url);
        created.Id.Should().NotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("https://hooks.example.com:8080/x")]
    [InlineData("https://hooks.example.com:444/x")]
    public async Task Any_other_port_is_refused(string url)
    {
        using InferenceApiDbContext db = NewDb();
        WebhookFailure failure = (await NewService(db).CreateAsync(Actor(), new WebhookEndpointCreate(url, null, null, null), default)).Failure!;
        failure.Status.Should().Be(400);
        failure.Code.Should().Be("webhook_url_not_public");
        db.WebhookEndpoints.Count().Should().Be(0);
    }

    [Fact]
    public async Task Test_ping_is_rate_limited_while_one_is_pending_and_after_five_a_minute()
    {
        WebhookEndpointView created = await CreateAsync();
        using InferenceApiDbContext db = NewDb();
        WebhookEndpointService service = NewService(db);

        WebhookDeliveryView first = (await service.TestAsync(Actor(), created.Id, default)).Value!;
        WebhookFailure pending = (await service.TestAsync(Actor(), created.Id, default)).Failure!;
        pending.Status.Should().Be(429);
        pending.Code.Should().Be(WebhookEndpointService.TestRateLimitedCode);
        pending.RetryAfterSeconds.Should().BeGreaterThan(0);
        pending.ResetsAt.Should().NotBeNull();

        using (InferenceApiDbContext write = NewDb())
        {
            write.WebhookDeliveries.Single(d => d.Id == first.Id).Status = WebhookDelivery.Delivered;
            write.SaveChanges();
        }

        for (int i = 0; i < WebhookEndpointService.MaxTestsPerMinute - 1; i++)
        {
            WebhookDeliveryView ok = (await service.TestAsync(Actor(), created.Id, default)).Value!;
            using InferenceApiDbContext write = NewDb();
            write.WebhookDeliveries.Single(d => d.Id == ok.Id).Status = WebhookDelivery.Delivered;
            write.SaveChanges();
        }

        WebhookFailure limited = (await service.TestAsync(Actor(), created.Id, default)).Failure!;
        limited.Status.Should().Be(429);
        limited.Code.Should().Be(WebhookEndpointService.TestRateLimitedCode);
        limited.RetryAfterSeconds.Should().BeGreaterThan(0);
        limited.ResetsAt!.Value.Should().BeAfter(_clock.Now.UtcDateTime);

        // the public surface carries Retry-After and error.resetsAt
        WebhooksController controller = NewController(db);
        ObjectResult refused = (ObjectResult)await controller.Test(created.Id, default);
        refused.StatusCode.Should().Be(429);
        controller.Response.Headers.RetryAfter.ToString().Should().NotBeNullOrEmpty();
        PublicApi429Tests.AssertConsistent(controller.Response, refused);
        JsonNode error = ((JsonNode)refused.Value!)["error"]!;
        error["code"]!.GetValue<string>().Should().Be("webhook_test_rate_limited");
        DateTime.Parse(error["resetsAt"]!.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            .Should().BeAfter(_clock.Now.UtcDateTime);
    }

    [Fact]
    public async Task Rotate_with_revokePrevious_drops_the_old_secret_at_once()
    {
        WebhookEndpointView created = await CreateAsync();
        using (InferenceApiDbContext db = NewDb())
            await NewService(db).RotateSecretAsync(Actor(), created.Id, revokePrevious: false, ct: default);
        using (InferenceApiDbContext read = NewDb())
            read.WebhookEndpoints.Single().SecretPreviousEncrypted.Should().NotBeNull("the old one keeps signing for 24 h");

        WebhookSecretView revoked;
        using (InferenceApiDbContext db = NewDb())
            revoked = (WebhookSecretView)((OkObjectResult)await NewController(db)
                .RotateSecret(created.Id, revokePrevious: true, ct: default)).Value!;
        revoked.Secret.Should().StartWith("whsec_");
        using (InferenceApiDbContext read = NewDb())
        {
            WebhookEndpoint row = read.WebhookEndpoints.Single();
            row.SecretPreviousEncrypted.Should().BeNull();
            row.SecretRotatedAt.Should().Be(_clock.Now.UtcDateTime);
            _protector.TryUnprotect(row.SecretEncrypted, out string current).Should().BeTrue();
            current.Should().Be(revoked.Secret);
        }
    }

    [Fact]
    public async Task The_worker_nulls_a_previous_secret_after_PreviousSecretHours()
    {
        WebhookEndpointView created = await CreateAsync();
        using (InferenceApiDbContext db = NewDb())
            await NewService(db).RotateSecretAsync(Actor(), created.Id, false, default);

        using ServiceProvider sp = Provider();
        WebhookDeliveryWorker worker = NewWorker(sp);
        (await worker.SweepExpiredSecretsAsync(default)).Should().Be(0, "24 hours have not passed");

        _clock.Now = _clock.Now.AddHours(25);
        (await worker.SweepExpiredSecretsAsync(default)).Should().Be(1);
        using InferenceApiDbContext read = NewDb();
        read.WebhookEndpoints.Single().SecretPreviousEncrypted.Should().BeNull();
    }

    [Fact]
    public void Every_public_post_that_creates_or_enqueues_is_idempotent_and_every_action_needs_the_scope()
    {
        Type type = typeof(WebhooksController);
        foreach (string name in new[] { nameof(WebhooksController.Create), nameof(WebhooksController.Test), nameof(WebhooksController.Redeliver) })
            type.GetMethod(name)!.GetCustomAttributes<IdempotentAttribute>().Should().NotBeEmpty($"{name} must carry [Idempotent]");
        foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            m.GetCustomAttributes<RequireScopeAttribute>().SelectMany(a => a.AnyOf).Should().Equal(ApiScopes.WebhooksManage);
        // The rotate response carries the secret, so it is deliberately not stored for replay.
        type.GetMethod(nameof(WebhooksController.RotateSecret))!.GetCustomAttributes<IdempotentAttribute>().Should().BeEmpty();
    }

    [Fact]
    public async Task Test_ping_queues_a_webhook_test_delivery_and_deliveries_list_and_redeliver_work()
    {
        WebhookEndpointView created = await CreateAsync();
        using InferenceApiDbContext db = NewDb();
        WebhookEndpointService service = NewService(db);
        WebhookDeliveryView ping = (await service.TestAsync(Actor(), created.Id, default)).Value!;
        ping.EventType.Should().Be("webhook.test");
        ping.Id.Should().StartWith("msg_");
        ping.Status.Should().Be("pending");
        (await service.RedeliverAsync(Actor(), created.Id, ping.Id, default)).Failure!.Code.Should().Be("delivery_pending");

        WebhookDeliveryPage page = (await service.ListDeliveriesAsync(Actor(), created.Id, 10, null, default)).Value!;
        page.Data.Select(d => d.Id).Should().Equal(ping.Id);

        WebhookDelivery row = db.WebhookDeliveries.Single();
        row.Status = WebhookDelivery.Abandoned;
        row.Attempts = 7;
        db.SaveChanges();
        WebhookDeliveryView again = (await service.RedeliverAsync(Actor(), created.Id, ping.Id, default)).Value!;
        again.Id.Should().Be(ping.Id);
        again.Status.Should().Be("pending");
        again.Attempts.Should().Be(0);
    }

    // ------------------------------------------------------------------ consumer

    private async Task<WorkflowExecution> SeedRunAsync(
        ExecutionStatus status, Guid? project = null, WorkflowDefinitionKind kind = WorkflowDefinitionKind.Standard,
        Guid? apiKey = null, string? correlation = null, DateTime? completedAt = null, DateTime? queuedAt = null)
    {
        Guid workflowId = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = workflowId,
            Name = "w",
            ProjectId = project ?? _projectA,
            Kind = kind,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            ProjectId = project ?? _projectA,
            OrganizationId = _org,
            Status = status,
            ApiKeyId = apiKey,
            CorrelationId = correlation ?? Guid.NewGuid().ToString(),
            CompletedAt = completedAt,
            QueuedAt = queuedAt
        };
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private WebhookRunEventsConsumer NewConsumer(InferenceApiDbContext db) =>
        new(db, new WebhookEventWriter(db, null, _clock), Options.Create(_options));

    private static ConsumeContext<T> Context<T>(T message) where T : class
    {
        Mock<ConsumeContext<T>> ctx = new();
        ctx.SetupGet(c => c.Message).Returns(message);
        ctx.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return ctx.Object;
    }

    private List<WebhookDelivery> Deliveries() { using InferenceApiDbContext db = NewDb(); return db.WebhookDeliveries.OrderBy(d => d.CreatedAt).ToList(); }

    [Fact]
    public async Task Completed_run_makes_one_delivery_per_subscribed_endpoint_and_a_redelivered_message_adds_none()
    {
        WebhookEndpointView a = await CreateAsync();
        WebhookEndpointView b = await CreateAsync(events: ["run.succeeded"]);
        await CreateAsync(events: ["run.failed"]);
        WorkflowExecution run = await SeedRunAsync(ExecutionStatus.Passed);
        WorkflowExecutionCompleted message = new()
        {
            ExecutionId = run.Id,
            ProjectId = run.ProjectId,
            WorkflowDefinitionId = run.WorkflowDefinitionId,
            FinalStatus = "Passed",
            CompletedAt = new DateTime(2026, 11, 2, 11, 0, 0, DateTimeKind.Utc)
        };

        using (InferenceApiDbContext db = NewDb()) await NewConsumer(db).Consume(Context(message));
        using (InferenceApiDbContext db = NewDb()) await NewConsumer(db).Consume(Context(message));

        List<WebhookDelivery> rows = Deliveries();
        rows.Select(r => r.EndpointId).Should().BeEquivalentTo([a.Id, b.Id]);
        JsonNode payload = JsonNode.Parse(rows[0].PayloadJson)!;
        payload["type"]!.GetValue<string>().Should().Be("run.succeeded");
        payload["timestamp"]!.GetValue<string>().Should().StartWith("2026-11-02T11:00:00");
        payload["data"]!["runId"]!.GetValue<string>().Should().Be(run.Id.ToString());
        payload["data"]!["projectId"]!.GetValue<string>().Should().Be(_projectA.ToString());
        payload["data"]!["workflowId"]!.GetValue<string>().Should().Be(run.WorkflowDefinitionId.ToString());
        payload["data"]!["status"]!.GetValue<string>().Should().Be("succeeded");
        payload["data"]!["failureCode"].Should().BeNull();
    }

    [Fact]
    public async Task Failed_run_carries_the_failure_code_and_credits_and_a_cancelled_row_maps_to_run_cancelled()
    {
        await CreateAsync(events: ["run.failed", "run.cancelled"]);
        WorkflowExecution failed = await SeedRunAsync(ExecutionStatus.Failed);
        using (InferenceApiDbContext db = NewDb())
        {
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = failed.Id,
                WorkflowStepId = Guid.NewGuid(),
                FailureCode = "PROVIDER_REFUSED",
                CreditsChargedMillis = 2500
            });
            await db.SaveChangesAsync();
        }

        WorkflowExecution cancelled = await SeedRunAsync(ExecutionStatus.Cancelled,
            completedAt: new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc));
        using (InferenceApiDbContext db = NewDb())
        {
            NewConsumer(db);
            await NewConsumer(db).Consume(Context(new WorkflowExecutionFailed { ExecutionId = failed.Id, ErrorMessage = "boom" }));
            // a cancel arrives as a failed message; the row is terminal and completed before the message
            await NewConsumer(db).Consume(Context(new WorkflowExecutionFailed
            {
                ExecutionId = cancelled.Id,
                ErrorMessage = "stopped",
                FailedAt = new DateTime(2026, 11, 2, 11, 0, 0, DateTimeKind.Utc)
            }));
        }

        List<JsonNode> payloads = Deliveries().Select(d => JsonNode.Parse(d.PayloadJson)!).ToList();
        payloads.Should().HaveCount(2);
        JsonNode f = payloads.Single(p => p["type"]!.GetValue<string>() == "run.failed");
        f["data"]!["failureCode"]!.GetValue<string>().Should().Be("PROVIDER_REFUSED");
        f["data"]!["creditsCharged"]!.GetValue<decimal>().Should().Be(2.5m);
        f["data"]!["status"]!.GetValue<string>().Should().Be("failed");
        payloads.Single(p => p["type"]!.GetValue<string>() == "run.cancelled")["data"]!["status"]!.GetValue<string>().Should().Be("cancelled");
    }

    [Fact]
    public async Task Terminal_outcome_comes_from_the_message_not_a_stale_row()
    {
        await CreateAsync(events: ["run.succeeded", "run.failed", "run.cancelled"]);
        DateTime t = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);

        // A failed message for a row that only completed LATER as Passed (a retry's row) is still a failure.
        WorkflowExecution passedLater = await SeedRunAsync(ExecutionStatus.Passed, completedAt: t.AddHours(1));
        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionFailed
            {
                ExecutionId = passedLater.Id,
                ErrorMessage = "boom",
                FailedAt = t
            }));

        // A completed message that carries Cancelled wins over a row that says Passed.
        WorkflowExecution cancelledMessage = await SeedRunAsync(ExecutionStatus.Passed, completedAt: t);
        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionCompleted
            {
                ExecutionId = cancelledMessage.Id,
                FinalStatus = "Cancelled",
                CompletedAt = t.AddMinutes(1)
            }));

        Deliveries().Select(d => d.EventType).OrderBy(x => x, StringComparer.Ordinal).Should().Equal("run.cancelled", "run.failed");
    }

    [Fact]
    public async Task A_row_requeued_after_the_message_never_supplies_the_status()
    {
        await CreateAsync(events: ["run.succeeded", "run.failed", "run.cancelled"]);
        DateTime t = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);

        // An earlier attempt cancelled the row, but it was requeued after this failed message: the message kind wins.
        WorkflowExecution requeued = await SeedRunAsync(ExecutionStatus.Cancelled,
            completedAt: t.AddHours(-1), queuedAt: t.AddHours(1));
        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionFailed
            {
                ExecutionId = requeued.Id,
                ErrorMessage = "boom",
                FailedAt = t
            }));

        // A row that is not terminal never supplies the status either.
        WorkflowExecution running = await SeedRunAsync(ExecutionStatus.Running);
        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionCompleted
            {
                ExecutionId = running.Id,
                FinalStatus = string.Empty,
                CompletedAt = t
            }));

        Deliveries().Select(d => d.EventType).OrderBy(x => x, StringComparer.Ordinal).Should().Equal("run.failed", "run.succeeded");
    }

    [Fact]
    public async Task A_retried_run_outcome_is_a_new_event()
    {
        await CreateAsync(events: ["run.succeeded", "run.failed"]);
        DateTime t = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        WorkflowExecution run = await SeedRunAsync(ExecutionStatus.Failed, completedAt: t);

        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionFailed
            {
                ExecutionId = run.Id,
                ErrorMessage = "boom",
                FailedAt = t
            }));

        // the retry reuses the row and completes as Passed later
        using (InferenceApiDbContext db = NewDb())
        {
            WorkflowExecution row = db.WorkflowExecutions.Single(e => e.Id == run.Id);
            row.Status = ExecutionStatus.Passed;
            row.QueuedAt = t.AddMinutes(1);
            row.CompletedAt = t.AddMinutes(5);
            await db.SaveChangesAsync();
        }

        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionCompleted
            {
                ExecutionId = run.Id,
                FinalStatus = "Passed",
                CompletedAt = t.AddMinutes(5)
            }));

        Deliveries().Select(d => d.EventType).OrderBy(x => x, StringComparer.Ordinal).Should().Equal("run.failed", "run.succeeded");
    }

    [Fact]
    public async Task A_disabled_endpoint_gets_no_new_delivery()
    {
        WebhookEndpointView endpoint = await CreateAsync();
        using (InferenceApiDbContext db = NewDb())
            await NewService(db).UpdateAsync(Actor(), endpoint.Id, new WebhookEndpointPatch(null, null, null, null, false), default);

        WorkflowExecution run = await SeedRunAsync(ExecutionStatus.Passed);
        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionCompleted { ExecutionId = run.Id, FinalStatus = "Passed" }));

        Deliveries().Should().BeEmpty();
    }

    [Fact]
    public async Task Project_filter_flag_off_and_hidden_runs_produce_nothing()
    {
        await CreateAsync(projects: [_projectB]);
        WorkflowExecution inA = await SeedRunAsync(ExecutionStatus.Passed, _projectA);
        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionCompleted { ExecutionId = inA.Id, ProjectId = _projectA }));
        Deliveries().Should().BeEmpty("the endpoint only covers project B");

        await CreateAsync();
        WorkflowExecution hidden = await SeedRunAsync(ExecutionStatus.Passed, kind: WorkflowDefinitionKind.CompositionRevision);
        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionCompleted { ExecutionId = hidden.Id, ProjectId = hidden.ProjectId }));
        Deliveries().Should().BeEmpty("hidden composition-revision runs do not produce events");

        WorkflowExecution apiHidden = await SeedRunAsync(ExecutionStatus.Passed, kind: WorkflowDefinitionKind.CompositionRevision, apiKey: _key);
        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionCompleted { ExecutionId = apiHidden.Id, ProjectId = apiHidden.ProjectId }));
        Deliveries().Should().HaveCount(1, "a run the public API started always produces events");

        _options.Enabled = false;
        WorkflowExecution off = await SeedRunAsync(ExecutionStatus.Passed);
        using (InferenceApiDbContext db = NewDb())
            await NewConsumer(db).Consume(Context(new WorkflowExecutionCompleted { ExecutionId = off.Id, ProjectId = off.ProjectId, CompletedAt = DateTime.UtcNow.AddMinutes(1) }));
        Deliveries().Should().HaveCount(1, "flag off: the consumer returns immediately");
    }

    [Fact]
    public async Task Step_events_go_only_to_endpoints_that_subscribe_and_carry_the_run_request_id()
    {
        WorkflowExecution run = await SeedRunAsync(ExecutionStatus.Running, correlation: ApiRunRequest.CorrelationFor(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        WorkflowStepCompleted step = new()
        {
            ExecutionId = run.Id,
            StepId = Guid.NewGuid(),
            StepResultId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = "VideoCompile",
            StepStatus = "Completed"
        };

        await CreateAsync();
        using (InferenceApiDbContext db = NewDb()) await NewConsumer(db).Consume(Context(step));
        Deliveries().Should().BeEmpty();

        WebhookEndpointView subscriber = await CreateAsync(events: ["run.step_completed"]);
        using (InferenceApiDbContext db = NewDb()) await NewConsumer(db).Consume(Context(step));
        using (InferenceApiDbContext db = NewDb()) await NewConsumer(db).Consume(Context(step));
        WebhookDelivery only = Deliveries().Single();
        only.EndpointId.Should().Be(subscriber.Id);
        JsonNode data = JsonNode.Parse(only.PayloadJson)!["data"]!;
        data["status"]!.GetValue<string>().Should().Be("completed");
        data["stepOrder"]!.GetValue<int>().Should().Be(2);
        data["runRequestId"]!.GetValue<string>().Should().Be("11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public async Task Run_request_hook_writes_started_and_refused_deliveries_once()
    {
        await CreateAsync(events: ["run_request.started", "run_request.refused"]);
        using ServiceProvider sp = Provider();
        ApiRunRequestWebhookEvents hook = new(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(_options), _clock);
        Guid request = Guid.NewGuid();
        Guid execution = Guid.NewGuid();

        await hook.StartedAsync(request, _key, _org, _projectA, execution, default);
        await hook.StartedAsync(request, _key, _org, _projectA, execution, default);
        await hook.RefusedAsync(Guid.NewGuid(), _key, _org, _projectA, "INSUFFICIENT_CREDITS", "no", default);

        List<WebhookDelivery> rows = Deliveries();
        rows.Select(r => r.EventType).Should().BeEquivalentTo("run_request.started", "run_request.refused");
        JsonNode.Parse(rows.Single(r => r.EventType == "run_request.started").PayloadJson)!["data"]!["runId"]!.GetValue<string>().Should().Be(execution.ToString());
        JsonNode.Parse(rows.Single(r => r.EventType == "run_request.refused").PayloadJson)!["data"]!["failureCode"]!.GetValue<string>().Should().Be("INSUFFICIENT_CREDITS");
    }

    // ------------------------------------------------------------------ worker

    [Fact]
    public async Task Worker_posts_a_signed_body_and_marks_a_2xx_delivered()
    {
        WebhookEndpointView endpoint = await CreateAsync();
        WebhookDelivery seeded = await SeedDeliveryAsync(endpoint.Id);
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using ServiceProvider sp = Provider(handler);

        (await NewWorker(sp).TickAsync(default)).Should().Be(1);

        (HttpRequestMessage request, string body) = handler.Calls.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be(Url);
        request.Headers.GetValues("User-Agent").Should().Equal("ReelBolt-Webhooks/1");
        request.Headers.GetValues("webhook-id").Should().Equal(seeded.Id);
        long timestamp = long.Parse(request.Headers.GetValues("webhook-timestamp").Single());
        timestamp.Should().Be(_clock.Now.ToUnixTimeSeconds());
        body.Should().Be(seeded.PayloadJson);
        request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
        request.Headers.GetValues("webhook-signature").Single()
            .Should().Be(StandardWebhookSigner.Sign(endpoint.Secret!, seeded.Id, timestamp, body));

        WebhookDelivery after = Delivery(seeded.Id);
        after.Status.Should().Be("delivered");
        after.Attempts.Should().Be(1);
        after.LastStatusCode.Should().Be(204);
        after.DeliveredAt.Should().Be(_clock.Now.UtcDateTime);
        using InferenceApiDbContext db = NewDb();
        db.WebhookEndpoints.Single().LastSuccessAt.Should().Be(_clock.Now.UtcDateTime);
    }

    [Fact]
    public async Task Both_secrets_sign_for_24_hours_after_a_rotation_and_only_the_new_one_after()
    {
        WebhookEndpointView endpoint = await CreateAsync();
        string oldSecret = endpoint.Secret!;
        string newSecret;
        using (InferenceApiDbContext db = NewDb())
            newSecret = (await NewService(db).RotateSecretAsync(Actor(), endpoint.Id, false, default)).Value!.Secret!;

        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using ServiceProvider sp = Provider(handler);
        WebhookDelivery first = await SeedDeliveryAsync(endpoint.Id);
        await NewWorker(sp).TickAsync(default);
        string[] two = handler.Calls[0].Request.Headers.GetValues("webhook-signature").Single().Split(' ');
        long ts = long.Parse(handler.Calls[0].Request.Headers.GetValues("webhook-timestamp").Single());
        two.Should().Equal(
            StandardWebhookSigner.Sign(newSecret, first.Id, ts, first.PayloadJson),
            StandardWebhookSigner.Sign(oldSecret, first.Id, ts, first.PayloadJson));

        _clock.Now = _clock.Now.AddHours(25);
        await SeedDeliveryAsync(endpoint.Id);
        await NewWorker(sp).TickAsync(default);
        handler.Calls[1].Request.Headers.GetValues("webhook-signature").Single().Split(' ').Should().HaveCount(1);
    }

    [Fact]
    public async Task Failures_retry_on_the_schedule_then_abandon_and_the_endpoint_records_FailingSince()
    {
        WebhookEndpointView endpoint = await CreateAsync();
        WebhookDelivery seeded = await SeedDeliveryAsync(endpoint.Id);
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream\nexploded " + new string('z', 400))
        });
        using ServiceProvider sp = Provider(handler);
        WebhookDeliveryWorker worker = NewWorker(sp);
        DateTime failingSince = _clock.Now.UtcDateTime;

        TimeSpan[] expected = WebhookDeliveryWorker.RetrySchedule;
        expected.Should().Equal(
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12));
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            (await worker.TickAsync(default)).Should().Be(1);
            WebhookDelivery row = Delivery(seeded.Id);
            row.Attempts.Should().Be(attempt);
            row.Status.Should().Be("failed");
            row.NextAttemptAt.Should().Be(_clock.Now.UtcDateTime + expected[attempt - 1]);
            row.LastStatusCode.Should().Be(500);
            row.LastError.Should().StartWith("HTTP 500: upstream exploded").And.NotContain("\n");
            row.LastError!.Length.Should().BeLessThanOrEqualTo("HTTP 500: ".Length + WebhookDeliveryWorker.SnippetLength);

            // not due yet: nothing is sent
            (await worker.TickAsync(default)).Should().Be(0);
            _clock.Now = new DateTimeOffset(row.NextAttemptAt, TimeSpan.Zero);
        }

        (await worker.TickAsync(default)).Should().Be(1);
        WebhookDelivery last = Delivery(seeded.Id);
        last.Status.Should().Be("abandoned");
        last.Attempts.Should().Be(7);
        handler.Calls.Should().HaveCount(7);
        (await worker.TickAsync(default)).Should().Be(0);

        using InferenceApiDbContext db = NewDb();
        WebhookEndpoint after = db.WebhookEndpoints.Single();
        after.FailingSince.Should().Be(failingSince);
        after.Enabled.Should().BeTrue("about 21 hours of failures is under the 72 hour limit");
    }

    [Fact]
    public async Task A_redirect_is_a_failure_and_is_never_followed()
    {
        WebhookEndpointView endpoint = await CreateAsync();
        WebhookDelivery seeded = await SeedDeliveryAsync(endpoint.Id);
        StubHandler handler = new((_, _) =>
        {
            HttpResponseMessage r = new(HttpStatusCode.Found);
            r.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data");
            return r;
        });
        using ServiceProvider sp = Provider(handler);

        await NewWorker(sp).TickAsync(default);

        handler.Calls.Should().ContainSingle();
        WebhookDelivery row = Delivery(seeded.Id);
        row.Status.Should().Be("failed");
        row.LastStatusCode.Should().Be(302);
        GeneratedClipUrlGuard.CreateUserUrlHandler().AllowAutoRedirect.Should().BeFalse("the real handler follows no redirect");
    }

    [Fact]
    public async Task Private_addresses_are_refused_before_and_at_connect_time()
    {
        // Connect-time: the delivery handler refuses a non-public address even when asked directly.
        using HttpClient client = new(GeneratedClipUrlGuard.CreateUserUrlHandler());
        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://127.0.0.1:9/hook"));
        (ex is NonPublicAddressException || ex.InnerException is NonPublicAddressException).Should().BeTrue();

        // Worker: a row whose URL is private (changed straight in the database) fails without any request.
        WebhookEndpointView endpoint = await CreateAsync();
        using (InferenceApiDbContext db = NewDb())
        {
            db.WebhookEndpoints.Single().Url = "https://10.1.2.3/hook";
            db.SaveChanges();
        }

        WebhookDelivery seeded = await SeedDeliveryAsync(endpoint.Id);
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using ServiceProvider sp = Provider(handler);
        await NewWorker(sp).TickAsync(default);
        handler.Calls.Should().BeEmpty();
        Delivery(seeded.Id).LastError.Should().Contain("not allowed");
    }

    [Fact]
    public async Task Every_connect_time_refusal_reports_the_same_generic_reason()
    {
        WebhookEndpointView privateLike = await CreateAsync();
        WebhookEndpointView deadHost = await CreateAsync();
        WebhookDelivery first = await SeedDeliveryAsync(privateLike.Id);
        WebhookDelivery second = await SeedDeliveryAsync(deadHost.Id);
        int call = 0;
        StubHandler handler = new((_, _) => Interlocked.Increment(ref call) == 1
            ? throw new NonPublicAddressException()
            : throw new HttpRequestException("connection refused"));
        using ServiceProvider sp = Provider(handler);

        (await NewWorker(sp).TickAsync(default)).Should().Be(2);

        handler.Calls.Should().HaveCount(2, "both requests threw at connect time");
        Delivery(first.Id).LastError.Should().Be("The request failed.");
        Delivery(second.Id).LastError.Should().Be("The request failed.",
            "a private address and a dead host must not be distinguishable");
    }

    [Fact]
    public async Task Endpoint_without_success_for_72_hours_is_disabled_and_only_owners_and_admins_are_told()
    {
        WebhookEndpointView endpoint = await CreateAsync();
        using (InferenceApiDbContext db = NewDb())
        {
            db.WebhookEndpoints.Single().FailingSince = _clock.Now.UtcDateTime.AddHours(-73);
            db.SaveChanges();
        }

        WebhookDelivery seeded = await SeedDeliveryAsync(endpoint.Id);
        using ServiceProvider sp = Provider(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway)));
        await NewWorker(sp).TickAsync(default);

        using InferenceApiDbContext read = NewDb();
        WebhookEndpoint after = read.WebhookEndpoints.Single();
        after.Enabled.Should().BeFalse();
        after.DisabledReason.Should().Contain("72");

        _notifications.Select(n => n.RecipientUserId).Should().BeEquivalentTo([(Guid?)_owner, _admin]);
        _notifications.Should().OnlyContain(n =>
            n.Type == UserActivityNotificationRequested.WebhookDisabled && n.Subject == "hooks.example.com"
            && n.OrganizationId == _org && n.ActorUserId == null && n.Details == after.DisabledReason);
        _notifications.Select(n => n.Subject).Should().OnlyContain(s => !s.Contains('/'), "the subject is the host only");

        // a disabled endpoint's queue is not sent
        WebhookDelivery later = await SeedDeliveryAsync(endpoint.Id);
        _clock.Now = _clock.Now.AddDays(1);
        (await NewWorker(sp).TickAsync(default)).Should().Be(0);
        Delivery(later.Id).Status.Should().Be("pending");
        Delivery(seeded.Id).Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_success_clears_FailingSince_and_a_failed_test_ping_never_counts_against_health()
    {
        WebhookEndpointView endpoint = await CreateAsync();
        using (InferenceApiDbContext db = NewDb())
        {
            db.WebhookEndpoints.Single().FailingSince = _clock.Now.UtcDateTime.AddHours(-10);
            db.SaveChanges();
        }

        bool ok = true;
        using ServiceProvider sp = Provider(new StubHandler((_, _) => new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)));
        WebhookDelivery ping = await SeedDeliveryAsync(endpoint.Id, WebhookEventTypes.Test);
        ok = false;
        await NewWorker(sp).TickAsync(default);
        Delivery(ping.Id).Status.Should().Be("abandoned", "a test ping is sent once");
        using (InferenceApiDbContext db = NewDb()) db.WebhookEndpoints.Single().FailingSince.Should().Be(_clock.Now.UtcDateTime.AddHours(-10));

        ok = true;
        await SeedDeliveryAsync(endpoint.Id);
        await NewWorker(sp).TickAsync(default);
        using InferenceApiDbContext read = NewDb();
        read.WebhookEndpoints.Single().FailingSince.Should().BeNull();
    }

    [Fact]
    public async Task One_delivery_per_endpoint_is_in_flight_per_tick()
    {
        WebhookEndpointView a = await CreateAsync();
        WebhookEndpointView b = await CreateAsync();
        await SeedDeliveryAsync(a.Id);
        await SeedDeliveryAsync(a.Id);
        await SeedDeliveryAsync(b.Id);
        using ServiceProvider sp = Provider();

        (await NewWorker(sp).TickAsync(default)).Should().Be(2);
        Deliveries().Count(d => d.Status == "delivered").Should().Be(2);
        (await NewWorker(sp).TickAsync(default)).Should().Be(1);
        Deliveries().Should().OnlyContain(d => d.Status == "delivered");
    }

    [Fact]
    public async Task Deliveries_older_than_30_days_are_purged()
    {
        WebhookEndpointView endpoint = await CreateAsync();
        WebhookDelivery old = await SeedDeliveryAsync(endpoint.Id);
        WebhookDelivery fresh = await SeedDeliveryAsync(endpoint.Id);
        using (InferenceApiDbContext db = NewDb())
        {
            db.WebhookDeliveries.Single(d => d.Id == old.Id).CreatedAt = _clock.Now.UtcDateTime.AddDays(-31);
            db.SaveChanges();
        }

        using ServiceProvider sp = Provider();
        (await NewWorker(sp).PurgeAsync(default)).Should().Be(1);
        Deliveries().Select(d => d.Id).Should().Equal(fresh.Id);
    }

    [Fact]
    public async Task Worker_does_nothing_when_the_flag_is_off_through_the_consumer_and_the_hook()
    {
        await CreateAsync(events: ["run_request.started"]);
        _options.Enabled = false;
        using ServiceProvider sp = Provider();
        ApiRunRequestWebhookEvents hook = new(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(_options), _clock);
        await hook.StartedAsync(Guid.NewGuid(), _key, _org, _projectA, Guid.NewGuid(), default);
        Deliveries().Should().BeEmpty();
    }

    // ------------------------------------------------------------------ secrets never persisted by idempotency

    // A fresh context per call, like the scoped one per request in production.
    private Microsoft.Data.Sqlite.SqliteConnection? _sqlite;

    private InferenceApiDbContext SqliteDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_sqlite!).Options);

    private IdempotencyStore Store() => new(SqliteDb(), NullLogger<IdempotencyStore>.Instance);

    private async Task OpenSqliteAsync()
    {
        _sqlite = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await _sqlite.OpenAsync();
        await using InferenceApiDbContext setup = SqliteDb();
        await setup.Database.ExecuteSqlRawAsync(
            "CREATE TABLE api_idempotency_records (api_key_id TEXT NOT NULL, key TEXT NOT NULL, request_hash TEXT NOT NULL, status TEXT NOT NULL, response_status INTEGER NULL, response_body TEXT NULL, created_at TEXT NOT NULL, PRIMARY KEY (api_key_id, key))");
    }

    [Fact]
    public async Task Create_replay_stores_a_redacted_body_so_no_idempotency_record_holds_a_secret()
    {
        await OpenSqliteAsync();
        using InferenceApiDbContext db = NewDb();
        WebhooksController controller = NewController(db);
        ObjectResult created = (ObjectResult)await controller.Create(new WebhookEndpointCreate(Url, null, null, null), default);
        byte[] live = JsonSerializer.SerializeToUtf8Bytes(created.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        System.Text.Encoding.UTF8.GetString(live).Should().Contain("whsec_", "the first response carries the secret");

        IdempotentAttribute attribute = typeof(WebhooksController).GetMethod(nameof(WebhooksController.Create))!
            .GetCustomAttributes<IdempotentAttribute>().Single();
        attribute.RedactFields.Should().Equal("secret");
        byte[] stored = attribute.ForStorage(live);

        DateTime now = _clock.Now.UtcDateTime;
        (await Store().BeginAsync(_key, "k1", "h", now, default)).Status.Should().Be(IdempotencyBeginStatus.Proceed);
        await Store().CompleteAsync(_key, "k1", 201, stored, now, default);

        IdempotencyBegin replay = await Store().BeginAsync(_key, "k1", "h", now, default);
        replay.Status.Should().Be(IdempotencyBeginStatus.Replay);
        replay.ResponseStatus.Should().Be(201);
        JsonNode body = JsonNode.Parse(replay.ResponseBody!)!;
        body["secret"].Should().BeNull();
        body["secretRedacted"]!.GetValue<bool>().Should().BeTrue();
        body["url"]!.GetValue<string>().Should().Be(Url);
        using InferenceApiDbContext check = SqliteDb();
        check.ApiIdempotencyRecords.AsNoTracking().Select(r => r.ResponseBody).ToList()
            .Should().OnlyContain(b => b == null || (!b.Contains("whsec_") && !b.Contains("rbk_")));
    }

    [Fact]
    public async Task Idempotency_store_refuses_to_persist_a_body_with_a_webhook_secret_or_api_key()
    {
        await OpenSqliteAsync();
        DateTime now = _clock.Now.UtcDateTime;
        foreach ((string key, string body) in new[] { ("a", "{\"secret\":\"whsec_abc\"}"), ("b", "{\"token\":\"rbk_abc_def\"}") })
        {
            (await Store().BeginAsync(_key, key, "h", now, default)).Status.Should().Be(IdempotencyBeginStatus.Proceed);
            await Store().CompleteAsync(_key, key, 201, System.Text.Encoding.UTF8.GetBytes(body), now, default);
            (await Store().BeginAsync(_key, key, "h", now, default)).Status.Should().Be(IdempotencyBeginStatus.ResponseUnavailable);
        }

        using InferenceApiDbContext check = SqliteDb();
        check.ApiIdempotencyRecords.AsNoTracking().Select(r => r.ResponseBody).ToList().Should().OnlyContain(b => b == null);
    }
}
