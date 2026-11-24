using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.ApiKeys.ApiKeyTestSupport;

namespace ReelBolt.WorkflowEngine.Tests.ApiKeys;

public class ApiKeyTokenTests
{
    [Fact]
    public void Generated_token_has_the_documented_shape_and_parses()
    {
        ApiKeyToken.Generated g = ApiKeyToken.Generate();
        g.Token.Should().MatchRegex("^rbk_[0-9A-HJKMNP-TV-Z]{12}_[A-Za-z0-9_-]{43}$");
        ApiKeyToken.TryParse(g.Token, out string id).Should().BeTrue();
        id.Should().Be(g.KeyId);
        g.Token.Should().EndWith(g.LastFour);
        ApiKeyToken.Hash(g.Token).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Theory]
    [InlineData("")]
    [InlineData("rbk_")]
    [InlineData("xyz_ABCDEFGHJKMN_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("rbk_ABCDEFGHJKMI_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // I is not Crockford
    [InlineData("rbk_ABCDEFGHJKMN_short")]
    public void Malformed_tokens_are_rejected(string token) => ApiKeyToken.TryParse(token, out _).Should().BeFalse();

    [Fact]
    public void Overlong_token_is_rejected() =>
        ApiKeyToken.TryParse(ApiKeyToken.Generate().Token + new string('a', 40), out _).Should().BeFalse();
}

public class ApiKeyServiceTests
{
    [Fact]
    public async Task Create_stores_hash_not_plaintext_and_makes_a_member_service_account()
    {
        using var db = NewDb();
        var (creator, org) = await SeedCreatorAsync(db);
        CreatedApiKey created = await Service(db).CreateAsync(Command(org.Id, creator.Id, DateTime.UtcNow.AddDays(30)), default);

        ApiKey row = await db.ApiKeys.SingleAsync();
        row.SecretHash.Should().Be(ApiKeyToken.Hash(created.Token)).And.NotContain(created.Token);
        row.CreatedByUserId.Should().Be(creator.Id);
        row.ServiceUserId.Should().NotBe(creator.Id);

        ApplicationUser svc = await db.ApplicationUsers.SingleAsync(u => u.Id == row.ServiceUserId);
        svc.IsServiceAccount.Should().BeTrue();
        svc.IsAdmin.Should().BeFalse();
        svc.PasswordHash.Should().BeNull();
        svc.Email.Should().EndWith("@service.invalid");
        svc.DisplayName.Should().Be("CI key");
        svc.EmailVerifiedAt.Should().NotBeNull();
        svc.MustChangePassword.Should().BeFalse();
        (await db.OrganizationMemberships.Where(m => m.UserId == svc.Id).ToListAsync())
            .Should().ContainSingle().Which.Should().Match<OrganizationMembership>(m => m.OrganizationId == org.Id && m.Role == OrganizationRole.Member);
    }

    [Fact]
    public async Task Verify_covers_ok_wrong_unknown_revoked_expired_and_never_expires()
    {
        using var db = NewDb();
        var (creator, org) = await SeedCreatorAsync(db);
        ApiKeyService svc = Service(db);
        CreatedApiKey ok = await svc.CreateAsync(Command(org.Id, creator.Id, DateTime.UtcNow.AddDays(30)), default);
        CreatedApiKey never = await svc.CreateAsync(Command(org.Id, creator.Id, null), default);

        (await svc.VerifyAsync(ok.Token, default)).Status.Should().Be(ApiKeyVerifyStatus.Ok);
        (await svc.VerifyAsync(never.Token, default)).Status.Should().Be(ApiKeyVerifyStatus.Ok);
        string wrong = ok.Token[..^4] + (ok.Token.EndsWith("AAAA") ? "BBBB" : "AAAA");
        (await svc.VerifyAsync(wrong, default)).Status.Should().Be(ApiKeyVerifyStatus.Invalid);
        (await svc.VerifyAsync(ApiKeyToken.Generate().Token, default)).Status.Should().Be(ApiKeyVerifyStatus.Invalid);
        (await svc.VerifyAsync("garbage", default)).Status.Should().Be(ApiKeyVerifyStatus.Invalid);

        ApiKey row = await db.ApiKeys.SingleAsync(k => k.Id == ok.Key.Id);
        row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        (await svc.VerifyAsync(ok.Token, default)).Status.Should().Be(ApiKeyVerifyStatus.Expired);

        ApiKey nrow = await db.ApiKeys.SingleAsync(k => k.Id == never.Key.Id);
        nrow.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        (await svc.VerifyAsync(never.Token, default)).Status.Should().Be(ApiKeyVerifyStatus.Revoked);
        // a revoked key with a wrong secret reveals nothing
        (await svc.VerifyAsync(never.Token[..^4] + "ZZZZ", default)).Status.Should().Be(ApiKeyVerifyStatus.Invalid);
    }
}

public class ApiAccessCatalogTests
{
    [Fact]
    public void ApiAccess_is_off_on_trial_and_on_for_every_paid_plan_in_both_catalog_copies()
    {
        string root = AppContext.BaseDirectory;
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Combine(root, "site/lib/pricing/plans.catalog.json")))
            root = System.IO.Path.GetDirectoryName(root)!;
        string api = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json"));
        string site = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "site/lib/pricing/plans.catalog.json"));
        site.Should().Be(api);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(api);
        foreach (System.Text.Json.JsonElement plan in doc.RootElement.GetProperty("plans").EnumerateArray())
            plan.GetProperty("entitlements").GetProperty("apiAccess").GetBoolean()
                .Should().Be(plan.GetProperty("id").GetString() != "trial");
    }
}
