using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>The one plan catalog (R5): it loads, maps onto the entitlement set, and upserts idempotently.</summary>
public sealed class PlanCatalogTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public PlanCatalogTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private static string ShippedCatalog() => File.ReadAllText(PlanCatalogSeeder.DefaultPath);

    [Fact]
    public void ShippedCatalog_HasTheFiveDocumentedPlans_WithMillicreditAllowances()
    {
        var plans = PlanCatalogSeeder.Load(ShippedCatalog());
        plans.Select(p => p.Key).Should().Equal("trial", "creator", "pro", "team", "enterprise");

        plans.Single(p => p.Key == "trial").OneTimeCreditsMillis.Should().Be(100_000);
        plans.Single(p => p.Key == "creator").MonthlyCreditsMillis.Should().Be(1_000_000);
        plans.Single(p => p.Key == "creator").PriceMonthlyUsd.Should().Be(29m);
        plans.Single(p => p.Key == "pro").MonthlyCreditsMillis.Should().Be(3_500_000);
        Plan team = plans.Single(p => p.Key == "team");
        (team.Kind, team.MinSeats, team.MonthlyCreditsMillis).Should().Be((PlanKind.PerSeat, 3, 1_500_000));
        plans.Single(p => p.Key == "enterprise").Kind.Should().Be(PlanKind.Contact);
        plans.Where(p => p.TopUpAllowed).Select(p => p.Key).Should().Equal("creator", "pro", "team");
    }

    [Fact]
    public void PlanEntitlements_MapOntoOrganizationEntitlementSet()
    {
        var plans = PlanCatalogSeeder.Load(ShippedCatalog()).ToDictionary(p => p.Key);

        OrganizationEntitlementSet trial = PlanEntitlements.ToSet(plans["trial"]);
        trial.Tier.Should().Be("trial");
        (trial.ByoAllowed, trial.Watermark, trial.MaxOutputHeight, trial.RoomsAllowed, trial.VideoGenAllowed)
            .Should().Be((false, true, 720, false, false));

        OrganizationEntitlementSet pro = PlanEntitlements.ToSet(plans["pro"]);
        (pro.ByoAllowed, pro.Watermark, pro.MaxOutputHeight, pro.RoomsAllowed, pro.VideoGenAllowed, pro.Priority)
            .Should().Be((true, false, 2160, true, true, 1));

        PlanEntitlements.ToSet(plans["creator"]).MaxOutputHeight.Should().Be(1080);
        PlanEntitlements.ToSet(plans["team"]).MaxOutputHeight.Should().Be(2160);
    }

    [Fact]
    public void AudioGeneration_IsItsOwnEntitlement_OffOnTrial_OnForEveryPaidPlan_WithNoDailyCap()
    {
        // Product decision 6: separate from video generation; Creator has audio although it has no video generation.
        var plans = PlanCatalogSeeder.Load(ShippedCatalog()).ToDictionary(p => p.Key);
        plans.Keys.Select(k => (k, PlanEntitlements.ToSet(plans[k]).AudioGenAllowed)).Should().Equal(
            ("trial", false), ("creator", true), ("pro", true), ("team", true), ("enterprise", true));
        plans.Values.Should().OnlyContain(p => !p.EntitlementsJson.Contains("audioGenerationDaily"), "audio has no daily cap");

        PlanGateViolation? trial = PlanGate.CheckStep(ReelBolt.Shared.Data.Models.StepType.AudioGenerate, PlanEntitlements.ToSet(plans["trial"]));
        trial.Should().NotBeNull();
        (trial!.Code, trial.Feature, trial.StatusCode).Should().Be(("PLAN_NOT_ENTITLED", "audio_generation", 402));
        trial.Message.Should().Contain("audio generation");

        OrganizationEntitlementSet creator = PlanEntitlements.ToSet(plans["creator"]);
        PlanGate.CheckStep(ReelBolt.Shared.Data.Models.StepType.AudioGenerate, creator).Should().BeNull();
        PlanGate.CheckStep(ReelBolt.Shared.Data.Models.StepType.VideoGenerate, creator).Should().NotBeNull("creator still has no video generation");
    }

    [Fact]
    public void SocialPublishing_IsOffOnTrial_OnFromCreatorUp_AndIsNotAStepTypeGate()
    {
        var plans = PlanCatalogSeeder.Load(ShippedCatalog()).ToDictionary(p => p.Key);
        plans.Keys.Select(k => (k, PlanEntitlements.ToSet(plans[k]).SocialPublishingAllowed)).Should().Equal(
            ("trial", false), ("creator", true), ("pro", true), ("team", true), ("enterprise", true));

        PlanGateViolation? trial = PlanGate.CheckSocialPublishing(PlanEntitlements.ToSet(plans["trial"]));
        trial.Should().NotBeNull();
        (trial!.Code, trial.Feature, trial.StatusCode).Should().Be(("PLAN_NOT_ENTITLED", "social_publishing", 402));
        PlanGate.CheckSocialPublishing(PlanEntitlements.ToSet(plans["creator"])).Should().BeNull();
        new OrganizationEntitlementSet().SocialPublishingAllowed.Should().BeTrue("an omitted key means allowed");
    }

    [Fact]
    public void AudioCleanup_IsItsOwnEntitlement_OffOnTrial_OnForEveryPaidPlan_AndOnlyTheHostedTargetsAskForIt()
    {
        // the AI targets are off on Trial; the free local targets never consult the flag.
        var plans = PlanCatalogSeeder.Load(ShippedCatalog()).ToDictionary(p => p.Key);
        plans.Keys.Select(k => (k, PlanEntitlements.ToSet(plans[k]).AudioCleanupAllowed)).Should().Equal(
            ("trial", false), ("creator", true), ("pro", true), ("team", true), ("enterprise", true));

        PlanGateViolation? trial = PlanGate.CheckAudioCleanup(PlanEntitlements.ToSet(plans["trial"]));
        trial.Should().NotBeNull();
        (trial!.Code, trial.Feature, trial.StatusCode).Should().Be(("PLAN_NOT_ENTITLED", PlanGate.AudioCleanupFeature, 402));
        PlanGate.AudioCleanupFeature.Should().Be("audio_cleanup");
        trial.Message.Should().Contain("trial").And.Contain("free targets");
        PlanGate.CheckAudioCleanup(PlanEntitlements.ToSet(plans["creator"])).Should().BeNull();

        // Not a step-type gate: a Trial run of a local AudioCleanup is not refused by the type-based checks.
        PlanGate.FeatureFor(ReelBolt.Shared.Data.Models.StepType.AudioCleanup).Should().BeNull();
        PlanGate.CheckStep(ReelBolt.Shared.Data.Models.StepType.AudioCleanup, PlanEntitlements.ToSet(plans["trial"])).Should().BeNull();

        // An omitted key keeps the record default (allowed), so a catalog written before the flag keeps granting it.
        new OrganizationEntitlementSet().AudioCleanupAllowed.Should().BeTrue();
    }

    [Fact]
    public void VideoEnhance_IsItsOwnEntitlement_OffOnTrial_OnForEveryPaidPlan_AndOnlyTheHostedOperationsAskForIt()
    {
        // The AI operations are off on Trial; the free local operations never consult the flag.
        var plans = PlanCatalogSeeder.Load(ShippedCatalog()).ToDictionary(p => p.Key);
        plans.Keys.Select(k => (k, PlanEntitlements.ToSet(plans[k]).VideoEnhanceAllowed)).Should().Equal(
            ("trial", false), ("creator", true), ("pro", true), ("team", true), ("enterprise", true));

        PlanGateViolation? trial = PlanGate.CheckVideoEnhance(PlanEntitlements.ToSet(plans["trial"]));
        trial.Should().NotBeNull();
        (trial!.Code, trial.Feature, trial.StatusCode).Should().Be(("PLAN_NOT_ENTITLED", PlanGate.VideoEnhanceFeature, 402));
        PlanGate.VideoEnhanceFeature.Should().Be("video_enhance");
        trial.Message.Should().Contain("trial").And.Contain("free local operations");
        PlanGate.CheckVideoEnhance(PlanEntitlements.ToSet(plans["creator"])).Should().BeNull();

        // Not a step-type gate: a Trial run of a local VideoEnhance is not refused by the type-based checks.
        PlanGate.FeatureFor(ReelBolt.Shared.Data.Models.StepType.VideoEnhance).Should().BeNull();
        PlanGate.CheckStep(ReelBolt.Shared.Data.Models.StepType.VideoEnhance, PlanEntitlements.ToSet(plans["trial"])).Should().BeNull();

        // An omitted key keeps the record default (allowed), so a catalog written before the flag keeps granting it.
        new OrganizationEntitlementSet().VideoEnhanceAllowed.Should().BeTrue();
    }

    [Fact]
    public void TheSiteCopyOfTheCatalog_IsByteIdenticalToTheApisCanonicalFile()
    {
        string site = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../site/lib/pricing/plans.catalog.json"));
        if (!File.Exists(site))
            return; // the test host has no checkout of the site next to the engine tests

        File.ReadAllBytes(site).Should().Equal(File.ReadAllBytes(PlanCatalogSeeder.DefaultPath));
    }

    [Fact]
    public void PlanEntitlements_CarryTheAssistantAllowance_EnterpriseUnlimitedOnlyExplicitly()
    {
        var plans = PlanCatalogSeeder.Load(ShippedCatalog()).ToDictionary(p => p.Key);

        (int?, int?, bool) Allowance(string key)
        {
            OrganizationEntitlementSet set = PlanEntitlements.ToSet(plans[key]);
            return (set.AssistantMonthlyCredits, set.AssistantMonthlyCreditsPerSeat, set.AssistantUnlimited);
        }

        Allowance("trial").Should().Be(((int?)20, (int?)null, false));
        Allowance("creator").Should().Be(((int?)100, (int?)null, false));
        Allowance("pro").Should().Be(((int?)300, (int?)null, false));
        Allowance("team").Should().Be(((int?)null, (int?)100, false));
        Allowance("enterprise").Should().Be(((int?)null, (int?)null, true), "unlimited is an explicit entitlement (T-032, F-5.1)");

        // The config default (the fallback when no plan row exists) is never unlimited for the assistant.
        OrganizationEntitlementSet.Unlimited.AssistantMonthlyCredits.Should().BeNull();
        OrganizationEntitlementSet.Unlimited.AssistantMonthlyCreditsPerSeat.Should().BeNull();
        OrganizationEntitlementSet.Unlimited.AssistantUnlimited.Should().BeFalse();
    }

    [Fact]
    public void EveryPlan_NamesItsAssistantAllowanceInOneHighlight()
    {
        using JsonDocument doc = JsonDocument.Parse(ShippedCatalog());
        var expected = new System.Collections.Generic.Dictionary<string, string>
        {
            ["trial"] = "20 assistant credits a month",
            ["creator"] = "100 assistant credits a month",
            ["pro"] = "300 assistant credits a month",
            ["team"] = "100 assistant credits per seat a month",
            ["enterprise"] = "Unlimited assistant use",
        };
        foreach (JsonElement plan in doc.RootElement.GetProperty("plans").EnumerateArray())
        {
            string id = plan.GetProperty("id").GetString()!;
            var highlights = plan.GetProperty("highlights").EnumerateArray().Select(h => h.GetString()!).ToList();
            highlights.Where(h => h.Contains("assistant", StringComparison.OrdinalIgnoreCase))
                .Should().Equal([expected[id]], $"plan {id} names its assistant allowance once");
        }
    }

    [Fact]
    public void EveryCatalogEntitlementKey_IsARealEntitlementSetProperty()
    {
        string[] known = typeof(OrganizationEntitlementSet).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToArray();
        using JsonDocument doc = JsonDocument.Parse(ShippedCatalog());
        foreach (JsonElement plan in doc.RootElement.GetProperty("plans").EnumerateArray())
        {
            foreach (JsonProperty key in plan.GetProperty("entitlements").EnumerateObject())
            {
                known.Should().Contain(key.Name, $"plan {plan.GetProperty("id").GetString()} entitlement '{key.Name}' must map to the set");
            }
        }
    }

    [Fact]
    public void Catalog_CarriesTheDisplayFieldsAndNoCreditRates()
    {
        using JsonDocument doc = JsonDocument.Parse(ShippedCatalog());
        JsonElement root = doc.RootElement;
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("currency").GetString().Should().Be("USD");
        root.TryGetProperty("taxNote", out _).Should().BeTrue();
        root.GetProperty("topUp").GetProperty("credits").GetInt32().Should().BeGreaterThan(0);
        foreach (JsonElement plan in root.GetProperty("plans").EnumerateArray())
        {
            foreach (string field in new[] { "id", "name", "kind", "includedCredits", "highlights", "limits", "cta" })
            {
                plan.TryGetProperty(field, out _).Should().BeTrue(field);
            }
        }

        ShippedCatalog().Should().NotContainEquivalentOf("perUnit").And.NotContainEquivalentOf("millisPer");
    }

    [Fact]
    public void Load_RejectsDuplicateIds_AndUnknownKinds()
    {
        string dup = """{"plans":[{"id":"a","name":"A","kind":"trial"},{"id":"a","name":"B","kind":"trial"}]}""";
        Action act = () => PlanCatalogSeeder.Load(dup);
        act.Should().Throw<InvalidDataException>();

        string badKind = """{"plans":[{"id":"a","name":"A","kind":"nope"}]}""";
        Action act2 = () => PlanCatalogSeeder.Load(badKind);
        act2.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Upsert_IsIdempotent_PreservesProviderPriceIds_AndDeactivatesRemovedPlans()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            await PlanCatalogSeeder.UpsertAsync(db, PlanCatalogSeeder.Load(ShippedCatalog()));
        }

        Guid proId;
        DateTime stamp;
        await using (InferenceApiDbContext db = NewDb())
        {
            db.Plans.Count().Should().Be(5);
            Plan pro = db.Plans.Single(p => p.Key == "pro");
            proId = pro.Id;
            pro.ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_123"}}""";
            stamp = pro.UpdatedAt;
            await db.SaveChangesAsync();
        }

        // Re-running with the same catalog changes nothing and keeps the environment's price ids.
        await using (InferenceApiDbContext db = NewDb())
        {
            await PlanCatalogSeeder.UpsertAsync(db, PlanCatalogSeeder.Load(ShippedCatalog()));
        }

        await using (InferenceApiDbContext db = NewDb())
        {
            db.Plans.Count().Should().Be(5);
            Plan pro = db.Plans.Single(p => p.Key == "pro");
            pro.Id.Should().Be(proId);
            pro.UpdatedAt.Should().Be(stamp);
            pro.ProviderPriceIdsJson.Should().Contain("pri_123");
        }

        // An edited allowance updates in place; a plan dropped from the file is deactivated, not deleted.
        string edited = ShippedCatalog().Replace("\"includedCredits\": 3500", "\"includedCredits\": 4000");
        var trimmed = PlanCatalogSeeder.Load(edited).Where(p => p.Key != "enterprise").ToList();
        await using (InferenceApiDbContext db = NewDb())
        {
            await PlanCatalogSeeder.UpsertAsync(db, trimmed);
        }

        await using (InferenceApiDbContext db = NewDb())
        {
            Plan pro = db.Plans.Single(p => p.Key == "pro");
            pro.Id.Should().Be(proId);
            pro.MonthlyCreditsMillis.Should().Be(4_000_000);
            pro.ProviderPriceIdsJson.Should().Contain("pri_123");
            db.Plans.Single(p => p.Key == "enterprise").IsActive.Should().BeFalse();
            db.Plans.Count(p => p.IsActive).Should().Be(4);
        }
    }
}
