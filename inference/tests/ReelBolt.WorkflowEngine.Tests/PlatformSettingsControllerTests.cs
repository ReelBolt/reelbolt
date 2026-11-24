#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The operator's switch panel (F11). Two things are contractual here: only a platform admin may read or
/// write (an organisation Owner is refused, because <c>compute_paused</c> stops every tenant), and a
/// write lands as ONE row per key whose audit fields move — a resume is recorded, never deleted, because
/// "who turned this off and who turned it back on" is the question asked after an incident.
/// </summary>
public class PlatformSettingsControllerTests
{
    private static readonly Guid DisabledOrg = Guid.NewGuid();

    private sealed class User(bool isAdmin) : ICurrentUser
    {
        public Guid UserId => Guid.NewGuid();
        public string Email => "operator@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => isAdmin;
        public Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static PlatformSettingsController Controller(InferenceApiDbContext db, bool isAdmin)
        => new(db, new User(isAdmin));

    [Fact]
    public async Task A_non_admin_is_forbidden_and_writes_nothing()
    {
        await using InferenceApiDbContext db = NewDb();
        PlatformSettingsController controller = Controller(db, isAdmin: false);

        (await controller.List(CancellationToken.None)).Result.Should().BeOfType<ForbidResult>();
        (await controller.Set(PlatformSetting.ComputeKey, new SetPlatformSettingRequest(true), CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();

        // The refusal must be a refusal, not a refusal plus a write.
        db.PlatformSettings.Should().BeEmpty();
        (await db.PlatformSettings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_organization_owner_is_not_a_platform_admin()
    {
        // Stated explicitly because it is easy to assume the other way: setting compute_paused stops
        // every tenant, so no tenant's role — however senior — may set it.
        await using InferenceApiDbContext db = NewDb();
        PlatformSettingsController controller = Controller(db, isAdmin: false);

        (await controller.Set(PlatformSetting.OrganizationKey(DisabledOrg), new SetPlatformSettingRequest(true), CancellationToken.None))
            .Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Setting_a_switch_twice_leaves_one_row_with_the_newer_audit()
    {
        await using InferenceApiDbContext db = NewDb();
        PlatformSettingsController controller = Controller(db, isAdmin: true);

        await controller.Set(PlatformSetting.ComputeKey, new SetPlatformSettingRequest(true, "runaway spend"), CancellationToken.None);
        PlatformSettingResponse updated = (await controller.Set(
            PlatformSetting.ComputeKey, new SetPlatformSettingRequest(true, "still investigating"), CancellationToken.None)).Value!;

        db.PlatformSettings.Count().Should().Be(1);
        updated.Reason.Should().Be("still investigating");
        updated.Paused.Should().BeTrue();
        updated.UpdatedBy.Should().Be("operator@example.com");
        updated.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_resume_is_recorded_rather_than_deleted()
    {
        await using InferenceApiDbContext db = NewDb();
        PlatformSettingsController controller = Controller(db, isAdmin: true);

        await controller.Set(PlatformSetting.ComputeKey, new SetPlatformSettingRequest(true, "off"), CancellationToken.None);
        await controller.Set(PlatformSetting.ComputeKey, new SetPlatformSettingRequest(false, "resolved"), CancellationToken.None);

        PlatformSetting row = await db.PlatformSettings.SingleAsync();
        row.Paused.Should().BeFalse();
        row.Reason.Should().Be("resolved");

        // And the derived view agrees: a row that says "not paused" pauses nothing.
        PlatformSettingsResponse view = (await controller.List(CancellationToken.None)).Value!;
        view.ComputePaused.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_key_is_refused()
    {
        await using InferenceApiDbContext db = NewDb();
        PlatformSettingsController controller = Controller(db, isAdmin: true);

        (await controller.Set("everything", new SetPlatformSettingRequest(true), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.Set("org:not-a-guid", new SetPlatformSettingRequest(true), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        db.PlatformSettings.Should().BeEmpty();
    }

    [Fact]
    public async Task An_organization_key_is_canonicalised()
    {
        // The Go runner gateway compares keys by string, so two rows differing only in case would be
        // one switch that half works.
        await using InferenceApiDbContext db = NewDb();
        PlatformSettingsController controller = Controller(db, isAdmin: true);

        PlatformSettingResponse response = (await controller.Set(
            "org:" + DisabledOrg.ToString("D").ToUpperInvariant(),
            new SetPlatformSettingRequest(true, "abuse", PoolSlotCap: 3), CancellationToken.None)).Value!;

        response.Key.Should().Be(PlatformSetting.OrganizationKey(DisabledOrg));
        response.PoolSlotCap.Should().Be(3);
        (await db.PlatformSettings.SingleAsync()).Key.Should().Be("org:" + DisabledOrg.ToString("D").ToLowerInvariant());
    }

    [Fact]
    public async Task A_pool_cap_on_a_global_switch_is_refused_rather_than_ignored()
    {
        await using InferenceApiDbContext db = NewDb();
        PlatformSettingsController controller = Controller(db, isAdmin: true);

        (await controller.Set(PlatformSetting.ComputeKey, new SetPlatformSettingRequest(true, null, PoolSlotCap: 4), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.Set(PlatformSetting.OrganizationKey(DisabledOrg), new SetPlatformSettingRequest(true, null, PoolSlotCap: 5000), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task The_list_resolves_the_two_flags_and_the_disabled_organizations()
    {
        await using InferenceApiDbContext db = NewDb();
        PlatformSettingsController controller = Controller(db, isAdmin: true);

        await controller.Set(PlatformSetting.ComputeKey, new SetPlatformSettingRequest(true, "incident"), CancellationToken.None);
        await controller.Set(PlatformSetting.OrganizationKey(DisabledOrg), new SetPlatformSettingRequest(true, "abuse"), CancellationToken.None);
        await controller.Set(PlatformSetting.OrganizationKey(Guid.NewGuid()), new SetPlatformSettingRequest(false, "reinstated"), CancellationToken.None);

        PlatformSettingsResponse view = (await controller.List(CancellationToken.None)).Value!;

        view.ComputePaused.Should().BeTrue();
        view.PlatformInferencePaused.Should().BeFalse("inference was never paused");
        view.DisabledOrganizationIds.Should().Equal(DisabledOrg);
        view.Settings.Should().HaveCount(3);
    }
}
