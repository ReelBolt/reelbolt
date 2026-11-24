using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.ApiKeys.ApiKeyTestSupport;

namespace ReelBolt.WorkflowEngine.Tests.ApiKeys;

public class OrgApiKeysControllerTests
{
    private sealed class Rig
    {
        public required InferenceApiDbContext Db { get; init; }
        public required Guid UserId { get; init; }
        public required Guid OrgId { get; init; }
        public bool Enabled { get; set; } = true;
        public bool PlanAllows { get; set; } = true;
        public int MaxKeys { get; set; } = 50;

        public OrgApiKeysController Controller(OrganizationRole? role = OrganizationRole.Owner, Guid? org = null, MassTransit.IPublishEndpoint? publish = null)
        {
            FakeUser user = new(org ?? OrgId, role, UserId);
            Mock<IOrganizationEntitlements> ent = new();
            ent.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { ApiAccess = PlanAllows, Tier = "trial" }));
            return new OrgApiKeysController(Db, user, Service(Db), new ProjectAccessService(Db, user), ent.Object,
                Options.Create(new PublicApiOptions { Enabled = Enabled, MaxKeysPerOrg = MaxKeys }), publish: publish);
        }
    }

    private static async Task<Rig> NewRig()
    {
        InferenceApiDbContext db = NewDb();
        var (user, org) = await SeedCreatorAsync(db);
        return new Rig { Db = db, UserId = user.Id, OrgId = org.Id };
    }

    private static CreateApiKeyRequest Req(DateTimeOffset? exp = null, string[]? scopes = null, Guid[]? projects = null, ApiKeyCapsDto? caps = null, string name = "My key") =>
        new(name, scopes ?? ["runs.read"], projects, exp, caps);

    private static int Status(ActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        ForbidResult => 403,
        _ => throw new InvalidOperationException(r.GetType().Name)
    };

    private static string Code(ActionResult r) => ((ObjectResult)r).Value!.GetType().GetProperty("code")!.GetValue(((ObjectResult)r).Value)!.ToString()!;

    [Fact]
    public async Task Create_returns_the_secret_once_and_list_never_does()
    {
        Rig rig = await NewRig();
        ActionResult created = await rig.Controller().Create(Req(DateTimeOffset.UtcNow.AddDays(30), caps: new(5, null, 100)), default);
        Status(created).Should().Be(201);
        ApiKeyCreatedDto body = (ApiKeyCreatedDto)((ObjectResult)created).Value!;
        body.Secret.Should().StartWith("rbk_");
        body.Key.KeyPrefix.Should().StartWith("rbk_").And.NotBe(body.Secret);
        body.Key.Caps.Should().Be(new ApiKeyCapsView(5, null, 100));
        body.Key.Status.Should().Be("active");
        body.Key.NeverExpires.Should().BeFalse();
        (await rig.Db.ApiKeys.SingleAsync()).DailyCapMillis.Should().Be(5000);

        ActionResult list = await rig.Controller().List(default);
        IReadOnlyList<ApiKeyItemDto> items = ((ApiKeyListDto)((OkObjectResult)list).Value!).Items;
        items.Should().ContainSingle();
        System.Text.Json.JsonSerializer.Serialize(items).Should().NotContain(body.Secret);
    }

    [Fact]
    public async Task Expiry_is_optional_and_bounded()
    {
        Rig rig = await NewRig();
        ApiKeyCreatedDto never = (ApiKeyCreatedDto)((ObjectResult)await rig.Controller().Create(Req(null), default)).Value!;
        never.Key.NeverExpires.Should().BeTrue();
        never.Key.ExpiresAt.Should().BeNull();
        Status(await rig.Controller().Create(Req(DateTimeOffset.UtcNow.AddMinutes(30)), default)).Should().Be(400);
        Status(await rig.Controller().Create(Req(DateTimeOffset.UtcNow.AddDays(400)), default)).Should().Be(400);
    }

    [Fact]
    public async Task Validation_errors()
    {
        Rig rig = await NewRig();
        DateTimeOffset ok = DateTimeOffset.UtcNow.AddDays(10);
        Code(await rig.Controller().Create(Req(ok, scopes: ["nope"]), default)).Should().Be("invalid_scope");
        Code(await rig.Controller().Create(Req(ok, scopes: []), default)).Should().Be("invalid_scope");
        Code(await rig.Controller().Create(Req(ok, name: "  "), default)).Should().Be("invalid_name");
        Code(await rig.Controller().Create(Req(ok, name: "a\u0007b"), default)).Should().Be("invalid_name");
        Code(await rig.Controller().Create(Req(ok, name: new string('x', 81)), default)).Should().Be("invalid_name");
        Code(await rig.Controller().Create(Req(ok, caps: new(0, null, null)), default)).Should().Be("invalid_caps");
        Code(await rig.Controller().Create(Req(ok, caps: new(null, 10_000_001, null)), default)).Should().Be("invalid_caps");
        Code(await rig.Controller().Create(Req(ok, projects: [Guid.NewGuid()]), default)).Should().Be("invalid_project");
    }

    [Fact]
    public async Task Project_must_belong_to_the_org_and_is_listed_with_its_name()
    {
        Rig rig = await NewRig();
        Project mine = new() { Id = Guid.NewGuid(), Name = "Mine", OrganizationId = rig.OrgId, OwnerId = rig.UserId };
        Project foreign = new() { Id = Guid.NewGuid(), Name = "Foreign", OrganizationId = Guid.NewGuid(), OwnerId = Guid.NewGuid() };
        rig.Db.Projects.AddRange(mine, foreign);
        await rig.Db.SaveChangesAsync();
        DateTimeOffset ok = DateTimeOffset.UtcNow.AddDays(10);

        Status(await rig.Controller().Create(Req(ok, projects: [foreign.Id]), default)).Should().Be(400);
        ApiKeyCreatedDto created = (ApiKeyCreatedDto)((ObjectResult)await rig.Controller().Create(Req(ok, projects: [mine.Id]), default)).Value!;
        created.Key.Projects.Should().ContainSingle().Which.Name.Should().Be("Mine");
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Billing)]
    public async Task Non_admin_roles_get_403(OrganizationRole role)
    {
        Rig rig = await NewRig();
        Status(await rig.Controller(role).List(default)).Should().Be(403);
        Status(await rig.Controller(role).Create(Req(), default)).Should().Be(403);
    }

    [Fact]
    public async Task Admin_ok_flag_off_404_no_org_404()
    {
        Rig rig = await NewRig();
        Status(await rig.Controller(OrganizationRole.Admin).List(default)).Should().Be(200);
        Status(await rig.Controller(role: null).List(default)).Should().Be(404);
        rig.Enabled = false;
        Status(await rig.Controller().List(default)).Should().Be(404);
        Status(await rig.Controller().Create(Req(), default)).Should().Be(404);
        Status(await rig.Controller().Revoke(Guid.NewGuid(), default)).Should().Be(404);
    }

    [Fact]
    public async Task Trial_may_create_a_read_only_key_and_key_limit_409()
    {
        Rig rig = await NewRig();
        rig.PlanAllows = false;
        Status(await rig.Controller().Create(Req(DateTimeOffset.UtcNow.AddDays(5)), default)).Should().Be(201);

        rig.PlanAllows = true;
        rig.MaxKeys = 2;
        Status(await rig.Controller().Create(Req(null), default)).Should().Be(201);
        Code(await rig.Controller().Create(Req(null), default)).Should().Be("api_key_limit");
    }

    private static CreateApiKeyRequest CapReq(long? daily = null, bool? noDaily = null, bool? confirm = null) =>
        new("Cap key", ["runs.read"], null, null, new ApiKeyCapsDto(daily, null, null, noDaily), confirm);

    private static ApiKeyItemDto ItemOf(ActionResult r) => r is ObjectResult { Value: ApiKeyCreatedDto c } ? c.Key : (ApiKeyItemDto)((ObjectResult)r).Value!;

    [Fact]
    public async Task Create_without_a_daily_cap_gets_the_default_200_credits()
    {
        Rig rig = await NewRig();
        ApiKeyItemDto k = ItemOf(await rig.Controller().Create(Req(null), default));
        k.Caps.DailyCredits.Should().Be(200);
        k.DailyCap.Should().Be(new ApiKeyDailyCapView("capped", 200));
        (await rig.Db.ApiKeys.SingleAsync()).DailyCapWaivedAt.Should().BeNull();

        ApiKeyItemDto small = ItemOf(await rig.Controller().Create(CapReq(daily: 50), default));
        small.Caps.DailyCredits.Should().Be(50);
        ItemOf(await rig.Controller().Create(CapReq(daily: 200), default)).Caps.DailyCredits.Should().Be(200);
    }

    [Fact]
    public async Task Raising_past_the_default_or_removing_the_daily_cap_needs_confirmation()
    {
        Rig rig = await NewRig();
        Code(await rig.Controller().Create(CapReq(daily: 201), default)).Should().Be("cap_confirmation_required");
        Code(await rig.Controller().Create(CapReq(noDaily: true), default)).Should().Be("cap_confirmation_required");
        Code(await rig.Controller().Create(CapReq(noDaily: true, confirm: false), default)).Should().Be("cap_confirmation_required");
        Code(await rig.Controller().Create(CapReq(daily: 5, noDaily: true, confirm: true), default)).Should().Be("invalid_caps");
        (await rig.Db.ApiKeys.CountAsync()).Should().Be(0);

        ApiKeyItemDto raised = ItemOf(await rig.Controller().Create(CapReq(daily: 5000, confirm: true), default));
        raised.Caps.DailyCredits.Should().Be(5000);
        raised.DailyCap!.Status.Should().Be("capped");

        ApiKeyItemDto waived = ItemOf(await rig.Controller().Create(CapReq(noDaily: true, confirm: true), default));
        waived.Caps.DailyCredits.Should().BeNull();
        waived.DailyCap!.Status.Should().Be("waived");
        (await rig.Db.ApiKeys.SingleAsync(x => x.Id == waived.Id)).DailyCapWaivedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Patch_confirms_only_a_real_raise_or_removal()
    {
        Rig rig = await NewRig();
        ApiKeyItemDto k = ItemOf(await rig.Controller().Create(CapReq(daily: 1000, confirm: true), default));
        UpdateApiKeyRequest Patch(long? daily = null, bool? no = null, string? name = null, bool? confirm = null) =>
            new(name, new ApiKeyCapsDto(daily, null, null, no), ConfirmCaps: confirm);
        async Task<ActionResult> Send(UpdateApiKeyRequest r) => await rig.Controller().Update(k.Id, r, default);

        // re-save above the default, unchanged: no confirm; rename-only with caps resent: no confirm
        Status(await Send(Patch(daily: 1000))).Should().Be(200);
        Status(await Send(Patch(daily: 1000, name: "Renamed"))).Should().Be(200);
        // lowering (1000 -> 500) and anything within the default: no confirm
        ItemOf(await Send(Patch(daily: 500))).Caps.DailyCredits.Should().Be(500);
        ItemOf(await Send(Patch(daily: 150))).Caps.DailyCredits.Should().Be(150);
        // raising past both the default and the current cap needs confirm; within default does not
        Code(await Send(Patch(daily: 250))).Should().Be("cap_confirmation_required");
        ItemOf(await Send(Patch(daily: 200))).Caps.DailyCredits.Should().Be(200);
        Status(await Send(Patch(daily: 250, confirm: true))).Should().Be(200);
        Status(await Send(Patch(daily: 250))).Should().Be(200); // now the current cap

        // removal needs confirm; re-saving a waived key does not
        Code(await Send(Patch(no: true))).Should().Be("cap_confirmation_required");
        ItemOf(await Send(Patch(no: true, confirm: true))).DailyCap!.Status.Should().Be("waived");
        DateTime? waivedAt = (await rig.Db.ApiKeys.SingleAsync()).DailyCapWaivedAt;
        ItemOf(await Send(Patch(no: true))).DailyCap!.Status.Should().Be("waived");
        (await rig.Db.ApiKeys.SingleAsync()).DailyCapWaivedAt.Should().Be(waivedAt);

        // a legacy key (no cap, no waiver) asking for noDailyCap is a removal: confirm needed
        ApiKey row = await rig.Db.ApiKeys.SingleAsync();
        row.DailyCapWaivedAt = null;
        await rig.Db.SaveChangesAsync();
        Code(await Send(Patch(no: true))).Should().Be("cap_confirmation_required");
    }

    [Fact]
    public async Task Patch_with_an_explicit_null_daily_credits_never_removes_the_cap()
    {
        Rig rig = await NewRig();
        ApiKeyItemDto k = ItemOf(await rig.Controller().Create(CapReq(daily: 40), default));
        ApiKeyItemDto after = ItemOf(await rig.Controller().Update(k.Id,
            new UpdateApiKeyRequest(null, new ApiKeyCapsDto(null, 9, null, false)), default));
        after.Caps.DailyCredits.Should().Be(40);
        after.DailyCap!.Status.Should().Be("capped");
    }

    [Fact]
    public async Task A_key_with_no_daily_cap_and_no_waiver_is_listed_as_legacy_uncapped_and_the_list_names_the_default()
    {
        Rig rig = await NewRig();
        ApiKeyItemDto k = ItemOf(await rig.Controller().Create(Req(null), default));
        ApiKey row = await rig.Db.ApiKeys.SingleAsync();
        row.DailyCapMillis = null;
        await rig.Db.SaveChangesAsync();

        ApiKeyListDto list = (ApiKeyListDto)((ObjectResult)await rig.Controller().List(default)).Value!;
        list.DefaultDailyCapCredits.Should().Be(200);
        list.Items.Single(i => i.Id == k.Id).DailyCap!.Status.Should().Be("legacy_uncapped");
    }

    [Fact]
    public async Task Patch_leaves_the_daily_cap_alone_unless_it_is_sent_and_confirms_a_raise_or_removal()
    {
        Rig rig = await NewRig();
        ApiKeyItemDto k = ItemOf(await rig.Controller().Create(CapReq(daily: 40), default));

        // weekly only: daily unchanged, weekly overwritten
        ItemOf(await rig.Controller().Update(k.Id, new UpdateApiKeyRequest(null, new ApiKeyCapsDto(null, 70, null)), default)).Caps
            .Should().Be(new ApiKeyCapsView(40, 70, null));

        Code(await rig.Controller().Update(k.Id, new UpdateApiKeyRequest(null, new ApiKeyCapsDto(500, null, null)), default)).Should().Be("cap_confirmation_required");
        Code(await rig.Controller().Update(k.Id, new UpdateApiKeyRequest(null, new ApiKeyCapsDto(null, null, null, true)), default)).Should().Be("cap_confirmation_required");
        (await rig.Db.ApiKeys.SingleAsync()).DailyCapMillis.Should().Be(40_000);

        ApiKeyItemDto waived = ItemOf(await rig.Controller().Update(k.Id, new UpdateApiKeyRequest(null, new ApiKeyCapsDto(null, null, null, true), ConfirmCaps: true), default));
        waived.DailyCap!.Status.Should().Be("waived");

        // a numeric daily within the default needs no confirmation and clears the waiver
        ApiKeyItemDto again = ItemOf(await rig.Controller().Update(k.Id, new UpdateApiKeyRequest(null, new ApiKeyCapsDto(100, null, null)), default));
        again.DailyCap!.Status.Should().Be("capped");
        (await rig.Db.ApiKeys.SingleAsync()).DailyCapWaivedAt.Should().BeNull();
    }

    [Fact]
    public async Task Patch_renames_key_and_service_user_and_refuses_immutable_fields()
    {
        Rig rig = await NewRig();
        ApiKeyCreatedDto c = (ApiKeyCreatedDto)((ObjectResult)await rig.Controller().Create(Req(null), default)).Value!;
        Status(await rig.Controller().Update(c.Key.Id, new UpdateApiKeyRequest("New", new(null, 7, null)), default)).Should().Be(200);
        ApiKey row = await rig.Db.ApiKeys.SingleAsync();
        row.Name.Should().Be("New");
        row.WeeklyCapMillis.Should().Be(7000);
        (await rig.Db.ApplicationUsers.SingleAsync(u => u.Id == row.ServiceUserId)).DisplayName.Should().Be("New");
        Code(await rig.Controller().Update(c.Key.Id, new UpdateApiKeyRequest(null, null, Scopes: ["runs.read"]), default)).Should().Be("immutable_field");
        Code(await rig.Controller().Update(c.Key.Id, new UpdateApiKeyRequest(null, null, ExpiresAt: DateTimeOffset.UtcNow), default)).Should().Be("immutable_field");
    }

    [Fact]
    public async Task Revoke_is_idempotent_keeps_service_rows_and_foreign_org_is_404()
    {
        Rig rig = await NewRig();
        ApiKeyCreatedDto c = (ApiKeyCreatedDto)((ObjectResult)await rig.Controller().Create(Req(null), default)).Value!;
        Status(await rig.Controller().Revoke(c.Key.Id, default)).Should().Be(204);
        DateTime? first = (await rig.Db.ApiKeys.SingleAsync()).RevokedAt;
        first.Should().NotBeNull();
        Status(await rig.Controller().Revoke(c.Key.Id, default)).Should().Be(204);
        (await rig.Db.ApiKeys.SingleAsync()).RevokedAt.Should().Be(first);
        (await rig.Db.OrganizationMemberships.CountAsync(m => m.UserId == rig.Db.ApiKeys.Single().ServiceUserId)).Should().Be(1);

        Status(await rig.Controller(org: Guid.NewGuid()).Revoke(c.Key.Id, default)).Should().Be(404);
        Status(await rig.Controller().Revoke(Guid.NewGuid(), default)).Should().Be(404);
    }

    [Fact]
    public async Task Revoke_cancels_inflight_runs_queued_requests_and_disables_the_keys_webhooks()
    {
        Rig rig = await NewRig();
        ApiKeyCreatedDto c = (ApiKeyCreatedDto)((ObjectResult)await rig.Controller().Create(Req(null), default)).Value!;
        Guid keyId = c.Key.Id;
        Guid running = Guid.NewGuid(), queued = Guid.NewGuid(), done = Guid.NewGuid(), other = Guid.NewGuid();
        rig.Db.WorkflowExecutions.AddRange(
            new WorkflowExecution { Id = running, OrganizationId = rig.OrgId, ApiKeyId = keyId, Status = ExecutionStatus.Running },
            new WorkflowExecution { Id = queued, OrganizationId = rig.OrgId, ApiKeyId = keyId, Status = ExecutionStatus.Queued },
            new WorkflowExecution { Id = done, OrganizationId = rig.OrgId, ApiKeyId = keyId, Status = ExecutionStatus.Passed },
            new WorkflowExecution { Id = other, OrganizationId = rig.OrgId, ApiKeyId = Guid.NewGuid(), Status = ExecutionStatus.Running });
        rig.Db.ApiRunRequests.Add(new ApiRunRequest { Id = Guid.NewGuid(), ApiKeyId = keyId, OrganizationId = rig.OrgId });
        rig.Db.WebhookEndpoints.AddRange(
            new WebhookEndpoint { Id = Guid.NewGuid(), OrganizationId = rig.OrgId, CreatedByApiKeyId = keyId, Url = "https://a.example" },
            new WebhookEndpoint { Id = Guid.NewGuid(), OrganizationId = rig.OrgId, Url = "https://b.example" });
        await rig.Db.SaveChangesAsync();

        List<WorkflowExecutionStopRequested> stops = [];
        Mock<MassTransit.IPublishEndpoint> pub = new();
        pub.Setup(p => p.Publish(It.IsAny<WorkflowExecutionStopRequested>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionStopRequested, CancellationToken>((m, _) => stops.Add(m))
            .Returns(Task.CompletedTask);

        Status(await rig.Controller(publish: pub.Object).Revoke(keyId, default)).Should().Be(204);
        Status(await rig.Controller(publish: pub.Object).Revoke(keyId, default)).Should().Be(204);

        stops.Select(s => s.ExecutionId).Distinct().Should().BeEquivalentTo([running, queued]);
        stops.Should().OnlyContain(s => s.Reason == WorkflowExecutionStopRequested.ApiKeyRevokedReason);
        (await rig.Db.ApiRunRequests.SingleAsync()).Status.Should().Be(ApiRunRequest.Cancelled);
        WebhookEndpoint mine = await rig.Db.WebhookEndpoints.SingleAsync(e => e.CreatedByApiKeyId == keyId);
        mine.Enabled.Should().BeFalse();
        mine.DisabledReason.Should().Be("api_key_revoked");
        (await rig.Db.WebhookEndpoints.SingleAsync(e => e.CreatedByApiKeyId == null)).Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Create_refuses_format_characters_in_names()
    {
        Rig rig = await NewRig();
        Status(await rig.Controller().Create(Req(name: "ev\u202Eil"), default)).Should().Be(400);
        Status(await rig.Controller().Create(Req(name: "zero\u200Bwidth"), default)).Should().Be(400);
    }

    [Fact]
    public async Task Create_tolerates_a_clock_ten_minutes_ahead_but_not_more()
    {
        Rig rig = await NewRig();
        int max = new PublicApiOptions().MaxLifetimeDays;
        DateTimeOffset ok = DateTimeOffset.UtcNow.AddDays(max).AddMinutes(5);
        ApiKeyCreatedDto c = (ApiKeyCreatedDto)((ObjectResult)await rig.Controller().Create(Req(ok), default)).Value!;
        (await rig.Db.ApiKeys.SingleAsync(k => k.Id == c.Key.Id)).ExpiresAt.Should().Be(ok.UtcDateTime);
        Status(await rig.Controller().Create(Req(DateTimeOffset.UtcNow.AddDays(max).AddMinutes(30)), default)).Should().Be(400);
    }
}

public class ApiKeyProjectRestrictionTests
{
    [Fact]
    public async Task Restricted_key_gets_NotFound_elsewhere_and_cannot_create_projects()
    {
        InferenceApiDbContext db = NewDb();
        var (user, org) = await SeedCreatorAsync(db);
        Project a = new() { Id = Guid.NewGuid(), Name = "A", OrganizationId = org.Id, OwnerId = user.Id };
        Project b = new() { Id = Guid.NewGuid(), Name = "B", OrganizationId = org.Id, OwnerId = user.Id };
        db.Projects.AddRange(a, b);
        await db.SaveChangesAsync();
        FakeUser current = new(org.Id, OrganizationRole.Member, user.Id);

        ProjectAccessService restricted = new(db, current, new FakeKeyContext(Guid.NewGuid(), [a.Id]));
        (await restricted.GetAsync(a.Id, ProjectPermission.View, default)).Outcome.Should().Be(ProjectAccessOutcome.Found);
        (await restricted.GetAsync(b.Id, ProjectPermission.View, default)).Outcome.Should().Be(ProjectAccessOutcome.NotFound);
        restricted.Get(b.Id, ProjectPermission.View).Outcome.Should().Be(ProjectAccessOutcome.NotFound);
        restricted.AuthorizeOrganization(ProjectPermission.Edit).Should().Be(ProjectAccessOutcome.Forbidden);

        ProjectAccessService open = new(db, current, new FakeKeyContext(Guid.NewGuid(), null));
        (await open.GetAsync(b.Id, ProjectPermission.View, default)).Outcome.Should().Be(ProjectAccessOutcome.Found);
        open.AuthorizeOrganization(ProjectPermission.Edit).Should().Be(ProjectAccessOutcome.Found);
        new ProjectAccessService(db, current).Get(b.Id, ProjectPermission.View).Outcome.Should().Be(ProjectAccessOutcome.Found);
    }
}
